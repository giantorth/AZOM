using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Per-effect waveform synthesis for the Moza mBooster vibration motor.
    /// All four functions return an amplitude in [0, 1] that the caller scales
    /// to u16 via <see cref="Protocol.MozaMBoosterProtocol.EncodeAmp"/>.
    ///
    /// The motor itself is "dumb" — it plays the instantaneous amplitude we
    /// send. These functions are the host-side waveform generators called
    /// once per motor tick (~50 Hz).
    ///
    /// Reference: <c>docs/MozamBooster — Protocol Note.md</c> § 4
    /// "Waveform synthesizers". Formulas are reproduced verbatim from the
    /// protocol note's dirt-client reference implementation; do not modify
    /// without verifying against the protocol document.
    /// </summary>
    public static class MBoosterEffectSynthesizer
    {
        /// <summary>
        /// ABS — oscillating pulse, 80–100 % of intensity.
        /// <c>wave = 0.9 + 0.1 * sin(phase); amp = wave * intensity</c>.
        /// </summary>
        public static double SynthesizeAbs(double intensity, double phase)
        {
            double wave = 0.9 + 0.1 * Math.Sin(phase);
            return wave * intensity;
        }

        /// <summary>
        /// LOCKUP — linear ramp up over 0.5 s, then hold.
        /// <c>ramp = clamp(elapsed / 0.5, 0, 1); amp = ramp * intensity</c>.
        /// </summary>
        public static double SynthesizeLockup(double intensity, double elapsedSec)
        {
            double ramp = elapsedSec / 0.5;
            if (ramp < 0) ramp = 0;
            if (ramp > 1) ramp = 1;
            return ramp * intensity;
        }

        /// <summary>
        /// THRESHOLD — burst → sustain → off envelope, repeating every 200 ms (5 Hz).
        /// 20 ms burst at full + 120 ms sustain at 80 % + 60 ms gap.
        /// </summary>
        public static double SynthesizeThreshold(double intensity, double elapsedSec)
        {
            double cyclePos = elapsedSec - Math.Floor(elapsedSec / 0.2) * 0.2;
            if (cyclePos < 0.02) return intensity;            // 20 ms burst at full
            if (cyclePos < 0.14) return intensity * 0.8;      // 120 ms sustain at 80 %
            return 0;                                          // 60 ms gap
        }

        /// <summary>
        /// ENGINE — sine wave at the supplied frequency, biased into [0, 1].
        /// <c>wave = 0.5 + 0.5 * sin(phase); amp = wave * intensity</c>.
        /// </summary>
        public static double SynthesizeEngine(double intensity, double phase)
        {
            double wave = 0.5 + 0.5 * Math.Sin(phase);
            return wave * intensity;
        }
    }
}
