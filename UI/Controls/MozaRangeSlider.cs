using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// Two-thumb range slider — drag independent Low/High handles along one
    /// horizontal track, each clamped against the other by [MinGap, MaxGap]
    /// so the two can never cross and the gap between them always stays in
    /// range. Used for the mBooster Pedal Feel "Start/End of Travel (mm)"
    /// control, the Pedals tab's per-pedal output range, and the LFE/S12
    /// frequency limits. See docs/protocol/devices/mbooster.md "Pedal Feel".
    /// </summary>
    public class MozaRangeSlider : Control
    {
        static MozaRangeSlider()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(typeof(MozaRangeSlider)));
        }

        /// <summary>
        /// Fires whenever <see cref="LowValue"/> or <see cref="HighValue"/>
        /// changes (drag or programmatic set) — since these are plain DPs
        /// with no built-in "changed" CLR event, callers that need to react
        /// (persist to settings, e.g.) subscribe to this instead of wiring
        /// up a proxy Slider the way <c>MozaCurveEditor</c>'s YN properties do.
        /// </summary>
        public event EventHandler? RangeChanged;

        // -------- Values (two-way bindable, like MozaCurveEditor's YN) --------

        public static readonly DependencyProperty LowValueProperty =
            DependencyProperty.Register(nameof(LowValue), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).OnRangeValueChanged()));
        public double LowValue { get => (double)GetValue(LowValueProperty); set => SetValue(LowValueProperty, value); }

        public static readonly DependencyProperty HighValueProperty =
            DependencyProperty.Register(nameof(HighValue), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(100.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).OnRangeValueChanged()));
        public double HighValue { get => (double)GetValue(HighValueProperty); set => SetValue(HighValueProperty, value); }

        private void OnRangeValueChanged()
        {
            Recompute();
            RangeChanged?.Invoke(this, EventArgs.Empty);
        }

        // -------- Configuration --------

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

        /// <summary>Minimum allowed HighValue-LowValue gap. The two thumbs can never get closer than this.</summary>
        public static readonly DependencyProperty MinGapProperty =
            DependencyProperty.Register(nameof(MinGap), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public double MinGap { get => (double)GetValue(MinGapProperty); set => SetValue(MinGapProperty, value); }

        /// <summary>Maximum allowed HighValue-LowValue gap. The two thumbs can never get farther apart than this.</summary>
        public static readonly DependencyProperty MaxGapProperty =
            DependencyProperty.Register(nameof(MaxGap), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public double MaxGap { get => (double)GetValue(MaxGapProperty); set => SetValue(MaxGapProperty, value); }

        /// <summary>Decimal places shown in thumb/endpoint labels (mm values default to 1).</summary>
        public static readonly DependencyProperty DecimalsProperty =
            DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(MozaRangeSlider),
                new PropertyMetadata(null));
        public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

        // -------- Layout metrics (DP-driven so a compact/thin skin can shrink
        // the node + insets; defaults reproduce the original EQ-style look). --------

        /// <summary>Horizontal inset (px) on each end of the track, room for the node at the extremes.</summary>
        public static readonly DependencyProperty EdgePadProperty =
            DependencyProperty.Register(nameof(EdgePad), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public double EdgePad { get => (double)GetValue(EdgePadProperty); set => SetValue(EdgePadProperty, value); }

        /// <summary>Thumb hit-node size (px), square. The visible thumb centers within it.</summary>
        public static readonly DependencyProperty NodeSizeProperty =
            DependencyProperty.Register(nameof(NodeSize), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(34.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public double NodeSize { get => (double)GetValue(NodeSizeProperty); set => SetValue(NodeSizeProperty, value); }

        /// <summary>Track/range bar thickness (px).</summary>
        public static readonly DependencyProperty TrackThicknessProperty =
            DependencyProperty.Register(nameof(TrackThickness), typeof(double), typeof(MozaRangeSlider),
                new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaRangeSlider)d).Recompute()));
        public double TrackThickness { get => (double)GetValue(TrackThicknessProperty); set => SetValue(TrackThicknessProperty, value); }

        // -------- Read-only geometry / thumb positions surfaced to template --------

        private static readonly DependencyPropertyKey TrackGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(TrackGeometry), typeof(Geometry), typeof(MozaRangeSlider), new PropertyMetadata(null));
        public static readonly DependencyProperty TrackGeometryProperty = TrackGeometryKey.DependencyProperty;
        public Geometry? TrackGeometry => (Geometry?)GetValue(TrackGeometryProperty);

        private static readonly DependencyPropertyKey RangeGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(RangeGeometry), typeof(Geometry), typeof(MozaRangeSlider), new PropertyMetadata(null));
        public static readonly DependencyProperty RangeGeometryProperty = RangeGeometryKey.DependencyProperty;
        public Geometry? RangeGeometry => (Geometry?)GetValue(RangeGeometryProperty);

        private static readonly DependencyPropertyKey LowThumbXKey =
            DependencyProperty.RegisterReadOnly(nameof(LowThumbX), typeof(double), typeof(MozaRangeSlider), new PropertyMetadata(0.0));
        public static readonly DependencyProperty LowThumbXProperty = LowThumbXKey.DependencyProperty;
        public double LowThumbX => (double)GetValue(LowThumbXProperty);

        private static readonly DependencyPropertyKey HighThumbXKey =
            DependencyProperty.RegisterReadOnly(nameof(HighThumbX), typeof(double), typeof(MozaRangeSlider), new PropertyMetadata(0.0));
        public static readonly DependencyProperty HighThumbXProperty = HighThumbXKey.DependencyProperty;
        public double HighThumbX => (double)GetValue(HighThumbXProperty);

        private static readonly DependencyPropertyKey ThumbTopKey =
            DependencyProperty.RegisterReadOnly(nameof(ThumbTop), typeof(double), typeof(MozaRangeSlider), new PropertyMetadata(0.0));
        public static readonly DependencyProperty ThumbTopProperty = ThumbTopKey.DependencyProperty;
        public double ThumbTop => (double)GetValue(ThumbTopProperty);

        private static readonly DependencyPropertyKey LowLabelKey =
            DependencyProperty.RegisterReadOnly(nameof(LowLabel), typeof(string), typeof(MozaRangeSlider), new PropertyMetadata(""));
        public static readonly DependencyProperty LowLabelProperty = LowLabelKey.DependencyProperty;
        public string LowLabel => (string)GetValue(LowLabelProperty);

        private static readonly DependencyPropertyKey HighLabelKey =
            DependencyProperty.RegisterReadOnly(nameof(HighLabel), typeof(string), typeof(MozaRangeSlider), new PropertyMetadata(""));
        public static readonly DependencyProperty HighLabelProperty = HighLabelKey.DependencyProperty;
        public string HighLabel => (string)GetValue(HighLabelProperty);

        private static readonly DependencyPropertyKey MinLabelKey =
            DependencyProperty.RegisterReadOnly(nameof(MinLabel), typeof(string), typeof(MozaRangeSlider), new PropertyMetadata(""));
        public static readonly DependencyProperty MinLabelProperty = MinLabelKey.DependencyProperty;
        public string MinLabel => (string)GetValue(MinLabelProperty);

        private static readonly DependencyPropertyKey MaxLabelKey =
            DependencyProperty.RegisterReadOnly(nameof(MaxLabel), typeof(string), typeof(MozaRangeSlider), new PropertyMetadata(""));
        public static readonly DependencyProperty MaxLabelProperty = MaxLabelKey.DependencyProperty;
        public string MaxLabel => (string)GetValue(MaxLabelProperty);

        private static readonly DependencyPropertyKey EndLabelTopKey =
            DependencyProperty.RegisterReadOnly(nameof(EndLabelTop), typeof(double), typeof(MozaRangeSlider), new PropertyMetadata(0.0));
        public static readonly DependencyProperty EndLabelTopProperty = EndLabelTopKey.DependencyProperty;
        public double EndLabelTop => (double)GetValue(EndLabelTopProperty);

        // -------- Layout constants --------
        private const double EndLabelGap = 10; // px below track to endpoint labels

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
        private int _dragThumb = -1; // 0 = low, 1 = high, -1 = none
        private Canvas? _canvas;

        private void HookCanvas()
        {
            _canvas = GetTemplateChild("PART_Canvas") as Canvas;
            if (_canvas != null)
            {
                _canvas.MouseLeftButtonDown += OnMouseDown;
                _canvas.MouseMove += OnMouseMove;
                _canvas.MouseLeftButtonUp += OnMouseUp;
                _canvas.LostMouseCapture += (_, __) => _dragThumb = -1;
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_canvas == null) return;
            var p = e.GetPosition(_canvas);
            _dragThumb = FindClosestThumb(p);
            if (_dragThumb >= 0)
            {
                _canvas.CaptureMouse();
                ApplyDrag(p);
                e.Handled = true;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragThumb < 0 || _canvas == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragThumb = -1; _canvas.ReleaseMouseCapture(); return; }
            ApplyDrag(e.GetPosition(_canvas));
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_canvas != null && _canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
            _dragThumb = -1;
        }

        private int FindClosestThumb(Point p)
        {
            double nodeHalf = NodeSize / 2.0;
            double r = nodeHalf + 6.0;
            double r2 = r * r;
            double lowCx = LowThumbX + nodeHalf, lowCy = ThumbTop + nodeHalf;
            double highCx = HighThumbX + nodeHalf, highCy = ThumbTop + nodeHalf;
            double dLow = (p.X - lowCx) * (p.X - lowCx) + (p.Y - lowCy) * (p.Y - lowCy);
            double dHigh = (p.X - highCx) * (p.X - highCx) + (p.Y - highCy) * (p.Y - highCy);
            // Ties (thumbs overlapping at MinGap=0) resolve to whichever is closer;
            // exact ties favour the high thumb so it stays reachable when stacked.
            if (dLow < dHigh) return dLow <= r2 ? 0 : -1;
            return dHigh <= r2 ? 1 : -1;
        }

        private void ApplyDrag(Point p)
        {
            double w = _canvas?.ActualWidth ?? ActualWidth;
            double plotW = Math.Max(1, w - EdgePad - EdgePad);
            double frac = (p.X - EdgePad) / plotW;
            if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            double val = Minimum + frac * (Maximum - Minimum);
            double decimals = Math.Max(0, Decimals);

            if (_dragThumb == 0)
            {
                double lo = Math.Max(Minimum, HighValue - MaxGap);
                double hi = HighValue - MinGap;
                if (hi < lo) hi = lo;
                val = Math.Max(lo, Math.Min(hi, val));
                LowValue = Math.Round(val, (int)decimals);
            }
            else if (_dragThumb == 1)
            {
                double lo = LowValue + MinGap;
                double hi = Math.Min(Maximum, LowValue + MaxGap);
                if (hi < lo) hi = lo;
                val = Math.Max(lo, Math.Min(hi, val));
                HighValue = Math.Round(val, (int)decimals);
            }
        }

        private void Recompute()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double plotW = Math.Max(1, w - EdgePad - EdgePad);
            double range = Math.Max(1e-6, Maximum - Minimum);
            double nodeHalf = NodeSize / 2.0;
            double thumbCy = nodeHalf + 4;

            double lowClamped = Math.Max(Minimum, Math.Min(Maximum, LowValue));
            double highClamped = Math.Max(Minimum, Math.Min(Maximum, HighValue));

            double lowFrac = (lowClamped - Minimum) / range;
            double highFrac = (highClamped - Minimum) / range;
            double lowX = EdgePad + lowFrac * plotW;
            double highX = EdgePad + highFrac * plotW;

            SetValue(LowThumbXKey, lowX - nodeHalf);
            SetValue(HighThumbXKey, highX - nodeHalf);
            SetValue(ThumbTopKey, thumbCy - nodeHalf);

            string fmt = "F" + Math.Max(0, Decimals);
            string unit = Unit ?? "";
            SetValue(LowLabelKey, lowClamped.ToString(fmt, CultureInfo.InvariantCulture));
            SetValue(HighLabelKey, highClamped.ToString(fmt, CultureInfo.InvariantCulture));
            SetValue(MinLabelKey, Minimum.ToString(fmt, CultureInfo.InvariantCulture) + unit);
            SetValue(MaxLabelKey, Maximum.ToString(fmt, CultureInfo.InvariantCulture) + unit);
            SetValue(EndLabelTopKey, thumbCy + nodeHalf + EndLabelGap);

            // Full track (dim) spans the whole Minimum..Maximum span.
            var track = new RectangleGeometry(
                new Rect(EdgePad, thumbCy - TrackThickness / 2.0, plotW, TrackThickness),
                TrackThickness / 2.0, TrackThickness / 2.0);
            track.Freeze();
            SetValue(TrackGeometryKey, track);

            // Highlighted sub-range between the two thumbs (accent-coloured).
            double rangeLeft = Math.Min(lowX, highX);
            double rangeWidth = Math.Max(0, Math.Abs(highX - lowX));
            var activeRange = new RectangleGeometry(
                new Rect(rangeLeft, thumbCy - TrackThickness / 2.0, rangeWidth, TrackThickness),
                TrackThickness / 2.0, TrackThickness / 2.0);
            activeRange.Freeze();
            SetValue(RangeGeometryKey, activeRange);
        }
    }
}
