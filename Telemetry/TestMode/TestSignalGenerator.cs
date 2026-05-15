using System;

namespace MozaPlugin.Telemetry.TestMode
{
    /// <summary>
    /// Pure function that maps (TestSignal, wall-clock ms) → synthetic value.
    /// Shared by both telemetry sweep paths (V2 tier-frame builder in
    /// <c>Frames/TelemetryFrameBuilder.cs</c> and V0 URL value-frame builder
    /// in <c>TelemetrySender.cs</c>) so they cannot drift apart.
    ///
    /// Wall-clock driven (not phase-counter driven) so a "Gear stepping at
    /// 1 Hz" channel changes once per real second regardless of which tier
    /// (30 ms / 500 ms / 2000 ms) carries it.
    /// </summary>
    public static class TestSignalGenerator
    {
        // Wall-clock epoch (ms) from which Elapsed-kind counters tick.
        // Reset every time TestMode is switched on so the wheel sees a clean
        // 00:00:00 start (see TelemetrySender.TestMode setter).
        private static long _epochMs;

        public static void ResetEpoch(long nowMs)
        {
            _epochMs = nowMs;
        }

        public static double Compute(TestSignal s, long wallClockMs)
        {
            long t = wallClockMs + s.PhaseOffsetMs;
            if (t < 0) t = -t;

            switch (s.Kind)
            {
                case TestKind.Elapsed:
                {
                    double elapsedSec = (wallClockMs - _epochMs) / 1000.0;
                    if (elapsedSec < 0) elapsedSec = 0;
                    double scale = s.Max == 0 ? 1.0 : s.Max;
                    return s.Min + elapsedSec * scale;
                }

                case TestKind.Constant:
                    return s.Constant;

                case TestKind.Sweep:
                {
                    int period = s.PeriodMs > 0 ? s.PeriodMs : 5000;
                    double frac = (double)(t % period) / period;
                    double tri = 1.0 - Math.Abs(2.0 * frac - 1.0);
                    return s.Min + (s.Max - s.Min) * tri;
                }

                case TestKind.Step:
                {
                    int step = s.StepMs > 0 ? s.StepMs : 1000;
                    long range = (long)Math.Max(1, Math.Round(s.Max - s.Min) + 1);
                    long cycle = Math.Max(1, 2 * range - 2);
                    long idx = (t / step) % cycle;
                    if (idx >= range) idx = 2 * range - 2 - idx;
                    double v = s.Min + idx;
                    return s.StepIsInt ? Math.Round(v) : v;
                }

                case TestKind.Toggle:
                {
                    int step = s.StepMs > 0 ? s.StepMs : 4000;
                    return (t / step) % 2 == 0 ? s.Min : s.Max;
                }

                case TestKind.Increment:
                {
                    // Epoch-relative so counters always start at Min on
                    // every Test Start (predictable demo behaviour).
                    int step = s.StepMs > 0 ? s.StepMs : 30000;
                    long range = (long)Math.Max(1, Math.Round(s.Max - s.Min) + 1);
                    long elapsed = wallClockMs - _epochMs;
                    if (elapsed < 0) elapsed = 0;
                    long ticks = elapsed / step;
                    long idx = s.Wrap ? ticks % range : Math.Min(ticks, range - 1);
                    return s.Min + idx;
                }

                case TestKind.StringConstant:
                    return 0.0;

                default:
                    return 0.0;
            }
        }

        /// <summary>
        /// String-valued sibling of <see cref="Compute"/>. Used by the sess=0x01
        /// type=0x05 emitter (<c>TelemetrySender.TickEmitStringValues</c>) for
        /// string-typed channels in test mode. For <see cref="TestKind.StringConstant"/>
        /// returns the literal <c>StringValue</c>; for any other kind returns the
        /// numeric Compute() result formatted invariantly so a misconfigured channel
        /// (numeric signal accidentally bound to a string transport) still produces
        /// something on the wire instead of silently degrading.
        /// </summary>
        public static string ComputeString(TestSignal s, long wallClockMs)
        {
            if (s.Kind == TestKind.StringConstant)
                return s.StringValue ?? "";
            return Compute(s, wallClockMs)
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
