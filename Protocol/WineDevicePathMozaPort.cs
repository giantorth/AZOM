using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// <see cref="IMozaPort"/> that opens a serial device DIRECTLY by its unix
    /// device path (exposed to Wine as <c>Z:\dev\ttyACM2</c>) via raw Win32 comm
    /// APIs, instead of by COM name through SerialPort.
    ///
    /// <para>Why: under Wine the plugin must select the MOZA by USB identity and
    /// open ONLY it. Blind-probing COM ports opens whatever else is on the bus
    /// (an Arduino, another vendor's CDC-ACM interface), and a Wine
    /// SerialPort.Open on such a device can take the SHARED wineserver down with
    /// it — killing SimHub, which no out-of-process probe isolation can prevent.
    /// Verified under GE-Proton: <c>CreateFileW</c> on a unix device path returns
    /// a usable comm handle (GetCommModemStatus/SetCommState/ReadFile/WriteFile
    /// all work), while the COM↔ttyACM mapping is NOT readable read-only
    /// (reparse/QueryDosDevice/GetFinalPathName all blocked) — so we bypass COM
    /// names entirely. <see cref="LinuxUsbEnumerator"/> supplies the path and the
    /// USB identity that goes with it.</para>
    ///
    /// <para>Restored from 379d18c (rolled back in e302bd7 because the by-id path
    /// carried no VID/PID, which broke lane routing; sysfs enumeration supplies
    /// that now).</para>
    /// </summary>
    internal sealed class WineDevicePathMozaPort : IMozaPort
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint MAXDWORD = 0xFFFFFFFF;
        private const uint PURGE_RXCLEAR = 0x0008;
        private const uint PURGE_TXCLEAR = 0x0004;
        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        private readonly object _gate = new object();
        private IntPtr _handle = INVALID_HANDLE;
        private volatile bool _closed;
        private readonly string _path;

        /// <summary>Open and configure the device. Throws on failure (the caller
        /// classifies it exactly like a SerialPort open failure).</summary>
        public WineDevicePathMozaPort(string devicePath)
        {
            _path = devicePath;
            WarmUpEndpoint(devicePath);
            MozaLog.Debug("[AZOM] WineDevicePathPort: CreateFile");
            IntPtr h = CreateFileW(devicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE)
                throw OpenFailure(devicePath, Marshal.GetLastWin32Error());
            _handle = h;
            MozaLog.Debug("[AZOM] WineDevicePathPort: CreateFile ok — configuring comm");
            try
            {
                ConfigureComm(h);
            }
            catch
            {
                CloseHandle(h);
                _handle = INVALID_HANDLE;
                throw;
            }
            MozaLog.Debug("[AZOM] WineDevicePathPort: comm configured");
        }

        /// <summary>
        /// Native (non-Wine) open + termios of the tty before Wine touches it.
        ///
        /// <para>On a freshly-powered base, Wine's first comm-config IOCTL after
        /// CreateFile blocks FOREVER on a not-yet-fully-enumerated CDC-ACM
        /// endpoint, and that wedges the shared wineserver — every later open
        /// hangs and SimHub freezes. A native open + tcsetattr is hang-safe and
        /// performs the CDC SET_LINE_CODING that completes the endpoint setup,
        /// after which the Wine path sails through. <c>stty</c> is exactly that
        /// (it opens O_RDONLY|O_NONBLOCK, then tcgetattr/tcsetattr) and ships
        /// with coreutils and busybox alike. See
        /// <c>docs/linux-cold-start-fix.md</c>.</para>
        ///
        /// <para><c>-hupcl</c> is load-bearing: without it stty's own close drops
        /// DTR, adding a modem-line toggle immediately before our open.</para>
        ///
        /// <para>A non-zero exit is fine (the open will fail on its own and the
        /// reconnect timer retries). A TIMEOUT is not: the native side wedging is
        /// the one state in which entering Wine's comm-config is unsafe, so we
        /// refuse the open and let the 5 s reconnect try again.</para>
        /// </summary>
        internal static void WarmUpEndpoint(string devicePath)
        {
            if (!WineNativeExec.Available) return;
            string? node = WineHost.ToUnixPath(devicePath);
            if (string.IsNullOrEmpty(node)) return;

            var r = WineNativeExec.Run(
                new[] { "stty", "-F", node!, "115200", "raw", "-echo", "clocal", "-hupcl" },
                timeoutMs: 3000);
            if (r.Outcome == NativeSpawnOutcome.TimedOut)
                throw new IOException($"native warm-up of {node} did not return — refusing the Wine open");
            if (r.Outcome == NativeSpawnOutcome.Completed)
                MozaLog.Debug($"[AZOM] WineDevicePathPort: warmed {node} (status 0x{r.Status:X}, {r.ElapsedMs} ms)");
        }

        // MozaSerialConnection.TryOpen classifies open failures by exception
        // TYPE (it was written against SerialPort), so raise the same types here
        // or every failure on this lane degrades to "OpenFailedOther" and the
        // port-in-use banner never fires.
        private static Exception OpenFailure(string path, int err)
        {
            const int ERROR_FILE_NOT_FOUND = 2;
            const int ERROR_PATH_NOT_FOUND = 3;
            const int ERROR_ACCESS_DENIED = 5;
            const int ERROR_SHARING_VIOLATION = 32;
            string msg = $"CreateFile('{path}') failed: Win32 {err}";
            switch (err)
            {
                case ERROR_ACCESS_DENIED:
                case ERROR_SHARING_VIOLATION:
                    return new UnauthorizedAccessException(msg);
                case ERROR_FILE_NOT_FOUND:
                case ERROR_PATH_NOT_FOUND:
                    return new FileNotFoundException(msg, path);
                default:
                    return new IOException(msg);
            }
        }

        private static void ConfigureComm(IntPtr h)
        {
            // NOTE: do NOT call SetupComm here (or anywhere on a cold port). On a
            // freshly-enumerated base it WEDGES Wine — the whole process froze at the
            // SetupComm call. (Buffer enlargement for the cold-boot burst is deferred
            // until the open path is otherwise solid.)

            // Start from the driver defaults so any field we don't set stays sane.
            DCB dcb = default;
            dcb.DCBlength = (uint)Marshal.SizeOf(typeof(DCB));
            MozaLog.Debug("[AZOM] WineDevicePathPort: GetCommState");
            GetCommState(h, ref dcb); // best-effort; if it fails we fill below
            MozaLog.Debug("[AZOM] WineDevicePathPort: SetCommState");

            dcb.DCBlength = (uint)Marshal.SizeOf(typeof(DCB));
            dcb.BaudRate = (uint)MozaProtocol.BaudRate; // 115200
            dcb.ByteSize = 8;
            dcb.Parity = 0;   // NOPARITY
            dcb.StopBits = 0; // ONESTOPBIT
            // Flags bitfield: fBinary(bit0)=1 (required by Win32 serial), no parity
            // check, and fDtrControl(bits4-5)=DTR_CONTROL_ENABLE(01) — CDC-ACM uses
            // DTR as the host-connected signal, matching SerialPort.DtrEnable=true.
            dcb.Flags |= 0x1u;            // fBinary
            dcb.Flags &= ~0x2u;           // fParity = 0
            dcb.Flags &= ~(0x3u << 4);    // clear fDtrControl
            dcb.Flags |= (0x1u << 4);     // DTR_CONTROL_ENABLE
            if (!SetCommState(h, ref dcb))
                throw new IOException($"SetCommState failed: Win32 {Marshal.GetLastWin32Error()}");
            MozaLog.Debug("[AZOM] WineDevicePathPort: SetCommTimeouts");

            // Return-immediately reads: ReadFile yields whatever is buffered now and
            // never blocks (the ReadLoop polls BytesToRead itself). Writes get a
            // finite 500ms cap to match the old SerialPort.WriteTimeout.
            COMMTIMEOUTS t = default;
            t.ReadIntervalTimeout = MAXDWORD;
            t.ReadTotalTimeoutMultiplier = 0;
            t.ReadTotalTimeoutConstant = 0;
            t.WriteTotalTimeoutMultiplier = 0;
            t.WriteTotalTimeoutConstant = 500;
            if (!SetCommTimeouts(h, ref t))
                throw new IOException($"SetCommTimeouts failed: Win32 {Marshal.GetLastWin32Error()}");
        }

        public bool IsOpen => !_closed && _handle != INVALID_HANDLE;

        // ConfigureComm sets ReadIntervalTimeout=MAXDWORD with both read-total
        // timeouts at 0 — the Win32 idiom for "return whatever is buffered, never
        // block". So ReadFile here is safe to call unconditionally.
        public bool ReadReturnsImmediately => true;

        // Every syscall below runs under _gate together with its _closed check:
        // Close() (from Disconnect / ClosePortAndNotify on another thread) frees the
        // handle, and the OS may hand that value to an unrelated object — a read of
        // _handle followed by ReadFile/WriteFile outside the gate could land on it.
        // ReadFile returns immediately here (COMMTIMEOUTS) and WriteFile is capped
        // at 500 ms, so Close() never waits long for an in-flight call.
        public int BytesToRead
        {
            get
            {
                lock (_gate)
                {
                    if (_closed) return 0;
                    if (!ClearCommError(_handle, out _, out COMSTAT st)) return 0;
                    return (int)st.cbInQue;
                }
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            lock (_gate)
            {
                if (_closed) return 0;
                // ReadFile fills from the buffer start; honor offset via a temp on the
                // (unused-in-practice) offset!=0 path so the hot path stays copy-free.
                if (offset == 0)
                {
                    if (!ReadFile(_handle, buffer, (uint)count, out uint read, IntPtr.Zero))
                        throw new IOException($"ReadFile failed: Win32 {Marshal.GetLastWin32Error()}");
                    return (int)read;
                }
                byte[] tmp = new byte[count];
                if (!ReadFile(_handle, tmp, (uint)count, out uint rd, IntPtr.Zero))
                    throw new IOException($"ReadFile failed: Win32 {Marshal.GetLastWin32Error()}");
                Array.Copy(tmp, 0, buffer, offset, (int)rd);
                return (int)rd;
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            byte[] src;
            if (offset == 0)
            {
                src = buffer;
            }
            else
            {
                src = new byte[count];
                Array.Copy(buffer, offset, src, 0, count);
            }
            lock (_gate)
            {
                if (_closed) return;
                int written = 0;
                while (written < count)
                {
                    // WriteFile starts at the buffer head; slice the remainder when a
                    // partial write occurs (rare for a tty, but correct).
                    byte[] chunk;
                    if (written == 0) chunk = src;
                    else { chunk = new byte[count - written]; Array.Copy(src, written, chunk, 0, count - written); }
                    if (!WriteFile(_handle, chunk, (uint)(count - written), out uint n, IntPtr.Zero))
                        throw new IOException($"WriteFile failed: Win32 {Marshal.GetLastWin32Error()}");
                    if (n == 0) throw new IOException("WriteFile wrote 0 bytes");
                    written += (int)n;
                }
            }
        }

        public void DiscardInBuffer() { lock (_gate) { if (!_closed) PurgeComm(_handle, PURGE_RXCLEAR); } }
        public void DiscardOutBuffer() { lock (_gate) { if (!_closed) PurgeComm(_handle, PURGE_TXCLEAR); } }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                if (_handle != INVALID_HANDLE)
                {
                    try { CloseHandle(_handle); } catch { }
                    _handle = INVALID_HANDLE;
                }
            }
        }

        public void Dispose() => Close();

        // ── P/Invoke ──────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct DCB
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public sbyte XonChar;
            public sbyte XoffChar;
            public sbyte ErrorChar;
            public sbyte EofChar;
            public sbyte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMSTAT
        {
            public uint Flags;
            public uint cbInQue;
            public uint cbOutQue;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint access, uint share,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetCommState(IntPtr handle, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommState(IntPtr handle, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommTimeouts(IntPtr handle, ref COMMTIMEOUTS timeouts);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ClearCommError(IntPtr handle, out uint errors, out COMSTAT stat);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PurgeComm(IntPtr handle, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint toWrite, out uint written, IntPtr overlapped);
    }
}
