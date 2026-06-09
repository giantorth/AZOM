using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ShiftIndicatorColor</c>.
    /// SDK <c>SteeringWheelShiftIndicatorColor</c> — a <c>vector&lt;string&gt;</c>
    /// of per-LED color strings (one per RPM LED on the wheel's shift
    /// indicator bar).
    /// </summary>
    /// <remarks>
    /// <para>
    /// GET emits a CBOR array of <c>"#RRGGBB"</c> hex strings, one per LED,
    /// in the LED order the wheel firmware uses (LED 1 first). The length
    /// matches the plugin's known per-wheel RPM LED count when available
    /// (capped at <see cref="MozaData.WheelRpmColors"/>.Length) — non-existent
    /// LEDs default to "#000000".
    /// </para>
    /// <para>
    /// POST decodes a CBOR array of color strings and writes them per-LED via
    /// <c>wheel-rpm-color{N}</c> (1-indexed). Color strings accept the
    /// <c>"#RRGGBB"</c> / <c>"RRGGBB"</c> shape; unknown text falls through to
    /// black (0,0,0) rather than rejecting the whole payload because the
    /// vendor SDK also accepts color name strings ("red", "green", ...) that
    /// we cannot enumerate exhaustively. A one-shot WARN is emitted on the
    /// first unparsable entry so a maintainer can tighten the parser later.
    /// </para>
    /// </remarks>
    internal sealed class WheelShiftIndicatorColorResource : CoapResourceHandler
    {
        // Vendor SDK accepts color names; we recognise the common ones used in
        // PitHouse's default profiles + the six "flag" canonicals. Unknown
        // names fall through to black with a one-shot WARN.
        private static readonly Dictionary<string, (byte r, byte g, byte b)> NamedColors =
            new Dictionary<string, (byte, byte, byte)>(StringComparer.OrdinalIgnoreCase)
            {
                ["black"]   = (0, 0, 0),
                ["white"]   = (255, 255, 255),
                ["red"]     = (255, 0, 0),
                ["green"]   = (0, 255, 0),
                ["blue"]    = (0, 0, 255),
                ["yellow"]  = (255, 255, 0),
                ["magenta"] = (255, 0, 255),
                ["cyan"]    = (0, 255, 255),
                ["orange"]  = (255, 128, 0),
                ["purple"]  = (128, 0, 128),
            };

        private readonly MozaData _data;
        private readonly HardwareApplier _hardware;
        private int _unparsedWarned;

        public WheelShiftIndicatorColorResource(MozaData data, HardwareApplier hardware)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            var colors = _data.WheelRpmColors;
            int count = colors?.Length ?? 0;
            var asStrings = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                var c = colors![i];
                if (c == null || c.Length < 3)
                {
                    asStrings.Add("#000000");
                    continue;
                }
                asStrings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "#{0:X2}{1:X2}{2:X2}", c[0], c[1], c[2]));
            }
            try
            {
                byte[] payload = CborWriter.WriteArray(asStrings);
                return CoapResourceResponse.Content(payload, PayloadCodec.CFCbor);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM.Sdk] WheelShiftIndicatorColor GET failed: {ex.Message}");
                return CoapResourceResponse.InternalError(ex.Message);
            }
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!req.HasPayload)
                return CoapResourceResponse.BadRequest("empty CBOR payload");

            List<string> incoming;
            try
            {
                incoming = CborReader.ReadArrayOfText(req.Payload);
            }
            catch (CborFormatException ex)
            {
                return CoapResourceResponse.BadRequest($"malformed CBOR: {ex.Message}");
            }

            int max = Math.Min(incoming.Count, _data.WheelRpmColors.Length);
            for (int i = 0; i < max; i++)
            {
                if (!TryParseColor(incoming[i], out byte r, out byte g, out byte b))
                {
                    if (Interlocked.CompareExchange(ref _unparsedWarned, 1, 0) == 0)
                    {
                        MozaLog.Warn(
                            $"[AZOM.Sdk] WheelShiftIndicatorColor: unrecognised color text '{incoming[i]}' " +
                            "— falling through to black. Encoding map TBD vs real PitHouse capture.");
                    }
                    r = g = b = 0;
                }
                // Per-LED command name is 1-based: wheel-rpm-color1..wheel-rpm-colorN.
                _hardware.WriteColorIfWheelDetected($"wheel-rpm-color{i + 1}", r, g, b);
            }
            return CoapResourceResponse.Valid();
        }

        /// <summary>
        /// Parse a color string. Accepts:
        /// <list type="bullet">
        ///   <item><description><c>"#RRGGBB"</c> or <c>"RRGGBB"</c> hex (case-insensitive)</description></item>
        ///   <item><description>A handful of named colors (red, green, blue, ...)</description></item>
        /// </list>
        /// Returns false on anything else; callers should fall through to black
        /// and emit a one-time WARN per resource.
        /// </summary>
        internal static bool TryParseColor(string text, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 6 &&
                byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte rb) &&
                byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte gb) &&
                byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte bb))
            {
                r = rb; g = gb; b = bb;
                return true;
            }
            if (NamedColors.TryGetValue(text.Trim(), out var named))
            {
                r = named.r; g = named.g; b = named.b;
                return true;
            }
            return false;
        }
    }
}
