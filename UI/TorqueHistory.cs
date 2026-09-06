using System;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Thread-safe rolling history of live wheelbase torque in Nm, sampled by a
    /// plugin-lifetime background timer — the same model as
    /// <see cref="TemperatureHistory"/>, and for the same reason: the settings
    /// panel must not do per-sample work on the WPF dispatcher.
    ///
    /// Stores the UNSIGNED magnitude (see <see cref="MozaData.LiveTorqueNm"/>);
    /// direction is discarded before it gets here. Unlike the temperature ring
    /// there is no 0-means-absent convention — 0 Nm is the most common real
    /// reading — so a disconnected sample is recorded as 0 with no separate
    /// validity flag, which is honest for torque: no base, no torque.
    /// </summary>
    public sealed class TorqueHistory
    {
        private readonly object _gate = new object();
        private readonly int _capacity;
        private readonly double[] _nm;
        private int _writeIndex;
        private int _count;
        private double _peakNm;

        public TorqueHistory(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _nm = new double[_capacity];
        }

        public int Capacity => _capacity;

        /// <summary>Session peak in Nm; 0 until the first non-zero sample.</summary>
        public double PeakNm { get { lock (_gate) return _peakNm; } }

        public void Record(double nm)
        {
            if (double.IsNaN(nm) || double.IsInfinity(nm)) return;
            lock (_gate)
            {
                _nm[_writeIndex] = nm;
                _writeIndex = (_writeIndex + 1) % _capacity;
                if (_count < _capacity) _count++;
                if (nm > _peakNm) _peakNm = nm;
            }
        }

        /// <summary>
        /// Chronologically-ordered snapshot, always <see cref="Capacity"/> long so
        /// the graph renders full width from the first frame (unfilled slots read
        /// 0). Safe from any thread; the caller owns the returned array.
        /// </summary>
        public double[] Take()
        {
            lock (_gate)
            {
                var outArr = new double[_capacity];
                int start = (_writeIndex - _count + _capacity) % _capacity;
                // Unfilled slots stay 0 and sit at the LEFT (oldest) end, so the
                // trace grows rightward into a full-width axis.
                int offset = _capacity - _count;
                for (int i = 0; i < _count; i++)
                    outArr[offset + i] = _nm[(start + i) % _capacity];
                return outArr;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                Array.Clear(_nm, 0, _nm.Length);
                _writeIndex = 0;
                _count = 0;
                _peakNm = 0;
            }
        }
    }
}
