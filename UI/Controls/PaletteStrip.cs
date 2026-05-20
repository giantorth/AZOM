using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MozaControls
{
    /// <summary>
    /// Horizontal row of 16 curated swatches + a CUSTOM hue-picker chip that
    /// opens the legacy <c>ColorPickerDialog</c>. Sets <see cref="SelectedColor"/>
    /// and raises <see cref="ColorChanged"/> when the user picks.
    ///
    /// Designed as a drop-in replacement for the per-LED <c>Border</c> +
    /// <c>MouseLeftButtonUp</c> → <c>ColorPickerDialog</c> flow used throughout
    /// the device pane. The legacy dialog is preserved as the CUSTOM fallback.
    /// </summary>
    public class PaletteStrip : Control
    {
        static PaletteStrip()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(PaletteStrip),
                new FrameworkPropertyMetadata(typeof(PaletteStrip)));
        }

        public static readonly DependencyProperty SelectedColorProperty =
            DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(PaletteStrip),
                new FrameworkPropertyMetadata(Colors.Black,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (d, e) => ((PaletteStrip)d).Refresh()));
        public Color SelectedColor
        {
            get => (Color)GetValue(SelectedColorProperty);
            set => SetValue(SelectedColorProperty, value);
        }

        /// <summary>Raised after the user clicks a swatch or picks via CUSTOM.</summary>
        public event EventHandler<Color>? ColorChanged;

        /// <summary>Function the CUSTOM chip calls to render a hue picker.
        /// Returns the picked color or null if the user cancels.</summary>
        public static Func<Color, Color?>? CustomPickerFactory { get; set; }

        private StackPanel? _root;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _root = GetTemplateChild("PART_Swatches") as StackPanel;
            BuildSwatches();
            Refresh();
        }

        private void BuildSwatches()
        {
            if (_root == null) return;
            _root.Children.Clear();
            foreach (var sw in MozaPalette.Swatches)
            {
                _root.Children.Add(BuildSwatchBorder(sw));
            }
            // CUSTOM chip
            var customBorder = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Custom hue picker",
            };
            customBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrightBrush");
            // Rainbow conic-ish background — approximate with a horizontal hue gradient
            var grad = new LinearGradientBrush();
            grad.StartPoint = new Point(0, 0);
            grad.EndPoint = new Point(1, 1);
            grad.GradientStops.Add(new GradientStop(Colors.Red, 0));
            grad.GradientStops.Add(new GradientStop(Colors.Yellow, 0.2));
            grad.GradientStops.Add(new GradientStop(Colors.Lime, 0.4));
            grad.GradientStops.Add(new GradientStop(Colors.Cyan, 0.6));
            grad.GradientStops.Add(new GradientStop(Colors.Blue, 0.8));
            grad.GradientStops.Add(new GradientStop(Colors.Magenta, 1));
            customBorder.Background = grad;
            customBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var pick = CustomPickerFactory?.Invoke(SelectedColor);
                if (pick.HasValue)
                {
                    SelectedColor = pick.Value;
                    ColorChanged?.Invoke(this, SelectedColor);
                }
            };
            _root.Children.Add(customBorder);
        }

        private Border BuildSwatchBorder(MozaPalette.Swatch sw)
        {
            var border = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 0, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(sw.Value),
                ToolTip = sw.Label,
                Tag = sw,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrightBrush");
            if (sw.IsOff)
            {
                // Diagonal strikethrough overlay
                var grid = new Grid();
                grid.Children.Add(new Border { Background = new SolidColorBrush(sw.Value) });
                grid.Children.Add(new Line
                {
                    X1 = 4, Y1 = 22, X2 = 22, Y2 = 4,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x6A, 0x73, 0x7C)),
                    StrokeThickness = 1.5,
                });
                border.Child = grid;
            }
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SelectedColor = sw.Value;
                ColorChanged?.Invoke(this, sw.Value);
            };
            return border;
        }

        private void Refresh()
        {
            if (_root == null) return;
            // Update selected ring on each swatch
            foreach (var child in _root.Children)
            {
                if (child is Border b && b.Tag is MozaPalette.Swatch sw)
                {
                    bool isSelected = ColorsApproxEqual(sw.Value, SelectedColor);
                    if (isSelected)
                    {
                        b.BorderThickness = new Thickness(2);
                        b.SetResourceReference(Border.BorderBrushProperty, "CyanBrush");
                        b.Effect = (System.Windows.Media.Effects.Effect?)TryFindResource("CyanGlowSoftEffect");
                    }
                    else
                    {
                        b.BorderThickness = new Thickness(1);
                        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrightBrush");
                        b.Effect = null;
                    }
                }
            }
        }

        private static bool ColorsApproxEqual(Color a, Color b)
            => a.R == b.R && a.G == b.G && a.B == b.B;
    }
}
