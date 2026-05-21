using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ClutchPaddleCombinePos</c>.
    /// SDK <c>SteeringWheelClutchPaddleCombinePos</c> (0..100).
    /// </summary>
    /// <remarks>
    /// Both GET and POST are gaps as of Phase 6b — neither MozaData nor
    /// MozaCommandDatabase has a "combine position" entry. The wheel hardware
    /// supports this in the vendor SDK but the plugin's protocol surface
    /// hasn't been extended to cover it yet. Returns 4.04 / 4.05 with a single
    /// one-shot WARN.
    /// </remarks>
    internal sealed class WheelClutchPaddleCombinePosResource : WheelScalarResource
    {
        public WheelClutchPaddleCombinePosResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "ClutchPaddleCombinePos", read: null, commandName: null)
        {
        }
    }
}
