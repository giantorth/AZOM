using System;
using System.Collections.Generic;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Rescales each screen onto the wheel's canvas.
    ///
    /// <para>SimHub dashboards are mostly 1280x720 (16:9); the wheel is 780x248 (3.15:1).
    /// Nothing makes those aspect ratios agree, so the fit is <b>uniform</b> — scale by
    /// <c>min(sx, sy)</c> and centre — which keeps circles round and text proportioned at
    /// the cost of leaving side gutters. A 16:9 source ends up using about 57% of the
    /// canvas; the user finishes the layout in Dashboard Studio.</para>
    ///
    /// <para>The fit is computed from each screen's actual content bounding box rather than
    /// the declared <c>BaseWidth</c>/<c>BaseHeight</c>. It is never worse — an empty margin
    /// costs nothing to discard — and on dashboards whose content occupies a band it is
    /// much better (one stock screen goes from 56% to 94% canvas use).</para>
    /// </summary>
    public static class CanvasFitter
    {
        /// <summary>The W17/W18 dashboard canvas, and the value in all 24 factory
        /// dashboards examined. Passed through rather than hard-coded at the call sites so
        /// a different display family is a parameter change, not a code change.</summary>
        public const int WheelWidth = 780;
        public const int WheelHeight = 248;

        /// <summary>Fit every screen and record the scale used for each.</summary>
        public static void Fit(IrDashboard dashboard, int canvasWidth, int canvasHeight,
                               ConversionReport report)
        {
            foreach (var screen in dashboard.Screens)
            {
                double scale = FitScreen(screen, canvasWidth, canvasHeight);
                report.ScreenFitScales.Add(scale);
            }

            // The screen itself always fills the canvas exactly.
            foreach (var screen in dashboard.Screens)
            {
                screen.X = 0;
                screen.Y = 0;
                screen.Width = canvasWidth;
                screen.Height = canvasHeight;
            }
        }

        private static double FitScreen(IrNode screen, int canvasWidth, int canvasHeight)
        {
            var bounds = ContentBounds(screen);
            if (bounds == null) return 1.0;

            var (minX, minY, maxX, maxY) = bounds.Value;
            double w = maxX - minX;
            double h = maxY - minY;
            if (w <= 0 || h <= 0) return 1.0;

            double scale = Math.Min(canvasWidth / w, canvasHeight / h);

            // Centre what is left over, so the gutters are even rather than all on one side.
            double offsetX = (canvasWidth - w * scale) / 2.0;
            double offsetY = (canvasHeight - h * scale) / 2.0;

            foreach (var child in screen.Children)
                Apply(child, scale, minX, minY, offsetX, offsetY);

            return scale;
        }

        /// <summary>The bounding box of everything that will actually render.
        /// Layers are skipped: they carry no geometry in mzdash and their <c>.djson</c>
        /// Left/Top is only a bounding box of the children already counted here.</summary>
        private static (double minX, double minY, double maxX, double maxY)? ContentBounds(IrNode root)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool any = false;

            void Walk(IrNode node, bool visible)
            {
                bool v = visible && node.Visible;
                if (v && node.Kind != IrKind.Layer && node.Kind != IrKind.Screen
                    && node.Width > 0 && node.Height > 0)
                {
                    any = true;
                    if (node.X < minX) minX = node.X;
                    if (node.Y < minY) minY = node.Y;
                    if (node.X + node.Width > maxX) maxX = node.X + node.Width;
                    if (node.Y + node.Height > maxY) maxY = node.Y + node.Height;
                }
                foreach (var c in node.Children) Walk(c, v);
            }

            Walk(root, true);
            return any ? (minX, minY, maxX, maxY) : ((double, double, double, double)?)null;
        }

        private static void Apply(IrNode node, double s, double minX, double minY,
                                  double offsetX, double offsetY)
        {
            // A layer has no geometry of its own on the wheel, but its children do — and
            // Repetitions has already been expanded into their absolute coordinates.
            if (node.Kind != IrKind.Layer)
            {
                node.X = (node.X - minX) * s + offsetX;
                node.Y = (node.Y - minY) * s + offsetY;
                node.Width *= s;
                node.Height *= s;

                node.BorderWidth *= s;
                node.BorderRadius *= s;
                ScaleBorder(node.Border, s);
                ScaleEffect(node.Effect, s);

                if (node.Text != null)
                {
                    node.Text.FontSize *= s;
                    node.Text.PaddingTop *= s;
                    node.Text.PaddingBottom *= s;
                    node.Text.PaddingLeft *= s;
                    node.Text.PaddingRight *= s;
                }

                // Gauge min/max/value are data, not geometry — only the stroke scales.
                if (node.Gauge != null)
                    node.Gauge.StrokeThickness *= s;
            }

            foreach (var c in node.Children) Apply(c, s, minX, minY, offsetX, offsetY);
        }

        private static void ScaleBorder(IrBorder b, double s)
        {
            b.Top *= s; b.Bottom *= s; b.Left *= s; b.Right *= s;
            b.RadiusTopLeft *= s; b.RadiusTopRight *= s;
            b.RadiusBottomLeft *= s; b.RadiusBottomRight *= s;
        }

        private static void ScaleEffect(IrEffect e, double s)
        {
            e.BlurRadius *= s;
            e.ShadowBlur *= s;
            e.ShadowDepth *= s;
            // Opacity, rotation, shadow direction and blink timing are all scale-invariant.
        }
    }
}
