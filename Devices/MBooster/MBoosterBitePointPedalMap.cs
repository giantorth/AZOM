using System;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>
    /// One game's map from the bite point source's clutch position to the
    /// clutch mBooster pedal's own travel (its raw HID reading before Max
    /// Threshold — what the Pedal Feel graph's live dot shows), so a bite point
    /// calibrated in the source's units can be placed on the Pedal Feel curve.
    /// Learned only while both hold steady, so the game's telemetry lag doesn't
    /// skew it; in-game deadzones and curves end up in it. Bins update in place
    /// (single float stores); the dictionary holding the maps is copy-on-write.
    /// </summary>
    public sealed class MBoosterBitePointPedalMap
    {
        public const int Bins = 101;
        // Furthest a seen bin may be from the looked-up position (source %).
        private const int MaxGap = 5;
        // Weight of each new steady sample.
        private const double Smoothing = 0.1;
        // Bin change (pedal %) worth saving.
        private const double SaveDelta = 0.5;

        /// <summary>Pedal travel (0..100) per whole source percent; -1 = not seen.</summary>
        public float[] Pedal { get; set; } = Empty();

        /// <summary>Fold in one steady sample.</summary>
        /// <returns>True when the bin is new or moved enough to save.</returns>
        public bool Learn(double sourcePct, double pedalPct)
        {
            var bins = Valid();
            int bin = (int)Math.Round(Math.Max(0, Math.Min(100, sourcePct)), MidpointRounding.AwayFromZero);
            double p = Math.Max(0, Math.Min(100, pedalPct));
            float old = bins[bin];
            float next = old < 0 ? (float)p : (float)(old + Smoothing * (p - old));
            bins[bin] = next;
            return old < 0 || Math.Abs(next - old) >= SaveDelta;
        }

        /// <summary>Pedal travel at a source position: its own bin, else between
        /// the nearest seen bins on both sides within MaxGap.</summary>
        public bool TryMap(double sourcePct, out double pedalPct)
        {
            pedalPct = double.NaN;
            var bins = Pedal;
            if (bins == null || bins.Length != Bins) return false;
            int bin = (int)Math.Round(Math.Max(0, Math.Min(100, sourcePct)), MidpointRounding.AwayFromZero);
            if (bins[bin] >= 0)
            {
                pedalPct = bins[bin];
                return true;
            }
            int lo = -1, hi = -1;
            for (int d = 1; d <= MaxGap && (lo < 0 || hi < 0); d++)
            {
                if (lo < 0 && bin - d >= 0 && bins[bin - d] >= 0) lo = bin - d;
                if (hi < 0 && bin + d < Bins && bins[bin + d] >= 0) hi = bin + d;
            }
            if (lo < 0 || hi < 0) return false;
            pedalPct = bins[lo] + (bins[hi] - bins[lo]) * (bin - lo) / (double)(hi - lo);
            return true;
        }

        // A saved map of the wrong size (hand-edited, or a future layout) starts over.
        private float[] Valid()
        {
            var bins = Pedal;
            if (bins != null && bins.Length == Bins) return bins;
            return Pedal = Empty();
        }

        private static float[] Empty()
        {
            var a = new float[Bins];
            for (int i = 0; i < a.Length; i++) a[i] = -1;
            return a;
        }
    }
}
