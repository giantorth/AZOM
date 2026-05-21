using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ClutchPaddleAxisMode</c>.
    /// Maps the SDK <c>SteeringWheelClutchPaddleAxisMode</c> (1..3) to
    /// <see cref="MozaData.WheelPaddlesMode"/> + <c>wheel-paddles-mode</c>.
    /// </summary>
    /// <remarks>
    /// The wire / wheel-paddles-mode command uses 1/2/3 (separate / combined /
    /// independent — see <c>docs/protocol</c>); MozaData stores the 0..2 form
    /// (raw - 1) so the existing 0-based UI dropdown can bind directly. The
    /// SDK contract is 1..3 (per <c>api-inventory.md</c> §3.5), so this
    /// handler offsets both directions: GET adds +1 to MozaData's 0..2 value,
    /// POST passes the incoming 1..3 value straight through to the wire.
    /// </remarks>
    internal sealed class WheelClutchPaddleAxisModeResource : WheelScalarResource
    {
        public WheelClutchPaddleAxisModeResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware,
                "ClutchPaddleAxisMode",
                d => d.WheelPaddlesMode,
                "wheel-paddles-mode",
                getOffset: 1,
                postOffset: 0)
        {
        }
    }
}
