using System;

namespace MozaPlugin.Devices.MBooster
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
    /// without verifying against the protocol document. Exceptions:
    /// <see cref="SynthesizeAbs"/> and <see cref="SynthesizeThreshold"/> now
    /// take a host-side "smoothness"/"decay" parameter (not from the
    /// protocol note) that generalizes the verified formula — both reduce
    /// to the exact reference at their default value, see each method's doc
    /// comment.
    /// </summary>
    public static class MBoosterEffectSynthesizer
    {
        /// <summary>
        /// ABS — oscillating pulse. <paramref name="smoothness01"/> (0..1,
        /// from <c>MBoosterEffectSettings.SmoothnessPct / 100</c>) is a
        /// host-side extension controlling the ripple depth, NOT part of the
        /// protocol note: at <c>smoothness01 = 1</c> (the default, matching
        /// pre-existing profiles) this reduces to the exact verified
        /// reference formula <c>wave = 0.9 + 0.1 * sin(phase)</c>; at
        /// <c>smoothness01 = 0</c> it widens to <c>wave = 0.5 + 0.5 *
        /// sin(phase)</c> (the same full-swing shape as <see cref="SynthesizeEngine"/>)
        /// for a sharper, choppier pulse. <c>amp = wave * intensity</c>.
        /// </summary>
        public static double SynthesizeAbs(double intensity, double phase, double smoothness01)
        {
            if (double.IsNaN(smoothness01)) smoothness01 = 1.0;
            else if (smoothness01 < 0) smoothness01 = 0;
            else if (smoothness01 > 1) smoothness01 = 1;
            double depth = 0.5 - 0.4 * smoothness01;
            double wave = (1.0 - depth) + depth * Math.Sin(phase);
            return wave * intensity;
        }

        /// <summary>
        /// BITE POINT (Clutch-only) — same oscillating-pulse shape as
        /// <see cref="SynthesizeAbs"/> (identical formula, own function so
        /// each effect can be tuned/verified independently later, same
        /// rationale as <see cref="SynthesizeTractionControl"/>). Not a
        /// protocol-verified reference — a deliberate reuse of ABS's
        /// already-tuned "feel" for a tactile engagement cue, gated by
        /// pedal position rather than a wheel-slip/activation signal (see
        /// MBoosterEffectWorker.UpdateBitePointRequest).
        /// </summary>
        public static double SynthesizeBitePoint(double intensity, double phase, double smoothness01)
        {
            if (double.IsNaN(smoothness01)) smoothness01 = 1.0;
            else if (smoothness01 < 0) smoothness01 = 0;
            else if (smoothness01 > 1) smoothness01 = 1;
            double depth = 0.5 - 0.4 * smoothness01;
            double wave = (1.0 - depth) + depth * Math.Sin(phase);
            return wave * intensity;
        }

        /// <summary>
        /// TRACTION CONTROL — same oscillating-pulse shape as
        /// <see cref="SynthesizeAbs"/> (identical formula, own function so
        /// each effect can be tuned/verified independently later). Unlike
        /// ABS, this waveform is NOT reproduced from a protocol-note/capture
        /// reference — there is no verified Traction Control wire effect —
        /// it's a deliberate reuse of ABS's already-tuned "feel" for a
        /// structurally identical wheel-slip cue.
        /// </summary>
        public static double SynthesizeTractionControl(double intensity, double phase, double smoothness01)
        {
            if (double.IsNaN(smoothness01)) smoothness01 = 1.0;
            else if (smoothness01 < 0) smoothness01 = 0;
            else if (smoothness01 > 1) smoothness01 = 1;
            double depth = 0.5 - 0.4 * smoothness01;
            double wave = (1.0 - depth) + depth * Math.Sin(phase);
            return wave * intensity;
        }

        /// <summary>
        /// WHEEL SPIN — same oscillating-pulse shape as
        /// <see cref="SynthesizeAbs"/>/<see cref="SynthesizeTractionControl"/>
        /// (identical formula, own function per this file's one-function-
        /// per-effect convention). Like Traction Control, this waveform is
        /// NOT a protocol-verified reference — it's a deliberate reuse of
        /// ABS's already-tuned "feel" for another structurally identical
        /// wheel-slip cue, this time triggered by a raw wheel-speed
        /// heuristic (see MBoosterEffectWorker.UpdateWheelSpinRequest)
        /// rather than a game-provided activation flag.
        /// </summary>
        public static double SynthesizeWheelSpin(double intensity, double phase, double smoothness01)
        {
            if (double.IsNaN(smoothness01)) smoothness01 = 1.0;
            else if (smoothness01 < 0) smoothness01 = 0;
            else if (smoothness01 > 1) smoothness01 = 1;
            double depth = 0.5 - 0.4 * smoothness01;
            double wave = (1.0 - depth) + depth * Math.Sin(phase);
            return wave * intensity;
        }

        /// <summary>
        /// GEAR SHIFT — a short oscillating burst that linearly decays to
        /// silence over <paramref name="durationSec"/>, unlike every other
        /// effect in this file (all continuous/repeating while their gate
        /// stays true). Fixed ripple depth (no Smoothness slider, same as
        /// Traction Control/Wheel Spin) — <c>wave = 0.7 + 0.3*sin(phase)</c>,
        /// so the wave never crosses zero mid-burst. Not a protocol-verified
        /// reference; a deliberate, simple "click that fades fast" shape for
        /// a one-shot pulse. <c>amp = wave * envelope * intensity</c>, where
        /// <c>envelope</c> ramps linearly from 1 (burst start) to 0 (burst
        /// end). See MBoosterEffectWorker.UpdateGearShiftRequest/
        /// GearShiftPulseDurationSec for how the pulse itself is latched
        /// across ticks.
        /// </summary>
        public static double SynthesizeGearShift(double intensity, double phase, double elapsedSec, double durationSec)
        {
            if (durationSec <= 0) return 0;
            if (elapsedSec >= durationSec) return 0;
            double envelope = 1.0 - (elapsedSec / durationSec);
            double wave = 0.7 + 0.3 * Math.Sin(phase);
            return wave * envelope * intensity;
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
        /// THRESHOLD — burst → sustain → off envelope, repeating every 200 ms
        /// (5 Hz). 20 ms burst at full, then 120 ms sustain, then 60 ms gap.
        /// <paramref name="decay01"/> (0..1, from
        /// <c>MBoosterEffectSettings.DecayPct / 100</c>) is a host-side
        /// extension controlling the sustain level, NOT part of the protocol
        /// note: <c>sustain = intensity * (1 - decay01)</c>. At
        /// <c>decay01 = 0.2</c> (the default, matching pre-existing
        /// profiles) this reduces to the exact verified reference sustain
        /// (80 %); at <c>decay01 = 0</c> the sustain barely decays (stays at
        /// full strength for the whole 120 ms); at <c>decay01 = 1</c> it
        /// drops to silence immediately after the burst, for a short, sharp
        /// tick instead of a sustained buzz.
        /// </summary>
        public static double SynthesizeThreshold(double intensity, double elapsedSec, double decay01)
        {
            if (double.IsNaN(decay01)) decay01 = 0.2;
            else if (decay01 < 0) decay01 = 0;
            else if (decay01 > 1) decay01 = 1;
            double cyclePos = elapsedSec - Math.Floor(elapsedSec / 0.2) * 0.2;
            if (cyclePos < 0.02) return intensity;                       // 20 ms burst at full
            if (cyclePos < 0.14) return intensity * (1.0 - decay01);     // 120 ms sustain
            return 0;                                                    // 60 ms gap
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

        // ROAD TEXTURE noise octaves — grain riding on surface waviness.
        //
        // This used to be a single 0.35 s keyframe layer, picked to reproduce
        // the "~1.3-1.6 peaks/sec" rate the Pit House road-texture capture was
        // read as showing (docs/protocol/devices/mbooster.md "Road Texture").
        // It did reproduce it — and the result is a slow ram, not texture:
        // the emitted sample crossed zero only about every 1.4 s (bundle
        // NWS6EY7X, 13 crossings in 18.1 s), which the reporter felt as
        // "running over a speedbump every 2 seconds".
        //
        // That target rate is not trusted. Reading a frame-by-frame sample
        // sequence AS the waveform only holds if the signal is band-limited
        // well under the frame rate, which nothing established — and
        // near-full-range excursions at ~1.4/sec is the signature of
        // undersampled broadband noise, which is exactly what road chatter
        // would be. The capture's load-bearing finding is unaffected either
        // way: Intensity/Smoothness provably don't shape the noise across 4
        // settings each (a COMPARATIVE result, aliased or not), so the
        // firmware applies them and any reasonable road-like noise satisfies
        // the wire contract.
        //
        // So the fine layer is the primary one and the coarse layer is demoted
        // to a slow amplitude undulation — the physical shape of a road
        // surface, rather than a rate we're not confident we measured.
        private const double RoadTextureCoarseKeyframeSec = 0.35;
        // ~11 Hz grain. Kept at 2+ ticks per keyframe so smoothstep still
        // interpolates and the octave stays inside the 25 Hz Nyquist limit of
        // the 50 Hz motor-frame stream — shortening this toward one tick per
        // keyframe would alias on our own side.
        private const double RoadTextureFineKeyframeSec = 0.045;
        // Grain-dominant: ~10 zero-crossings/sec against the coarse layer's
        // 1.4, with the coarse swell still audible underneath.
        private const double RoadTextureFineWeight = 2.5;
        // Decorrelates the two octaves' keyframe sequences — without it both
        // layers would share index 0 at onset and move together.
        private const long RoadTextureFineSalt = 0x5F5F5F;

        // ROAD TEXTURE directional attack transient — a haptics technique,
        // not a protocol-verified behavior: injecting a brief, strongly
        // asymmetric spike at the very start of a bump (instead of easing
        // in from the symmetric ambient noise baseline) can bias the
        // *perceived* direction of a vibration motor's pulse, since touch
        // is far more sensitive to a sudden onset's acceleration than to
        // steady-state amplitude. There is NO capture evidence establishing
        // which raw-sample polarity the firmware/motor treats as which
        // physical direction — prior Road Texture work only needed to match
        // the noise's amplitude/oscillation character (see
        // RoadTextureCoarseKeyframeSec's doc comment), never its sign's physical
        // meaning. RoadTextureAttackSign is therefore a guess at "pushes the
        // pedal face toward the driver's foot" — if it feels backwards on
        // real hardware, negate this one constant.
        private const double RoadTextureAttackSec = 0.08;
        private const double RoadTextureAttackSign = 1.0;

        /// <summary>
        /// ROAD TEXTURE noise generator — unlike the other three effects,
        /// this doesn't return an amplitude the caller scales by intensity;
        /// it returns a raw signed sample in [-1, 1] that gets sent to the
        /// device UNSCALED (see MozaMBoosterProtocol.BuildRoadTextureFrame).
        /// A real Pit House capture confirmed the firmware — not Pit House
        /// or this plugin — applies Intensity/Smoothness to shape this
        /// signal internally, so this is a host-side stand-in for whatever
        /// reference noise Pit House streams, not a decoded/verified
        /// algorithm. Deterministic two-octave value noise: the grain layer at
        /// <see cref="RoadTextureFineKeyframeSec"/> over a slow surface
        /// undulation at <see cref="RoadTextureCoarseKeyframeSec"/>, each
        /// smoothstep-
        /// interpolated between keyframes so the output can't jump
        /// discontinuously. <paramref name="elapsedSec"/> is time since
        /// THIS bump's activation edge (resets to 0 each time the effect
        /// goes silent-to-active — see MBoosterEffectWorker.
        /// ProcessRoadTextureEffect) — for the first
        /// <see cref="RoadTextureAttackSec"/> of a new bump, the ambient
        /// noise is cross-faded in from the directional attack transient
        /// above instead of starting cold, so every bump/kerb strike leads
        /// with a punchy directional "hit" before settling into regular
        /// road chatter.
        /// </summary>
        public static double SynthesizeRoadTextureNoise(double elapsedSec)
        {
            // Normalized so the summed octaves still span [-1, 1] — the wire
            // sample is sent unscaled, so clipping here would flatten peaks
            // into a buzz instead of leaving headroom for the coarse swell.
            double ambient =
                (ValueNoise(elapsedSec, RoadTextureCoarseKeyframeSec, 0)
                 + ValueNoise(elapsedSec, RoadTextureFineKeyframeSec, RoadTextureFineSalt) * RoadTextureFineWeight)
                / (1.0 + RoadTextureFineWeight);

            if (elapsedSec >= RoadTextureAttackSec) return ambient;

            double blend = elapsedSec / RoadTextureAttackSec; // 0 at onset -> 1 at end of attack window
            double impulse = RoadTextureAttackSign * Math.Exp(-elapsedSec / (RoadTextureAttackSec / 3.0));
            return impulse * (1.0 - blend) + ambient * blend;
        }

        /// <summary>One octave: smoothstep-interpolated value noise over
        /// keyframes <paramref name="keyframeSec"/> apart, in [-1, 1].
        /// <paramref name="salt"/> offsets the keyframe index so separate
        /// octaves draw independent sequences.</summary>
        private static double ValueNoise(double elapsedSec, double keyframeSec, long salt)
        {
            double t = elapsedSec / keyframeSec;
            double t0 = Math.Floor(t);
            double frac = t - t0;
            double a = KeyframeNoise((long)t0 + salt);
            double b = KeyframeNoise((long)t0 + 1 + salt);
            double s = frac * frac * (3.0 - 2.0 * frac); // smoothstep
            return a + (b - a) * s;
        }

        /// <summary>Deterministic hash of an integer keyframe index to a pseudo-random value in [-1, 1].</summary>
        private static double KeyframeNoise(long seed)
        {
            unchecked
            {
                ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL;
                h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
                h ^= h >> 27; h *= 0x94D049BB133111EBUL;
                h ^= h >> 31;
                return ((h & 0xFFFFFF) / (double)0xFFFFFF) * 2.0 - 1.0;
            }
        }
    }
}
