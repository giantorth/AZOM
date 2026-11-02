using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MozaPlugin.Resources;

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
            /// <summary>Localized name, looked up from <see cref="Id"/> on each
            /// read so it follows the UI language.</summary>
            public string Label => LabelFor(Id);
            /// <summary>Written to the LED and matched for the selection ring.</summary>
            public Color Value { get; }
            /// <summary>Painted on the chip; differs from Value only for Off,
            /// which has to stay visible on the dark theme.</summary>
            public Color Display { get; }
            public bool IsOff { get; }
            public Swatch(string id, Color value, bool isOff = false, Color? display = null)
            {
                Id = id; Value = value; IsOff = isOff; Display = display ?? value;
            }
        }

        private static string LabelFor(string id) => id switch
        {
            "red"            => Strings.ShifterColor_Red,
            "orange"         => Strings.ShifterColor_Orange,
            "yellow"         => Strings.ShifterColor_Yellow,
            "green"          => Strings.ShifterColor_Green,
            "cyan"           => Strings.ShifterColor_Cyan,
            "blue"           => Strings.ShifterColor_Blue,
            "purple"         => Strings.ShifterColor_Purple,
            "magenta"        => Strings.Swatch_Magenta,
            "pastel-red"     => Strings.Swatch_PastelRed,
            "pastel-orange"  => Strings.Swatch_PastelOrange,
            "pastel-yellow"  => Strings.Swatch_PastelYellow,
            "pastel-green"   => Strings.Swatch_PastelGreen,
            "pastel-cyan"    => Strings.Swatch_PastelCyan,
            "pastel-blue"    => Strings.Swatch_PastelBlue,
            "pastel-purple"  => Strings.Swatch_PastelPurple,
            "pastel-magenta" => Strings.Swatch_PastelMagenta,
            "off"            => Strings.Option_Off,
            "white"          => Strings.ShifterColor_White,
            "saved"          => Strings.Tooltip_SavedColor,
            _                => id,
        };

        // Strip row 1, in spectral order.
        public static readonly IReadOnlyList<Swatch> PureSwatches = new[]
        {
            new Swatch("red",     Color.FromRgb(0xFF, 0x00, 0x00)),
            new Swatch("orange",  Color.FromRgb(0xFF, 0x80, 0x00)),
            new Swatch("yellow",  Color.FromRgb(0xFF, 0xFF, 0x00)),
            new Swatch("green",   Color.FromRgb(0x00, 0xFF, 0x00)),
            new Swatch("cyan",    Color.FromRgb(0x00, 0xFF, 0xFF)),
            new Swatch("blue",    Color.FromRgb(0x00, 0x00, 0xFF)),
            new Swatch("purple",  Color.FromRgb(0x80, 0x00, 0xFF)),
            new Swatch("magenta", Color.FromRgb(0xFF, 0x00, 0xFF)),
        };

        // Strip row 2: 50 % white tints of the row above, column-aligned with it.
        public static readonly IReadOnlyList<Swatch> PastelSwatches = new[]
        {
            new Swatch("pastel-red",     Color.FromRgb(0xFF, 0x80, 0x80)),
            new Swatch("pastel-orange",  Color.FromRgb(0xFF, 0xC0, 0x80)),
            new Swatch("pastel-yellow",  Color.FromRgb(0xFF, 0xFF, 0x80)),
            new Swatch("pastel-green",   Color.FromRgb(0x80, 0xFF, 0x80)),
            new Swatch("pastel-cyan",    Color.FromRgb(0x80, 0xFF, 0xFF)),
            new Swatch("pastel-blue",    Color.FromRgb(0x80, 0x80, 0xFF)),
            new Swatch("pastel-purple",  Color.FromRgb(0xC0, 0x80, 0xFF)),
            new Swatch("pastel-magenta", Color.FromRgb(0xFF, 0x80, 0xFF)),
        };

        // Utility column at the end of row 1 (row 2 holds CUSTOM + SAVED beneath).
        public static readonly Swatch Off =
            new Swatch("off", Colors.Black, isOff: true, display: Color.FromRgb(0x1A, 0x1F, 0x23));
        public static readonly Swatch White =
            new Swatch("white", Color.FromRgb(0xFF, 0xFF, 0xFF));

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
