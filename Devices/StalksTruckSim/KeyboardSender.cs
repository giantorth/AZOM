using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Sends keystrokes to the foreground game via <c>SendInput</c> (scan-code based —
    /// what ETS2/ATS read). All taps are played on a dedicated worker thread so the
    /// callers (HID read thread, SimHub <c>DataUpdate</c> thread) never block on
    /// key hold / inter-tap timing, and every send is gated on the game being the
    /// foreground window so keys can never leak into another app.
    /// </summary>
    internal sealed class KeyboardSender : IDisposable
    {
        /// <summary>How long a key is held down per tap (ms).</summary>
        public int HoldMs { get; set; } = 30;
        /// <summary>Gap after a tap before the next queued tap (ms).</summary>
        public int GapMs { get; set; } = 40;

        // Process names (no .exe) that count as "the game is foreground". Empty = allow
        // all (the controller still gates on game-running, but set this in practice).
        private volatile string[] _foregroundProcs = Array.Empty<string>();

        private enum Op : byte { Tap, Down, Up }
        private readonly BlockingCollection<(ushort scan, Op op)> _queue =
            new BlockingCollection<(ushort, Op)>(new ConcurrentQueue<(ushort, Op)>());
        private readonly object _downLock = new object();
        private readonly HashSet<ushort> _down = new HashSet<ushort>();
        private readonly Thread _worker;
        private readonly Timer _repeatTimer;
        private const int RepeatMs = 40;   // re-assert held keys at ~25 Hz
        private volatile bool _disposed;

        public KeyboardSender()
        {
            _worker = new Thread(Run) { IsBackground = true, Name = "StalksKeyboard" };
            _worker.Start();
            // Re-assert held keys so games that sample the flash/high-beam key as an
            // edge (ETS2's J) keep it active for the whole hold, not just one frame.
            // Armed only while a key is held (KeyDown/KeyUp): this sender exists for
            // the plugin's whole life whether or not stalks are in use, and a
            // permanent 25 Hz wakeup for an idle feature is pure power draw.
            _repeatTimer = new Timer(_ => RepeatHeld(), null, Timeout.Infinite, Timeout.Infinite);
        }

        private void SetRepeatArmed(bool armed)
        {
            if (_disposed) return;
            int period = armed ? RepeatMs : Timeout.Infinite;
            try { _repeatTimer.Change(period, period); }
            catch (ObjectDisposedException) { }
        }

        private void RepeatHeld()
        {
            if (_disposed) return;
            ushort[] held;
            lock (_downLock)
            {
                if (_down.Count == 0) { SetRepeatArmed(false); return; }
                held = new ushort[_down.Count];
                _down.CopyTo(held);
            }
            foreach (var s in held) Enqueue(s, Op.Down);
        }

        /// <summary>Restrict key output to when one of these process names is the
        /// foreground window (e.g. "eurotrucks2", "amtrucks").</summary>
        public void SetForegroundProcesses(params string[] names)
            => _foregroundProcs = names ?? Array.Empty<string>();

        /// <summary>Queue a single key tap (down+up) by scan code. No-op for scan 0.</summary>
        public void Tap(ushort scan) => Enqueue(scan, Op.Tap);

        /// <summary>Queue a key tap by key name (resolved via <see cref="ScanCode"/>).</summary>
        public void Tap(string keyName) => Tap(ScanCode(keyName));

        /// <summary>Queue a key-down (held until <see cref="KeyUp"/>) by scan code.</summary>
        public void KeyDown(ushort scan)
        {
            if (scan == 0) return;
            lock (_downLock) _down.Add(scan);
            SetRepeatArmed(true);
            Enqueue(scan, Op.Down);
        }
        public void KeyDown(string keyName) => KeyDown(ScanCode(keyName));

        /// <summary>Queue a key-up by scan code.</summary>
        public void KeyUp(ushort scan)
        {
            if (scan == 0) return;
            bool anyHeld;
            lock (_downLock) { _down.Remove(scan); anyHeld = _down.Count > 0; }
            if (!anyHeld) SetRepeatArmed(false);
            Enqueue(scan, Op.Up);
        }
        public void KeyUp(string keyName) => KeyUp(ScanCode(keyName));

        /// <summary>Release every currently-held key — prevents a key getting stuck
        /// down on disable / focus loss / game exit.</summary>
        public void ReleaseAll()
        {
            ushort[] held;
            lock (_downLock) { held = new ushort[_down.Count]; _down.CopyTo(held); _down.Clear(); }
            SetRepeatArmed(false);
            foreach (var s in held) Enqueue(s, Op.Up);
        }

        private void Enqueue(ushort scan, Op op)
        {
            if (scan != 0 && !_disposed && !_queue.IsAddingCompleted)
                _queue.TryAdd((scan, op));
        }

        /// <summary>Drop any pending taps (e.g. on disable / focus loss).</summary>
        public void Flush()
        {
            while (_queue.TryTake(out _)) { }
        }

        private void Run()
        {
            try
            {
                foreach (var (scan, op) in _queue.GetConsumingEnumerable())
                {
                    if (_disposed) break;
                    try
                    {
                        // Key-up always fires — even if the game isn't foreground — so a
                        // held key can never get stuck down.
                        if (op == Op.Up) { SendKey(scan, down: false); continue; }
                        if (op == Op.Down)
                        {
                            // Only assert a key-down if the key is still held (a repeat
                            // Down enqueued just before release must not re-press it) and
                            // the game is foreground.
                            bool held; lock (_downLock) held = _down.Contains(scan);
                            if (held && IsGameForeground()) SendKey(scan, down: true);
                            continue;
                        }
                        if (!IsGameForeground()) continue;
                        SendKey(scan, down: true);   // Tap
                        Thread.Sleep(HoldMs);
                        SendKey(scan, down: false);
                        Thread.Sleep(GapMs);
                    }
                    catch { }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>True when a configured truck-game process owns the foreground
        /// window. Used by the controller to avoid advancing tracked wiper/light
        /// stages while alt-tabbed (when key output would be dropped).</summary>
        internal bool IsGameForeground()
        {
            var procs = _foregroundProcs;
            if (procs.Length == 0) return true;
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return false;
                using (var p = Process.GetProcessById((int)pid))
                {
                    string name = p.ProcessName;
                    foreach (var want in procs)
                        if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Resolve a stored key string to a Set-1 scan code. Returns 0 if unknown.</summary>
        public static ushort ScanCode(string keyName) => KeyCodes.Parse(keyName);

        // ------------------------------------------------------------------
        // Win32 SendInput
        // ------------------------------------------------------------------
        private void SendKey(ushort scan, bool down)
        {
            // Extended (E0-prefixed) keys carry 0xE0 in the high byte of the code.
            bool ext = (scan & 0xFF00) == 0xE000;
            uint flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP)
                | (ext ? KEYEVENTF_EXTENDEDKEY : 0u);
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = (ushort)(scan & 0xFF),
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            };
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        // Union sized to the largest member (MOUSEINPUT) so sizeof(INPUT) matches the
        // OS's expectation on both x86 and x64 SimHub builds.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData;
            public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _repeatTimer.Dispose(); } catch { }
            // Release any held keys directly — the worker is stopping and won't drain
            // queued key-ups.
            try
            {
                ushort[] held;
                lock (_downLock) { held = new ushort[_down.Count]; _down.CopyTo(held); _down.Clear(); }
                foreach (var s in held) { try { SendKey(s, down: false); } catch { } }
            }
            catch { }
            try { _queue.CompleteAdding(); } catch { }
            try { _worker.Join(500); } catch { }
            try { _queue.Dispose(); } catch { }
        }
    }
}
