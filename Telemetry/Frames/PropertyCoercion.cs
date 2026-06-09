using System;
using System.Collections.Concurrent;
using System.Globalization;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Coerces SimHub property values (returned as <c>object</c> from
    /// <c>PluginManager.GetPropertyValue</c>) into a double for bit-packing.
    /// </summary>
    public static class PropertyCoercion
    {
        // Tracks paths that have already emitted a null/unsupported warning so
        // a bad mapping doesn't flood the log at 20+ Hz. Bounded at MaxWarned so a
        // pathological game with thousands of distinct bad properties can't grow
        // this set indefinitely (each entry is a string heap allocation).
        private const int MaxWarned = 256;
        private static readonly ConcurrentDictionary<string, byte> _warnedPaths = new();

        /// <summary>
        /// Coerce a SimHub property value to double. Handles the common numeric
        /// types, bool, TimeSpan, and string (including "R"/"N" gears).
        /// </summary>
        public static double Coerce(object? value, string propertyPath)
        {
            switch (value)
            {
                case null:
                    WarnOnce(propertyPath, "returned null");
                    return 0.0;
                case double d:   return d;
                case float f:    return f;
                case int i:      return i;
                case uint ui:    return ui;
                case short s:    return s;
                case ushort us:  return us;
                case long l:     return l;
                case ulong ul:   return ul;
                case byte b:     return b;
                case sbyte sb:   return sb;
                case bool bo:    return bo ? 1.0 : 0.0;
                case TimeSpan ts: return ts.TotalSeconds;
                case string str: return ParseString(str, propertyPath);
                default:
                    WarnOnce(propertyPath, $"unsupported type {value.GetType().Name}");
                    return 0.0;
            }
        }

        /// <summary>
        /// Parse a gear string: "R" = -1, "N"/empty = 0, numeric = parsed.
        /// Shared between GameDataSnapshot and the property resolver path so
        /// gear strings coerce the same way regardless of which source feeds them.
        /// </summary>
        public static double ParseGear(string? gear)
        {
            if (string.IsNullOrEmpty(gear)) return 0.0;
            if (gear == "R") return -1.0;
            if (gear == "N") return 0.0;
            return double.TryParse(gear, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0.0;
        }

        private static double ParseString(string s, string propertyPath)
        {
            if (s.Length == 0) return 0.0;
            // Gear-style single-character values
            if (s == "R") return -1.0;
            if (s == "N") return 0.0;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return n;
            WarnOnce(propertyPath, $"non-numeric string '{s}'");
            return 0.0;
        }

        private static void WarnOnce(string path, string detail)
        {
            // Cap before TryAdd: the size guard is best-effort under contention
            // (multiple threads can race past this point), but a few extra
            // entries above the cap is acceptable. Once full, silently drop —
            // re-checking later won't help, and logging the suppression once
            // per excess path would defeat the rate-limit it exists for.
            if (_warnedPaths.Count >= MaxWarned) return;
            if (_warnedPaths.TryAdd(path, 0))
            {
                try
                {
                    MozaLog.Warn($"[AZOM] Property '{path}' {detail}; channel will send 0 until mapping is fixed.");
                }
                catch { /* logging may not be initialised in tests */ }
            }
        }

        /// <summary>Reset the once-per-path warning tracker. Intended for tests.</summary>
        public static void ResetWarnings() => _warnedPaths.Clear();
    }
}
