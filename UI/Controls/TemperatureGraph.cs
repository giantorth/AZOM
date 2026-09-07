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

        private void ScheduleRecompute()
        {
            if (_recomputePending) return;
            _recomputePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _recomputePending = false;
                Recompute();
            }));
        }

        private static List<double> Collect(IEnumerable? src)
        {
            var list = new List<double>();
            if (src == null) return list;
            foreach (var v in src)
            {
                if (v is IConvertible c)
                    list.Add(c.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
            }
            return list;
        }

        private void Recompute()
        {
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

            var mcu = Collect(McuSamples);
            var mos = Collect(MosfetSamples);
            var mot = Collect(MotorSamples);

            // ---- Auto-scale Y range ----
            // Aggregate all non-zero samples; zero is the "no data" sentinel
            // pushed into the buffer at startup / when disconnected, and would
            // otherwise drag the scale floor down and squash the live readings
            // up into a sliver at the top.
            var live = mcu.Concat(mos).Concat(mot).Where(v => v > 0).ToList();
            double dataMin, dataMax;
            if (live.Count == 0)
            {
                dataMin = 0; dataMax = MinRange;
            }
            else
            {
                dataMin = live.Min();
                dataMax = live.Max();
            }

            // Pad 10 % above/below so the lines don't kiss the edges, then
            // enforce the minimum range so a flat trace still has room to wiggle.
            double range = Math.Max(dataMax - dataMin, MinRange);
            double cx = (dataMin + dataMax) / 2.0;
            double yMin = Math.Max(0, cx - range / 2.0 * 1.20);
            double yMax = cx + range / 2.0 * 1.20;
            double yRange = Math.Max(1, yMax - yMin);

            double Y(double v) => h - pad - (Math.Min(Math.Max(v, yMin), yMax) - yMin) / yRange * plotH;

            (Geometry? line, Geometry? fill, double tipX, double tipY, double last)
                BuildSeries(List<double> vals)
            {
                if (vals.Count == 0) return (null, null, 0, 0, 0);
                int n = vals.Count;
                double X(int i) => pad + (n == 1 ? 0 : plotW * i / (n - 1));

                var lineFig = new PathFigure { StartPoint = new Point(X(0), Y(vals[0])), IsClosed = false, IsFilled = false };
                for (int i = 1; i < n; i++)
                    lineFig.Segments.Add(new LineSegment(new Point(X(i), Y(vals[i])), true));
                var lineGeom = new PathGeometry(); lineGeom.Figures.Add(lineFig); lineGeom.Freeze();

                // Fill mirrors the bandwidth sparkline: traces along the same
                // points, then closes down to the bottom-right and bottom-left
                // corners so the area between the line and the X axis fills in.
                var fillFig = new PathFigure { StartPoint = new Point(pad, h - pad), IsClosed = true, IsFilled = true };
                for (int i = 0; i < n; i++)
                    fillFig.Segments.Add(new LineSegment(new Point(X(i), Y(vals[i])), false));
                fillFig.Segments.Add(new LineSegment(new Point(pad + plotW, h - pad), false));
                var fillGeom = new PathGeometry(); fillGeom.Figures.Add(fillFig); fillGeom.Freeze();

                double tipX = pad + plotW;
                double tipY = Y(vals[n - 1]);
                return (lineGeom, fillGeom, tipX - 3, tipY - 3, vals[n - 1]);
            }

            var (mcuLine, mcuFill, mcuTipX, mcuTipY, mcuLast) = BuildSeries(mcu);
            var (mosLine, mosFill, mosTipX, mosTipY, mosLast) = BuildSeries(mos);
            var (motLine, motFill, motTipX, motTipY, motLast) = BuildSeries(mot);

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
            var ranked = new[] { ("mcu", mcuLast), ("mos", mosLast), ("mot", motLast) }
                .OrderBy(t => t.Item2)
                .Select((t, idx) => (t.Item1, idx))
                .ToDictionary(t => t.Item1, t => t.idx);
            SetValue(McuZIndexKey,    ranked["mcu"]);
            SetValue(MosfetZIndexKey, ranked["mos"]);
            SetValue(MotorZIndexKey,  ranked["mot"]);

            // ---- Grid lines: 3 dashed horizontals at 25/50/75 % of the plot. ----
            var grid = new GeometryGroup();
            for (int i = 1; i <= 3; i++)
            {
                double y = pad + plotH * i / 4.0;
                grid.Children.Add(new LineGeometry(new Point(pad, y), new Point(pad + plotW, y)));
            }
            grid.Freeze();
            SetValue(GridGeometryKey, grid);
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
