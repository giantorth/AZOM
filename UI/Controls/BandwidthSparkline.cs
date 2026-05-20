using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MozaControls
{
    /// <summary>
    /// Rolling-window sparkline: filled area + line stroke + trailing dot.
    /// Bound to an <see cref="IReadOnlyList{T}"/> or <see cref="INotifyCollectionChanged"/>
    /// source. Recomputes geometry on Samples-changed or resize.
    /// </summary>
    public class BandwidthSparkline : Control
    {
        static BandwidthSparkline()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(typeof(BandwidthSparkline)));
        }

        public static readonly DependencyProperty SamplesProperty =
            DependencyProperty.Register(nameof(Samples), typeof(System.Collections.IEnumerable),
                typeof(BandwidthSparkline),
                new PropertyMetadata(null, OnSamplesChanged));
        public System.Collections.IEnumerable? Samples
        {
            get => (System.Collections.IEnumerable?)GetValue(SamplesProperty);
            set => SetValue(SamplesProperty, value);
        }

        public static readonly DependencyProperty LineBrushProperty =
            DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((BandwidthSparkline)d).Recompute()));
        public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

        /// <summary>Fixed maximum used for vertical scaling. When > 0 the sparkline
        /// shows samples relative to this ceiling (e.g. the serial port's byte-
        /// rate cap), so quiet traffic reads as a small bar instead of being
        /// auto-stretched to fill the panel. When 0 (default) the sparkline
        /// auto-scales to the rolling-window peak.</summary>
        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((BandwidthSparkline)d).Recompute()));
        public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

        // Read-only surfaced state ------------------------------------------------

        private static readonly DependencyPropertyKey LineGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(LineGeometry), typeof(Geometry),
                typeof(BandwidthSparkline), new PropertyMetadata(null));
        public static readonly DependencyProperty LineGeometryProperty = LineGeometryKey.DependencyProperty;
        public Geometry? LineGeometry => (Geometry?)GetValue(LineGeometryProperty);

        private static readonly DependencyPropertyKey FillGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(FillGeometry), typeof(Geometry),
                typeof(BandwidthSparkline), new PropertyMetadata(null));
        public static readonly DependencyProperty FillGeometryProperty = FillGeometryKey.DependencyProperty;
        public Geometry? FillGeometry => (Geometry?)GetValue(FillGeometryProperty);

        private static readonly DependencyPropertyKey TipXKey =
            DependencyProperty.RegisterReadOnly(nameof(TipX), typeof(double),
                typeof(BandwidthSparkline), new PropertyMetadata(0.0));
        public static readonly DependencyProperty TipXProperty = TipXKey.DependencyProperty;
        public double TipX => (double)GetValue(TipXProperty);

        private static readonly DependencyPropertyKey TipYKey =
            DependencyProperty.RegisterReadOnly(nameof(TipY), typeof(double),
                typeof(BandwidthSparkline), new PropertyMetadata(0.0));
        public static readonly DependencyProperty TipYProperty = TipYKey.DependencyProperty;
        public double TipY => (double)GetValue(TipYProperty);

        // --------------------------------------------------------------------

        private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (BandwidthSparkline)d;
            if (e.OldValue is INotifyCollectionChanged oldNcc)
                oldNcc.CollectionChanged -= self.OnCollectionChanged;
            if (e.NewValue is INotifyCollectionChanged newNcc)
                newNcc.CollectionChanged += self.OnCollectionChanged;
            self.Recompute();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Recompute();

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Recompute();
        }

        private void Recompute()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0 || Samples == null)
            {
                SetValue(LineGeometryKey, null);
                SetValue(FillGeometryKey, null);
                return;
            }

            var values = new List<double>();
            foreach (var v in Samples)
            {
                if (v is IConvertible c)
                    values.Add(c.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (values.Count == 0)
            {
                SetValue(LineGeometryKey, null);
                SetValue(FillGeometryKey, null);
                return;
            }

            // Fixed-ceiling scaling lets the user read absolute saturation
            // (e.g. against the 115200-baud serial port's ~11.5 kB/s cap)
            // instead of seeing the line auto-stretched to fill the panel.
            double scaleRef = MaxValue > 0 ? MaxValue : values.Max();
            double max = Math.Max(1, scaleRef);
            double clip = max;  // any sample over the cap saturates at the top.
            int n = values.Count;
            double pad = 2;
            double plotW = w - pad * 2;
            double plotH = h - pad * 2;

            double Y(int i) => h - pad - Math.Min(values[i], clip) / max * plotH;

            var line = new PathFigure { StartPoint = new Point(pad, Y(0)), IsClosed = false, IsFilled = false };
            for (int i = 1; i < n; i++)
            {
                double x = pad + (n == 1 ? 0 : plotW * i / (n - 1));
                line.Segments.Add(new LineSegment(new Point(x, Y(i)), true));
            }
            var lineGeom = new PathGeometry();
            lineGeom.Figures.Add(line);
            lineGeom.Freeze();

            var fill = new PathFigure { StartPoint = new Point(pad, h - pad), IsClosed = true, IsFilled = true };
            for (int i = 0; i < n; i++)
            {
                double x = pad + (n == 1 ? 0 : plotW * i / (n - 1));
                fill.Segments.Add(new LineSegment(new Point(x, Y(i)), false));
            }
            fill.Segments.Add(new LineSegment(new Point(pad + plotW, h - pad), false));
            var fillGeom = new PathGeometry();
            fillGeom.Figures.Add(fill);
            fillGeom.Freeze();

            SetValue(LineGeometryKey, lineGeom);
            SetValue(FillGeometryKey, fillGeom);

            double tipX = pad + plotW;
            double tipY = Y(n - 1);
            SetValue(TipXKey, tipX - 3);
            SetValue(TipYKey, tipY - 3);
        }
    }
}
