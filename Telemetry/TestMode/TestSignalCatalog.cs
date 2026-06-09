using System.Collections.Generic;
using MozaPlugin.Telemetry.Protocol;

namespace MozaPlugin.Telemetry.TestMode
{
    /// <summary>
    /// Resolves a final <see cref="TestSignal"/> for a channel by composing,
    /// in priority order:
    ///   1. Hard-coded overrides (<see cref="TestSignalOverrides"/>) — for
    ///      channels whose desired test behaviour can't be expressed as a
    ///      pure-bounds sweep (Gear stepping, MaxRpm constant, booleans
    ///      toggling, counters incrementing).
    ///   2. Telemetry.json <c>range</c> + <c>data_type</c> parsed by
    ///      <see cref="TestRangeParser"/>.
    ///   3. <see cref="CompressionTable"/>'s per-compression <c>TestRange</c>
    ///      as a final wire-bounds-safe fallback.
    /// Resolution happens once per channel at dashboard-load time and the
    /// result is stored on <see cref="Dashboard.ChannelDefinition.TestSignal"/>.
    /// </summary>
    public static class TestSignalCatalog
    {
        private static readonly HashSet<string> _fallbackWarned =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        private static int _fallbackCount;

        public static TestSignal Resolve(
            string name,
            string? range,
            string? dataType,
            string compression)
        {
            // 1. Override wins outright.
            if (!string.IsNullOrEmpty(name) && TestSignalOverrides.TryGet(name, out var ov))
                return ov;

            int phaseOffsetMs = !string.IsNullOrEmpty(name)
                ? TestSignalOverrides.StableHash(name) % 5000
                : 0;

            // 2. Telemetry.json range field.
            var parsed = TestRangeParser.Parse(range);
            if (parsed.Ok && parsed.Hint == TestRangeParser.ParseHint.Toggle)
                return TestSignal.Toggle(stepMs: 4000, phaseOffsetMs: phaseOffsetMs);

            // 3. data_type tells us how to interpret.
            string dt = (dataType ?? "").Trim().ToLowerInvariant();
            if (dt == "bool")
                return TestSignal.Toggle(stepMs: 4000, phaseOffsetMs: phaseOffsetMs);
            if (dt == "string")
                return TestSignal.StringConstant_("STR-" + (string.IsNullOrEmpty(name) ? "?" : name));

            // 4. Compression-based fallback for the upper bound when the
            // JSON range was "≥0" / ">0" or unparseable. uint16-style RPMy
            // channels: clamp the open-ended upper to 8000 (typical redline);
            // smaller ints get the compression's own test range.
            var compEntry = CompressionTable.TryGetByName(compression, out var entry) ? entry : null;
            double compMin = compEntry?.TestRange.min ?? 0;
            double compMax = compEntry?.TestRange.max ?? 100;

            double min, max;
            if (parsed.Ok)
            {
                min = parsed.Min;
                // Open-ended upper from ">0" / ">=N": pick from compression
                // (it's the wire-safe ceiling) but if the compression's max
                // is unrealistically high for a "rate / count" channel,
                // clamp to a visually-plausible 8000 for uint16-class.
                if (double.IsNaN(parsed.Max))
                {
                    max = compMax;
                    if (compression == "uint16_t" && max > 8000) max = 8000;
                }
                else
                {
                    max = parsed.Max;
                }
            }
            else
            {
                // Genuinely unknown. Log once with the channel name so we
                // know to add an override later.
                RegisterFallback(name);
                min = compMin;
                max = compMax;
            }

            if (max <= min) max = min + 1;

            int periodMs = 5000 + (phaseOffsetMs % 3000); // spread sweep periods a little
            return TestSignal.Sweep(min, max, periodMs: periodMs, phaseOffsetMs: phaseOffsetMs);
        }

        private static void RegisterFallback(string name)
        {
            lock (_fallbackWarned)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (_fallbackWarned.Add(name))
                    _fallbackCount++;
            }
        }

        /// <summary>
        /// Log one aggregated warning listing every channel that had to fall
        /// through to the compression-table default. Caller: invoke once after
        /// all dashboard profiles have been built so the message captures
        /// every gap in one line instead of one-per-resolve.
        /// </summary>
        public static void FlushFallbackLog()
        {
            lock (_fallbackWarned)
            {
                if (_fallbackCount == 0) return;
                var names = string.Join(", ", _fallbackWarned);
                MozaLog.Debug($"[AZOM] TestSignalCatalog: {_fallbackCount} channels use compression-default sweep (no override or parseable JSON range): {names}");
                _fallbackWarned.Clear();
                _fallbackCount = 0;
            }
        }
    }
}
