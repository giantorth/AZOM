using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MozaPlugin.PitHouseStub
{
    /// <summary>
    /// PitHouse name-impersonation stub. Phase 1 diagnostic build: in addition
    /// to staying alive forever, captures every byte the vendor SDK writes to
    /// our stdin and logs it to <c>%LOCALAPPDATA%\SimHub\MozaPlugin\PitHouseStub\
    /// stub-trace-&lt;pid&gt;-&lt;launchstamp&gt;.log</c>. Output-only — the stub
    /// writes nothing to stdout/stderr yet (deliberately, so we can observe the
    /// vendor's request without our reply mutating the conversation). Heartbeat
    /// lines fire every 2s so we can see whether the SDK kills the process and
    /// when.
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

                // Background: read stdin until EOF, log each chunk as hex + ASCII.
                var stdinThread = new Thread(StdinReaderLoop) { IsBackground = true, Name = "stub-stdin-reader" };
                stdinThread.Start();

                // Trap Ctrl-C / Ctrl-Break so we can log the signal before exit.
                Console.CancelKeyPress += OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                // Main loop: heartbeat once every 2s. Stays alive indefinitely;
                // the plugin's JobObject (or the SDK's TerminateProcess) ends us.
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    Thread.Sleep(2000);
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
                "SimHub", "MozaPlugin", "PitHouseStub");
            Directory.CreateDirectory(dir);
            int pid = Process.GetCurrentProcess().Id;
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string path = Path.Combine(dir, $"stub-trace-{pid}-{stamp}.log");
            _trace = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        }

        private static void LogHeader(string[] args)
        {
            Trace($"=== PitHouseStub diagnostic build ===");
            Trace($"PID={Process.GetCurrentProcess().Id}");
            Trace($"Parent={GetParentInfo()}");
            Trace($"Exe={Process.GetCurrentProcess().MainModule?.FileName ?? "<unknown>"}");
            Trace($"Args={string.Join(" | ", args)}");
            Trace($"CmdLine={Environment.CommandLine}");
            Trace($"CWD={Environment.CurrentDirectory}");
            Trace($"User={Environment.UserName}");
            Trace($"Machine={Environment.MachineName}");
            Trace($"OSVersion={Environment.OSVersion}");
        }

        private static string GetParentInfo()
        {
            try
            {
                // No portable .NET API; rely on WMI which isn't available net48 without ref.
                return "(parent-pid lookup omitted in this build)";
            }
            catch { return "<error>"; }
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
