using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// Series plumbing shared by <see cref="BandwidthSparkline"/> and
    /// <see cref="TemperatureGraph"/>: copies a bound sample source into a
    /// reused buffer and builds the line + under-glow fill geometries.
    /// </summary>
    internal static class SparklineSeries
    {
        /// <summary>Copy <paramref name="src"/> into <paramref name="buf"/>
        /// (grown as needed) and return the sample count. IList&lt;double&gt;
        /// (ObservableCollection, double[]) is read without boxing.</summary>
        public static int Read(IEnumerable? src, ref double[] buf)
        {
            if (src == null) return 0;
            if (src is IList<double> list)
            {
                int n = list.Count;
                if (buf.Length < n) buf = new double[n];
                list.CopyTo(buf, 0);
                return n;
            }
            int count = 0;
            foreach (var v in src)
            {
                if (v is not IConvertible c) continue;
                if (count == buf.Length) Array.Resize(ref buf, Math.Max(16, buf.Length * 2));
                buf[count++] = c.ToDouble(CultureInfo.InvariantCulture);
            }
            return count;
        }

        /// <summary>
        /// Build the open stroked line and the closed fill (down to
        /// <paramref name="baseY"/>) over <c>vals[0..n)</c>, spread evenly
        /// across <paramref name="plotW"/> from x = <paramref name="pad"/>.
        /// Each sample maps to y = baseY - (clamp(v, lo, hi) - offset) * scale.
        /// <paramref name="lastY"/> is the final sample's y.
        /// </summary>
        public static void Build(double[] vals, int n, double pad, double plotW, double baseY,
            double lo, double hi, double offset, double scale,
            out Geometry? line, out Geometry? fill, out double lastY)
        {
            if (n == 0) { line = null; fill = null; lastY = 0; return; }

            double X(int i) => pad + (n == 1 ? 0 : plotW * i / (n - 1));
            double Y(int i) => baseY - (Math.Min(Math.Max(vals[i], lo), hi) - offset) * scale;

            var lineGeom = new StreamGeometry();
            using (var ctx = lineGeom.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(0)), false, false);
                for (int i = 1; i < n; i++)
                    ctx.LineTo(new Point(X(i), Y(i)), true, false);
            }
            lineGeom.Freeze();

            var fillGeom = new StreamGeometry();
            using (var ctx = fillGeom.Open())
            {
                ctx.BeginFigure(new Point(pad, baseY), true, true);
                for (int i = 0; i < n; i++)
                    ctx.LineTo(new Point(X(i), Y(i)), false, false);
                ctx.LineTo(new Point(pad + plotW, baseY), false, false);
            }
            fillGeom.Freeze();

            line = lineGeom;
            fill = fillGeom;
            lastY = Y(n - 1);
        }
    }
}
