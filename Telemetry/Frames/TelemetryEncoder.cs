using MozaPlugin.Telemetry.Protocol;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Thin facade over <see cref="CompressionTable"/> — preserves the v1 call
    /// sites in <see cref="TelemetryFrameBuilder"/> and <see cref="TelemetrySender"/>
    /// while delegating all data to the single authoritative table.
    /// </summary>
    public static class TelemetryEncoder
    {
        public static bool IsDouble(string compression) =>
            CompressionTable.TryGetByName(compression, out var e) && e.BitWidth == 64;

        public static bool IsFloat(string compression) => compression == "float";

        public static uint Encode(string compression, double gameValue)
        {
            if (CompressionTable.TryGetByName(compression, out var entry))
                return (uint)entry.Encode(gameValue);

            if (double.IsNaN(gameValue) || double.IsInfinity(gameValue))
                gameValue = 0.0;
            return (uint)(int)gameValue;
        }

        // Per-compression wire-bounds-safe range. No longer the primary
        // source for test-mode sweep values — that's now resolved per channel
        // in Telemetry/TestMode/TestSignalCatalog.cs from Telemetry.json
        // range + overrides. Kept as a final fallback for channels without a
        // parseable range / override.
        public static (double min, double max) GetTestRange(string compression)
        {
            if (CompressionTable.TryGetByName(compression, out var entry))
                return entry.TestRange;
            return (0.0, 1.0);
        }
    }
}
