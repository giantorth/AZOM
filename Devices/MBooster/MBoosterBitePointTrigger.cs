using System;
using System.Diagnostics;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>Which way the clutch must be moving through the bite point to fire the pulse.</summary>
    public enum MBoosterBitePointDirection
    {
        Both,
        // Pushing the clutch in: entering the zone from below the bite point.
        Press,
        // Letting the clutch out: entering the zone from above the bite point.
        Release,
    }

    /// <summary>
    /// Bite Point trigger, shared by the plugin-tab worker and the ShakeIt effect.
    /// A pulse starts when the clutch enters the zone within Width of the bite
    /// point (kept between 5 % and the pressed-in level) moving the chosen way, lasts at least the minimum length even if the
    /// clutch passes straight through (or jumps across it between samples), ends
    /// once the clutch leaves (after the minimum) or at the maximum length, and
    /// re-arms only after the clutch leaves the zone. The direction is the side
    /// the clutch entered from, which a jittery foot can't flip the way a
    /// frame-to-frame delta can.
    ///
    /// Prediction: touching the clutch (leaving either end of travel) arms it for
    /// the 95th-percentile time this clutch takes from that end to the zone,
    /// learned per direction and saved (MBoosterBitePointTravel). While armed the clutch is judged where it will be
    /// one source update ahead at its current speed, so a fast pedal fires on
    /// time. The projection only runs toward the bite point and never passes it,
    /// so it brings the entry forward; leaving the zone follows the real pedal.
    /// </summary>
    internal struct MBoosterBitePointTrigger
    {
        /// <summary>Extra width (0..1) on the way out, so a pedal resting on an edge doesn't chatter.</summary>
        public const double EdgeHysteresis = 0.01;

        // The clutch rests within this much of either end; leaving it is a touch.
        private const double RestBand = 0.05;
        // Speed is measured across at least this span, so jitter and repeated
        // samples average out.
        private const double SpeedWindowSec = 0.04;
        // Samples older than this give no speed (the source stalled).
        private const double SpeedMaxAgeSec = 0.2;
        private const int HistorySize = 8;
        // Lead is the source's update interval: how late a sample can see the entry.
        private const double MinLeadSec = 0.005;
        private const double MaxLeadSec = 0.1;

        private bool _inZone;
        private bool _pulsing;
        // No pulse until the clutch leaves the zone: the last one hit its maximum
        // inside it, or this entry was the wrong direction.
        private bool _spent;
        // Side the clutch was last seen outside the zone: +1 above, -1 below, 0 unknown.
        private int _outsideSide;
        private long _startTicks;

        private long[]? _histTicks;
        private double[]? _histPos;
        private int _histHead;
        private int _histCount;

        // End of travel the clutch rests at (+1 pressed, -1 released, 0 neither),
        // and the touch that left it.
        private int _restSide;
        private int _touchSide;
        private long _touchTicks;
        private bool _timingTravel;
        private double _lastPos;
        private long _lastChangeTicks;
        private double _updateIntervalSec;

        /// <param name="pedalPos01">Clutch position, 0..1.</param>
        /// <param name="bitePoint01">Bite point, 0..1.</param>
        /// <param name="width01">Zone half-width, 0..1.</param>
        /// <param name="minSec">Shortest pulse.</param>
        /// <param name="maxSec">Longest pulse; raised to <paramref name="minSec"/> if below it.</param>
        /// <param name="direction">Which entries fire; an entry from an unknown side fires only on Both.</param>
        /// <param name="predict">Fire ahead of a fast clutch after a touch.</param>
        /// <param name="nowTicks"><see cref="Stopwatch.GetTimestamp"/>.</param>
        /// <returns>True while a pulse plays.</returns>
        public bool Update(double pedalPos01, double bitePoint01, double width01, double minSec, double maxSec,
            MBoosterBitePointDirection direction, bool predict, long nowTicks)
        {
            double speed = Speed(pedalPos01, nowTicks);
            Learn(pedalPos01, bitePoint01, width01, nowTicks);
            double pos = pedalPos01;
            if (predict && Armed(nowTicks) && (bitePoint01 - pedalPos01) * speed > 0)
            {
                pos = pedalPos01 + speed * Math.Max(MinLeadSec, Math.Min(MaxLeadSec, _updateIntervalSec));
                if (Math.Sign(pos - bitePoint01) != Math.Sign(pedalPos01 - bitePoint01)) pos = bitePoint01;
            }

            bool wasInZone = _inZone;
            _inZone = InZone(pos, bitePoint01, width01, _inZone ? EdgeHysteresis : 0);
            int prevSide = _outsideSide;
            if (!_inZone)
            {
                _spent = false;
                _outsideSide = pos > ZoneTop(bitePoint01, width01) ? 1 : -1;
            }

            // Jumped across the whole zone between two samples.
            bool crossed = !_inZone && !wasInZone && prevSide != 0 && prevSide != _outsideSide;
            if (!_pulsing && ((_inZone && !_spent) || crossed))
            {
                int fromSide = crossed ? prevSide : _outsideSide;
                bool fires = direction switch
                {
                    MBoosterBitePointDirection.Press => fromSide < 0,
                    MBoosterBitePointDirection.Release => fromSide > 0,
                    _ => true,
                };
                if (fires)
                {
                    _pulsing = true;
                    _startTicks = nowTicks;
                }
                else if (_inZone)
                {
                    _spent = true;
                }
            }
            if (_pulsing)
            {
                double elapsed = (nowTicks - _startTicks) / (double)Stopwatch.Frequency;
                if (elapsed >= Math.Max(minSec, maxSec))
                {
                    _pulsing = false;
                    _spent = _inZone;
                }
                else if (!_inZone && elapsed >= minSec && !crossed)
                {
                    _pulsing = false;
                }
            }
            return _pulsing;
        }

        /// <summary>Top of the bite zone: Width above the bite point, capped at
        /// <see cref="MBoosterBitePointDetector.ClutchInLevel"/>.</summary>
        public static double ZoneTop(double bitePoint01, double width01) =>
            Math.Min(bitePoint01 + width01, MBoosterBitePointDetector.ClutchInLevel);

        /// <summary>Bottom of the bite zone: Width below the bite point, floored at
        /// <see cref="ZoneFloor"/>.</summary>
        public static double ZoneBottom(double bitePoint01, double width01) =>
            Math.Max(bitePoint01 - width01, ZoneFloor);

        /// <summary>Lowest travel (0..1) the bite zone reaches.</summary>
        public const double ZoneFloor = 0.05;

        // In the zone, widened by margin at both edges.
        private static bool InZone(double pos, double bite, double width, double margin) =>
            pos >= ZoneBottom(bite, width) - margin && pos <= ZoneTop(bite, width) + margin;

        // Within the arm window of the last touch.
        private bool Armed(long now)
        {
            if (_touchSide == 0) return false;
            return (now - _touchTicks) / (double)Stopwatch.Frequency <= MBoosterBitePointTravel.ArmSec(_touchSide > 0);
        }

        // Touches, travel times from the touch to the real zone entry
        // (MBoosterBitePointTravel), and the source's update interval. Uses the
        // real position, not the projection.
        private void Learn(double pos, double bite, double width, long now)
        {
            if (pos != _lastPos)
            {
                double dt = (now - _lastChangeTicks) / (double)Stopwatch.Frequency;
                if (_lastChangeTicks != 0 && dt > 0 && dt <= SpeedMaxAgeSec)
                    _updateIntervalSec = _updateIntervalSec <= 0 ? dt : _updateIntervalSec * 0.9 + dt * 0.1;
                _lastPos = pos;
                _lastChangeTicks = now;
            }

            int side = pos >= 1 - RestBand ? 1 : pos <= RestBand ? -1 : 0;
            if (side != 0)
            {
                _restSide = side;
                _touchSide = 0;
                _timingTravel = false;
                return;
            }
            if (_restSide != 0)
            {
                _touchSide = _restSide;
                _touchTicks = now;
                _timingTravel = true;
                _restSide = 0;
            }
            if (_timingTravel && InZone(pos, bite, width, 0))
            {
                _timingTravel = false;
                MBoosterBitePointTravel.Add(_touchSide > 0, (now - _touchTicks) / (double)Stopwatch.Frequency);
            }
        }

        // Clutch speed (travel/s) over the newest sample at least SpeedWindowSec
        // old, then records this sample.
        private double Speed(double pos, long now)
        {
            _histTicks ??= new long[HistorySize];
            _histPos ??= new double[HistorySize];
            long window = (long)(SpeedWindowSec * Stopwatch.Frequency);
            long maxAge = (long)(SpeedMaxAgeSec * Stopwatch.Frequency);
            double speed = 0;
            for (int i = 1; i <= _histCount; i++)
            {
                int idx = (_histHead - i + HistorySize) % HistorySize;
                long dt = now - _histTicks[idx];
                if (dt > maxAge) break;
                if (dt <= 0) continue;
                speed = (pos - _histPos[idx]) * Stopwatch.Frequency / dt;
                if (dt >= window) break;
            }
            _histTicks[_histHead] = now;
            _histPos[_histHead] = pos;
            _histHead = (_histHead + 1) % HistorySize;
            if (_histCount < HistorySize) _histCount++;
            return speed;
        }

        /// <summary>Clears the pulse and motion state.</summary>
        public void Reset()
        {
            _inZone = false;
            _pulsing = false;
            _spent = false;
            _outsideSide = 0;
            _histCount = 0;
            _restSide = 0;
            _touchSide = 0;
            _timingTravel = false;
            _lastChangeTicks = 0;
        }
    }
}
