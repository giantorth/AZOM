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
    /// point moving the chosen way, lasts at least the minimum length even if the
    /// clutch passes straight through, ends once the clutch leaves (after the
    /// minimum) or at the maximum length, and re-arms only after the clutch
    /// leaves the zone. The direction is the side the clutch entered from, which
    /// a jittery foot can't flip the way a frame-to-frame delta can.
    /// </summary>
    internal struct MBoosterBitePointTrigger
    {
        /// <summary>Extra width (0..1) on the way out, so a pedal resting on an edge doesn't chatter.</summary>
        public const double EdgeHysteresis = 0.01;

        private bool _inZone;
        private bool _pulsing;
        // No pulse until the clutch leaves the zone: the last one hit its maximum
        // inside it, or this entry was the wrong direction.
        private bool _spent;
        // Side the clutch was last seen outside the zone: +1 above, -1 below, 0 unknown.
        private int _outsideSide;
        private long _startTicks;

        /// <param name="pedalPos01">Clutch position, 0..1.</param>
        /// <param name="bitePoint01">Bite point, 0..1.</param>
        /// <param name="width01">Zone half-width, 0..1.</param>
        /// <param name="minSec">Shortest pulse.</param>
        /// <param name="maxSec">Longest pulse; raised to <paramref name="minSec"/> if below it.</param>
        /// <param name="direction">Which entries fire; an entry from an unknown side fires only on Both.</param>
        /// <param name="nowTicks"><see cref="Stopwatch.GetTimestamp"/>.</param>
        /// <returns>True while a pulse plays.</returns>
        public bool Update(double pedalPos01, double bitePoint01, double width01, double minSec, double maxSec,
            MBoosterBitePointDirection direction, long nowTicks)
        {
            double halfWidth = _inZone ? width01 + EdgeHysteresis : width01;
            _inZone = Math.Abs(pedalPos01 - bitePoint01) <= halfWidth;
            if (!_inZone)
            {
                _spent = false;
                _outsideSide = pedalPos01 > bitePoint01 ? 1 : -1;
            }

            if (!_pulsing && _inZone && !_spent)
            {
                bool fires = direction switch
                {
                    MBoosterBitePointDirection.Press => _outsideSide < 0,
                    MBoosterBitePointDirection.Release => _outsideSide > 0,
                    _ => true,
                };
                if (fires)
                {
                    _pulsing = true;
                    _startTicks = nowTicks;
                }
                else
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
                else if (!_inZone && elapsed >= minSec)
                {
                    _pulsing = false;
                }
            }
            return _pulsing;
        }

        public void Reset()
        {
            _inZone = false;
            _pulsing = false;
            _spent = false;
            _outsideSide = 0;
        }
    }
}
