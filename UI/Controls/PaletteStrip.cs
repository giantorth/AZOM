using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MozaPlugin.Resources;

namespace MozaControls
{
    /// <summary>
    /// Two column-aligned rows of swatches: the eight pure colours over their
    /// pastel tints, then a gapped utility column with Off/White over a CUSTOM
    /// hue-picker chip (opens the legacy <c>ColorPickerDialog</c>) and a SAVED
    /// chip mirroring <see cref="MozaPalette.SavedColor"/> (hidden until a
    /// CUSTOM pick has been confirmed). Sets <see cref="SelectedColor"/> and
    /// raises <see cref="ColorChanged"/> when the user picks.
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
        private Border? _savedSwatch;

        public PaletteStrip()
        {
            // Static event: subscribed only while in the tree so SimHub's per-game
            // control rebuilds don't root stale strips.
            Loaded += (_, __) =>
            {
                MozaPalette.SavedColorChanged += OnSavedColorChanged;
                SyncSavedSwatch();
            };
            Unloaded += (_, __) => MozaPalette.SavedColorChanged -= OnSavedColorChanged;
        }

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
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var bottom = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var sw in MozaPalette.PureSwatches) top.Children.Add(BuildSwatchBorder(sw));
            top.Children.Add(BuildSwatchBorder(MozaPalette.Off, gapBefore: true));
            top.Children.Add(BuildSwatchBorder(MozaPalette.White));
            foreach (var sw in MozaPalette.PastelSwatches) bottom.Children.Add(BuildSwatchBorder(sw));
            bottom.Children.Add(BuildCustomChip());
            _savedSwatch = BuildSavedSwatch();
            bottom.Children.Add(_savedSwatch);
            _root.Children.Add(top);
            _root.Children.Add(bottom);
            SyncSavedSwatch();
        }

        private IEnumerable<Border> Chips()
        {
            if (_root == null) yield break;
            foreach (var row in _root.Children)
                if (row is Panel p)
                    foreach (var child in p.Children)
                        if (child is Border b) yield return b;
        }

        private static Thickness ChipMargin(bool gapBefore) => new Thickness(gapBefore ? 8 : 2, 0, 0, 0);

        private Border BuildCustomChip()
        {
            var customBorder = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = ChipMargin(gapBefore: true),
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
            return customBorder;
        }

        private Border BuildSwatchBorder(MozaPalette.Swatch sw, bool gapBefore = false)
        {
            var border = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = ChipMargin(gapBefore),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(sw.Display),
                ToolTip = sw.Label,
                Tag = sw,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrightBrush");
            if (sw.IsOff)
            {
                // Diagonal strikethrough overlay
                var grid = new Grid();
                grid.Children.Add(new Border { Background = new SolidColorBrush(sw.Display) });
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

        // Trailing SAVED chip; Tag/Background/Visibility follow MozaPalette.SavedColor.
        private Border BuildSavedSwatch()
        {
            var border = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = ChipMargin(gapBefore: false),
                Cursor = Cursors.Hand,
                ToolTip = Strings.Tooltip_SavedColor,
                Visibility = Visibility.Collapsed,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrightBrush");
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!(border.Tag is MozaPalette.Swatch sw)) return;
                SelectedColor = sw.Value;
                ColorChanged?.Invoke(this, sw.Value);
            };
            return border;
        }

        private void OnSavedColorChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) SyncSavedSwatch();
            else Dispatcher.BeginInvoke(new Action(SyncSavedSwatch));
        }

        private void SyncSavedSwatch()
        {
            if (_savedSwatch == null) return;
            var saved = MozaPalette.SavedColor;
            if (saved == null)
            {
                _savedSwatch.Tag = null;
                _savedSwatch.Visibility = Visibility.Collapsed;
            }
            else
            {
                _savedSwatch.Tag = new MozaPalette.Swatch("saved", Strings.Tooltip_SavedColor, saved.Value);
                _savedSwatch.Background = new SolidColorBrush(saved.Value);
                _savedSwatch.Visibility = Visibility.Visible;
            }
            Refresh();
        }

        private void Refresh()
        {
            if (_root == null) return;
            // One ring at most: a standard swatch wins, SAVED only when none matched.
            bool standardMatched = false;
            foreach (var b in Chips())
            {
                if (ReferenceEquals(b, _savedSwatch)) continue;
                if (b.Tag is MozaPalette.Swatch sw)
                {
                    bool isSelected = ColorsApproxEqual(sw.Value, SelectedColor);
                    standardMatched |= isSelected;
                    SetRing(b, isSelected);
                }
            }
            if (_savedSwatch != null && _savedSwatch.Tag is MozaPalette.Swatch saved)
                SetRing(_savedSwatch, !standardMatched && ColorsApproxEqual(saved.Value, SelectedColor));
        }

        private void SetRing(Border b, bool on)
        {
            if (on)
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

        private static bool ColorsApproxEqual(Color a, Color b)
            => a.R == b.R && a.G == b.G && a.B == b.B;
    }
}
