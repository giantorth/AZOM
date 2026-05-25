using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Spawns, supervises, and tears down the <c>MOZA Pit House.exe</c> stub
    /// child process used by the third-party SDK emulation feature. The stub
    /// is an idle process whose only purpose is to satisfy applications that
    /// look up the vendor PitHouse binary by name — see Stream 3 of the
    /// implementation plan (<c>docs/sdk/</c>).
    ///
    /// Lifecycle:
    /// <list type="number">
    /// <item><see cref="Start"/> extracts the embedded resource
    /// <c>MozaPlugin.Sdk.CoapStub.exe</c> to
    /// <c>%LOCALAPPDATA%\SimHub\MozaPlugin\CoapStub\MOZA Pit House.exe</c>,
    /// creates a Win32 JobObject with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, spawns the stub with the
    /// thread created suspended, assigns the process to the job, then
    /// resumes the primary thread.</item>
    /// <item><see cref="Stop"/> calls <see cref="Process.Kill"/> and disposes
    /// the JobObject handle. Closing the handle is a belt-and-braces
    /// guarantee: <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> fires both on
    /// parent-process exit and on the last handle to the job being closed.
    /// </item>
    /// </list>
    ///
    /// The class is process-singleton: callers should reuse the same
    /// instance. Stream 7 wires it into <see cref="MozaPlugin"/>.Init/End;
    /// until then nothing in the plugin constructs one.
    /// </summary>
    public sealed class CoapStubManager : IDisposable
    {
        // Logical resource name embedded by MozaPlugin.csproj — see the
        // <EmbeddedResource Include="..\CoapStub\..."> item there.
        // Must match exactly; mismatched names fail silently in
        // GetManifestResourceStream so the constant lives here in one place.
        public const string EmbeddedResourceName = "MozaPlugin.Sdk.CoapStub.exe";

        // On-disk name must match the vendor binary so process-name
        // impersonation works for callers that look up "MOZA Pit House" by
        // executable name. Spaces are intentional.
        private const string StubExeFileName = "MOZA Pit House.exe";

        private static readonly object _gate = new object();

        private Process? _process;
        // Raw kernel HANDLE returned by CreateProcess. Distinct from
        // _process.Handle (which is opened by Process.GetProcessById and
        // owned by that managed wrapper). Owned by this manager — closed in
        // CleanupProcessLocked. Held primarily so AssignProcessToJobObject
        // gets a real handle even before any managed code touches _process.
        private IntPtr _processHandle;
        private SafeJobHandle? _jobHandle;
        private string _status = "Stopped";
        private string? _lastError;
        private bool _disposed;
        // True between successful ApplyRegistryRedirect and matching restore.
        // Gates RestoreRegistryRedirect so we don't touch the registry on
        // Stop()/Dispose() paths where Start() never reached the redirect
        // step (e.g., CreateProcess failed).
        private bool _redirectApplied;

        /// <summary>True while the spawned process is alive.</summary>
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    try { return _process != null && !_process.HasExited; }
                    catch { return false; }
                }
            }
        }

        /// <summary>OS process id of the spawned stub, or null if not running.</summary>
        public int? ProcessId
        {
            get
            {
                lock (_gate)
                {
                    try { return _process != null && !_process.HasExited ? _process.Id : (int?)null; }
                    catch { return null; }
                }
            }
        }

        /// <summary>Human-readable status string for surfacing in the UI tab.</summary>
        public string Status
        {
            get { lock (_gate) return _status; }
        }

        /// <summary>Most recent error message, if any. Cleared on successful <see cref="Start"/>.</summary>
        public string? LastError
        {
            get { lock (_gate) return _lastError; }
        }

        /// <summary>
        /// Path on disk the stub is extracted to. Exposed for diagnostics/UI.
        /// </summary>
        public static string StubExePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHub", "MozaPlugin", "CoapStub", StubExeFileName);

        // Registry coordinates the MOZA SDK reads to locate PitHouse.
        // (See iRacing's moza_sdk.dll sub_1800d8d00 — RegOpenKeyExA on
        //  HKCU, "Software\MOZA\PitHouse" then RegQueryValueExA on "path".)
        // The same subkey/value name is read by the public 1.0.1.8 SDK build.
        private const string MozaRegSubKey = @"Software\MOZA\PitHouse";
        private const string MozaRegValueName = "path";

        // Persisted snapshot of the user's original "path" value, written
        // just before we overwrite it. Lives next to the extracted stub so
        // a single Recycle-Bin sweep of the CoapStub folder cleans up
        // everything we own. Stored as raw UTF-8 path text; an empty file
        // means "no value existed → restore by deleting the value." File
        // existence is the authoritative signal that a redirect is active
        // and must be undone, even across a SimHub crash.
        private static string RegistryBackupPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHub", "MozaPlugin", "CoapStub", "registry-backup.path");

        /// <summary>
        /// Extract the embedded stub (if missing or stale) and spawn it under
        /// a JobObject pinned to the current process. No-op when already
        /// running.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    MozaLog.Warn("[Moza] CoapStubManager.Start called after Dispose; ignored.");
                    return;
                }

                if (_process != null)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            MozaLog.Warn($"[Moza] CoAP stub already running (PID {_process.Id}); Start() ignored.");
                            return;
                        }
                    }
                    catch { /* fall through and respawn */ }

                    CleanupProcessLocked();
                }

                try
                {
                    var exePath = ExtractStubExe();

                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // Manager is only functional on Windows — JobObject
                        // P/Invokes resolve to kernel32 stubs the Linux build
                        // will never call. Surface a clear "Disabled" status
                        // so the UI doesn't show a misleading "Stopped".
                        _status = "Disabled (non-Windows host)";
                        _lastError = null;
                        MozaLog.Info("[Moza] CoapStubManager.Start skipped: host is not Windows.");
                        return;
                    }

                    // Sweep orphan stub processes from prior crashed sessions
                    // before spawning our own. JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    // is supposed to kill the child when the parent process
                    // dies, but Wine/Proton has historically had gaps in that
                    // semantics. Without this sweep, a SimHub hard-crash leaves
                    // the stub child running; the next launch spawns another
                    // one and the orphan accumulates port/registry conflicts.
                    // Once per process — gated by static. Only matches by full
                    // exe path, so a real PitHouse install (different path) is
                    // never touched.
                    KillOrphanStubProcesses(exePath);

                    // Create the JobObject up front. Anonymous (lpName=null)
                    // so we don't collide with other instances of SimHub.
                    var jobRaw = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                    if (jobRaw == IntPtr.Zero)
                        throw new InvalidOperationException($"CreateJobObject failed: 0x{Marshal.GetLastWin32Error():X8}");

                    var jobHandle = new SafeJobHandle(jobRaw);

                    var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                    int infoSize = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
                    try
                    {
                        Marshal.StructureToPtr(info, infoPtr, false);
                        if (!NativeMethods.SetInformationJobObject(
                                jobHandle.DangerousGetHandle(),
                                NativeMethods.JobObjectInformationClass.JobObjectExtendedLimitInformation,
                                infoPtr,
                                (uint)infoSize))
                        {
                            int err = Marshal.GetLastWin32Error();
                            jobHandle.Dispose();
                            throw new InvalidOperationException($"SetInformationJobObject failed: 0x{err:X8}");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(infoPtr);
                    }

                    // Spawn the stub via Win32 CreateProcess directly so we
                    // can pass bInheritHandles=FALSE. Process.Start with
                    // UseShellExecute=false on Windows always passes
                    // bInheritHandles=TRUE, which means the child would
                    // inherit every inheritable handle in the SimHub process
                    // — including the game-receiver UDP sockets bound by
                    // other plugins (RBR 6776, DiRT 20776, AC/ACC 9996, etc.).
                    // Once the stub holds an inherited reference, the kernel
                    // keeps those ports reserved even after the original
                    // owners release them, which surfaces as SimHub's
                    // "the game UDP port is blocked" warning whenever the
                    // user switches games with SDK emulation enabled.
                    //
                    // CREATE_SUSPENDED lets us attach to the JobObject before
                    // the stub runs any code — kill-on-job-close is in force
                    // from the first instruction.
                    var si = new NativeMethods.STARTUPINFO
                    {
                        cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                    };
                    NativeMethods.PROCESS_INFORMATION pi;

                    // lpCommandLine must be writable per CreateProcessW docs.
                    // Quote the path because it contains spaces.
                    string cmdLine = $"\"{exePath}\"";

                    if (!NativeMethods.CreateProcess(
                            lpApplicationName: null,
                            lpCommandLine: cmdLine,
                            lpProcessAttributes: IntPtr.Zero,
                            lpThreadAttributes: IntPtr.Zero,
                            bInheritHandles: false,
                            dwCreationFlags: NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_NO_WINDOW,
                            lpEnvironment: IntPtr.Zero,
                            lpCurrentDirectory: Path.GetDirectoryName(exePath)!,
                            lpStartupInfo: ref si,
                            lpProcessInformation: out pi))
                    {
                        int err = Marshal.GetLastWin32Error();
                        jobHandle.Dispose();
                        throw new InvalidOperationException($"CreateProcess failed: 0x{err:X8}");
                    }

                    try
                    {
                        if (!NativeMethods.AssignProcessToJobObject(jobHandle.DangerousGetHandle(), pi.hProcess))
                        {
                            int err = Marshal.GetLastWin32Error();
                            // Stub is still suspended — terminate before it
                            // escapes the job's control.
                            try { NativeMethods.TerminateProcess(pi.hProcess, 1); } catch { }
                            throw new InvalidOperationException($"AssignProcessToJobObject failed: 0x{err:X8}");
                        }

                        if (NativeMethods.ResumeThread(pi.hThread) == unchecked((uint)-1))
                        {
                            int err = Marshal.GetLastWin32Error();
                            try { NativeMethods.TerminateProcess(pi.hProcess, 1); } catch { }
                            throw new InvalidOperationException($"ResumeThread failed: 0x{err:X8}");
                        }

                        // Thread handle no longer needed once resumed.
                        NativeMethods.CloseHandle(pi.hThread);
                        pi.hThread = IntPtr.Zero;

                        // Wrap the kernel handle in a managed Process so the
                        // existing Kill / HasExited / Exited paths keep
                        // working. Process.GetProcessById opens its own handle
                        // internally; we keep pi.hProcess too and close it in
                        // CleanupProcessLocked.
                        var p = Process.GetProcessById(pi.dwProcessId);
                        p.EnableRaisingEvents = true;
                        p.Exited += (_, _) => OnProcessExited();

                        _process = p;
                        _processHandle = pi.hProcess;
                        _jobHandle = jobHandle;
                        _status = $"Running (PID {p.Id})";
                        _lastError = null;
                        MozaLog.Info($"[Moza] CoAP stub started (PID {p.Id}, exe '{exePath}').");

                        // Redirect HKCU\Software\MOZA\PitHouse\path to our
                        // stub so the SDK's env check reads the FileVersion
                        // resource we embed (1.3.8.34, ≥ the hardcoded
                        // minimum 1.3.1.19). Without this, the SDK's
                        // version check reads whatever exe was previously
                        // registered — works today on installs with real
                        // PitHouse on disk, breaks for users without it.
                        //
                        // Done AFTER the process is confirmed running so
                        // any consumer that races a registry read with the
                        // env check's Toolhelp32 process scan sees a
                        // consistent (stub path, stub process) pair.
                        // ApplyRegistryRedirect swallows its own errors and
                        // logs — the stub stays useful for the process-name
                        // check even if we can't write the registry.
                        if (ApplyRegistryRedirect(exePath))
                            _redirectApplied = true;
                    }
                    catch
                    {
                        if (pi.hThread != IntPtr.Zero) NativeMethods.CloseHandle(pi.hThread);
                        if (pi.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(pi.hProcess);
                        jobHandle.Dispose();
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _status = "Failed to start";
                    MozaLog.Error($"[Moza] CoAP stub start failed: {ex}");
                    CleanupProcessLocked();
                }
            }
        }

        /// <summary>
        /// Kill the stub (if running) and dispose the JobObject. Idempotent.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_process == null && _jobHandle == null)
                {
                    _status = "Stopped";
                    return;
                }

                int? pid = null;
                try { pid = _process != null && !_process.HasExited ? _process.Id : (int?)null; }
                catch { }

                CleanupProcessLocked();

                _status = "Stopped";
                if (pid.HasValue)
                    MozaLog.Info($"[Moza] CoAP stub stopped (was PID {pid.Value}).");
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CleanupProcessLocked();
            }
        }

        /// <summary>
        /// Bounded variant of <see cref="Stop"/>. Spawns Stop on a worker
        /// thread and waits at most <paramref name="timeoutMs"/> milliseconds
        /// for it to return. Use this at every Stop() call site that runs on
        /// a thread the user is waiting on (SimHub UI thread during End(),
        /// ProcessExit, etc.) so a Wine-side wedge in Process.Kill or
        /// JobObject.Dispose can never block the caller.
        ///
        /// <para>Returns <c>true</c> if Stop() actually completed inside the
        /// budget; <c>false</c> on timeout. On timeout the inner Stop()
        /// continues running on the ThreadPool — that's intentional: the
        /// JobObject's <c>KILL_ON_JOB_CLOSE</c> flag still backstops child
        /// cleanup when the parent eventually exits, and the next plugin
        /// launch's orphan sweep cleans up if even that fails.</para>
        /// </summary>
        public bool TryStop(int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = 1500;
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                try { Stop(); }
                catch (Exception ex)
                {
                    try { MozaLog.Warn($"[Moza] CoAP stub Stop() raised: {ex.GetType().Name}: {ex.Message}"); } catch { }
                }
            });
            bool completed;
            try { completed = task.Wait(timeoutMs); }
            catch { completed = false; }
            if (!completed)
            {
                try
                {
                    MozaLog.Warn(
                        $"[Moza] CoAP stub Stop() timed out after {timeoutMs} ms — abandoning. " +
                        "JobObject KILL_ON_JOB_CLOSE will reap the child on process exit; " +
                        "orphan sweep covers the case where Wine doesn't honor it.");
                }
                catch { }
            }
            return completed;
        }

        // Process-once gate. The sweep needs to run only on the FIRST Start()
        // in this SimHub process — after our own child has been spawned,
        // any matching process is by definition ours (or an inherited
        // descendant of ours), and killing it would be a regression.
        private static int s_orphanSweepDone;

        /// <summary>
        /// Kill any running process whose <see cref="Process.MainModule"/>
        /// path matches <paramref name="ourExePath"/>. These are stub
        /// children orphaned by a prior SimHub crash where the JobObject's
        /// <c>KILL_ON_JOB_CLOSE</c> didn't fire — typically Wine/Proton
        /// gaps in that semantic. Only matches our own extracted exe path,
        /// so a real PitHouse install (different path) is never touched.
        ///
        /// <para>Idempotent within a process via <see cref="s_orphanSweepDone"/>.
        /// Failures are non-fatal (logged at Debug) — we'd rather lose a
        /// kill than fail Start().</para>
        /// </summary>
        private static void KillOrphanStubProcesses(string ourExePath)
        {
            if (System.Threading.Interlocked.Exchange(ref s_orphanSweepDone, 1) != 0) return;
            if (string.IsNullOrEmpty(ourExePath)) return;

            string exeName;
            try { exeName = Path.GetFileNameWithoutExtension(ourExePath); }
            catch { return; }
            if (string.IsNullOrEmpty(exeName)) return;

            Process[] candidates;
            try { candidates = Process.GetProcessesByName(exeName); }
            catch (Exception ex)
            {
                try { MozaLog.Debug($"[Moza] Orphan-sweep enumerate failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
                return;
            }

            int killed = 0;
            foreach (var p in candidates)
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; }
                    catch
                    {
                        // Access-denied (different user / elevated) or
                        // already-exited race: skip rather than guess.
                        continue;
                    }
                    if (!string.Equals(path, ourExePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int pid = p.Id;
                    try
                    {
                        p.Kill();
                        try { p.WaitForExit(500); } catch { }
                        killed++;
                        try { MozaLog.Info($"[Moza] Orphan-sweep killed prior-session stub PID {pid} (path={path})"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { MozaLog.Warn($"[Moza] Orphan-sweep kill PID {pid} failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
                    }
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            if (killed == 0)
            {
                try { MozaLog.Debug($"[Moza] Orphan-sweep: no prior-session stub processes found ({candidates.Length} candidate(s) checked)"); } catch { }
            }
        }

        private void OnProcessExited()
        {
            lock (_gate)
            {
                if (_process == null) return;
                int code;
                try { code = _process.ExitCode; } catch { code = -1; }

                // Only update status if we didn't already mark it Stopped via
                // explicit Stop(). A nonzero exit while we still think it's
                // running counts as a crash.
                if (_status.StartsWith("Running", StringComparison.Ordinal))
                {
                    _status = code == 0
                        ? "Stopped (exit 0)"
                        : $"Crashed (exit {code})";
                    MozaLog.Warn($"[Moza] CoAP stub exited unexpectedly with code {code}.");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Resource extraction
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Copy the embedded stub exe to its on-disk location. If a file is
        /// already there with the same SHA-1 we leave it alone (so we never
        /// race a running stub that has the file open for execution).
        /// Returns the path to the on-disk exe.
        /// </summary>
        private static string ExtractStubExe()
        {
            var dir = Path.GetDirectoryName(StubExePath)!;
            Directory.CreateDirectory(dir);

            var asm = typeof(CoapStubManager).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{EmbeddedResourceName}' not found in {asm.GetName().Name}. " +
                    "Verify MozaPlugin.csproj embeds the CoapStub output.");

            // Read the resource into memory once — net48 streams over a
            // resource section are short and this lets us hash and write in
            // one pass without holding the stream open across the file
            // write.
            byte[] payload;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                payload = ms.ToArray();
            }

            string embeddedHash = HashSha1(payload);

            if (File.Exists(StubExePath))
            {
                try
                {
                    var existing = File.ReadAllBytes(StubExePath);
                    if (HashSha1(existing) == embeddedHash)
                    {
                        // Identical — don't touch (avoids sharing-violation
                        // if the same binary is currently running).
                        return StubExePath;
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] Could not hash existing stub at '{StubExePath}': {ex.Message}; will overwrite.");
                }
            }

            File.WriteAllBytes(StubExePath, payload);
            return StubExePath;
        }

        private static string HashSha1(byte[] data)
        {
            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(data);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Must hold _gate.
        private void CleanupProcessLocked()
        {
            // Restore the registry FIRST, while the stub is still alive.
            // That ordering means any SDK consumer that races a registry
            // read against the shutdown sees a coherent (real path, real
            // process exits) sequence — never (stub path, no process).
            // Restore reads the backup file we wrote in ApplyRegistryRedirect;
            // both halves are gated by the same _redirectApplied flag.
            if (_redirectApplied)
            {
                try { RestoreRegistryRedirect(); }
                catch (Exception ex) { MozaLog.Warn($"[Moza] Registry restore raised in cleanup: {ex.GetType().Name}: {ex.Message}"); }
                _redirectApplied = false;
            }

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited) _process.Kill();
                }
                catch { /* already gone or never started */ }

                try { _process.Dispose(); } catch { }
                _process = null;
            }

            // Close the raw kernel handle CreateProcess returned. Separate
            // from the managed Process wrapper above (which owns its own
            // GetProcessById handle).
            if (_processHandle != IntPtr.Zero)
            {
                try { NativeMethods.CloseHandle(_processHandle); } catch { }
                _processHandle = IntPtr.Zero;
            }

            if (_jobHandle != null)
            {
                try { _jobHandle.Dispose(); } catch { }
                _jobHandle = null;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Registry redirect — HKCU\Software\MOZA\PitHouse\path
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Snapshot the user's current registry value (if any) to a
        /// crash-safe backup file, then overwrite the value with
        /// <paramref name="stubExePath"/>. Returns true if the redirect is
        /// active and a matching <see cref="RestoreRegistryRedirect"/>
        /// must run on shutdown; false if we couldn't write the registry
        /// and left the user's state untouched.
        ///
        /// Crash semantics: if a backup file already exists from a prior
        /// (crashed) session, it is NOT overwritten — that earlier file
        /// is the truth about the user's *real* original value. The
        /// current registry contents are treated as untrusted in that
        /// case (they may be a stale stub path) and simply replaced.
        /// </summary>
        private static bool ApplyRegistryRedirect(string stubExePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MozaLog.Info("[Moza] Registry redirect skipped: host is not Windows.");
                return false;
            }

            var backupPath = RegistryBackupPath;

            // Take the snapshot ONLY when no prior backup exists — otherwise
            // the snapshot below would capture our own previous-session stub
            // path and we'd "restore" to that on Stop, never reaching the
            // user's true original.
            if (!File.Exists(backupPath))
            {
                string? original = null;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(MozaRegSubKey, writable: false);
                    if (key != null && key.GetValue(MozaRegValueName) is string s)
                        original = s;
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] Reading HKCU\\{MozaRegSubKey}\\{MozaRegValueName} for backup failed: {ex.GetType().Name}: {ex.Message}; treating as absent.");
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.WriteAllText(backupPath, original ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch (Exception ex)
                {
                    // Without a durable backup we can't promise to restore
                    // on shutdown — refuse the redirect rather than leave
                    // the user's registry permanently pointed at the stub.
                    MozaLog.Error($"[Moza] Could not persist registry backup to '{backupPath}': {ex.GetType().Name}: {ex.Message}; refusing to redirect.");
                    return false;
                }
            }
            else
            {
                MozaLog.Info($"[Moza] Reusing existing registry backup '{backupPath}' (prior session did not clean up).");
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(MozaRegSubKey, writable: true)
                    ?? throw new InvalidOperationException("CreateSubKey returned null");
                key.SetValue(MozaRegValueName, stubExePath, RegistryValueKind.String);
                MozaLog.Info($"[Moza] HKCU\\{MozaRegSubKey}\\{MozaRegValueName} redirected to stub '{stubExePath}'.");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Registry redirect write failed: {ex.GetType().Name}: {ex.Message}. Backup file kept; restore on Stop will undo any partial state.");
                return false;
            }
        }

        /// <summary>
        /// Restore the registry value captured by the most recent
        /// <see cref="ApplyRegistryRedirect"/>. Empty backup file means
        /// "no value existed" — restore is then a delete. Backup file is
        /// removed on success; on failure it is kept so the next Start
        /// can retry the restore before snapshotting fresh state.
        /// </summary>
        private static void RestoreRegistryRedirect()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var backupPath = RegistryBackupPath;
            if (!File.Exists(backupPath))
                return; // Nothing to restore — either never redirected or already restored.

            string original;
            try
            {
                original = File.ReadAllText(backupPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Reading registry backup '{backupPath}' failed: {ex.GetType().Name}: {ex.Message}; leaving registry untouched.");
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(original))
                {
                    using var key = Registry.CurrentUser.OpenSubKey(MozaRegSubKey, writable: true);
                    if (key != null)
                    {
                        // throwOnMissingValue=false: someone else may have
                        // already deleted it between our write and now.
                        try { key.DeleteValue(MozaRegValueName, throwOnMissingValue: false); }
                        catch (Exception ex) { MozaLog.Debug($"[Moza] DeleteValue ignored: {ex.Message}"); }
                    }
                    MozaLog.Info($"[Moza] HKCU\\{MozaRegSubKey}\\{MozaRegValueName} restored (deleted — original had no value).");
                }
                else
                {
                    using var key = Registry.CurrentUser.CreateSubKey(MozaRegSubKey, writable: true)
                        ?? throw new InvalidOperationException("CreateSubKey returned null");
                    key.SetValue(MozaRegValueName, original, RegistryValueKind.String);
                    MozaLog.Info($"[Moza] HKCU\\{MozaRegSubKey}\\{MozaRegValueName} restored to original '{original}'.");
                }
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Registry restore write failed: {ex.GetType().Name}: {ex.Message}. Backup file kept for next attempt.");
                return; // Don't delete backup — preserve for retry.
            }

            try { File.Delete(backupPath); }
            catch (Exception ex) { MozaLog.Warn($"[Moza] Could not delete registry backup '{backupPath}' after restore: {ex.GetType().Name}: {ex.Message}."); }
        }

        // ─────────────────────────────────────────────────────────────
        // Win32 P/Invoke
        // ─────────────────────────────────────────────────────────────

        private sealed class SafeJobHandle : SafeHandle
        {
            public SafeJobHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
            {
                SetHandle(handle);
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
        }

        private static class NativeMethods
        {
            public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

            public const uint CREATE_SUSPENDED = 0x00000004;
            public const uint CREATE_NO_WINDOW = 0x08000000;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct STARTUPINFO
            {
                public int cb;
                public string? lpReserved;
                public string? lpDesktop;
                public string? lpTitle;
                public uint dwX;
                public uint dwY;
                public uint dwXSize;
                public uint dwYSize;
                public uint dwXCountChars;
                public uint dwYCountChars;
                public uint dwFillAttribute;
                public uint dwFlags;
                public short wShowWindow;
                public short cbReserved2;
                public IntPtr lpReserved2;
                public IntPtr hStdInput;
                public IntPtr hStdOutput;
                public IntPtr hStdError;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PROCESS_INFORMATION
            {
                public IntPtr hProcess;
                public IntPtr hThread;
                public int dwProcessId;
                public int dwThreadId;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CreateProcess(
                string? lpApplicationName,
                string lpCommandLine,
                IntPtr lpProcessAttributes,
                IntPtr lpThreadAttributes,
                [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
                uint dwCreationFlags,
                IntPtr lpEnvironment,
                string? lpCurrentDirectory,
                [In] ref STARTUPINFO lpStartupInfo,
                out PROCESS_INFORMATION lpProcessInformation);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern uint ResumeThread(IntPtr hThread);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

            public enum JobObjectInformationClass : int
            {
                JobObjectBasicAccountingInformation = 1,
                JobObjectBasicLimitInformation = 2,
                JobObjectBasicProcessIdList = 3,
                JobObjectBasicUIRestrictions = 4,
                JobObjectSecurityLimitInformation = 5,
                JobObjectEndOfJobTimeInformation = 6,
                JobObjectAssociateCompletionPortInformation = 7,
                JobObjectBasicAndIoAccountingInformation = 8,
                JobObjectExtendedLimitInformation = 9,
                JobObjectJobSetInformation = 10,
                JobObjectGroupInformation = 11,
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct IO_COUNTERS
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(
                IntPtr hJob,
                JobObjectInformationClass JobObjectInformationClass,
                IntPtr lpJobObjectInformation,
                uint cbJobObjectInformationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr hObject);
        }
    }
}
