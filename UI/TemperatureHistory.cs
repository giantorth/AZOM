using System;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Thread-safe rolling history of wheelbase temperatures (MCU / MOSFET /
    /// Motor), sampled by a plugin-lifetime background timer so the Base-tab
    /// graph shows the full recent window the instant the settings panel opens —
    /// instead of starting empty and only filling while the panel stays open.
    ///
    /// Stores RAW sensor values (100×°C ints, as <see cref="MozaData"/> exposes
    /// them) plus a per-sample "base connected" flag. Unit conversion (°C/°F)
    /// happens at render time so a live °C↔°F toggle reflows the whole window,
    /// not just samples taken after the toggle. A disconnected sample is stored
    /// as 0/false so the window trails off to baseline while the base is gone.
    /// </summary>
    public sealed class TemperatureHistory
    {
        private readonly object _gate = new object();
        private readonly int _capacity;
        private readonly int[] _mcu;
        private readonly int[] _mosfet;
        private readonly int[] _motor;
        private readonly bool[] _connected;
        private int _writeIndex; // next slot to overwrite
        private int _count;      // valid samples, saturating at capacity

        // Session peaks (raw), -1 until the first live sample. Tracked here so
        // "max NN" in the legend reflects the whole plugin session regardless of
        // whether/when the settings panel was open.
        private int _mcuMaxRaw = -1;
        private int _mosfetMaxRaw = -1;
        private int _motorMaxRaw = -1;

        public TemperatureHistory(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _mcu = new int[_capacity];
            _mosfet = new int[_capacity];
            _motor = new int[_capacity];
            _connected = new bool[_capacity];
        }

        public int Capacity => _capacity;

        /// <summary>Append one sample. When <paramref name="connected"/> is false
        /// the raw values are ignored (stored as 0) so the window shows a gap.</summary>
        public void Record(int mcu, int mosfet, int motor, bool connected)
        {
            lock (_gate)
            {
                _mcu[_writeIndex]       = connected ? mcu : 0;
                _mosfet[_writeIndex]    = connected ? mosfet : 0;
                _motor[_writeIndex]     = connected ? motor : 0;
                _connected[_writeIndex] = connected;
                _writeIndex = (_writeIndex + 1) % _capacity;
                if (_count < _capacity) _count++;

                if (connected)
                {
                    if (mcu > _mcuMaxRaw) _mcuMaxRaw = mcu;
                    if (mosfet > _mosfetMaxRaw) _mosfetMaxRaw = mosfet;
                    if (motor > _motorMaxRaw) _motorMaxRaw = motor;
                }
            }
        }

        /// <summary>Chronologically-ordered snapshot of the current window plus
        /// the session peaks. Safe to call from any thread; returns fresh arrays
        /// the caller owns.</summary>
        public Snapshot Take()
        {
            lock (_gate)
            {
                var mcu = new int[_count];
                var mosfet = new int[_count];
                var motor = new int[_count];
                var connected = new bool[_count];
                int start = (_writeIndex - _count + _capacity) % _capacity;
                for (int i = 0; i < _count; i++)
                {
                    int idx = (start + i) % _capacity;
                    mcu[i]       = _mcu[idx];
                    mosfet[i]    = _mosfet[idx];
                    motor[i]     = _motor[idx];
                    connected[i] = _connected[idx];
                }
                return new Snapshot(mcu, mosfet, motor, connected,
                    _mcuMaxRaw, _mosfetMaxRaw, _motorMaxRaw);
            }
        }

        public sealed class Snapshot
        {
            public Snapshot(int[] mcu, int[] mosfet, int[] motor, bool[] connected,
                int mcuMaxRaw, int mosfetMaxRaw, int motorMaxRaw)
            {
                Mcu = mcu; Mosfet = mosfet; Motor = motor; Connected = connected;
                McuMaxRaw = mcuMaxRaw; MosfetMaxRaw = mosfetMaxRaw; MotorMaxRaw = motorMaxRaw;
            }

            public int[] Mcu { get; }
            public int[] Mosfet { get; }
            public int[] Motor { get; }
            public bool[] Connected { get; }
            public int McuMaxRaw { get; }
            public int MosfetMaxRaw { get; }
            public int MotorMaxRaw { get; }
        }
    }
}
