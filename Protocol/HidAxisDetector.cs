using System;
using System.Collections.Generic;
using System.Threading;
using HidSharp;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Watches every axis of the given controllers and reports the one that has
    /// moved furthest, as a fraction of its logical range — "move the pedal you
    /// want" axis picking. One read thread per device until disposed.
    /// </summary>
    internal sealed class HidAxisDetector : IDisposable
    {
        private const int ReadTimeoutMs = 250;

        private sealed class Track
        {
            public HidAxisDevice Device = null!;
            public HidAxisInfo Axis;
            public bool Seen;
            public int Min, Max;
        }

        private readonly object _lock = new object();
        private readonly List<Track> _tracks = new List<Track>();
        private readonly List<HidStream> _streams = new List<HidStream>();
        private volatile bool _stop;

        public HidAxisDetector(IReadOnlyList<HidAxisDevice> devices)
        {
            foreach (var d in devices)
            {
                var device = d;
                new Thread(() => Watch(device)) { Name = "AZOM-HidAxisDetect", IsBackground = true }.Start();
            }
        }

        /// <summary>The axis that moved furthest, if it moved at least <paramref name="minFraction"/> of its range.</summary>
        public bool TryGetBest(double minFraction, out HidAxisDevice? device, out HidAxisInfo axis)
        {
            device = null;
            axis = default;
            double best = minFraction;
            lock (_lock)
            {
                foreach (var t in _tracks)
                {
                    if (!t.Seen) continue;
                    double span = (double)t.Axis.LogicalMax - t.Axis.LogicalMin;
                    if (span <= 0) continue;
                    double moved = (t.Max - t.Min) / span;
                    if (moved < best) continue;
                    best = moved;
                    device = t.Device;
                    axis = t.Axis;
                }
            }
            return device != null;
        }

        private void Watch(HidAxisDevice target)
        {
            HidStream? stream = null;
            try
            {
                HidDevice? dev = null;
                foreach (var d in DeviceList.Local.GetHidDevices(target.VendorId, target.ProductId))
                {
                    string p;
                    try { p = d.DevicePath ?? ""; } catch { continue; }
                    if (string.Equals(p, target.Path, StringComparison.OrdinalIgnoreCase)) { dev = d; break; }
                }
                if (dev == null || !dev.TryOpen(out stream)) return;
                lock (_lock)
                {
                    if (_stop) return;
                    _streams.Add(stream);
                }

                using (var layout = HidInputLayout.Open(target.Path))
                {
                    var fields = new List<(HidValueField field, Track track)>();
                    foreach (var axis in target.Axes)
                        foreach (var v in layout.Values)
                            if (v.FullUsage == axis.Usage)
                            {
                                var track = new Track { Device = target, Axis = axis };
                                lock (_lock) _tracks.Add(track);
                                fields.Add((v, track));
                                break;
                            }
                    if (fields.Count == 0) return;

                    stream.ReadTimeout = ReadTimeoutMs;
                    var buffer = new byte[layout.InputReportLength];
                    while (!_stop)
                    {
                        int n;
                        try { n = stream.Read(buffer, 0, buffer.Length); }
                        catch (TimeoutException) { continue; }
                        if (n <= 0) continue;
                        if (n < buffer.Length) Array.Clear(buffer, n, buffer.Length - n);
                        foreach (var (field, track) in fields)
                        {
                            if (buffer[0] != field.ReportId || !layout.TryGetValue(field, buffer, out int value)) continue;
                            lock (_lock)
                            {
                                if (!track.Seen) { track.Min = track.Max = value; track.Seen = true; }
                                else if (value < track.Min) track.Min = value;
                                else if (value > track.Max) track.Max = value;
                            }
                        }
                    }
                }
            }
            catch
            {
                // A device that can't be read just doesn't take part.
            }
            finally
            {
                try { stream?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            List<HidStream> streams;
            lock (_lock)
            {
                _stop = true;
                streams = new List<HidStream>(_streams);
                _streams.Clear();
            }
            foreach (var s in streams)
                try { s.Dispose(); } catch { }
        }
    }
}
