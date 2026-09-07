using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MozaPlugin.Protocol
{
    internal enum NativeSpawnOutcome
    {
        /// <summary>Not under Wine, or the export is missing — nothing was run.</summary>
        Unavailable,
        /// <summary>The child ran to completion (check <see cref="NativeSpawnResult.Status"/>).</summary>
        Completed,
        /// <summary>The child outlived the timeout; the waiting thread was abandoned.</summary>
        TimedOut,
    }

    internal readonly struct NativeSpawnResult
    {
        public readonly NativeSpawnOutcome Outcome;
        public readonly int Status;
        public readonly int ElapsedMs;

        public NativeSpawnResult(NativeSpawnOutcome outcome, int status, int elapsedMs)
        {
            Outcome = outcome;
            Status = status;
            ElapsedMs = elapsedMs;
        }

        public bool Ran => Outcome == NativeSpawnOutcome.Completed;
    }

    /// <summary>
    /// Runs a NATIVE (non-Wine) Linux program from inside the Wine process.
    ///
    /// <para>Why this exists: some things can only be done outside Wine's
    /// device layer. The cold-start case is the CDC-ACM warm-up — a native
    /// open+termios completes the endpoint's <c>SET_LINE_CODING</c>, without
    /// which Wine's first comm-config IOCTL deadlocks the shared wineserver
    /// (see <c>docs/linux-cold-start-fix.md</c>). The other is reaping stub
    /// orphans whose wineserver died: they reparent to init and are invisible
    /// to <c>Process.GetProcessesByName</c>.</para>
    ///
    /// <para>Mechanism: <c>ntdll!__wine_unix_spawnvp</c> — the primitive
    /// <c>winebrowser.exe</c> uses to reach the host's <c>xdg-open</c>. It
    /// <c>fork</c>+<c>execvp</c>s and, with <c>wait=1</c>, waits for the child.
    /// Neither <c>CreateProcess</c> nor <c>start.exe /unix</c> can launch a
    /// native binary (kernelbase's <c>STATUS_INVALID_IMAGE_NOT_MZ</c> fallback
    /// only covers .com/.pif/.bat; start.exe only path-converts and calls
    /// ShellExecuteEx), so this export is the only in-process route.
    /// Present in system Wine and in Proton 8/9/11 + GE-Proton.</para>
    ///
    /// <para>execvp does the PATH lookup, so no shell is spawned and there is
    /// no quoting or injection surface — pass argv already split.</para>
    /// </summary>
    internal static class WineNativeExec
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        // NTSTATUS WINAPI __wine_unix_spawnvp( char *const argv[], int wait )
        // stdcall / 2 dwords — verified against the 32-bit ntdll thunk (ret 0x8)
        // and winebrowser's call site (argv in arg1, wait=0 in arg2).
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UnixSpawnvpDelegate(IntPtr argv, int wait);

        private static readonly object s_gate = new object();
        // Set LAST in Probe (after s_spawn is resolved) — a second caller that saw
        // the flag early read s_spawn == null and skipped the CDC warm-up.
        private static volatile bool s_probed;
        private static UnixSpawnvpDelegate? s_spawn;
        private static volatile string s_lastRun = "(none)";

        /// <summary>True when a native child can actually be launched.</summary>
        public static bool Available { get { Probe(); return s_spawn != null; } }

        /// <summary>Last run's command + outcome, for the diagnostics dump.</summary>
        public static string LastRun => s_lastRun;

        private static void Probe()
        {
            if (s_probed) return;
            lock (s_gate)
            {
                if (s_probed) return;
                try
                {
                    // Gate on IsWine AND a unix root: Wine on macOS has no /sys
                    // and none of the callers' unix paths would mean anything.
                    if (!WineHost.IsWine || WineHost.UnixRoot == null) return;

                    IntPtr ntdll = GetModuleHandle("ntdll.dll");
                    if (ntdll == IntPtr.Zero) return;
                    IntPtr p = GetProcAddress(ntdll, "__wine_unix_spawnvp");
                    if (p == IntPtr.Zero)
                    {
                        MozaLog.Debug("[AZOM] WineNativeExec: ntdll!__wine_unix_spawnvp missing — native exec unavailable");
                        return;
                    }
                    s_spawn = (UnixSpawnvpDelegate)Marshal.GetDelegateForFunctionPointer(
                        p, typeof(UnixSpawnvpDelegate));
                }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] WineNativeExec probe failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    s_probed = true;
                }
            }
        }

        /// <summary>
        /// Run <paramref name="argv"/> natively and wait up to
        /// <paramref name="timeoutMs"/> for it. Never throws.
        ///
        /// <para>On timeout the waiting thread is ABANDONED, never aborted — the
        /// same rule the serial probe follows (<see cref="MozaSerialConnection"/>
        /// ProbeWithTimeout). The thread owns the unmanaged argv it allocated and
        /// frees it itself, so an abandoned call can't be freed out from under.</para>
        /// </summary>
        public static NativeSpawnResult Run(string[] argv, int timeoutMs)
        {
            Probe();
            var spawn = s_spawn;
            if (spawn == null || argv == null || argv.Length == 0)
                return new NativeSpawnResult(NativeSpawnOutcome.Unavailable, 0, 0);

            int status = 0;
            var started = DateTime.UtcNow;
            var t = new Thread(() =>
            {
                IntPtr block = IntPtr.Zero;
                var parts = new IntPtr[argv.Length];
                try
                {
                    for (int i = 0; i < argv.Length; i++) parts[i] = AllocUtf8(argv[i]);
                    block = Marshal.AllocHGlobal(IntPtr.Size * (argv.Length + 1));
                    for (int i = 0; i < argv.Length; i++) Marshal.WriteIntPtr(block, i * IntPtr.Size, parts[i]);
                    Marshal.WriteIntPtr(block, argv.Length * IntPtr.Size, IntPtr.Zero);
                    status = spawn(block, 1);
                }
                catch (Exception ex)
                {
                    status = -1;
                    try { MozaLog.Debug($"[AZOM] WineNativeExec run failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
                }
                finally
                {
                    if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
                    for (int i = 0; i < parts.Length; i++)
                        if (parts[i] != IntPtr.Zero) Marshal.FreeHGlobal(parts[i]);
                }
            })
            { IsBackground = true, Name = "MozaNativeExec" };

            try { t.Start(); }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] WineNativeExec thread start failed: {ex.GetType().Name}: {ex.Message}");
                return new NativeSpawnResult(NativeSpawnOutcome.Unavailable, 0, 0);
            }

            bool done = t.Join(timeoutMs);
            int ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            var outcome = done ? NativeSpawnOutcome.Completed : NativeSpawnOutcome.TimedOut;
            s_lastRun = done
                ? $"{argv[0]} -> status 0x{status:X} in {ms} ms"
                : $"{argv[0]} -> TIMED OUT after {ms} ms (thread abandoned)";
            if (!done)
                MozaLog.Warn($"[AZOM] WineNativeExec: '{argv[0]}' did not return within {timeoutMs} ms — abandoning");
            return new NativeSpawnResult(outcome, done ? status : 0, ms);
        }

        private static IntPtr AllocUtf8(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0);
            return p;
        }
    }
}
