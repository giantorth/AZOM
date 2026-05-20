using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MozaControls
{
    /// <summary>
    /// 6-band vertical EQ. Each band drags vertically 0–400%. Bars >100% switch
    /// to green (boost) per the design. Band1..Band6 DPs are intended to bind
    /// two-way to the underlying Eq1Slider..Eq6Slider so existing handlers fire.
    /// </summary>
    public class MozaEqualizer : Control
    {
        static MozaEqualizer()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(MozaEqualizer),
                new FrameworkPropertyMetadata(typeof(MozaEqualizer)));
        }

        // -------- Band value DPs (two-way bindable to underlying Eq1..Eq6 sliders) --------
        public static readonly DependencyProperty Band1Property = RegisterBand(nameof(Band1), 100);
        public static readonly DependencyProperty Band2Property = RegisterBand(nameof(Band2), 100);
        public static readonly DependencyProperty Band3Property = RegisterBand(nameof(Band3), 100);
        public static readonly DependencyProperty Band4Property = RegisterBand(nameof(Band4), 100);
        public static readonly DependencyProperty Band5Property = RegisterBand(nameof(Band5), 100);
        public static readonly DependencyProperty Band6Property = RegisterBand(nameof(Band6), 100);

        private static DependencyProperty RegisterBand(string name, double dflt)
            => DependencyProperty.Register(name, typeof(double), typeof(MozaEqualizer),
                new FrameworkPropertyMetadata(dflt,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaEqualizer)d).Recompute()));

        public double Band1 { get => (double)GetValue(Band1Property); set => SetValue(Band1Property, value); }
        public double Band2 { get => (double)GetValue(Band2Property); set => SetValue(Band2Property, value); }
        public double Band3 { get => (double)GetValue(Band3Property); set => SetValue(Band3Property, value); }
        public double Band4 { get => (double)GetValue(Band4Property); set => SetValue(Band4Property, value); }
        public double Band5 { get => (double)GetValue(Band5Property); set => SetValue(Band5Property, value); }
        public double Band6 { get => (double)GetValue(Band6Property); set => SetValue(Band6Property, value); }

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(MozaEqualizer),
                new FrameworkPropertyMetadata(400.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaEqualizer)d).Recompute()));
        public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

        // -------- Readonly per-bar rendering DPs surfaced to template --------
        private static readonly DependencyPropertyKey[] BarHeightKeys = new DependencyPropertyKey[6];
        private static readonly DependencyPropertyKey[] BarIsBoostKeys = new DependencyPropertyKey[6];
        private static readonly DependencyPropertyKey[] BarLabelKeys = new DependencyPropertyKey[6];

        public static readonly DependencyProperty[] BarHeightProperties = new DependencyProperty[6];
        public static readonly DependencyProperty[] BarIsBoostProperties = new DependencyProperty[6];
        public static readonly DependencyProperty[] BarLabelProperties = new DependencyProperty[6];

        private static readonly bool _staticInit = StaticInit();
        private static bool StaticInit()
        {
            for (int i = 0; i < 6; i++)
            {
                BarHeightKeys[i] = DependencyProperty.RegisterReadOnly($"Bar{i + 1}Height", typeof(double),
                    typeof(MozaEqualizer), new PropertyMetadata(0.0));
                BarHeightProperties[i] = BarHeightKeys[i].DependencyProperty;
                BarIsBoostKeys[i] = DependencyProperty.RegisterReadOnly($"Bar{i + 1}IsBoost", typeof(bool),
                    typeof(MozaEqualizer), new PropertyMetadata(false));
                BarIsBoostProperties[i] = BarIsBoostKeys[i].DependencyProperty;
                BarLabelKeys[i] = DependencyProperty.RegisterReadOnly($"Bar{i + 1}Label", typeof(string),
                    typeof(MozaEqualizer), new PropertyMetadata(string.Empty));
                BarLabelProperties[i] = BarLabelKeys[i].DependencyProperty;
            }
            return true;
        }

        public double Bar1Height => (double)GetValue(BarHeightProperties[0]);
        public double Bar2Height => (double)GetValue(BarHeightProperties[1]);
        public double Bar3Height => (double)GetValue(BarHeightProperties[2]);
        public double Bar4Height => (double)GetValue(BarHeightProperties[3]);
        public double Bar5Height => (double)GetValue(BarHeightProperties[4]);
        public double Bar6Height => (double)GetValue(BarHeightProperties[5]);

        public bool Bar1IsBoost => (bool)GetValue(BarIsBoostProperties[0]);
        public bool Bar2IsBoost => (bool)GetValue(BarIsBoostProperties[1]);
        public bool Bar3IsBoost => (bool)GetValue(BarIsBoostProperties[2]);
        public bool Bar4IsBoost => (bool)GetValue(BarIsBoostProperties[3]);
        public bool Bar5IsBoost => (bool)GetValue(BarIsBoostProperties[4]);
        public bool Bar6IsBoost => (bool)GetValue(BarIsBoostProperties[5]);

        public string Bar1Label => (string)GetValue(BarLabelProperties[0]);
        public string Bar2Label => (string)GetValue(BarLabelProperties[1]);
        public string Bar3Label => (string)GetValue(BarLabelProperties[2]);
        public string Bar4Label => (string)GetValue(BarLabelProperties[3]);
        public string Bar5Label => (string)GetValue(BarLabelProperties[4]);
        public string Bar6Label => (string)GetValue(BarLabelProperties[5]);

        public static readonly DependencyProperty FrequencyLabelsProperty =
            DependencyProperty.Register(nameof(FrequencyLabels), typeof(string), typeof(MozaEqualizer),
                new FrameworkPropertyMetadata("10Hz,15Hz,25Hz,40Hz,60Hz,100Hz",
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaEqualizer)d).Recompute()));
        public string FrequencyLabels { get => (string)GetValue(FrequencyLabelsProperty); set => SetValue(FrequencyLabelsProperty, value); }

        // -------- Layout / drag --------
        // Bar area is the inner canvas height (160 default); we expose Height of each bar's fill rectangle.
        public const double BarTrackHeight = 160;

        private Grid? _barGrid;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _barGrid = GetTemplateChild("PART_BarGrid") as Grid;
            if (_barGrid != null)
            {
                _barGrid.MouseLeftButtonDown += OnDown;
                _barGrid.MouseMove += OnMove;
                _barGrid.MouseLeftButtonUp += OnUp;
                _barGrid.LostMouseCapture += (_, __) => _dragBand = -1;
            }
            Recompute();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Recompute();
        }

        private int _dragBand = -1;

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (_barGrid == null) return;
            var p = e.GetPosition(_barGrid);
            _dragBand = HitBand(p.X, _barGrid.ActualWidth);
            if (_dragBand >= 0)
            {
                _barGrid.CaptureMouse();
                ApplyDrag(p.Y, _barGrid.ActualHeight);
                e.Handled = true;
            }
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_dragBand < 0 || _barGrid == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragBand = -1; _barGrid.ReleaseMouseCapture(); return; }
            ApplyDrag(e.GetPosition(_barGrid).Y, _barGrid.ActualHeight);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_barGrid != null && _barGrid.IsMouseCaptured) _barGrid.ReleaseMouseCapture();
            _dragBand = -1;
        }

        private int HitBand(double x, double w)
        {
            if (w <= 0) return -1;
            int idx = (int)Math.Floor(x / w * 6);
            return Math.Max(0, Math.Min(5, idx));
        }

        private void ApplyDrag(double y, double h)
        {
            if (h <= 0) return;
            double pct = Math.Max(0, Math.Min(1, (h - y) / h));
            double v = Math.Round(pct * MaxValue);
            SetBand(_dragBand, v);
        }

        private void SetBand(int i, double v)
        {
            switch (i)
            {
                case 0: Band1 = v; break;
                case 1: Band2 = v; break;
                case 2: Band3 = v; break;
                case 3: Band4 = v; break;
                case 4: Band5 = v; break;
                case 5: Band6 = v; break;
            }
        }

        private void Recompute()
        {
            double[] values = { Band1, Band2, Band3, Band4, Band5, Band6 };
            double trackH = _barGrid?.ActualHeight ?? BarTrackHeight;
            if (trackH <= 0) trackH = BarTrackHeight;
            double max = Math.Max(1, MaxValue);
            for (int i = 0; i < 6; i++)
            {
                double v = Math.Max(0, Math.Min(max, values[i]));
                SetValue(BarHeightKeys[i], v / max * trackH);
                SetValue(BarIsBoostKeys[i], v > 100);
            }

            var labels = (FrequencyLabels ?? "").Split(',');
            for (int i = 0; i < 6; i++)
            {
                SetValue(BarLabelKeys[i], i < labels.Length ? labels[i].Trim() : string.Empty);
            }
        }
    }
}
