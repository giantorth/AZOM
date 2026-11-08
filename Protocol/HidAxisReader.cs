using System;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Reads one axis of any HID device on its own thread, straight off the
    /// input reports (no SimHub frame in between). Reopens the device when it
    /// disappears or the target changes. Optionally records the axis' raw
    /// min/max for a travel capture.
    /// </summary>
    internal sealed class HidAxisReader : IDisposable
    {
        private const int ReadTimeoutMs = 250;
        private const int ReopenDelayMs = 1000;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ_WRITE = 3;
        private const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa,
                                                        uint disposition, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int length);

        private readonly object _lock = new object();
        private HidAxisSelection? _target;
        private Thread? _thread;
        private volatile bool _stop;
        private HidStream? _stream;

        private int _raw;
        private volatile bool _hasValue;
        private bool _capturing;
        private int _captureMin, _captureMax;
        private bool _captureSeen;

        /// <summary>Read <paramref name="target"/>'s axis, or stop reading when null.</summary>
        public void SetTarget(HidAxisSelection? target)
        {
            lock (_lock)
            {
                if (target != null && _target != null && target.SameAxis(_target))
                {
                    _target = target; // same axis, new travel: keep the open stream
                    return;
                }
                _target = target;
                _hasValue = false;
                CloseStream();
                if (target != null && _thread == null)
                {
                    _stop = false;
                    _thread = new Thread(Run) { Name = "AZOM-HidAxis", IsBackground = true };
                    _thread.Start();
                }
            }
        }

        /// <summary>The latest raw value; false until the device has reported one.</summary>
        public bool TryGetRaw(out int raw)
        {
            raw = Volatile.Read(ref _raw);
            return _hasValue;
        }

        public void BeginRangeCapture()
        {
            lock (_lock)
            {
                _capturing = true;
                _captureSeen = false;
                if (_hasValue)
                {
                    _captureMin = _captureMax = _raw;
                    _captureSeen = true;
                }
            }
        }

        /// <summary>
        /// End a capture. The pedal is taken to be at rest now, so the extreme
        /// nearest the current value is "released".
        /// </summary>
        /// <returns>False when the axis moved too little to tell.</returns>
        public bool EndRangeCapture(out int released, out int pressed)
        {
            lock (_lock)
            {
                _capturing = false;
                released = pressed = 0;
                if (!_captureSeen || !_hasValue || _captureMax == _captureMin) return false;
                int now = _raw;
                bool restAtMin = Math.Abs(now - _captureMin) <= Math.Abs(now - _captureMax);
                released = restAtMin ? _captureMin : _captureMax;
                pressed = restAtMin ? _captureMax : _captureMin;
                return true;
            }
        }

        public bool IsCapturing
        {
            get { lock (_lock) return _capturing; }
        }

        private void Store(int value)
        {
            lock (_lock)
            {
                _raw = value;
                _hasValue = true;
                if (!_capturing) return;
                if (!_captureSeen)
                {
                    _captureMin = _captureMax = value;
                    _captureSeen = true;
                }
                else if (value < _captureMin) _captureMin = value;
                else if (value > _captureMax) _captureMax = value;
            }
        }

        private void Run()
        {
            while (!_stop)
            {
                HidAxisSelection? target;
                lock (_lock) target = _target;
                if (target == null)
                {
                    Thread.Sleep(ReadTimeoutMs);
                    continue;
                }
                try
                {
                    ReadUntilChanged(target);
                }
                catch (Exception ex)
                {
                    if (!_stop) MozaLog.Debug($"[AZOM] HID axis read ({target.DeviceName}): {ex.Message}");
                }
                _hasValue = false;
                CloseStream();
                if (!_stop && ReferenceEquals(Volatile.Read(ref _target), target)) Thread.Sleep(ReopenDelayMs);
            }
        }

        // Reads until the target changes, the device goes away, or Dispose.
        private void ReadUntilChanged(HidAxisSelection target)
        {
            string? path = HidAxisCatalog.Resolve(target);
            if (path == null) return;
            HidDevice? device = null;
            foreach (var d in DeviceList.Local.GetHidDevices(target.VendorId, target.ProductId))
            {
                string p;
                try { p = d.DevicePath ?? ""; } catch { continue; }
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) { device = d; break; }
            }
            if (device == null || !device.TryOpen(out HidStream stream)) return;

            using (var layout = HidInputLayout.Open(path))
            {
                HidValueField? field = null;
                foreach (var v in layout.Values)
                    if (v.FullUsage == target.Usage) { field = v; break; }
                if (field == null) { stream.Dispose(); return; }
                var f = field.Value;

                lock (_lock)
                {
                    if (!ReferenceEquals(_target, target) && !(_target?.SameAxis(target) ?? false))
                    {
                        stream.Dispose();
                        return;
                    }
                    _stream = stream;
                }
                stream.ReadTimeout = ReadTimeoutMs;
                var buffer = new byte[layout.InputReportLength];
                SeedCurrentValue(path, layout, f, buffer);

                while (!_stop)
                {
                    lock (_lock)
                        if (_target == null || !_target.SameAxis(target)) return;
                    int n;
                    try { n = stream.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    if (n <= 0) continue;
                    if (n < buffer.Length) Array.Clear(buffer, n, buffer.Length - n);
                    if (buffer[0] != f.ReportId) continue;
                    if (layout.TryGetValue(f, buffer, out int value)) Store(value);
                }
            }
        }

        // Devices that only report on change would read nothing until the pedal
        // moves; ask for the current report once. Best effort.
        private void SeedCurrentValue(string path, HidInputLayout layout, in HidValueField field, byte[] buffer)
        {
            try
            {
                using (var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE,
                           IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) return;
                    Array.Clear(buffer, 0, buffer.Length);
                    buffer[0] = field.ReportId;
                    if (HidD_GetInputReport(handle, buffer, buffer.Length)
                        && layout.TryGetValue(field, buffer, out int value))
                        Store(value);
                }
            }
            catch { }
        }

        private void CloseStream()
        {
            HidStream? s;
            lock (_lock)
            {
                s = _stream;
                _stream = null;
            }
            try { s?.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _stop = true;
            CloseStream();
            Thread? t;
            lock (_lock)
            {
                t = _thread;
                _thread = null;
                _target = null;
            }
            try { t?.Join(1000); } catch { }
        }
    }
}
