using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MozaControls
{
    /// <summary>
    /// Dual-series rolling sparkline (inbound + outbound traffic). Both series
    /// share a single Y scale so the user can read relative weight at a glance.
    /// Each series renders as gradient fill + line stroke + trailing tip dot.
    /// Bind <see cref="InSamples"/> / <see cref="OutSamples"/> to
    /// <see cref="System.Collections.IEnumerable"/>s (typically
    /// ObservableCollection&lt;double&gt; that the timer tick mutates).
    /// </summary>
    public class BandwidthSparkline : Control
    {
        static BandwidthSparkline()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(typeof(BandwidthSparkline)));
        }

        // -------- Samples DPs --------

        public static readonly DependencyProperty InSamplesProperty =
            DependencyProperty.Register(nameof(InSamples), typeof(System.Collections.IEnumerable),
                typeof(BandwidthSparkline), new PropertyMetadata(null, OnSamplesChanged));
        public System.Collections.IEnumerable? InSamples
        {
            get => (System.Collections.IEnumerable?)GetValue(InSamplesProperty);
            set => SetValue(InSamplesProperty, value);
        }

        public static readonly DependencyProperty OutSamplesProperty =
            DependencyProperty.Register(nameof(OutSamples), typeof(System.Collections.IEnumerable),
                typeof(BandwidthSparkline), new PropertyMetadata(null, OnSamplesChanged));
        public System.Collections.IEnumerable? OutSamples
        {
            get => (System.Collections.IEnumerable?)GetValue(OutSamplesProperty);
            set => SetValue(OutSamplesProperty, value);
        }

        /// <summary>Optional third series (e.g. the mBooster pedal-trace graph's
        /// Clutch line) — unused/null for the original two-series bandwidth
        /// chart. Shares the same Y scale as In/Out.</summary>
        public static readonly DependencyProperty ThirdSamplesProperty =
            DependencyProperty.Register(nameof(ThirdSamples), typeof(System.Collections.IEnumerable),
                typeof(BandwidthSparkline), new PropertyMetadata(null, OnSamplesChanged));
        public System.Collections.IEnumerable? ThirdSamples
        {
            get => (System.Collections.IEnumerable?)GetValue(ThirdSamplesProperty);
            set => SetValue(ThirdSamplesProperty, value);
        }

        // -------- Line + fill brush DPs --------

        public static readonly DependencyProperty InBrushProperty =
            DependencyProperty.Register(nameof(InBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush InBrush { get => (Brush)GetValue(InBrushProperty); set => SetValue(InBrushProperty, value); }

        public static readonly DependencyProperty OutBrushProperty =
            DependencyProperty.Register(nameof(OutBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush OutBrush { get => (Brush)GetValue(OutBrushProperty); set => SetValue(OutBrushProperty, value); }

        public static readonly DependencyProperty InFillBrushProperty =
            DependencyProperty.Register(nameof(InFillBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(null));
        public Brush? InFillBrush { get => (Brush?)GetValue(InFillBrushProperty); set => SetValue(InFillBrushProperty, value); }

        public static readonly DependencyProperty OutFillBrushProperty =
            DependencyProperty.Register(nameof(OutFillBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(null));
        public Brush? OutFillBrush { get => (Brush?)GetValue(OutFillBrushProperty); set => SetValue(OutFillBrushProperty, value); }

        public static readonly DependencyProperty ThirdBrushProperty =
            DependencyProperty.Register(nameof(ThirdBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(Brushes.Blue, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush ThirdBrush { get => (Brush)GetValue(ThirdBrushProperty); set => SetValue(ThirdBrushProperty, value); }

        public static readonly DependencyProperty ThirdFillBrushProperty =
            DependencyProperty.Register(nameof(ThirdFillBrush), typeof(Brush), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(null));
        public Brush? ThirdFillBrush { get => (Brush?)GetValue(ThirdFillBrushProperty); set => SetValue(ThirdFillBrushProperty, value); }

        /// <summary>Fixed maximum used for vertical scaling. When > 0 both series
        /// render relative to this ceiling (e.g. the serial port's byte-rate cap)
        /// so quiet traffic stays small instead of being auto-stretched. When 0
        /// the chart auto-scales to the rolling-window peak across both series.</summary>
        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((BandwidthSparkline)d).Recompute()));
        public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

        /// <summary>Opt-in dashed horizontal reference lines at 100/75/50/25 %
        /// of the plot height (see <see cref="GridGeometry"/>) — off by
        /// default so the original bandwidth chart (auto-scaled, no fixed
        /// percent semantics) is unaffected; the mBooster pedal-trace graph
        /// turns this on since its MaxValue is a fixed 100 (%).</summary>
        public static readonly DependencyProperty ShowGridlinesProperty =
            DependencyProperty.Register(nameof(ShowGridlines), typeof(bool), typeof(BandwidthSparkline),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((BandwidthSparkline)d).Recompute()));
        public bool ShowGridlines { get => (bool)GetValue(ShowGridlinesProperty); set => SetValue(ShowGridlinesProperty, value); }

        // -------- Read-only geometries + tip points --------

        private static DependencyPropertyKey RoG(string name) =>
            DependencyProperty.RegisterReadOnly(name, typeof(Geometry),
                typeof(BandwidthSparkline), new PropertyMetadata(null));
        private static DependencyPropertyKey RoD(string name) =>
            DependencyProperty.RegisterReadOnly(name, typeof(double),
                typeof(BandwidthSparkline), new PropertyMetadata(0.0));

        private static readonly DependencyPropertyKey InLineGeometryKey =  RoG("InLineGeometry");
        private static readonly DependencyPropertyKey InFillGeometryKey =  RoG("InFillGeometry");
        private static readonly DependencyPropertyKey OutLineGeometryKey = RoG("OutLineGeometry");
        private static readonly DependencyPropertyKey OutFillGeometryKey = RoG("OutFillGeometry");
        private static readonly DependencyPropertyKey ThirdLineGeometryKey = RoG("ThirdLineGeometry");
        private static readonly DependencyPropertyKey ThirdFillGeometryKey = RoG("ThirdFillGeometry");
        private static readonly DependencyPropertyKey GridGeometryKey = RoG("GridGeometry");
        private static readonly DependencyPropertyKey InTipXKey =  RoD("InTipX");
        private static readonly DependencyPropertyKey InTipYKey =  RoD("InTipY");
        private static readonly DependencyPropertyKey OutTipXKey = RoD("OutTipX");
        private static readonly DependencyPropertyKey OutTipYKey = RoD("OutTipY");
        private static readonly DependencyPropertyKey ThirdTipXKey = RoD("ThirdTipX");
        private static readonly DependencyPropertyKey ThirdTipYKey = RoD("ThirdTipY");

        public static readonly DependencyProperty InLineGeometryProperty =  InLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty InFillGeometryProperty =  InFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty OutLineGeometryProperty = OutLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty OutFillGeometryProperty = OutFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty ThirdLineGeometryProperty = ThirdLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty ThirdFillGeometryProperty = ThirdFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty GridGeometryProperty = GridGeometryKey.DependencyProperty;
        public static readonly DependencyProperty InTipXProperty =  InTipXKey.DependencyProperty;
        public static readonly DependencyProperty InTipYProperty =  InTipYKey.DependencyProperty;
        public static readonly DependencyProperty OutTipXProperty = OutTipXKey.DependencyProperty;
        public static readonly DependencyProperty OutTipYProperty = OutTipYKey.DependencyProperty;
        public static readonly DependencyProperty ThirdTipXProperty = ThirdTipXKey.DependencyProperty;
        public static readonly DependencyProperty ThirdTipYProperty = ThirdTipYKey.DependencyProperty;

        public Geometry? InLineGeometry  => (Geometry?)GetValue(InLineGeometryProperty);
        public Geometry? InFillGeometry  => (Geometry?)GetValue(InFillGeometryProperty);
        public Geometry? OutLineGeometry => (Geometry?)GetValue(OutLineGeometryProperty);
        public Geometry? OutFillGeometry => (Geometry?)GetValue(OutFillGeometryProperty);
        public Geometry? ThirdLineGeometry => (Geometry?)GetValue(ThirdLineGeometryProperty);
        public Geometry? ThirdFillGeometry => (Geometry?)GetValue(ThirdFillGeometryProperty);
        public Geometry? GridGeometry => (Geometry?)GetValue(GridGeometryProperty);
        public double InTipX  => (double)GetValue(InTipXProperty);
        public double InTipY  => (double)GetValue(InTipYProperty);
        public double OutTipX => (double)GetValue(OutTipXProperty);
        public double OutTipY => (double)GetValue(OutTipYProperty);
        public double ThirdTipX => (double)GetValue(ThirdTipXProperty);
        public double ThirdTipY => (double)GetValue(ThirdTipYProperty);

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

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRecompute();

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            ScheduleRecompute();
        }

        // Coalesce: the hosts push samples as Add + RemoveAt(0) on up to three
        // bound collections per frame (six change events at 30 Hz on the pedal
        // trace), and each one used to rebuild every series' geometry. One
        // render-priority pass per dispatcher frame rebuilds them once.
        private bool _recomputePending;

        public BandwidthSparkline()
        {
            // Hosts keep feeding samples while the graph is off-screen (another
            // tab, panel closed); Recompute skips then, so catch up when shown.
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) ScheduleRecompute(); };
        }

        private void ScheduleRecompute()
        {
            if (_recomputePending || !IsVisible) return;
            _recomputePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _recomputePending = false;
                Recompute();
            }));
        }

        // Reused sample buffers, one per series.
        private double[] _inBuf = new double[0];
        private double[] _outBuf = new double[0];
        private double[] _thirdBuf = new double[0];

        // Gridlines sit at these fractions of the plot height (i.e. 100/75/50/25
        // when MaxValue is a fixed 100) regardless of the data's own scale —
        // they're a fixed visual reference, not tied to the rolling window's
        // auto-scaled peak.
        private static readonly double[] GridlineFractions = { 1.0, 0.75, 0.5, 0.25 };

        // Gridlines depend only on size; rebuilt when it changes.
        private Geometry? _gridCache;
        private double _gridCacheW, _gridCacheH;

        private Geometry GridFor(double w, double h, double pad, double plotW, double plotH)
        {
            if (_gridCache != null && _gridCacheW == w && _gridCacheH == h) return _gridCache;
            var grid = new GeometryGroup();
            foreach (double frac in GridlineFractions)
            {
                double y = h - pad - frac * plotH;
                grid.Children.Add(new LineGeometry(new Point(pad, y), new Point(pad + plotW, y)));
            }
            grid.Freeze();
            _gridCache = grid;
            _gridCacheW = w;
            _gridCacheH = h;
            return grid;
        }

        private static double PeakOf(double[] vals, int n, double peak)
        {
            for (int i = 0; i < n; i++)
                if (vals[i] > peak) peak = vals[i];
            return peak;
        }

        private void Recompute()
        {
            if (!IsVisible) return;

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
            {
                Clear();
                return;
            }

            double pad = 2;
            double plotW = w - pad * 2;
            double plotH = h - pad * 2;

            SetValue(GridGeometryKey, ShowGridlines ? GridFor(w, h, pad, plotW, plotH) : null);

            int inN    = SparklineSeries.Read(InSamples, ref _inBuf);
            int outN   = SparklineSeries.Read(OutSamples, ref _outBuf);
            int thirdN = SparklineSeries.Read(ThirdSamples, ref _thirdBuf);
            if (inN == 0 && outN == 0 && thirdN == 0)
            {
                ClearSeries();
                return;
            }

            // Shared Y scale across all series so the user can compare them.
            // Fixed ceiling (MaxValue) wins when set, else auto-scale to the
            // combined window peak.
            double peak = 0;
            peak = PeakOf(_inBuf, inN, peak);
            peak = PeakOf(_outBuf, outN, peak);
            peak = PeakOf(_thirdBuf, thirdN, peak);
            double scaleRef = MaxValue > 0 ? MaxValue : peak;
            double max = Math.Max(1, scaleRef);
            double baseY = h - pad;
            double scale = plotH / max;
            double tipX = pad + plotW - 3;

            SparklineSeries.Build(_inBuf, inN, pad, plotW, baseY, double.NegativeInfinity, max, 0, scale,
                out var inLine, out var inFill, out double inLastY);
            SparklineSeries.Build(_outBuf, outN, pad, plotW, baseY, double.NegativeInfinity, max, 0, scale,
                out var outLine, out var outFill, out double outLastY);
            SparklineSeries.Build(_thirdBuf, thirdN, pad, plotW, baseY, double.NegativeInfinity, max, 0, scale,
                out var thirdLine, out var thirdFill, out double thirdLastY);
            double inTipX = inN > 0 ? tipX : 0, inTipY = inN > 0 ? inLastY - 3 : 0;
            double outTipX = outN > 0 ? tipX : 0, outTipY = outN > 0 ? outLastY - 3 : 0;
            double thirdTipX = thirdN > 0 ? tipX : 0, thirdTipY = thirdN > 0 ? thirdLastY - 3 : 0;

            SetValue(InLineGeometryKey,  inLine);
            SetValue(InFillGeometryKey,  inFill);
            SetValue(OutLineGeometryKey, outLine);
            SetValue(OutFillGeometryKey, outFill);
            SetValue(ThirdLineGeometryKey, thirdLine);
            SetValue(ThirdFillGeometryKey, thirdFill);
            SetValue(InTipXKey,  inTipX);  SetValue(InTipYKey,  inTipY);
            SetValue(OutTipXKey, outTipX); SetValue(OutTipYKey, outTipY);
            SetValue(ThirdTipXKey, thirdTipX); SetValue(ThirdTipYKey, thirdTipY);
        }

        private void ClearSeries()
        {
            SetValue(InLineGeometryKey,  null);
            SetValue(InFillGeometryKey,  null);
            SetValue(OutLineGeometryKey, null);
            SetValue(OutFillGeometryKey, null);
            SetValue(ThirdLineGeometryKey, null);
            SetValue(ThirdFillGeometryKey, null);
        }

        private void Clear()
        {
            ClearSeries();
            SetValue(GridGeometryKey, null);
        }
    }
}
