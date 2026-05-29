using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Maps a boolean to one of two pre-configured brushes. Used by the
    /// import dialog's diff list so changed rows can render cyan/white and
    /// unchanged rows render muted via plain WPF binding (instead of a
    /// DataTemplate.Triggers TargetName lookup, which silently failed when
    /// the targeted x:Name collided with a StaticResource style key).
    /// </summary>
    public sealed class BoolToBrushConverter : IValueConverter
    {
        public Brush? TrueBrush { get; set; }
        public Brush? FalseBrush { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool b = value is bool x && x;
            return b ? (object?)TrueBrush : (object?)FalseBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
