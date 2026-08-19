using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// X/Y plot for Segmented Damping (mBooster Pedal Feel): two draggable
    /// vertical dividers split 0-100% pedal travel (X axis) into 3 segments,
    /// each with its own independently draggable damping amount (Y axis,
    /// 0-100%). Unlike <see cref="MozaRangeSlider"/> — a plain dual-thumb
    /// slider sharing ONE range — each divider here has its OWN independent
    /// [Min,Max] bound (Pit House's own asymmetric bounds: Divider1
    /// 10-80%, Divider2 20-90%), plus a minimum gap between them. See
    /// docs/protocol/devices/mbooster.md "Segmented Damping".
    /// </summary>
    public class MozaSegmentedBarEditor : Control
    {
        static MozaSegmentedBarEditor()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(typeof(MozaSegmentedBarEditor)));
        }

        /// <summary>Fires whenever any divider or segment value changes (drag or programmatic set).</summary>
        public event EventHandler? ValuesChanged;

        // -------- Divider positions (X axis, 0-100%, two-way bindable) --------

        public static readonly DependencyProperty Divider1Property =
            DependencyProperty.Register(nameof(Divider1), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(33.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).OnValueChanged()));
        public double Divider1 { get => (double)GetValue(Divider1Property); set => SetValue(Divider1Property, value); }

        public static readonly DependencyProperty Divider2Property =
            DependencyProperty.Register(nameof(Divider2), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(67.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).OnValueChanged()));
        public double Divider2 { get => (double)GetValue(Divider2Property); set => SetValue(Divider2Property, value); }

        // -------- Per-divider independent bounds + minimum gap --------

        public static readonly DependencyProperty Divider1MinProperty =
            DependencyProperty.Register(nameof(Divider1Min), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Divider1Min { get => (double)GetValue(Divider1MinProperty); set => SetValue(Divider1MinProperty, value); }

        public static readonly DependencyProperty Divider1MaxProperty =
            DependencyProperty.Register(nameof(Divider1Max), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Divider1Max { get => (double)GetValue(Divider1MaxProperty); set => SetValue(Divider1MaxProperty, value); }

        public static readonly DependencyProperty Divider2MinProperty =
            DependencyProperty.Register(nameof(Divider2Min), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Divider2Min { get => (double)GetValue(Divider2MinProperty); set => SetValue(Divider2MinProperty, value); }

        public static readonly DependencyProperty Divider2MaxProperty =
            DependencyProperty.Register(nameof(Divider2Max), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Divider2Max { get => (double)GetValue(Divider2MaxProperty); set => SetValue(Divider2MaxProperty, value); }

        /// <summary>Minimum allowed gap between Divider1 and Divider2 (the two may never be dragged closer than this).</summary>
        public static readonly DependencyProperty MinGapProperty =
            DependencyProperty.Register(nameof(MinGap), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double MinGap { get => (double)GetValue(MinGapProperty); set => SetValue(MinGapProperty, value); }

        // -------- Segment damping values (Y axis, 0-100%, two-way bindable) --------

        public static readonly DependencyProperty Seg1ValueProperty =
            DependencyProperty.Register(nameof(Seg1Value), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).OnValueChanged()));
        public double Seg1Value { get => (double)GetValue(Seg1ValueProperty); set => SetValue(Seg1ValueProperty, value); }

        public static readonly DependencyProperty Seg2ValueProperty =
            DependencyProperty.Register(nameof(Seg2Value), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).OnValueChanged()));
        public double Seg2Value { get => (double)GetValue(Seg2ValueProperty); set => SetValue(Seg2ValueProperty, value); }

        public static readonly DependencyProperty Seg3ValueProperty =
            DependencyProperty.Register(nameof(Seg3Value), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).OnValueChanged()));
        public double Seg3Value { get => (double)GetValue(Seg3ValueProperty); set => SetValue(Seg3ValueProperty, value); }

        private void OnValueChanged()
        {
            Recompute();
            ValuesChanged?.Invoke(this, EventArgs.Empty);
        }

        // -------- Appearance --------

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(MozaSegmentedBarEditor),
                new PropertyMetadata(null));
        public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

        /// <summary>Horizontal inset (px) on each end of the plot, room for divider handles at the extremes.</summary>
        public static readonly DependencyProperty EdgePadProperty =
            DependencyProperty.Register(nameof(EdgePad), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).Recompute()));
        public double EdgePad { get => (double)GetValue(EdgePadProperty); set => SetValue(EdgePadProperty, value); }

        /// <summary>Vertical inset (px) above the plot, room for the divider handle row + 100% bar.</summary>
        public static readonly DependencyProperty TopPadProperty =
            DependencyProperty.Register(nameof(TopPad), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).Recompute()));
        public double TopPad { get => (double)GetValue(TopPadProperty); set => SetValue(TopPadProperty, value); }

        /// <summary>Vertical inset (px) below the plot, room for the 0%/100% X-axis labels.</summary>
        public static readonly DependencyProperty BottomPadProperty =
            DependencyProperty.Register(nameof(BottomPad), typeof(double), typeof(MozaSegmentedBarEditor),
                new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaSegmentedBarEditor)d).Recompute()));
        public double BottomPad { get => (double)GetValue(BottomPadProperty); set => SetValue(BottomPadProperty, value); }

        /// <summary>Divider handle hit-test radius (px) — a click within this X distance of a divider line grabs it instead of the segment underneath.</summary>
        public static readonly DependencyProperty DividerHitRadiusProperty =
            DependencyProperty.Register(nameof(DividerHitRadius), typeof(double), typeof(MozaSegmentedBarEditor),
                new PropertyMetadata(12.0));
        public double DividerHitRadius { get => (double)GetValue(DividerHitRadiusProperty); set => SetValue(DividerHitRadiusProperty, value); }

        // -------- Read-only geometry / labels surfaced to the template --------

        private static readonly DependencyPropertyKey Seg1RectKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg1Rect), typeof(Geometry), typeof(MozaSegmentedBarEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty Seg1RectProperty = Seg1RectKey.DependencyProperty;
        public Geometry? Seg1Rect => (Geometry?)GetValue(Seg1RectProperty);

        private static readonly DependencyPropertyKey Seg2RectKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg2Rect), typeof(Geometry), typeof(MozaSegmentedBarEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty Seg2RectProperty = Seg2RectKey.DependencyProperty;
        public Geometry? Seg2Rect => (Geometry?)GetValue(Seg2RectProperty);

        private static readonly DependencyPropertyKey Seg3RectKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg3Rect), typeof(Geometry), typeof(MozaSegmentedBarEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty Seg3RectProperty = Seg3RectKey.DependencyProperty;
        public Geometry? Seg3Rect => (Geometry?)GetValue(Seg3RectProperty);

        private static readonly DependencyPropertyKey PlotBackgroundRectKey =
            DependencyProperty.RegisterReadOnly(nameof(PlotBackgroundRect), typeof(Geometry), typeof(MozaSegmentedBarEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty PlotBackgroundRectProperty = PlotBackgroundRectKey.DependencyProperty;
        public Geometry? PlotBackgroundRect => (Geometry?)GetValue(PlotBackgroundRectProperty);

        /// <summary>Smoothed line tracing the three segments' current values —
        /// flat across the middle of each segment's travel range, easing
        /// through a short Catmull-Rom-style curve around each divider
        /// instead of jumping vertically — so the damping profile reads as
        /// one continuous shape instead of three disconnected bars. See
        /// <see cref="AddSmoothPolyline"/> (same 1/6-tangent Bezier
        /// conversion <c>MozaCurveEditor</c> uses for its own curves).</summary>
        private static readonly DependencyPropertyKey StepLineGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(StepLineGeometry), typeof(Geometry), typeof(MozaSegmentedBarEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty StepLineGeometryProperty = StepLineGeometryKey.DependencyProperty;
        public Geometry? StepLineGeometry => (Geometry?)GetValue(StepLineGeometryProperty);

        private static readonly DependencyPropertyKey Divider1XKey =
            DependencyProperty.RegisterReadOnly(nameof(Divider1X), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Divider1XProperty = Divider1XKey.DependencyProperty;
        public double Divider1X => (double)GetValue(Divider1XProperty);

        private static readonly DependencyPropertyKey Divider2XKey =
            DependencyProperty.RegisterReadOnly(nameof(Divider2X), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Divider2XProperty = Divider2XKey.DependencyProperty;
        public double Divider2X => (double)GetValue(Divider2XProperty);

        private static readonly DependencyPropertyKey DividerTopKey =
            DependencyProperty.RegisterReadOnly(nameof(DividerTop), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty DividerTopProperty = DividerTopKey.DependencyProperty;
        public double DividerTop => (double)GetValue(DividerTopProperty);

        private static readonly DependencyPropertyKey DividerBottomKey =
            DependencyProperty.RegisterReadOnly(nameof(DividerBottom), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty DividerBottomProperty = DividerBottomKey.DependencyProperty;
        public double DividerBottom => (double)GetValue(DividerBottomProperty);

        private static readonly DependencyPropertyKey Divider1LabelKey =
            DependencyProperty.RegisterReadOnly(nameof(Divider1Label), typeof(string), typeof(MozaSegmentedBarEditor), new PropertyMetadata(""));
        public static readonly DependencyProperty Divider1LabelProperty = Divider1LabelKey.DependencyProperty;
        public string Divider1Label => (string)GetValue(Divider1LabelProperty);

        private static readonly DependencyPropertyKey Divider2LabelKey =
            DependencyProperty.RegisterReadOnly(nameof(Divider2Label), typeof(string), typeof(MozaSegmentedBarEditor), new PropertyMetadata(""));
        public static readonly DependencyProperty Divider2LabelProperty = Divider2LabelKey.DependencyProperty;
        public string Divider2Label => (string)GetValue(Divider2LabelProperty);

        private static readonly DependencyPropertyKey Seg1LabelKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg1Label), typeof(string), typeof(MozaSegmentedBarEditor), new PropertyMetadata(""));
        public static readonly DependencyProperty Seg1LabelProperty = Seg1LabelKey.DependencyProperty;
        public string Seg1Label => (string)GetValue(Seg1LabelProperty);

        private static readonly DependencyPropertyKey Seg2LabelKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg2Label), typeof(string), typeof(MozaSegmentedBarEditor), new PropertyMetadata(""));
        public static readonly DependencyProperty Seg2LabelProperty = Seg2LabelKey.DependencyProperty;
        public string Seg2Label => (string)GetValue(Seg2LabelProperty);

        private static readonly DependencyPropertyKey Seg3LabelKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg3Label), typeof(string), typeof(MozaSegmentedBarEditor), new PropertyMetadata(""));
        public static readonly DependencyProperty Seg3LabelProperty = Seg3LabelKey.DependencyProperty;
        public string Seg3Label => (string)GetValue(Seg3LabelProperty);

        // Label/handle sizes — fixed so their Canvas.Left/Top can be pre-
        // computed as already-centered top-left positions (same technique
        // MozaRangeSlider uses for its thumbs: LowThumbX = centerX - half),
        // rather than relying on a WPF RenderTransform to center a
        // variable-width TextBlock after the fact.
        private const double HandleWidth = 30, HandleHeight = 20;
        private const double LabelWidth = 34, LabelHeight = 18;

        private static readonly DependencyPropertyKey Divider1HandleLeftKey =
            DependencyProperty.RegisterReadOnly(nameof(Divider1HandleLeft), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Divider1HandleLeftProperty = Divider1HandleLeftKey.DependencyProperty;
        public double Divider1HandleLeft => (double)GetValue(Divider1HandleLeftProperty);

        private static readonly DependencyPropertyKey Divider2HandleLeftKey =
            DependencyProperty.RegisterReadOnly(nameof(Divider2HandleLeft), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Divider2HandleLeftProperty = Divider2HandleLeftKey.DependencyProperty;
        public double Divider2HandleLeft => (double)GetValue(Divider2HandleLeftProperty);

        private static readonly DependencyPropertyKey Seg1LabelLeftKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg1LabelLeft), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Seg1LabelLeftProperty = Seg1LabelLeftKey.DependencyProperty;
        public double Seg1LabelLeft => (double)GetValue(Seg1LabelLeftProperty);

        private static readonly DependencyPropertyKey Seg2LabelLeftKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg2LabelLeft), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Seg2LabelLeftProperty = Seg2LabelLeftKey.DependencyProperty;
        public double Seg2LabelLeft => (double)GetValue(Seg2LabelLeftProperty);

        private static readonly DependencyPropertyKey Seg3LabelLeftKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg3LabelLeft), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Seg3LabelLeftProperty = Seg3LabelLeftKey.DependencyProperty;
        public double Seg3LabelLeft => (double)GetValue(Seg3LabelLeftProperty);

        // Each label floats just above its own bar's current height (like a
        // tooltip pinned to the bar top), clamped so it never rises above
        // the plot's own top inset.
        private static readonly DependencyPropertyKey Seg1LabelTopKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg1LabelTop), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Seg1LabelTopProperty = Seg1LabelTopKey.DependencyProperty;
        public double Seg1LabelTop => (double)GetValue(Seg1LabelTopProperty);

        private static readonly DependencyPropertyKey Seg2LabelTopKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg2LabelTop), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Seg2LabelTopProperty = Seg2LabelTopKey.DependencyProperty;
        public double Seg2LabelTop => (double)GetValue(Seg2LabelTopProperty);

        private static readonly DependencyPropertyKey Seg3LabelTopKey =
            DependencyProperty.RegisterReadOnly(nameof(Seg3LabelTop), typeof(double), typeof(MozaSegmentedBarEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty Seg3LabelTopProperty = Seg3LabelTopKey.DependencyProperty;
        public double Seg3LabelTop => (double)GetValue(Seg3LabelTopProperty);

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            HookCanvas();
            Recompute();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Recompute();
        }

        // -------- Drag state --------
        // -1 = none, 0 = Divider1, 1 = Divider2, 2/3/4 = Segment 1/2/3.
        private int _dragTarget = -1;
        private Canvas? _canvas;

        private void HookCanvas()
        {
            _canvas = GetTemplateChild("PART_Canvas") as Canvas;
            if (_canvas != null)
            {
                _canvas.MouseLeftButtonDown += OnMouseDown;
                _canvas.MouseMove += OnMouseMove;
                _canvas.MouseLeftButtonUp += OnMouseUp;
                _canvas.LostMouseCapture += (_, __) => _dragTarget = -1;
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_canvas == null) return;
            var p = e.GetPosition(_canvas);

            double d1x = Divider1X, d2x = Divider2X;
            if (Math.Abs(p.X - d1x) <= DividerHitRadius)
            {
                _dragTarget = 0;
                _canvas.CaptureMouse();
                ApplyDividerDrag(0, p.X);
                e.Handled = true;
                return;
            }
            if (Math.Abs(p.X - d2x) <= DividerHitRadius)
            {
                _dragTarget = 1;
                _canvas.CaptureMouse();
                ApplyDividerDrag(1, p.X);
                e.Handled = true;
                return;
            }

            double plotLeft = EdgePad, plotRight = Math.Max(plotLeft, ActualWidth - EdgePad);
            double plotTop = TopPad, plotBottom = Math.Max(plotTop, ActualHeight - BottomPad);
            if (p.X < plotLeft || p.X > plotRight || p.Y < plotTop || p.Y > plotBottom) return;

            int seg = p.X < d1x ? 2 : (p.X < d2x ? 3 : 4);
            // Segments only respond once an actual drag starts (no jump on a
            // plain click) — capture the mouse here but don't apply a value
            // until OnMouseMove sees real movement.
            _dragTarget = seg;
            _canvas.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragTarget < 0 || _canvas == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragTarget = -1; _canvas.ReleaseMouseCapture(); return; }
            var p = e.GetPosition(_canvas);
            if (_dragTarget <= 1) ApplyDividerDrag(_dragTarget, p.X);
            else ApplySegmentDrag(_dragTarget - 2, p.Y);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_canvas != null && _canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
            _dragTarget = -1;
        }

        private void ApplyDividerDrag(int which, double x)
        {
            double plotW = Math.Max(1, ActualWidth - EdgePad - EdgePad);
            double frac = (x - EdgePad) / plotW;
            frac = Math.Max(0, Math.Min(1, frac));
            double val = frac * 100.0;

            if (which == 0)
            {
                double lo = Divider1Min;
                double hi = Math.Min(Divider1Max, Divider2 - MinGap);
                if (hi < lo) hi = lo;
                Divider1 = Math.Round(Math.Max(lo, Math.Min(hi, val)), 0);
            }
            else
            {
                double lo = Math.Max(Divider2Min, Divider1 + MinGap);
                double hi = Divider2Max;
                if (hi < lo) hi = lo;
                Divider2 = Math.Round(Math.Max(lo, Math.Min(hi, val)), 0);
            }
        }

        private void ApplySegmentDrag(int segIndex, double y)
        {
            double plotH = Math.Max(1, ActualHeight - TopPad - BottomPad);
            double plotBottom = TopPad + plotH;
            double frac = (plotBottom - y) / plotH;
            frac = Math.Max(0, Math.Min(1, frac));
            double val = Math.Round(frac * 100.0, 0);

            switch (segIndex)
            {
                case 0: Seg1Value = val; break;
                case 1: Seg2Value = val; break;
                case 2: Seg3Value = val; break;
            }
        }

        private void Recompute()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double plotW = Math.Max(1, w - EdgePad - EdgePad);
            double plotH = Math.Max(1, h - TopPad - BottomPad);
            double plotBottom = TopPad + plotH;

            double d1 = Math.Max(Divider1Min, Math.Min(Divider1Max, Divider1));
            double d2 = Math.Max(Divider2Min, Math.Min(Divider2Max, Divider2));
            double d1x = EdgePad + d1 / 100.0 * plotW;
            double d2x = EdgePad + d2 / 100.0 * plotW;

            SetValue(Divider1XKey, d1x);
            SetValue(Divider2XKey, d2x);
            double handleTop = TopPad - HandleHeight - 2;
            SetValue(DividerTopKey, handleTop);
            SetValue(DividerBottomKey, plotBottom);
            SetValue(Divider1LabelKey, Math.Round(d1) + "%");
            SetValue(Divider2LabelKey, Math.Round(d2) + "%");
            SetValue(Divider1HandleLeftKey, d1x - HandleWidth / 2.0);
            SetValue(Divider2HandleLeftKey, d2x - HandleWidth / 2.0);

            double YOf(double pct)
            {
                double clamped = Math.Max(0, Math.Min(100, pct));
                return plotBottom - clamped / 100.0 * plotH;
            }

            double s1v = Math.Max(0, Math.Min(100, Seg1Value));
            double s2v = Math.Max(0, Math.Min(100, Seg2Value));
            double s3v = Math.Max(0, Math.Min(100, Seg3Value));

            var seg1 = new RectangleGeometry(new Rect(EdgePad, YOf(s1v), Math.Max(0, d1x - EdgePad), Math.Max(0, plotBottom - YOf(s1v))));
            var seg2 = new RectangleGeometry(new Rect(d1x, YOf(s2v), Math.Max(0, d2x - d1x), Math.Max(0, plotBottom - YOf(s2v))));
            var seg3 = new RectangleGeometry(new Rect(d2x, YOf(s3v), Math.Max(0, EdgePad + plotW - d2x), Math.Max(0, plotBottom - YOf(s3v))));
            seg1.Freeze(); seg2.Freeze(); seg3.Freeze();
            SetValue(Seg1RectKey, seg1);
            SetValue(Seg2RectKey, seg2);
            SetValue(Seg3RectKey, seg3);

            var bg = new RectangleGeometry(new Rect(EdgePad, TopPad, plotW, plotH));
            bg.Freeze();
            SetValue(PlotBackgroundRectKey, bg);

            // Smoothed line ON TOP of the bars, at each segment's own height —
            // flat across the middle of its travel range, easing through a
            // short, tight curve right at each divider instead of jumping
            // vertically — the same shape the three bars already imply, just
            // traced as one continuous line so the overall profile is easier
            // to read at a glance. Sized to match Pit House's own rendering
            // (a quick S right at the divider, flat everywhere else): the
            // half-width is ~1/4 of the SMALLEST segment's width, measured
            // against a real Pit House screenshot's proportions (its
            // transition-to-segment-width ratio came out close to that,
            // vs. the previous /2.2 divisor here which was visibly wider/
            // more "curvy" than the reference — a long, gradual bow reaching
            // deep into each segment instead of a quick kink at the
            // divider). The 60px ceiling is just a backstop for an unusually
            // wide segment, not the normal-case constraint; the /4.0 term
            // does the real work and scales with the control's actual
            // rendered size. Still shrinks for narrow segments/gaps so the
            // six control points below can never cross each other or the
            // plot edges.
            double transitionHalfWidth = Math.Max(2.0, Math.Min(60.0,
                Math.Min(d1x - EdgePad, Math.Min(d2x - d1x, EdgePad + plotW - d2x)) / 4.0));
            var stepPts = new[]
            {
                new Point(EdgePad, YOf(s1v)),
                new Point(d1x - transitionHalfWidth, YOf(s1v)),
                new Point(d1x + transitionHalfWidth, YOf(s2v)),
                new Point(d2x - transitionHalfWidth, YOf(s2v)),
                new Point(d2x + transitionHalfWidth, YOf(s3v)),
                new Point(EdgePad + plotW, YOf(s3v)),
            };
            var stepFig = new PathFigure { StartPoint = stepPts[0], IsClosed = false, IsFilled = false };
            AddSmoothPolyline(stepFig, stepPts);
            var stepGeom = new PathGeometry();
            stepGeom.Figures.Add(stepFig);
            stepGeom.Freeze();
            SetValue(StepLineGeometryKey, stepGeom);

            string fmt = "F0";
            SetValue(Seg1LabelKey, s1v.ToString(fmt, CultureInfo.InvariantCulture) + "%");
            SetValue(Seg2LabelKey, s2v.ToString(fmt, CultureInfo.InvariantCulture) + "%");
            SetValue(Seg3LabelKey, s3v.ToString(fmt, CultureInfo.InvariantCulture) + "%");

            double seg1CenterX = (EdgePad + d1x) / 2.0;
            double seg2CenterX = (d1x + d2x) / 2.0;
            double seg3CenterX = (d2x + EdgePad + plotW) / 2.0;
            SetValue(Seg1LabelLeftKey, seg1CenterX - LabelWidth / 2.0);
            SetValue(Seg2LabelLeftKey, seg2CenterX - LabelWidth / 2.0);
            SetValue(Seg3LabelLeftKey, seg3CenterX - LabelWidth / 2.0);

            double LabelTopFor(double barTopY) => Math.Max(TopPad, barTopY - LabelHeight - 4);
            SetValue(Seg1LabelTopKey, LabelTopFor(YOf(s1v)));
            SetValue(Seg2LabelTopKey, LabelTopFor(YOf(s2v)));
            SetValue(Seg3LabelTopKey, LabelTopFor(YOf(s3v)));
        }

        /// <summary>
        /// Append a smooth Catmull-Rom-style curve through <paramref name="pts"/>
        /// to <paramref name="fig"/> as a chain of cubic Bezier segments — same
        /// 1/6-tangent conversion <c>MozaCurveEditor.Recompute</c> uses for its
        /// own curves, so this reads as the same "smooth line" visual language
        /// elsewhere in the app. <paramref name="fig"/>.StartPoint must already
        /// be set to <c>pts[0]</c>. The first/last points are their own
        /// duplicated neighbour (rather than wrapping or extrapolating), so the
        /// curve starts/ends exactly AT <c>pts[0]</c>/<c>pts[^1]</c> with a
        /// sensible (non-overshooting) tangent instead of curving past them.
        /// </summary>
        private static void AddSmoothPolyline(PathFigure fig, Point[] pts)
        {
            int n = pts.Length;
            for (int i = 0; i < n - 1; i++)
            {
                Point p0 = i == 0 ? pts[0] : pts[i - 1];
                Point p1 = pts[i];
                Point p2 = pts[i + 1];
                Point p3 = (i + 2 < n) ? pts[i + 2] : pts[n - 1];
                Point c1, c2;
                if (p1.Y == p2.Y)
                {
                    // A flat run (both endpoints at the same height — the
                    // plateau segments in Recompute's 6-point layout) must
                    // stay flat. Reaching past it to p0/p3 at a DIFFERENT
                    // height (the neighbouring divider transition) leaks a
                    // phantom slope into this segment's own tangent,
                    // drawing a small dip/bump right at the plateau's edge
                    // instead of a straight line — exactly the artifact
                    // visible right before/after each divider. Force a
                    // zero-slope tangent instead; the transition segment on
                    // the other side of the divider still computes its own
                    // tangent correctly, since IT reaches back into a flat
                    // neighbour that's at the SAME height as its own near
                    // endpoint.
                    c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y);
                    c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y);
                }
                else
                {
                    c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                    c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                }
                fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
            }
        }
    }
}
