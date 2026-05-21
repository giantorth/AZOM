using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>Returns Collapsed for null/empty strings, Visible otherwise.</summary>
    public sealed class EmptyStringToVisibilityConverter : IValueConverter
    {
        public static readonly EmptyStringToVisibilityConverter Instance = new EmptyStringToVisibilityConverter();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Static color constants used by the palette + LED dots.</summary>
    public static class MozaPalette
    {
        public sealed class Swatch
        {
            public string Id { get; }
            public string Label { get; }
            public Color Value { get; }
            public bool IsOff { get; }
            public Swatch(string id, string label, Color value, bool isOff = false)
            {
                Id = id; Label = label; Value = value; IsOff = isOff;
            }
        }

        public static readonly IReadOnlyList<Swatch> Swatches = new[]
        {
            new Swatch("off",     "Off",     Color.FromRgb(0x1A, 0x1F, 0x23), isOff: true),
            new Swatch("white",   "White",   Color.FromRgb(0xFF, 0xFF, 0xFF)),
            new Swatch("red",     "Red",     Color.FromRgb(0xFF, 0x2A, 0x2A)),
            new Swatch("orange",  "Orange",  Color.FromRgb(0xFF, 0x7A, 0x14)),
            new Swatch("amber",   "Amber",   Color.FromRgb(0xFF, 0xB4, 0x00)),
            new Swatch("yellow",  "Yellow",  Color.FromRgb(0xFF, 0xE6, 0x00)),
            new Swatch("lime",    "Lime",    Color.FromRgb(0xA4, 0xFF, 0x14)),
            new Swatch("green",   "Green",   Color.FromRgb(0x22, 0xE0, 0x57)),
            new Swatch("mint",    "Mint",    Color.FromRgb(0x39, 0xFF, 0x88)),
            new Swatch("cyan",    "Cyan",    Color.FromRgb(0x00, 0xE5, 0xFF)),
            new Swatch("azure",   "Azure",   Color.FromRgb(0x1C, 0x8C, 0xFF)),
            new Swatch("blue",    "Blue",    Color.FromRgb(0x2B, 0x3E, 0xFF)),
            new Swatch("violet",  "Violet",  Color.FromRgb(0x7A, 0x3B, 0xFF)),
            new Swatch("magenta", "Magenta", Color.FromRgb(0xFF, 0x2A, 0xD4)),
            new Swatch("pink",    "Pink",    Color.FromRgb(0xFF, 0x5F, 0xA8)),
            new Swatch("rose",    "Rose",    Color.FromRgb(0xC4, 0x1E, 0x3A)),
        };
    }
}
