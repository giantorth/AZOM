using System;
using System.Diagnostics;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>
    /// Latest ShakeIt output per pedal role (0 throttle, 1 brake, 2 clutch),
    /// posted from SimHub's data thread and read by every effect worker on its
    /// own 50 Hz tick. Latest-state, no queue: a worker plays whatever its role
    /// last received, and a sample older than <see cref="StaleMs"/> reads as
    /// silent so a source that stops posting goes quiet on its own.
    /// </summary>
    public sealed class MBoosterShakeItSink
    {
        public const int RoleCount = 3;
        private const int StaleMs = 500;

        private readonly Func<bool> _routed;
        private readonly object _lock = new object();
        private readonly double[] _gain = new double[RoleCount];
        private readonly double[] _freq = new double[RoleCount];
        private readonly long[] _stamp = new long[RoleCount];

        /// <param name="routed">True while ShakeIt, not the plugin tab, drives the vibrations.</param>
        public MBoosterShakeItSink(Func<bool> routed)
        {
            _routed = routed ?? throw new ArgumentNullException(nameof(routed));
        }

        public bool Routed => _routed();

        public void Post(int role, double gain01, double freqHz)
        {
            if (role < 0 || role >= RoleCount) return;
            long now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                _gain[role] = gain01;
                _freq[role] = freqHz;
                _stamp[role] = now;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                for (int r = 0; r < RoleCount; r++)
                {
                    _gain[r] = 0;
                    _freq[r] = 0;
                    _stamp[r] = 0;
                }
            }
        }

        /// <returns>False when the role has no fresh sample.</returns>
        public bool TryGet(int role, out double gain01, out double freqHz)
        {
            gain01 = 0;
            freqHz = 0;
            if (role < 0 || role >= RoleCount) return false;
            long now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                long stamp = _stamp[role];
                if (stamp == 0 || (now - stamp) * 1000 / Stopwatch.Frequency > StaleMs) return false;
                gain01 = _gain[role];
                freqHz = _freq[role];
                return true;
            }
        }
    }
}
