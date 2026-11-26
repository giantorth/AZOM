using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MozaPlugin.Devices.MBooster;

namespace MozaControls
{
    /// <summary>
    /// Clutch bite point calibration pill: dot lit while a standing start can be
    /// measured and for its result, text from <see cref="MozaPlugin.MozaPlugin.BitePointIndicatorText"/>.
    /// Uses the theme's brushes where present (plugin tab), else the same colours
    /// (ShakeIt editor).
    /// </summary>
    public class BitePointCalIndicator : Border
    {
        private static readonly Brush ReadyFallback = Frozen(0x39, 0xFF, 0x88);
        private static readonly Brush MeasuringFallback = Frozen(0x00, 0xE5, 0xFF);
        private static readonly Brush TooFastFallback = Frozen(0xFF, 0xB8, 0x4D);
        private static readonly Brush IdleFallback = Frozen(0x6A, 0x73, 0x7C);

        private readonly Ellipse _dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        private readonly TextBlock _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        public BitePointCalIndicator()
        {
            CornerRadius = new CornerRadius(999);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10, 2, 10, 2);
            VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_dot);
            row.Children.Add(_text);
            Child = row;
        }

        internal void Show(in MozaPlugin.MozaPlugin.BitePointInfo info, BitePointCalState state,
                           MozaPlugin.MozaPlugin.BitePointLaunch? launch)
        {
            _text.Text = MozaPlugin.MozaPlugin.BitePointIndicatorText(info, state, launch);
            _dot.Fill = state switch
            {
                BitePointCalState.Armed or BitePointCalState.Captured => ThemeBrush("GreenBrush", ReadyFallback),
                BitePointCalState.Releasing => ThemeBrush("CyanBrush", MeasuringFallback),
                BitePointCalState.TooFast => ThemeBrush("AmberBrush", TooFastFallback),
                _ => ThemeBrush("TextMutedBrush", IdleFallback),
            };
        }

        private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
