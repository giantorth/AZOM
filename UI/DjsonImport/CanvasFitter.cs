using System;
using System.Collections.Generic;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Rescales a dashboard onto the wheel's canvas.
    ///
    /// <para>The transform is derived from the source's <b>declared</b> canvas
    /// (<c>BaseWidth</c>/<c>BaseHeight</c>) and applied <b>identically to every screen</b>.
    /// Both of those matter:</para>
    ///
    /// <list type="bullet">
    /// <item>The declared canvas is the frame the author composed against. Fitting each
    /// screen to its own content instead looks appealing — it fills more of the wheel —
    /// but it silently rescales and re-centres every page differently, so nothing lines
    /// up between pages, and it is wrecked by the off-canvas elements SimHub dashboards
    /// routinely park outside the frame (one stock dashboard reaches y=-91 and x=808 on
    /// an 800x268 canvas, dragging a faithful 0.93 fit down to 0.55).</item>
    /// <item>One shared transform keeps a multi-page dashboard coherent: a header drawn at
    /// the same coordinates on every page still lands in the same place.</item>
    /// </list>
    ///
    /// <para>Aspect ratio is preserved, so a source that does not match the wheel's ratio
    /// leaves gutters rather than distorting. Content outside the declared canvas stays
    /// outside it — that is where the author put it.</para>
    /// </summary>
    public static class CanvasFitter
    {
        /// <summary>The W17/W18/W20 dashboard canvas, and the value in all 24 factory
        /// dashboards examined. Passed through rather than hard-coded at the call sites so
        /// a different display family is a parameter change, not a code change.</summary>
        public const int WheelWidth = 780;
        public const int WheelHeight = 248;

        /// <summary>
        /// Fit the dashboard onto <paramref name="canvasWidth"/> x
        /// <paramref name="canvasHeight"/>.
        /// </summary>
        /// <param name="sourceWidth">The source's declared canvas width. Zero or negative
        /// falls back to the union of every screen's content.</param>
        public static void Fit(IrDashboard dashboard, int canvasWidth, int canvasHeight,
                               double sourceWidth, double sourceHeight,
                               ConversionReport report)
        {
            double frameX = 0, frameY = 0;
            double frameW = sourceWidth, frameH = sourceHeight;

            if (frameW <= 0 || frameH <= 0)
            {
                var bounds = UnionContentBounds(dashboard);
                if (bounds == null)
                {
                    report.Notes.Add("nothing to lay out — no positioned elements");
                    return;
                }
                (frameX, frameY, double maxX, double maxY) = bounds.Value;
                frameW = maxX - frameX;
                frameH = maxY - frameY;
                report.Notes.Add(
                    "source declares no canvas size — fitted to the content bounds instead");
            }

            if (frameW <= 0 || frameH <= 0) return;

            double scale = Math.Min(canvasWidth / frameW, canvasHeight / frameH);
            double offsetX = (canvasWidth - frameW * scale) / 2.0;
            double offsetY = (canvasHeight - frameH * scale) / 2.0;

            foreach (var screen in dashboard.Screens)
            {
                foreach (var child in screen.Children)
                    Transform(child, scale, frameX, frameY, offsetX, offsetY);

                screen.X = 0;
                screen.Y = 0;
                screen.Width = canvasWidth;
                screen.Height = canvasHeight;

                report.ScreenFitScales.Add(scale);
            }

            ReportOffCanvas(dashboard, canvasWidth, canvasHeight, report);
        }

        /// <summary>
        /// Scale and translate a subtree: <c>out = (in - origin) * scale + offset</c>.
        ///
        /// <para>Also used for widget inlining, where a referenced dashboard is drawn into
        /// a host rectangle and so needs the same treatment one level down.</para>
        /// </summary>
        public static void Transform(IrNode node, double scale,
                                     double originX, double originY,
                                     double offsetX, double offsetY)
        {
            // A layer has no geometry of its own on the wheel, but its children do — and
            // Repetitions has already been expanded into their absolute coordinates.
            if (node.Kind != IrKind.Layer)
            {
                node.X = (node.X - originX) * scale + offsetX;
                node.Y = (node.Y - originY) * scale + offsetY;
                node.Width *= scale;
                node.Height *= scale;

                node.BorderWidth *= scale;
                node.BorderRadius *= scale;
                ScaleBorder(node.Border, scale);
                ScaleEffect(node.Effect, scale);

                if (node.Text != null)
                {
                    node.Text.FontSize *= scale;
                    node.Text.PaddingTop *= scale;
                    node.Text.PaddingBottom *= scale;
                    node.Text.PaddingLeft *= scale;
                    node.Text.PaddingRight *= scale;
                }

                // Gauge min/max/value are data, not geometry — only the stroke scales.
                if (node.Gauge != null)
                    node.Gauge.StrokeThickness *= scale;
            }

            foreach (var c in node.Children)
                Transform(c, scale, originX, originY, offsetX, offsetY);
        }

        /// <summary>Scale a subtree in place about its own origin, for widget inlining.</summary>
        public static void ScaleAndOffset(IrNode node, double scale, double offsetX, double offsetY)
            => Transform(node, scale, 0, 0, offsetX, offsetY);

        /// <summary>The bounding box of everything that will actually render, across every
        /// screen. Layers are skipped: they carry no geometry in mzdash.</summary>
        private static (double minX, double minY, double maxX, double maxY)? UnionContentBounds(
            IrDashboard dashboard)
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

            foreach (var screen in dashboard.Screens) Walk(screen, true);
            return any ? (minX, minY, maxX, maxY) : ((double, double, double, double)?)null;
        }

        /// <summary>Count what ends up outside the visible canvas. Some of it is deliberate
        /// — SimHub dashboards park alternates off-frame — but a large number usually means
        /// the source was authored for a taller screen.</summary>
        private static void ReportOffCanvas(IrDashboard dashboard, int width, int height,
                                            ConversionReport report)
        {
            int outside = 0, total = 0;

            void Walk(IrNode node, bool visible)
            {
                bool v = visible && node.Visible;
                if (v && node.Kind != IrKind.Layer && node.Kind != IrKind.Screen
                    && node.Width > 0 && node.Height > 0)
                {
                    total++;
                    if (node.X + node.Width <= 0 || node.Y + node.Height <= 0
                        || node.X >= width || node.Y >= height)
                        outside++;
                }
                foreach (var c in node.Children) Walk(c, v);
            }

            foreach (var screen in dashboard.Screens) Walk(screen, true);

            if (outside > 0)
            {
                report.Notes.Add($"{outside} of {total} visible elements sit outside the "
                               + "canvas — they were off-frame in the source too");
            }
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
