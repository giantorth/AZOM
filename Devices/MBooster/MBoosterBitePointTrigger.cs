using System;
using System.Diagnostics;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>
    /// Bite Point trigger, shared by the plugin-tab worker and the ShakeIt effect.
    /// A pulse starts when the clutch enters the zone within Width of the bite
    /// point, lasts at least the minimum length even if the clutch passes
    /// straight through, ends once the clutch leaves (after the minimum) or at
    /// the maximum length, and re-arms only after the clutch leaves the zone.
    /// </summary>
    internal struct MBoosterBitePointTrigger
    {
        /// <summary>Extra width (0..1) on the way out, so a pedal resting on an edge doesn't chatter.</summary>
        public const double EdgeHysteresis = 0.01;

        private bool _inZone;
        private bool _pulsing;
        // The last pulse hit its maximum inside the zone; wait for the clutch to leave.
        private bool _spent;
        private long _startTicks;

        /// <param name="pedalPos01">Clutch position, 0..1.</param>
        /// <param name="bitePoint01">Bite point, 0..1.</param>
        /// <param name="width01">Zone half-width, 0..1.</param>
        /// <param name="minSec">Shortest pulse.</param>
        /// <param name="maxSec">Longest pulse; raised to <paramref name="minSec"/> if below it.</param>
        /// <param name="nowTicks"><see cref="Stopwatch.GetTimestamp"/>.</param>
        /// <returns>True while a pulse plays.</returns>
        public bool Update(double pedalPos01, double bitePoint01, double width01, double minSec, double maxSec, long nowTicks)
        {
            double halfWidth = _inZone ? width01 + EdgeHysteresis : width01;
            _inZone = Math.Abs(pedalPos01 - bitePoint01) <= halfWidth;
            if (!_inZone) _spent = false;

            if (!_pulsing && _inZone && !_spent)
            {
                _pulsing = true;
                _startTicks = nowTicks;
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
        }
    }
}
