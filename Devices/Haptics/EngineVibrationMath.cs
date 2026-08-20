using System;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// Shared math for the "engine vibration" effect across the three FFB
    /// hardware types that each render it over a different wire protocol —
    /// <see cref="BaseLfeEffectWorker"/> (wheelbase LFE engine/ABS/gearshift
    /// streams), <see cref="MBoosterEffectWorker"/> (mBooster vibration
    /// motor), and <see cref="Ab9EngineVibrationWorker"/> (AB9 shifter). The
    /// wire encoding stays per-device (frame shapes, param tables, and
    /// sub-streams are hardware-specific and not interchangeable), but the
    /// carrier-phase oscillator and the RPM-to-redline scaling underneath it
    /// are the same math each worker used to re-derive independently.
    /// </summary>
    internal static class EngineVibrationMath
    {
        /// <summary>Redline fallback when the game doesn't report MaxRpm — the
        /// convention <see cref="Ab9EngineVibrationWorker"/> and
        /// <see cref="MBoosterEffectWorker"/>'s Engine effect both use.</summary>
        public const double DefaultRedlineRpm = 8000.0;

        /// <summary>
        /// RPM as a fraction of redline, clamped to at most 1 so an over-rev
        /// can't exceed the redline pitch/period. <paramref name="maxRpm"/>
        /// below 100 (the game not reporting it) falls back to
        /// <paramref name="defaultRedlineRpm"/>. Assumes
        /// <paramref name="rpm"/> is non-negative (both callers already gate
        /// on rpm > 0 before reaching here).
        /// </summary>
        public static double RedlineFraction(double rpm, double maxRpm, double defaultRedlineRpm = DefaultRedlineRpm)
        {
            double redline = maxRpm > 100.0 ? maxRpm : defaultRedlineRpm;
            double fraction = rpm / redline;
            return fraction > 1.0 ? 1.0 : fraction;
        }

        /// <summary>
        /// Advance a phase accumulator by one tick at the given carrier
        /// frequency, wrapped to [0, 2π) for numerical stability over a long
        /// running session — the oscillator underneath every sine-based
        /// vibration waveform in this app.
        /// </summary>
        public static double AdvancePhase(double phase, double freqHz, double dtSec)
        {
            double p = phase + 2.0 * Math.PI * freqHz * dtSec;
            if (p >= 2.0 * Math.PI)
                p -= 2.0 * Math.PI * Math.Floor(p / (2.0 * Math.PI));
            return p;
        }
    }
}
