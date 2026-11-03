namespace MozaPlugin
{
    public partial class MozaPlugin
    {
        // Compatibility entry points for integrations built against the LED
        // methods on MozaPlugin before they moved to HardwareApplier. Keep all
        // routing, stream coalescing and hardware behavior in HardwareApplier;
        // these methods neither discover devices nor reconnect a port.
        internal bool WriteDashLedBitmask(int bitmask) =>
            _hardwareApplier?.WriteDashLedBitmask(bitmask) ?? false;
        internal bool WriteDashRpmColor(int index, byte r, byte g, byte b) =>
            _hardwareApplier?.WriteDashRpmColor(index, r, g, b) ?? false;
        internal bool WriteDashFlagColors(byte[] rgb) =>
            _hardwareApplier?.WriteDashFlagColors(rgb) ?? false;
        internal bool WriteCm2LiveLedColorChunk(byte[] chunk, int chunkIdx) =>
            _hardwareApplier?.WriteCm2LiveLedColorChunk(chunk, chunkIdx) ?? false;
        internal bool WriteCm2LiveLedBitmask(byte[] activeWindow8) =>
            _hardwareApplier?.WriteCm2LiveLedBitmask(activeWindow8) ?? false;
        internal bool WriteCm2Config(string commandName, int value) =>
            _hardwareApplier?.WriteCm2Config(commandName, value) ?? false;
        internal bool WriteCm2Config(string commandName, byte[] payload) =>
            _hardwareApplier?.WriteCm2Config(commandName, payload) ?? false;
    }
}
