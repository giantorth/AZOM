using System;
using System.Collections;
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
    /// Three-trace rolling temperature graph styled like
    /// <see cref="BandwidthSparkline"/>. Renders MCU / MOSFET / MOTOR lines on
    /// a shared Y scale that auto-fits the current data window (with a small
    /// pad above/below and a minimum range so flat traces don't collapse to a
    /// single horizontal line). Each series also gets a gradient under-glow
    /// fill; the z-index of those fills is recomputed every tick so the
    /// hottest current sample sits on top of the cooler ones and the visual
    /// stacks cleanly from coldest at the back to hottest at the front.
    ///
    /// Bind each *Samples property to an IEnumerable&lt;double&gt; (typically
    /// an ObservableCollection&lt;double&gt; that the timer tick mutates).
    /// Geometry recomputes on Samples-changed or resize.
    /// </summary>
    public class TemperatureGraph : Control
    {
        static TemperatureGraph()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(typeof(TemperatureGraph)));
        }

        // -------- Samples DPs (one per series) --------

        public static readonly DependencyProperty McuSamplesProperty =
            DependencyProperty.Register(nameof(McuSamples), typeof(IEnumerable), typeof(TemperatureGraph),
                new PropertyMetadata(null, OnSamplesChanged));
        public IEnumerable? McuSamples
        {
            get => (IEnumerable?)GetValue(McuSamplesProperty);
            set => SetValue(McuSamplesProperty, value);
        }

        public static readonly DependencyProperty MosfetSamplesProperty =
            DependencyProperty.Register(nameof(MosfetSamples), typeof(IEnumerable), typeof(TemperatureGraph),
                new PropertyMetadata(null, OnSamplesChanged));
        public IEnumerable? MosfetSamples
        {
            get => (IEnumerable?)GetValue(MosfetSamplesProperty);
            set => SetValue(MosfetSamplesProperty, value);
        }

        public static readonly DependencyProperty MotorSamplesProperty =
            DependencyProperty.Register(nameof(MotorSamples), typeof(IEnumerable), typeof(TemperatureGraph),
                new PropertyMetadata(null, OnSamplesChanged));
        public IEnumerable? MotorSamples
        {
            get => (IEnumerable?)GetValue(MotorSamplesProperty);
            set => SetValue(MotorSamplesProperty, value);
        }

        // -------- Line color DPs --------

        public static readonly DependencyProperty McuBrushProperty =
            DependencyProperty.Register(nameof(McuBrush), typeof(Brush), typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(Brushes.Cyan));
        public Brush McuBrush { get => (Brush)GetValue(McuBrushProperty); set => SetValue(McuBrushProperty, value); }

        public static readonly DependencyProperty MosfetBrushProperty =
            DependencyProperty.Register(nameof(MosfetBrush), typeof(Brush), typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(Brushes.Orange));
        public Brush MosfetBrush { get => (Brush)GetValue(MosfetBrushProperty); set => SetValue(MosfetBrushProperty, value); }

        public static readonly DependencyProperty MotorBrushProperty =
            DependencyProperty.Register(nameof(MotorBrush), typeof(Brush), typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(Brushes.Tomato));
        public Brush MotorBrush { get => (Brush)GetValue(MotorBrushProperty); set => SetValue(MotorBrushProperty, value); }

        // -------- Fill (under-glow) brush DPs --------

        public static readonly DependencyProperty McuFillBrushProperty =
            DependencyProperty.Register(nameof(McuFillBrush), typeof(Brush), typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(null));
        public Brush? McuFillBrush { get => (Brush?)GetValue(McuFillBrushProperty); set => SetValue(McuFillBrushProperty, value); }

        public static readonly DependencyProperty MosfetFillBrushProperty =
            DependencyProperty.Register(nameof(MosfetFillBrush), typeof(Brush), typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(null));
        public Brush? MosfetFillBrush { get => (Brush?)GetValue(MosfetFillBrushProperty); set => SetValue(MosfetFillBrushProperty, value); }

        public static readonly DependencyProperty MotorFillBrushProperty =
            DependencyProperty.Register(nameof(MotorFillBrush), typeof(Brush), typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(null));
        public Brush? MotorFillBrush { get => (Brush?)GetValue(MotorFillBrushProperty); set => SetValue(MotorFillBrushProperty, value); }

        // -------- Auto-scale knobs --------

        /// <summary>Minimum Y-range the chart will collapse to even when all
        /// samples are clustered together (default 20). Without this a flat
        /// 25 °C reading would render as a razor-thin band; the floor keeps
        /// some headroom so individual movement is still visible.</summary>
        public static readonly DependencyProperty MinRangeProperty =
            DependencyProperty.Register(nameof(MinRange), typeof(double), typeof(TemperatureGraph),
                new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((TemperatureGraph)d).Recompute()));
        public double MinRange { get => (double)GetValue(MinRangeProperty); set => SetValue(MinRangeProperty, value); }

        // -------- Read-only geometries / Z-indices surfaced to template --------

        private static readonly DependencyPropertyKey McuLineGeometryKey =     RoG("McuLineGeometry");
        private static readonly DependencyPropertyKey MosfetLineGeometryKey =  RoG("MosfetLineGeometry");
        private static readonly DependencyPropertyKey MotorLineGeometryKey =   RoG("MotorLineGeometry");
        private static readonly DependencyPropertyKey McuFillGeometryKey =     RoG("McuFillGeometry");
        private static readonly DependencyPropertyKey MosfetFillGeometryKey =  RoG("MosfetFillGeometry");
        private static readonly DependencyPropertyKey MotorFillGeometryKey =   RoG("MotorFillGeometry");
        private static readonly DependencyPropertyKey GridGeometryKey =        RoG("GridGeometry");
        public static readonly DependencyProperty McuLineGeometryProperty =    McuLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty MosfetLineGeometryProperty = MosfetLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty MotorLineGeometryProperty =  MotorLineGeometryKey.DependencyProperty;
        public static readonly DependencyProperty McuFillGeometryProperty =    McuFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty MosfetFillGeometryProperty = MosfetFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty MotorFillGeometryProperty =  MotorFillGeometryKey.DependencyProperty;
        public static readonly DependencyProperty GridGeometryProperty =       GridGeometryKey.DependencyProperty;
        public Geometry? McuLineGeometry    => (Geometry?)GetValue(McuLineGeometryProperty);
        public Geometry? MosfetLineGeometry => (Geometry?)GetValue(MosfetLineGeometryProperty);
        public Geometry? MotorLineGeometry  => (Geometry?)GetValue(MotorLineGeometryProperty);
        public Geometry? McuFillGeometry    => (Geometry?)GetValue(McuFillGeometryProperty);
        public Geometry? MosfetFillGeometry => (Geometry?)GetValue(MosfetFillGeometryProperty);
        public Geometry? MotorFillGeometry  => (Geometry?)GetValue(MotorFillGeometryProperty);
        public Geometry? GridGeometry       => (Geometry?)GetValue(GridGeometryProperty);

        // Z-indices: hottest current sample → highest Z (rendered on top).
        // Used by the template via Panel.ZIndex.
        private static readonly DependencyPropertyKey McuZIndexKey =    RoI("McuZIndex");
        private static readonly DependencyPropertyKey MosfetZIndexKey = RoI("MosfetZIndex");
        private static readonly DependencyPropertyKey MotorZIndexKey =  RoI("MotorZIndex");
        public static readonly DependencyProperty McuZIndexProperty =    McuZIndexKey.DependencyProperty;
        public static readonly DependencyProperty MosfetZIndexProperty = MosfetZIndexKey.DependencyProperty;
        public static readonly DependencyProperty MotorZIndexProperty =  MotorZIndexKey.DependencyProperty;
        public int McuZIndex    => (int)GetValue(McuZIndexProperty);
        public int MosfetZIndex => (int)GetValue(MosfetZIndexProperty);
        public int MotorZIndex  => (int)GetValue(MotorZIndexProperty);

        // Trailing tip positions for each series.
        private static readonly DependencyPropertyKey McuTipXKey =    RoD("McuTipX");
        private static readonly DependencyPropertyKey McuTipYKey =    RoD("McuTipY");
        private static readonly DependencyPropertyKey MosfetTipXKey = RoD("MosfetTipX");
        private static readonly DependencyPropertyKey MosfetTipYKey = RoD("MosfetTipY");
        private static readonly DependencyPropertyKey MotorTipXKey =  RoD("MotorTipX");
        private static readonly DependencyPropertyKey MotorTipYKey =  RoD("MotorTipY");
        public static readonly DependencyProperty McuTipXProperty =    McuTipXKey.DependencyProperty;
        public static readonly DependencyProperty McuTipYProperty =    McuTipYKey.DependencyProperty;
        public static readonly DependencyProperty MosfetTipXProperty = MosfetTipXKey.DependencyProperty;
        public static readonly DependencyProperty MosfetTipYProperty = MosfetTipYKey.DependencyProperty;
        public static readonly DependencyProperty MotorTipXProperty =  MotorTipXKey.DependencyProperty;
        public static readonly DependencyProperty MotorTipYProperty =  MotorTipYKey.DependencyProperty;
        public double McuTipX => (double)GetValue(McuTipXProperty);
        public double McuTipY => (double)GetValue(McuTipYProperty);
        public double MosfetTipX => (double)GetValue(MosfetTipXProperty);
        public double MosfetTipY => (double)GetValue(MosfetTipYProperty);
        public double MotorTipX => (double)GetValue(MotorTipXProperty);
        public double MotorTipY => (double)GetValue(MotorTipYProperty);

        private static DependencyPropertyKey RoD(string name) =>
            DependencyProperty.RegisterReadOnly(name, typeof(double),
                typeof(TemperatureGraph), new PropertyMetadata(0.0));
        private static DependencyPropertyKey RoG(string name) =>
            DependencyProperty.RegisterReadOnly(name, typeof(Geometry),
                typeof(TemperatureGraph), new PropertyMetadata(null));
        private static DependencyPropertyKey RoI(string name) =>
            DependencyProperty.RegisterReadOnly(name, typeof(int),
                typeof(TemperatureGraph), new PropertyMetadata(0));

        // --------------------------------------------------------------------

        private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (TemperatureGraph)d;
            if (e.OldValue is INotifyCollectionChanged oldNcc)
                oldNcc.CollectionChanged -= self.OnCollectionChanged;
            if (e.NewValue is INotifyCollectionChanged newNcc)
                newNcc.CollectionChanged += self.OnCollectionChanged;
            self.ScheduleRecompute();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRecompute();

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            ScheduleRecompute();
        }

        // Coalesce: the host assigns all three series per 500 ms tick, and each
        // assignment used to rebuild every series' geometry. One render-priority
        // pass per dispatcher frame rebuilds them once (see BandwidthSparkline).
        private bool _recomputePending;

        public TemperatureGraph()
        {
            // The host keeps assigning samples while the graph is off-screen;
            // Recompute skips then, so catch up when shown.
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
        private double[] _mcuBuf = new double[0];
        private double[] _mosBuf = new double[0];
        private double[] _motBuf = new double[0];

        // Gridlines depend only on size; rebuilt when it changes.
        private Geometry? _gridCache;
        private double _gridCacheW, _gridCacheH;

        // Min/max over the non-zero samples (zero is the no-data sentinel).
        private static void LiveRange(double[] vals, int n, ref bool any, ref double min, ref double max)
        {
            for (int i = 0; i < n; i++)
            {
                double v = vals[i];
                if (!(v > 0)) continue;
                if (!any) { min = v; max = v; any = true; continue; }
                if (v < min) min = v;
                if (v > max) max = v;
            }
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

            int mcuN = SparklineSeries.Read(McuSamples, ref _mcuBuf);
            int mosN = SparklineSeries.Read(MosfetSamples, ref _mosBuf);
            int motN = SparklineSeries.Read(MotorSamples, ref _motBuf);

            // ---- Auto-scale Y range ----
            // Aggregate all non-zero samples; zero is the "no data" sentinel
            // pushed into the buffer at startup / when disconnected, and would
            // otherwise drag the scale floor down and squash the live readings
            // up into a sliver at the top.
            bool anyLive = false;
            double dataMin = 0, dataMax = 0;
            LiveRange(_mcuBuf, mcuN, ref anyLive, ref dataMin, ref dataMax);
            LiveRange(_mosBuf, mosN, ref anyLive, ref dataMin, ref dataMax);
            LiveRange(_motBuf, motN, ref anyLive, ref dataMin, ref dataMax);
            if (!anyLive)
            {
                dataMin = 0; dataMax = MinRange;
            }

            // Pad 10 % above/below so the lines don't kiss the edges, then
            // enforce the minimum range so a flat trace still has room to wiggle.
            double range = Math.Max(dataMax - dataMin, MinRange);
            double cx = (dataMin + dataMax) / 2.0;
            double yMin = Math.Max(0, cx - range / 2.0 * 1.20);
            double yMax = cx + range / 2.0 * 1.20;
            double yRange = Math.Max(1, yMax - yMin);

            // Fill mirrors the bandwidth sparkline: traces along the same
            // points, then closes down to the bottom-right and bottom-left
            // corners so the area between the line and the X axis fills in.
            double baseY = h - pad;
            double scale = plotH / yRange;
            double tipX = pad + plotW - 3;
            SparklineSeries.Build(_mcuBuf, mcuN, pad, plotW, baseY, yMin, yMax, yMin, scale,
                out var mcuLine, out var mcuFill, out double mcuLastY);
            SparklineSeries.Build(_mosBuf, mosN, pad, plotW, baseY, yMin, yMax, yMin, scale,
                out var mosLine, out var mosFill, out double mosLastY);
            SparklineSeries.Build(_motBuf, motN, pad, plotW, baseY, yMin, yMax, yMin, scale,
                out var motLine, out var motFill, out double motLastY);
            double mcuTipX = mcuN > 0 ? tipX : 0, mcuTipY = mcuN > 0 ? mcuLastY - 3 : 0;
            double mosTipX = mosN > 0 ? tipX : 0, mosTipY = mosN > 0 ? mosLastY - 3 : 0;
            double motTipX = motN > 0 ? tipX : 0, motTipY = motN > 0 ? motLastY - 3 : 0;
            double mcuLast = mcuN > 0 ? _mcuBuf[mcuN - 1] : 0;
            double mosLast = mosN > 0 ? _mosBuf[mosN - 1] : 0;
            double motLast = motN > 0 ? _motBuf[motN - 1] : 0;

            SetValue(McuLineGeometryKey,    mcuLine);
            SetValue(MosfetLineGeometryKey, mosLine);
            SetValue(MotorLineGeometryKey,  motLine);
            SetValue(McuFillGeometryKey,    mcuFill);
            SetValue(MosfetFillGeometryKey, mosFill);
            SetValue(MotorFillGeometryKey,  motFill);
            SetValue(McuTipXKey, mcuTipX); SetValue(McuTipYKey, mcuTipY);
            SetValue(MosfetTipXKey, mosTipX); SetValue(MosfetTipYKey, mosTipY);
            SetValue(MotorTipXKey, motTipX); SetValue(MotorTipYKey, motTipY);

            // ---- Z-order: hottest current reading on top ----
            // Sort the three latest readings descending. The series with the
            // highest current temp gets the highest ZIndex, so its fill (and
            // tip dot) overlap any cooler series sitting underneath.
            // Ties keep declaration order (mcu, mos, mot), as a stable sort would.
            SetValue(McuZIndexKey,    Rank(0, mcuLast, mosLast, motLast));
            SetValue(MosfetZIndexKey, Rank(1, mcuLast, mosLast, motLast));
            SetValue(MotorZIndexKey,  Rank(2, mcuLast, mosLast, motLast));

            // ---- Grid lines: 3 dashed horizontals at 25/50/75 % of the plot. ----
            if (_gridCache == null || _gridCacheW != w || _gridCacheH != h)
            {
                var grid = new GeometryGroup();
                for (int i = 1; i <= 3; i++)
                {
                    double y = pad + plotH * i / 4.0;
                    grid.Children.Add(new LineGeometry(new Point(pad, y), new Point(pad + plotW, y)));
                }
                grid.Freeze();
                _gridCache = grid;
                _gridCacheW = w;
                _gridCacheH = h;
            }
            SetValue(GridGeometryKey, _gridCache);
        }

        // Ascending position of series `index` among (a, b, c).
        private static int Rank(int index, double a, double b, double c)
        {
            double v = index == 0 ? a : index == 1 ? b : c;
            return Before(a, 0) + Before(b, 1) + Before(c, 2);

            int Before(double x, int j)
            {
                int cmp = x.CompareTo(v);
                return cmp < 0 || (cmp == 0 && j < index) ? 1 : 0;
            }
        }

        private void Clear()
        {
            SetValue(McuLineGeometryKey, null);
            SetValue(MosfetLineGeometryKey, null);
            SetValue(MotorLineGeometryKey, null);
            SetValue(McuFillGeometryKey, null);
            SetValue(MosfetFillGeometryKey, null);
            SetValue(MotorFillGeometryKey, null);
            SetValue(GridGeometryKey, null);
        }
    }
}
