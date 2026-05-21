using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/KnobMode</c>.
    /// Maps SDK <c>SteeringWheelKnobMode</c> (0/1) to
    /// <see cref="MozaData.WheelKnobMode"/> + <c>wheel-knob-mode</c>.
    /// </summary>
    /// <remarks>
    /// <c>wheel-knob-mode</c> (cmd 0x40/0x3F sub 10) controls the global
    /// encoder signal mode (Buttons vs Knob output). Per-rotary signal
    /// modes — <c>WheelKnobSignalModes[0..4]</c> — are a separate, newer
    /// firmware surface and are not part of the SDK's KnobMode contract.
    /// </remarks>
    internal sealed class WheelKnobModeResource : WheelScalarResource
    {
        public WheelKnobModeResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "KnobMode", d => d.WheelKnobMode, "wheel-knob-mode")
        {
        }
    }
}
