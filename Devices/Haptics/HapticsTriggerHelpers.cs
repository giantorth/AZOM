using System;
using System.Diagnostics;
using System.Globalization;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// On-change trigger edge detection shared by the wheelbase LFE and S12
    /// workers: a change in the trigger value fires, except a change into
    /// neutral (≈0) unless opted in, and nothing within the debounce window of
    /// the last fire. The first value after a reset is adopted without firing.
    /// </summary>
    internal struct HapticsEdgeDetector
    {
        private bool _warm;
        private double _last;
        private long _lastFireTicks;

        /// <summary>Forget the last value — the next sample re-arms without firing.</summary>
        public void Reset() => _warm = false;

        /// <summary>True when <paramref name="raw"/> is a fresh, accepted edge.</summary>
        public bool Fire(double raw, bool vibrateOnNeutral, int debounceMs, long now)
        {
            if (!_warm) { _last = raw; _warm = true; return false; }
            if (Math.Abs(raw - _last) <= 1e-6) return false;

            bool intoNeutral = Math.Abs(raw) < 0.5;   // gear ≈ 0
            _last = raw;
            if (debounceMs < 0) debounceMs = 0;
            bool debounced = (now - _lastFireTicks) * 1000.0 / Stopwatch.Frequency < debounceMs;
            if ((intoNeutral && !vibrateOnNeutral) || debounced) return false;
            _lastFireTicks = now;
            return true;
        }
    }

    /// <summary>
    /// Formula evaluation for parked Test sweeps: substitutes a synthetic
    /// "accelerating flat out" snapshot (RPM + speed sweeping, full throttle, no
    /// brake) into the real formulas, so every slot sees the same RPM at each
    /// instant and the test matches in-game output. A formula left with only
    /// numbers is parsed directly; anything still referencing other telemetry
    /// evaluates live.
    /// </summary>
    internal static class HapticsTestFormula
    {
        public const double SynthRedlineRpm = EngineVibrationMath.DefaultRedlineRpm;
        public const double SynthMaxSpeedKmh = 250.0;
        /// <summary>Sweeps start ~12% up the band (idle ≈ 600 of ~5000 rpm), not at 0 rpm.</summary>
        public const double SweepIdleFraction = 0.12;

        private const string RpmToken = "[DataCorePlugin.GameData.Rpms]";
        private const string MaxRpmToken = "[DataCorePlugin.GameData.MaxRpm]";
        private const string ThrottleToken = "[DataCorePlugin.GameData.Throttle]";
        private const string BrakeToken = "[DataCorePlugin.GameData.Brake]";
        private const string SpeedToken = "[DataCorePlugin.GameData.SpeedKmh]";

        public static double Eval(string formula, double synthRpm, double synthSpeed, Func<string, double> eval)
        {
            var inv = CultureInfo.InvariantCulture;
            string s = formula
                .Replace(RpmToken, synthRpm.ToString("R", inv))
                .Replace(MaxRpmToken, SynthRedlineRpm.ToString("R", inv))
                .Replace(ThrottleToken, "100")      // flat out
                .Replace(BrakeToken, "0")
                .Replace(SpeedToken, synthSpeed.ToString("R", inv));
            if (double.TryParse(s, NumberStyles.Any, inv, out double lit))
                return lit;
            return eval(s);
        }

        /// <summary>Static value when no formula is set, else the synthetic evaluation.</summary>
        public static double Param(string? formula, double staticVal, double synthRpm, double synthSpeed, Func<string, double> eval)
            => string.IsNullOrWhiteSpace(formula) ? staticVal : Eval(formula!, synthRpm, synthSpeed, eval);
    }
}
