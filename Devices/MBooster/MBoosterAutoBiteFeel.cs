using System;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>
    /// Automatic bite point settings for one pedal (IMBoosterPedalConfig.AutoBiteFeel).
    /// Scalars only, so the data thread reads them while the UI writes.
    /// </summary>
    public sealed class MBoosterAutoBiteFeel
    {
        public bool Enabled { get; set; }
        // Peak half-width, pedal travel (%).
        public int WidthPct { get; set; } = MBoosterUiConstants.AutoBiteWidthDefaultPct;
        // Force up to the peak and after it, % of the Deadzone→Max Force span.
        public int BeforePct { get; set; } = MBoosterUiConstants.AutoBiteBeforeDefaultPct;
        public int AfterPct { get; set; } = MBoosterUiConstants.AutoBiteAfterDefaultPct;

        public MBoosterAutoBiteFeel Clone() => (MBoosterAutoBiteFeel)MemberwiseClone();
    }

    /// <summary>When a feel curve push may write the pedal's defaults (0 kg
    /// Deadzone, 200 kg Max Force, Linear) for what the profile leaves unset.</summary>
    internal enum MBoosterFeelPushGuard
    {
        // Always: a UI edit of the curve.
        None,
        // Only with the automatic curve or a stored feel value — never over the
        // unit's own values (connect/profile apply, Travel).
        IfSet,
        // As IfSet, or over a curve already sent this connection (Automatic bite
        // point taking its curve back off).
        IfSetOrSent,
    }

    /// <summary>
    /// The automatic bite point Pedal Feel template: a ramp from the Deadzone
    /// start point up to Before at bite − Width, the peak (100 %, Max Force) at
    /// the bite point, After at bite + Width and flat on to the last point. The
    /// three other nodes go to the two sides in proportion to their length,
    /// evenly spaced. The peak sits on a node, so it holds whether the unit
    /// interpolates between nodes linearly or by spline.
    /// </summary>
    internal static class MBoosterAutoBiteCurve
    {
        private const int PeakNodes = 3;
        // 1-unit X gaps to the start point (X=0) and the last point (X=100),
        // as the curve editor keeps.
        private const int MinX = 1;
        private const int MaxX = 99;

        /// <param name="bitePct">Bite point, pedal travel 0..100.</param>
        /// <param name="y">Node Y, % of the Deadzone→Max Force span (InputCurveY's units).</param>
        /// <param name="x">Node X, % of travel (InputCurveX's units), whole and increasing.</param>
        /// <param name="endY">Last point Y (InputCurveEndY's units).</param>
        public static void Build(double bitePct, int widthPct, int beforePct, int afterPct,
            out float[] y, out float[] x, out float endY)
        {
            int n = MBoosterUiConstants.PedalFeelNodeCount;
            int free = n - PeakNodes;
            int w = Clamp(widthPct, MBoosterUiConstants.AutoBiteWidthMinPct, MBoosterUiConstants.AutoBiteWidthMaxPct);
            int bite = Clamp((int)Math.Round(bitePct, MidpointRounding.AwayFromZero), MinX + 1, MaxX - 1);
            int left = Math.Max(MinX, bite - w);
            int right = Math.Min(MaxX, bite + w);
            double before = Clamp(beforePct, 0, 100);
            double after = Clamp(afterPct, 0, 100);

            // Whole X positions left free on each side; together always more
            // than `free`, since Width is at most 25.
            int leftRoom = left - MinX;
            int rightRoom = MaxX - right;
            int nLeft = (int)Math.Round(free * (double)left / (left + 100 - right), MidpointRounding.AwayFromZero);
            nLeft = Clamp(nLeft, Math.Max(0, free - rightRoom), Math.Min(free, leftRoom));
            int nRight = free - nLeft;

            x = new float[n];
            y = new float[n];
            int k = 0;
            // Spacing of at least 1 keeps the rounded positions distinct and
            // strictly inside each side.
            for (int i = 1; i <= nLeft; i++, k++)
            {
                int xi = (int)Math.Round(left * i / (double)(nLeft + 1), MidpointRounding.AwayFromZero);
                x[k] = xi;
                y[k] = Pct(before * xi / left);
            }
            x[k] = left; y[k++] = Pct(before);
            x[k] = bite; y[k++] = 100f;
            x[k] = right; y[k++] = Pct(after);
            for (int i = 1; i <= nRight; i++, k++)
            {
                x[k] = right + (int)Math.Round((100 - right) * i / (double)(nRight + 1), MidpointRounding.AwayFromZero);
                y[k] = Pct(after);
            }
            endY = Pct(after);
        }

        private static float Pct(double v) => (float)Math.Round(v, MBoosterUiConstants.PedalFeelYDecimals);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
