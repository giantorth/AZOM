using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MozaPlugin.Devices.Ui
{
    /// <summary>
    /// Static helpers shared by the per-wheel settings UI: brush caching for
    /// frequently-painted color swatches, payload formatters for idle-speed
    /// commands, property-value formatters for the channel-mapping grid, and
    /// hex helpers.
    /// </summary>
    internal static class WheelUiHelpers
    {
        private static readonly Dictionary<int, SolidColorBrush> s_brushCache
            = new Dictionary<int, SolidColorBrush>();

        /// <summary>
        /// Frozen <see cref="SolidColorBrush"/> per (r,g,b), reused across all
        /// swatch repaints. Avoids per-tick brush allocation in the refresh loop.
        /// </summary>
        public static SolidColorBrush GetCachedBrush(byte r, byte g, byte b)
        {
            int key = (r << 16) | (g << 8) | b;
            if (s_brushCache.TryGetValue(key, out var brush)) return brush;
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            s_brushCache[key] = brush;
            return brush;
        }

        /// <summary>Paint a bank of swatches from a parallel color array.</summary>
        public static void UpdateSwatches(Border[] swatches, byte[][] colors, int count)
        {
            for (int i = 0; i < count && i < swatches.Length; i++)
            {
                if (swatches[i] == null) continue;
                var c = colors[i];
                swatches[i].Background = GetCachedBrush(c[0], c[1], c[2]);
            }
        }

        /// <summary>Center a swatch in a fixed-width grid cell for row layout.</summary>
        public static FrameworkElement WrapInCell(Border swatch)
        {
            var cell = new Grid { Width = 60, HorizontalAlignment = HorizontalAlignment.Center };
            swatch.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(swatch);
            return cell;
        }

        /// <summary>
        /// Per-effect idle-speed payload (cmd 0x1E [group] [effect_id] [BE u16 ms]):
        /// <c>[effect_id, ms_msb, ms_lsb]</c>.
        /// </summary>
        public static byte[] BuildIdleSpeedPayload(int effectId, int ms)
        {
            ms = Math.Max(0, Math.Min(0xFFFF, ms));
            return new byte[] {
                (byte)(effectId & 0xFF),
                (byte)((ms >> 8) & 0xFF),
                (byte)(ms & 0xFF),
            };
        }

        /// <summary>Wheel MCU UID → lowercase hex string (no separators).</summary>
        public static string UidToHex(byte[] uid)
            => uid == null ? "" : BitConverter.ToString(uid).Replace("-", "").ToLowerInvariant();

        /// <summary>
        /// Format a SimHub property value for the channel-mapping grid's
        /// "Current value" column. Truncates long strings to 32 chars.
        /// </summary>
        public static string FormatPropertyValue(object? value)
        {
            if (value == null) return "(null)";
            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case double d: return (!double.IsNaN(d) && !double.IsInfinity(d)) ? d.ToString("0.###") : d.ToString();
                case float f: return (!float.IsNaN(f) && !float.IsInfinity(f)) ? f.ToString("0.###") : f.ToString();
                case int i: return i.ToString();
                case long l: return l.ToString();
                case uint ui: return ui.ToString();
                case ushort us: return us.ToString();
                case short sh: return sh.ToString();
                case byte by: return by.ToString();
                case TimeSpan ts: return ts.ToString(@"mm\:ss\.fff");
                case string s: return s.Length > 32 ? s.Substring(0, 32) + "…" : s;
                default:
                    var str = value.ToString() ?? "";
                    return str.Length > 32 ? str.Substring(0, 32) + "…" : str;
            }
        }
    }
}
