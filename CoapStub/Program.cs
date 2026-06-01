using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MozaPlugin.CoapStub
{
    /// <summary>
    /// Stub that runs as <c>MOZA Pit House.exe</c> so process-name probes from
    /// the vendor CoAP SDK find the expected name. Its lifetime is bound to the
    /// SimHub process that owns the SDK-emulation feature:
    ///
    /// <list type="bullet">
    /// <item>The plugin's <c>CoapStubManager</c> spawns us with
    /// <c>--parent-pid &lt;SimHub PID&gt;</c>. We watch that PID and exit the
    /// moment it goes away. This is the primary shutdown path — it works even
    /// under Wine/Proton, where the JobObject's <c>KILL_ON_JOB_CLOSE</c> (our
    /// backstop) has historically been unreliable.</item>
    /// <item>If launched WITHOUT <c>--parent-pid</c> (e.g. the vendor SDK
    /// started us directly via the registry redirect), we look for a running
    /// <c>SimHubWPF</c> process and watch that instead. If none is running we
    /// exit immediately — a PitHouse stand-in is pointless with no SimHub.</item>
    /// </list>
    ///
    /// In addition the stub captures every byte the vendor SDK writes to our
    /// stdin and logs it to
    /// <c>%LOCALAPPDATA%\SimHub\MozaPlugin\CoapStub\stub-trace-&lt;pid&gt;-&lt;launchstamp&gt;.log</c>.
    /// Output-only — the stub writes nothing to stdout/stderr (deliberately, so
    /// we can observe the vendor's request without our reply mutating the
    /// conversation). Heartbeat lines fire every 2s and double as the
    /// parent-liveness poll.
    /// </summary>
    internal static class Program
    {
        private static StreamWriter? _trace;
        private static readonly object _traceGate = new object();

        private static int Main(string[] args)
        {
            try
            {
                OpenTrace(args);
                LogHeader(args);

                // Resolve the process whose lifetime we mirror. Exits the
                // process immediately (return) if no such SimHub is running —
                // satisfies "exit immediately if launched and SimHub is not
                // running."
                Process? parent = ResolveParent(args);
                if (parent == null)
                {
                    Trace("parent not running at startup — exiting");
                    return 0;
                }
                Trace($"watching parent PID={parent.Id} (name={SafeProcessName(parent)})");

                // Background: read stdin until EOF, log each chunk as hex + ASCII.
                var stdinThread = new Thread(StdinReaderLoop) { IsBackground = true, Name = "stub-stdin-reader" };
                stdinThread.Start();

                // Trap Ctrl-C / Ctrl-Break so we can log the signal before exit.
                Console.CancelKeyPress += OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                // Main loop: heartbeat once every 2s, which doubles as the
                // parent-liveness poll. When the parent (SimHub) exits we exit
                // too — the Wine-proof primary teardown path; the plugin's
                // JobObject is the backstop.
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    Thread.Sleep(2000);
                    if (HasExitedSafe(parent))
                    {
                        Trace($"parent exited — shutting down (uptime={sw.Elapsed:hh\\:mm\\:ss})");
                        return 0;
                    }
                    Trace($"heartbeat uptime={sw.Elapsed:hh\\:mm\\:ss}");
                }
            }
            catch (Exception ex)
            {
                Trace($"FATAL in Main: {ex.GetType().FullName}: {ex.Message}");
                Trace(ex.StackTrace ?? "");
                return 1;
            }
        }

        /// <summary>
        /// Resolve the process whose lifetime this stub mirrors. Returns a live
        /// <see cref="Process"/> handle, or null if no suitable SimHub is
        /// running (caller exits immediately in that case).
        ///
        /// <para>Preference order: an explicit <c>--parent-pid &lt;N&gt;</c>
        /// passed by <c>CoapStubManager</c> (authoritative — it's the exact
        /// SimHub that owns us), then a by-name lookup of <c>SimHubWPF</c> for
        /// the case where the vendor SDK launched us directly.</para>
        /// </summary>
        private static Process? ResolveParent(string[] args)
        {
            int pid = ParseParentPid(args);
            if (pid > 0)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    // GetProcessById throws if the PID isn't running; a returned
                    // handle that already reports HasExited is also "gone".
                    if (!p.HasExited) return p;
                    Trace($"--parent-pid {pid} already exited");
                }
                catch (ArgumentException)
                {
                    Trace($"--parent-pid {pid} not running");
                }
                catch (Exception ex)
                {
                    Trace($"--parent-pid {pid} lookup failed: {ex.GetType().Name}: {ex.Message}");
                }
                return null;
            }

            // No explicit parent — we were launched by something other than the
            // plugin (most likely the vendor SDK via the registry redirect).
            // Bind to a running SimHub if there is one; otherwise exit.
            Trace("no --parent-pid; searching for a running SimHub process");
            foreach (var name in new[] { "SimHubWPF", "SimHub" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { if (!p.HasExited) return p; } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Trace($"GetProcessesByName({name}) failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Extract the integer following <c>--parent-pid</c> in the argument
        /// list. Returns 0 when absent or unparseable.
        /// </summary>
        private static int ParseParentPid(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--parent-pid", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pid))
                    return pid;
                return 0;
            }
            return 0;
        }

        private static bool HasExitedSafe(Process p)
        {
            try { return p.HasExited; }
            catch (Exception ex)
            {
                // If we can no longer query the handle, assume the process is
                // gone — better to exit a still-orphaned stub than to leak one.
                Trace($"parent HasExited query failed ({ex.GetType().Name}: {ex.Message}); treating as exited");
                return true;
            }
        }

        private static string SafeProcessName(Process p)
        {
            try { return p.ProcessName; }
            catch { return "<unknown>"; }
        }

        private static void StdinReaderLoop()
        {
            try
            {
                Trace($"stdin reader thread started; IsInputRedirected={Console.IsInputRedirected}");
                using var stdin = Console.OpenStandardInput();
                var buf = new byte[4096];
                while (true)
                {
                    int n = stdin.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        Trace("stdin EOF");
                        return;
                    }
                    var bytes = new byte[n];
                    Array.Copy(buf, bytes, n);
                    Trace($"stdin recv {n}B: {FormatHex(bytes)}  ascii=\"{FormatAscii(bytes)}\"");
                }
            }
            catch (Exception ex)
            {
                Trace($"stdin reader threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            Trace($"Console.CancelKeyPress SpecialKey={e.SpecialKey}");
            // Don't cancel — let the SDK / job-object kill us so we can see it.
            e.Cancel = true;
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Trace("ProcessExit event");
            lock (_traceGate) { _trace?.Flush(); _trace?.Close(); }
        }

        private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Trace($"UnhandledException IsTerminating={e.IsTerminating}: {ex?.GetType().FullName}: {ex?.Message}");
            Trace(ex?.StackTrace ?? "");
            lock (_traceGate) { _trace?.Flush(); }
        }

        private static void OpenTrace(string[] args)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHub", "MozaPlugin", "CoapStub");
            Directory.CreateDirectory(dir);
            int pid = Process.GetCurrentProcess().Id;
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string path = Path.Combine(dir, $"stub-trace-{pid}-{stamp}.log");
            _trace = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        }

        private static void LogHeader(string[] args)
        {
            Trace($"=== CoapStub diagnostic build ===");
            Trace($"PID={Process.GetCurrentProcess().Id}");
            Trace($"Parent={GetParentInfo(args)}");
            Trace($"Exe={Process.GetCurrentProcess().MainModule?.FileName ?? "<unknown>"}");
            Trace($"Args={string.Join(" | ", args)}");
            Trace($"CmdLine={Environment.CommandLine}");
            Trace($"CWD={Environment.CurrentDirectory}");
            Trace($"User={Environment.UserName}");
            Trace($"Machine={Environment.MachineName}");
            Trace($"OSVersion={Environment.OSVersion}");
        }

        private static string GetParentInfo(string[] args)
        {
            int pid = ParseParentPid(args);
            return pid > 0
                ? $"--parent-pid={pid}"
                : "(no --parent-pid; will bind to a running SimHub by name)";
        }

        private static void Trace(string line)
        {
            var stamped = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {line}";
            lock (_traceGate)
            {
                try { _trace?.WriteLine(stamped); }
                catch { /* writer may be closed during exit */ }
            }
        }

        private static string FormatHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string FormatAscii(byte[] b)
        {
            var sb = new StringBuilder(b.Length);
            foreach (var x in b)
                sb.Append(x >= 0x20 && x < 0x7F ? (char)x : '.');
            return sb.ToString();
        }
    }
}
