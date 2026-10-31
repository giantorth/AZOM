using MozaPlugin.Telemetry.Frames;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Compression-aware scalar encode over <see cref="CompressionTable"/>, with a
    /// sanitized <c>(uint)(int)</c> fallback for uncatalogued compressions.
    /// </summary>
    public static class TelemetryEncoder
    {
        public static uint Encode(string compression, double gameValue)
        {
            if (CompressionTable.TryGetByName(compression, out var entry))
                return (uint)entry.Encode(gameValue);

            if (double.IsNaN(gameValue) || double.IsInfinity(gameValue))
                gameValue = 0.0;
            return (uint)(int)gameValue;
        }
    }
}
