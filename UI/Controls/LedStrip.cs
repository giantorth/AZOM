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
    /// Wrapping horizontal row of LED dots (RPM / Buttons / Flag LEDs).
    /// Click a dot to select it; the embedded <see cref="PaletteStrip"/> below
    /// then applies its color to the selected LED.
    ///
    /// Off-state LEDs (color == #1a1f23 / palette "off") render as a dark
    /// circle with a diagonal strikethrough.
    /// </summary>
    public class LedStrip : Control
    {
        static LedStrip()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(LedStrip),
                new FrameworkPropertyMetadata(typeof(LedStrip)));
        }

        public static readonly DependencyProperty ColorsProperty =
            DependencyProperty.Register(nameof(Colors), typeof(ObservableCollection<Color>),
                typeof(LedStrip),
                new FrameworkPropertyMetadata(null, OnColorsChanged));
        public ObservableCollection<Color>? Colors
        {
            get => (ObservableCollection<Color>?)GetValue(ColorsProperty);
            set => SetValue(ColorsProperty, value);
        }

        public static readonly DependencyProperty LabelsProperty =
            DependencyProperty.Register(nameof(Labels), typeof(IList<string>), typeof(LedStrip),
                new PropertyMetadata(null, (d, e) => ((LedStrip)d).Rebuild()));
        public IList<string>? Labels
        {
            get => (IList<string>?)GetValue(LabelsProperty);
            set => SetValue(LabelsProperty, value);
        }

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(LedStrip),
                new FrameworkPropertyMetadata(-1,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (d, e) => ((LedStrip)d).Refresh()));
        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        /// <summary>Raised when the user clicks an LED dot to select it.</summary>
        public event EventHandler<int>? LedSelected;

        /// <summary>Raised when a Colors[] entry is replaced via SetLed.</summary>
        public event EventHandler<int>? LedColorChanged;

        private WrapPanel? _root;
        private PaletteStrip? _palette;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _root = GetTemplateChild("PART_Wrap") as WrapPanel;
            _palette = GetTemplateChild("PART_Palette") as PaletteStrip;
            if (_palette != null)
            {
                _palette.ColorChanged += (_, color) =>
                {
                    if (SelectedIndex >= 0 && Colors != null && SelectedIndex < Colors.Count)
                    {
                        Colors[SelectedIndex] = color;
                        LedColorChanged?.Invoke(this, SelectedIndex);
                    }
                };
            }
            if (FindName("PART_FillAll") is Button fillBtn)
                fillBtn.Click += (_, __) =>
                {
                    if (Colors == null || _palette == null) return;
                    var c = _palette.SelectedColor;
                    for (int i = 0; i < Colors.Count; i++) { Colors[i] = c; LedColorChanged?.Invoke(this, i); }
                };
            if (FindName("PART_Reverse") is Button revBtn)
                revBtn.Click += (_, __) =>
                {
                    if (Colors == null) return;
                    var copy = new List<Color>(Colors);
                    for (int i = 0; i < copy.Count; i++) { Colors[i] = copy[copy.Count - 1 - i]; LedColorChanged?.Invoke(this, i); }
                };
            Rebuild();
        }

        private static void OnColorsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (LedStrip)d;
            if (e.OldValue is INotifyCollectionChanged oldNcc)
                oldNcc.CollectionChanged -= self.OnCollChanged;
            if (e.NewValue is INotifyCollectionChanged newNcc)
                newNcc.CollectionChanged += self.OnCollChanged;
            self.Rebuild();
        }

        private void OnCollChanged(object? s, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            if (_root == null) return;
            _root.Children.Clear();
            if (Colors == null) return;
            for (int i = 0; i < Colors.Count; i++)
            {
                int idx = i;
                var slot = new StackPanel
                {
                    Margin = new Thickness(3),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var dot = BuildLedDot(Colors[i]);
                dot.Tag = idx;
                dot.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SelectedIndex = idx;
                    if (_palette != null && Colors != null && idx < Colors.Count)
                        _palette.SelectedColor = Colors[idx];
                    LedSelected?.Invoke(this, idx);
                };
                slot.Children.Add(dot);
                var label = (Labels != null && idx < Labels.Count) ? Labels[idx] : (idx + 1).ToString();
                var lbl = new TextBlock
                {
                    Text = label,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontFamily = (FontFamily)(TryFindResource("FontMono") ?? new FontFamily("Consolas")),
                    FontSize = 10,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
                slot.Children.Add(lbl);
                _root.Children.Add(slot);
            }
            Refresh();
        }

        private Border BuildLedDot(Color c)
        {
            var border = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrightBrush");
            bool isOff = IsOffColor(c);
            if (isOff)
            {
                var g = new Grid();
                var bg = new Ellipse { Fill = (Brush)(TryFindResource("BgDeepBrush") ?? Brushes.Black) };
                g.Children.Add(bg);
                g.Children.Add(new Line
                {
                    X1 = 4, Y1 = 22, X2 = 22, Y2 = 4,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x6A, 0x73, 0x7C)),
                    StrokeThickness = 1.5,
                });
                border.Child = g;
            }
            else
            {
                var fill = new Ellipse { Fill = new SolidColorBrush(c) };
                fill.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = c, ShadowDepth = 0, BlurRadius = 8, Opacity = 0.55
                };
                border.Child = fill;
            }
            return border;
        }

        private static bool IsOffColor(Color c)
            => (c.R <= 0x20 && c.G <= 0x20 && c.B <= 0x25) || (c.A == 0);

        private void Refresh()
        {
            if (_root == null) return;
            for (int i = 0; i < _root.Children.Count; i++)
            {
                if (_root.Children[i] is StackPanel slot && slot.Children.Count >= 1
                    && slot.Children[0] is Border dot && dot.Tag is int idx)
                {
                    bool sel = idx == SelectedIndex;
                    if (sel)
                    {
                        dot.BorderThickness = new Thickness(1.5);
                        dot.SetResourceReference(Border.BorderBrushProperty, "CyanBrush");
                        dot.Effect = (System.Windows.Media.Effects.Effect?)TryFindResource("CyanGlowEffect");
                    }
                    else
                    {
                        dot.BorderThickness = new Thickness(1);
                        dot.SetResourceReference(Border.BorderBrushProperty, "BorderBrightBrush");
                        if (Colors != null && idx < Colors.Count && !IsOffColor(Colors[idx]))
                            dot.Effect = null;
                    }
                }
            }
        }
    }
}
