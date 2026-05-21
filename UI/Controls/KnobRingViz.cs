using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MozaControls
{
    /// <summary>
    /// N LED dots arranged evenly in a ring around a central swatch. Slot count
    /// follows <see cref="RingColors"/>.Count: 12 for most knobs, 8 for the KS
    /// Pro middle knob, but any N works (e.g. 8 → 45° spacing, 12 → 30°). Click
    /// any dot or the centre to select it for editing. Stationary — no runtime
    /// indicator of which LED is currently lit; the firmware decides at runtime,
    /// the host only stores per-slot colour values.
    /// </summary>
    public class KnobRingViz : Control
    {
        static KnobRingViz()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(KnobRingViz),
                new FrameworkPropertyMetadata(typeof(KnobRingViz)));
        }

        public static readonly DependencyProperty RingColorsProperty =
            DependencyProperty.Register(nameof(RingColors), typeof(ObservableCollection<Color>),
                typeof(KnobRingViz),
                new FrameworkPropertyMetadata(null, OnRingChanged));
        public ObservableCollection<Color>? RingColors
        {
            get => (ObservableCollection<Color>?)GetValue(RingColorsProperty);
            set => SetValue(RingColorsProperty, value);
        }

        public static readonly DependencyProperty ActiveColorProperty =
            DependencyProperty.Register(nameof(ActiveColor), typeof(Color), typeof(KnobRingViz),
                new FrameworkPropertyMetadata(Colors.Cyan,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (d, e) => ((KnobRingViz)d).Rebuild()));
        public Color ActiveColor
        {
            get => (Color)GetValue(ActiveColorProperty);
            set => SetValue(ActiveColorProperty, value);
        }

        /// <summary>-1 means none selected; -2 means the centre (active);
        /// 0..N-1 means the corresponding ring LED.</summary>
        public static readonly DependencyProperty SelectedSlotProperty =
            DependencyProperty.Register(nameof(SelectedSlot), typeof(int), typeof(KnobRingViz),
                new FrameworkPropertyMetadata(-1,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (d, e) => ((KnobRingViz)d).Refresh()));
        public int SelectedSlot
        {
            get => (int)GetValue(SelectedSlotProperty);
            set => SetValue(SelectedSlotProperty, value);
        }

        public event EventHandler<int>? SlotSelected;

        private Canvas? _canvas;
        private readonly List<Ellipse> _ledDots = new List<Ellipse>();
        private Ellipse? _centerDot;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _canvas = GetTemplateChild("PART_Canvas") as Canvas;
            Rebuild();
        }

        private static void OnRingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (KnobRingViz)d;
            if (e.OldValue is INotifyCollectionChanged oldNcc)
                oldNcc.CollectionChanged -= self.OnCollChanged;
            if (e.NewValue is INotifyCollectionChanged newNcc)
                newNcc.CollectionChanged += self.OnCollChanged;
            self.Rebuild();
        }

        private void OnCollChanged(object? s, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            if (_canvas == null) return;
            _canvas.Children.Clear();
            _ledDots.Clear();
            _centerDot = null;

            const double size = 100;
            const double cx = size / 2;
            const double cy = size / 2;
            const double ringR = (size / 2) - 11;
            const double dotR = 6.75;

            // Outer ring outline
            var outerRing = new Ellipse
            {
                Width = size - 2, Height = size - 2,
                StrokeThickness = 1,
            };
            outerRing.SetResourceReference(Ellipse.StrokeProperty, "BorderBrush");
            Canvas.SetLeft(outerRing, 1);
            Canvas.SetTop(outerRing, 1);
            _canvas.Children.Add(outerRing);

            int n = RingColors?.Count ?? 12;
            for (int i = 0; i < n; i++)
            {
                double a = -Math.PI / 2 + (i / (double)n) * 2 * Math.PI;
                double dx = cx + Math.Cos(a) * ringR;
                double dy = cy + Math.Sin(a) * ringR;
                Color c = RingColors != null && i < RingColors.Count ? RingColors[i] : Colors.Black;
                var dot = new Ellipse
                {
                    Width = dotR * 2, Height = dotR * 2,
                    Fill = new SolidColorBrush(c),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand,
                    Tag = i,
                };
                dot.SetResourceReference(Ellipse.StrokeProperty, "BorderBrightBrush");
                Canvas.SetLeft(dot, dx - dotR);
                Canvas.SetTop(dot, dy - dotR);
                int idx = i;
                dot.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SelectedSlot = idx;
                    SlotSelected?.Invoke(this, idx);
                };
                _canvas.Children.Add(dot);
                _ledDots.Add(dot);
            }

            // Centre swatch (slot index = -2)
            _centerDot = new Ellipse
            {
                Width = 22, Height = 22,
                Fill = new SolidColorBrush(ActiveColor),
                StrokeThickness = 1,
                Cursor = Cursors.Hand,
            };
            _centerDot.SetResourceReference(Ellipse.StrokeProperty, "BorderBrightBrush");
            Canvas.SetLeft(_centerDot, cx - 11);
            Canvas.SetTop(_centerDot, cy - 11);
            _centerDot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SelectedSlot = -2;
                SlotSelected?.Invoke(this, -2);
            };
            _canvas.Children.Add(_centerDot);

            Refresh();
        }

        private void Refresh()
        {
            for (int i = 0; i < _ledDots.Count; i++)
            {
                bool sel = i == SelectedSlot;
                _ledDots[i].StrokeThickness = sel ? 1.5 : 1;
                if (sel)
                {
                    _ledDots[i].SetResourceReference(Ellipse.StrokeProperty, "CyanBrush");
                    _ledDots[i].Effect = (System.Windows.Media.Effects.Effect?)TryFindResource("CyanGlowSoftEffect");
                }
                else
                {
                    _ledDots[i].SetResourceReference(Ellipse.StrokeProperty, "BorderBrightBrush");
                    _ledDots[i].Effect = null;
                }
            }
            if (_centerDot != null)
            {
                bool sel = SelectedSlot == -2;
                _centerDot.StrokeThickness = sel ? 1.5 : 1;
                if (sel)
                {
                    _centerDot.SetResourceReference(Ellipse.StrokeProperty, "CyanBrush");
                    _centerDot.Effect = (System.Windows.Media.Effects.Effect?)TryFindResource("CyanGlowSoftEffect");
                }
                else
                {
                    _centerDot.SetResourceReference(Ellipse.StrokeProperty, "BorderBrightBrush");
                    _centerDot.Effect = null;
                }
            }
        }
    }
}
