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

    /// <summary>Static color constants used by the palette + LED dots, plus the
    /// process-wide SAVED colour (last CUSTOM pick) every PaletteStrip mirrors.</summary>
    public static class MozaPalette
    {
        public sealed class Swatch
        {
            public string Id { get; }
            public string Label { get; }
            /// <summary>Written to the LED and matched for the selection ring.</summary>
            public Color Value { get; }
            /// <summary>Painted on the chip; differs from Value only for Off,
            /// which has to stay visible on the dark theme.</summary>
            public Color Display { get; }
            public bool IsOff { get; }
            public Swatch(string id, string label, Color value, bool isOff = false, Color? display = null)
            {
                Id = id; Label = label; Value = value; IsOff = isOff; Display = display ?? value;
            }
        }

        // Strip row 1, in spectral order.
        public static readonly IReadOnlyList<Swatch> PureSwatches = new[]
        {
            new Swatch("red",     "Red",     Color.FromRgb(0xFF, 0x00, 0x00)),
            new Swatch("orange",  "Orange",  Color.FromRgb(0xFF, 0x80, 0x00)),
            new Swatch("yellow",  "Yellow",  Color.FromRgb(0xFF, 0xFF, 0x00)),
            new Swatch("green",   "Green",   Color.FromRgb(0x00, 0xFF, 0x00)),
            new Swatch("cyan",    "Cyan",    Color.FromRgb(0x00, 0xFF, 0xFF)),
            new Swatch("blue",    "Blue",    Color.FromRgb(0x00, 0x00, 0xFF)),
            new Swatch("purple",  "Purple",  Color.FromRgb(0x80, 0x00, 0xFF)),
            new Swatch("magenta", "Magenta", Color.FromRgb(0xFF, 0x00, 0xFF)),
        };

        // Strip row 2: 50 % white tints of the row above, column-aligned with it.
        public static readonly IReadOnlyList<Swatch> PastelSwatches = new[]
        {
            new Swatch("pastel-red",     "Pastel red",     Color.FromRgb(0xFF, 0x80, 0x80)),
            new Swatch("pastel-orange",  "Pastel orange",  Color.FromRgb(0xFF, 0xC0, 0x80)),
            new Swatch("pastel-yellow",  "Pastel yellow",  Color.FromRgb(0xFF, 0xFF, 0x80)),
            new Swatch("pastel-green",   "Pastel green",   Color.FromRgb(0x80, 0xFF, 0x80)),
            new Swatch("pastel-cyan",    "Pastel cyan",    Color.FromRgb(0x80, 0xFF, 0xFF)),
            new Swatch("pastel-blue",    "Pastel blue",    Color.FromRgb(0x80, 0x80, 0xFF)),
            new Swatch("pastel-purple",  "Pastel purple",  Color.FromRgb(0xC0, 0x80, 0xFF)),
            new Swatch("pastel-magenta", "Pastel magenta", Color.FromRgb(0xFF, 0x80, 0xFF)),
        };

        // Utility column at the end of row 1 (row 2 holds CUSTOM + SAVED beneath).
        public static readonly Swatch Off =
            new Swatch("off", "Off", Colors.Black, isOff: true, display: Color.FromRgb(0x1A, 0x1F, 0x23));
        public static readonly Swatch White =
            new Swatch("white", "White", Color.FromRgb(0xFF, 0xFF, 0xFF));

        /// <summary>Last colour confirmed in the CUSTOM dialog; null until one is picked.</summary>
        public static Color? SavedColor { get; private set; }

        /// <summary>Raised on whichever thread changed <see cref="SavedColor"/>.</summary>
        public static event EventHandler? SavedColorChanged;

        /// <summary>Persistence hook; the plugin sets it at Init and clears it at End.</summary>
        public static Action<Color>? SavedColorPersist { get; set; }

        /// <summary>Restore from settings: publish without persisting.</summary>
        public static void SeedSavedColor(Color? c)
        {
            SavedColor = c;
            SavedColorChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>User pick: publish to every strip, then persist.</summary>
        public static void SetSavedColor(Color c)
        {
            SavedColor = c;
            SavedColorChanged?.Invoke(null, EventArgs.Empty);
            SavedColorPersist?.Invoke(c);
        }
    }
}
