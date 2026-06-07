using MozaPlugin.Telemetry.TestMode;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Synthetic value source for the "Send Test Pattern" button on the standalone
    /// FSR1 (group-0x42) and CM1 (group-0x35) display drivers. The tier-def pipeline
    /// drives its pattern through <see cref="TelemetrySender.TestMode"/> +
    /// <see cref="TestSignalCatalog"/>; those drivers have no per-channel range
    /// metadata, so this produces a per-field triangle sweep over a sensible range so
    /// every field visibly moves with no game running. Reuses
    /// <see cref="TestSignalGenerator"/> so the wave shape matches the tier-def path.
    /// </summary>
    internal static class DashboardTestPattern
    {
        /// <summary>Monotonic milliseconds — same clock the tier-def TestMode uses.</summary>
        internal static long NowMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L /
            System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Triangle sweep over [0, max], phase-staggered per key so fields
        /// don't all move in lockstep.</summary>
        internal static double Sweep(string key, double max, long nowMs)
        {
            if (max <= 0) max = 1;
            int phase = (int)(Hash(key ?? "") % 4000u);
            return TestSignalGenerator.Compute(
                TestSignal.Sweep(0, max, periodMs: 5000, phaseOffsetMs: phase), nowMs);
        }

        /// <summary>Best-effort natural range for a field with no range metadata
        /// (CM1 catalog), inferred from its id/property so the sweep is plausible
        /// per channel rather than a flat 0..1.</summary>
        internal static double NaturalMax(string fieldId, string property)
        {
            string s = ((fieldId ?? "") + " " + (property ?? "")).ToLowerInvariant();
            if (s.Contains("gear")) return 8;
            if (s.Contains("rpm")) return 9000;
            if (s.Contains("speed")) return 320;
            if (s.Contains("temp")) return 120;
            if (s.Contains("pressure")) return 200;
            if (s.Contains("fuel")) return 100;
            if (s.Contains("lap") || s.Contains("position")) return 20;
            return 100; // percentages, throttle/brake/clutch, abs/tc, generic
        }

        // FNV-1a — stable across runs so a field keeps its phase offset.
        private static uint Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in s) { h ^= c; h *= 16777619u; }
                return h;
            }
        }
    }
}
