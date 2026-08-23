using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Per-base-model facts for the wheelbase ambient LED strips. The only
    /// model-dependent quantity is strip length: the R16 Ultra carries 6 LEDs
    /// per strip (12 total), the R21/R25/R27 bodies carry 9 (18 total).
    ///
    /// There is no geometry register — nothing in the group 0x22 read sweep
    /// returns a LED count — and PID cannot discriminate, since R16 and R21
    /// share PID 0x0000. The only signal is the base model-name string read at
    /// group 0x07 / dev 0x12 into <see cref="MozaData.BaseModelName"/>
    /// (e.g. "R16 Black # MOT-3-V01").
    ///
    /// See docs/protocol/leds/base-ambient-0x20-0x22.md § Strip geometry.
    /// </summary>
    internal static class BaseModelInfo
    {
        /// <summary>Strip length used when the base model is not yet known.</summary>
        public const int DefaultLedsPerStrip = 9;

        /// <summary>Largest strip length any known base uses. Sizes the
        /// superset arrays and command registrations.</summary>
        public const int MaxLedsPerStrip = 9;

        // Model-name prefix → LEDs per strip. Anything absent falls back to
        // DefaultLedsPerStrip, so an unrecognised base behaves exactly as it
        // did before this table existed.
        private static readonly (string Prefix, int LedsPerStrip)[] StripLengths =
        {
            ("R16", 6),
            ("R21", 9),
            ("R25", 9),
            ("R27", 9),
        };

        /// <summary>
        /// LEDs per ambient strip for a base model-name string. Matches on the
        /// leading model token, so the full firmware string can be passed in
        /// unmodified. Unknown or empty → <see cref="DefaultLedsPerStrip"/>.
        /// </summary>
        public static int LedsPerStrip(string? baseModelName)
        {
            if (string.IsNullOrEmpty(baseModelName))
                return DefaultLedsPerStrip;

            foreach (var (prefix, leds) in StripLengths)
            {
                if (baseModelName!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return leds;
            }

            return DefaultLedsPerStrip;
        }

        /// <summary>Total ambient LEDs across both strips.</summary>
        public static int TotalLeds(string? baseModelName) => LedsPerStrip(baseModelName) * 2;

        /// <summary>
        /// True when the model name resolves to a known entry. Used to decide
        /// whether a geometry-dependent artifact (the SimHub device definition)
        /// is safe to write yet, rather than writing a default-9 definition for
        /// a base whose identity simply has not arrived.
        /// </summary>
        public static bool IsKnown(string? baseModelName)
        {
            if (string.IsNullOrEmpty(baseModelName))
                return false;

            foreach (var (prefix, _) in StripLengths)
            {
                if (baseModelName!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
