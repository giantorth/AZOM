using System;
using System.Diagnostics;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// Wheel lockup / wheelspin heuristics shared by the mBooster effects and the
    /// exported <c>AZOM.WheelLockup</c> / <c>AZOM.WheelSlip</c> properties. Both need
    /// per-wheel speeds; without them neither fires — guessing from pedal position
    /// alone would fire on every hard stop or launch.
    /// </summary>
    internal static class WheelSlipDetector
    {
        /// <summary>Minimum absolute slip (m/s) before wheelspin counts — keeps normal launch slip out.</summary>
        public const double WheelSpinMinSlipMs = 2.0;

        /// <summary>Hard braking with any one wheel well below ground speed.</summary>
        public static bool IsLocking(bool hasWheelSpeeds, double brake01, double groundSpeedMs, double minWheelSpeedMs)
        {
            double ground = Math.Abs(groundSpeedMs);
            return hasWheelSpeeds
                && brake01 > 0.8
                && ground > 5
                && minWheelSpeedMs < ground * 0.3;
        }

        /// <summary>Near-full throttle at low speed with the fastest (driven) wheel well above ground speed.</summary>
        public static bool IsSpinning(bool hasWheelSpeeds, double throttle01, double groundSpeedMs, double maxWheelSpeedMs)
        {
            double ground = Math.Abs(groundSpeedMs);
            return hasWheelSpeeds
                && throttle01 > 0.8
                && ground < 40
                && maxWheelSpeedMs > ground * 1.3
                && maxWheelSpeedMs - ground > WheelSpinMinSlipMs;
        }
    }

    /// <summary>
    /// S12 Road Texture wants a changing suspension position (0..100). SimHub has
    /// no generic suspension-travel channel, so vertical acceleration stands in:
    /// high-passed (drops gravity and any steady offset, whatever the game's unit)
    /// and centred on 50. Data thread only.
    /// </summary>
    internal sealed class RoadPositionSignal
    {
        private const double LowPassTauSec = 0.3;
        // Position units per unit of high-passed heave; ±5 m/s² spans the full range.
        private const double Scale = 10.0;

        private double _baseline;
        private bool _warm;
        private long _lastTicks;

        /// <summary>0..100, 50 at rest.</summary>
        public double Position { get; private set; } = 50;

        public void Update(bool active, double heave)
        {
            long now = Stopwatch.GetTimestamp();
            if (!active || double.IsNaN(heave))
            {
                _warm = false;
                Position = 50;
                return;
            }
            if (!_warm)
            {
                _baseline = heave;
                _lastTicks = now;
                _warm = true;
            }
            double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;
            if (dt < 0 || dt > 1) dt = 0;
            _baseline += (heave - _baseline) * (dt / (LowPassTauSec + dt));
            double pos = 50 + (heave - _baseline) * Scale;
            Position = pos < 0 ? 0 : (pos > 100 ? 100 : pos);
        }
    }
}
