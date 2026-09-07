using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Per-base-model facts, resolved from the firmware model-name string read at
    /// group 0x07 / dev 0x12 into <see cref="MozaData.BaseModelName"/>
    /// (e.g. "R16 Black # MOT-3-V01"). Two things depend on it: the ambient strip
    /// length, and the SimHub device definition's name/identity.
    ///
    /// There is no geometry register — nothing in the group 0x22 read sweep
    /// returns a LED count — and PID cannot discriminate, since R16 and R21
    /// share PID 0x0000. The model-name string is the only signal.
    ///
    /// Names are the bare firmware token. Firmware never reports the "Ultra"
    /// marketing suffix, so nothing here can prove it and nothing here invents it.
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

        /// <summary>
        /// Known wheelbase models. <c>LedsPerStrip == 0</c> means the body has no
        /// ambient strip at all (R3/R5/R9/R12 silently drop the 0xA2 read) — not
        /// "unknown", which falls through to <see cref="DefaultLedsPerStrip"/>.
        ///
        /// <c>RatedNm</c> is the model's peak torque, scaling the Base-tab torque
        /// graph and reported as <c>AZOM.TorqueLimit</c>. <c>0</c> means "not
        /// established" — an unrecognised model — and the graph falls back to
        /// auto-scaling. It is a double because the rating is not always the model
        /// number: the R5 is 5.5 Nm.
        /// </summary>
        public static readonly (string Prefix, string FriendlyName, int LedsPerStrip, double RatedNm)[] KnownModels =
        {
            ("R3",  "R3",  0, 3),
            ("R5",  "R5",  0, 5.5),
            ("R9",  "R9",  0, 9),
            ("R12", "R12", 0, 12),
            ("R16", "R16", 6, 16),
            ("R21", "R21", 9, 21),
            ("R25", "R25", 9, 25),
            ("R27", "R27", 9, 27),
        };

        /// <summary>Model peak torque in Nm, or 0 when not established.</summary>
        public static double RatedNm(string? baseModelName)
        {
            var prefix = ExtractPrefix(baseModelName);
            if (prefix.Length == 0)
                return 0;

            foreach (var (p, _, _, nm) in KnownModels)
            {
                if (string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase))
                    return nm;
            }
            return 0;
        }

        /// <summary>
        /// The leading model token of a firmware model-name string, upper-cased
        /// ("R16 Black # MOT-3-V01" → "R16"), whether or not the catalog knows it.
        /// Names the SimHub device, so an unrecognised base still gets one — a
        /// generically-named device beats no device at all.
        /// </summary>
        public static string ExtractToken(string? baseModelName)
        {
            if (string.IsNullOrWhiteSpace(baseModelName))
                return "";

            var trimmed = baseModelName!.Trim();
            int space = trimmed.IndexOfAny(new[] { ' ', '\t', '#' });
            return (space > 0 ? trimmed.Substring(0, space) : trimmed).ToUpperInvariant();
        }

        /// <summary>
        /// Model token, but only when the catalog recognises it — this is what
        /// gates geometry decisions, which must never be guessed. Falls back to a
        /// longest-first prefix match so a string with no separator still resolves.
        /// Empty when nothing matches; use <see cref="ExtractToken"/> for naming.
        /// </summary>
        public static string ExtractPrefix(string? baseModelName)
        {
            var token = ExtractToken(baseModelName);
            if (token.Length == 0)
                return "";

            var trimmed = baseModelName!.Trim();
            foreach (var (prefix, _, _, _) in KnownModels)
            {
                if (string.Equals(prefix, token, StringComparison.OrdinalIgnoreCase))
                    return prefix;
            }

            // No exact token match — fall back to longest-first StartsWith so an
            // unseparated string ("R16Black") still lands on the right entry.
            string best = "";
            foreach (var (prefix, _, _, _) in KnownModels)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > best.Length)
                    best = prefix;
            }
            return best;
        }

        /// <summary>Display name for a model token; the token itself when unknown.</summary>
        public static string GetFriendlyName(string? prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return "";

            foreach (var (p, friendly, _, _) in KnownModels)
            {
                if (string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase))
                    return friendly;
            }
            return prefix!;
        }

        /// <summary>
        /// LEDs per ambient strip for a base model-name string. Unknown or empty →
        /// <see cref="DefaultLedsPerStrip"/>; a known strip-less base also reports
        /// the default, since callers that size wire buffers must never see 0 —
        /// use <see cref="HasAmbientLeds"/> to ask whether a strip exists.
        /// </summary>
        public static int LedsPerStrip(string? baseModelName)
        {
            var prefix = ExtractPrefix(baseModelName);
            if (prefix.Length == 0)
                return DefaultLedsPerStrip;

            foreach (var (p, _, leds, _) in KnownModels)
            {
                if (string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase))
                    return leds > 0 ? leds : DefaultLedsPerStrip;
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
        public static bool IsKnown(string? baseModelName) => ExtractPrefix(baseModelName).Length != 0;

        /// <summary>
        /// True when this model has an ambient LED strip. Distinct from
        /// <see cref="IsKnown"/>: R5/R9/R12 are known bases with no strip.
        /// Unknown models report false — a definition is never written for them.
        /// </summary>
        public static bool HasAmbientLeds(string? prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return false;

            foreach (var (p, _, leds, _) in KnownModels)
            {
                if (string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase))
                    return leds > 0;
            }
            return false;
        }

        /// <summary>
        /// Hardware revision code from a base version string — "D12" out of
        /// "RS21-D12-MC WB" or "RS21-D12-HW BM-C".
        ///
        /// Only base-suffixed strings count. The RS21-Dnn namespace is shared
        /// across device classes — "RS21-D01-MC PB" is a PEDAL box and
        /// "RS21-D03-…FW" a wheel — so the suffix is the only thing that makes a
        /// code a wheelbase's. Empty when the string isn't a base's.
        /// </summary>
        public static string ExtractHardwareCode(params string?[] versionStrings)
        {
            foreach (var version in versionStrings)
            {
                if (string.IsNullOrWhiteSpace(version)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(
                    version!, @"RS21-(D\d+)-(?:MC\s+WB|HW\s+BM)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return "";
        }

        /// <summary>
        /// Hardware code → thumbnail art key. A model token is coarser than the
        /// product: "R16" covers V1, V2 and the Ultra, and they look nothing alike.
        /// The hardware code is what actually identifies the unit, so art keys off
        /// it where we have evidence and falls back to the token otherwise.
        ///
        /// Only codes confirmed against a base-suffixed string are listed. Notably
        /// absent: R3, R9 and the pre-Ultra R16s — no capture has shown their codes.
        /// </summary>
        private static readonly (string Code, string ThumbnailKey)[] HardwareArt =
        {
            ("D05", "R5"),     // RS21-D05-MC WB
            ("D07", "R12"),    // RS21-D07-MC WB, PID 0x0006
            ("D11", "R21U"),   // R21 / R25 / R27
            ("D12", "R16U"),   // R16 Ultra, RS21-D12-HW BM-CU-V10
        };

        /// <summary>Art key for a hardware code, or empty when the code is unknown.</summary>
        public static string ThumbnailKeyForHardware(string? hardwareCode)
        {
            if (string.IsNullOrEmpty(hardwareCode)) return "";
            foreach (var (code, key) in HardwareArt)
                if (string.Equals(code, hardwareCode, StringComparison.OrdinalIgnoreCase))
                    return key;
            return "";
        }

        /// <summary>Strip length for a model token (0 when the model has no strip).</summary>
        public static int LedsPerStripForPrefix(string? prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return 0;

            foreach (var (p, _, leds, _) in KnownModels)
            {
                if (string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase))
                    return leds;
            }
            return 0;
        }
    }
}
