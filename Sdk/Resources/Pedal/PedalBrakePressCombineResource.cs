using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/BrakePressCombine</c>.
    /// Two-sensor brake-pedal mixing ratio (0 = pure angle sensor, 100 = pure
    /// load cell), per the inventory's 0..100 documentation. Backed by
    /// <see cref="MozaData.PedalsBrakeAngleRatio"/> and the
    /// <c>pedals-brake-angle-ratio</c> wire command (a 4-byte float command —
    /// hence <c>writeAsFloat: true</c>).
    /// </summary>
    internal sealed class PedalBrakePressCombineResource : PedalScalarResource
    {
        public PedalBrakePressCombineResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "BrakePressCombine", d => d.PedalsBrakeAngleRatio, "pedals-brake-angle-ratio", writeAsFloat: true)
        {
        }
    }
}
