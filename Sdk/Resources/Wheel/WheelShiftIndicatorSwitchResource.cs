using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ShiftIndicatorSwitch</c>.
    /// Maps SDK <c>SteeringWheelShiftIndicatorSwitch</c> (1..3) to
    /// <see cref="MozaData.WheelRpmIndicatorMode"/> + <c>wheel-rpm-indicator-mode</c>.
    /// </summary>
    /// <remarks>
    /// Like ClutchPaddleAxisMode, the wire takes 1..3 but MozaData stores the
    /// raw - 1 form (0..2) for the existing UI dropdown. GET adds +1; POST
    /// passes through verbatim.
    /// </remarks>
    internal sealed class WheelShiftIndicatorSwitchResource : WheelScalarResource
    {
        public WheelShiftIndicatorSwitchResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware,
                "ShiftIndicatorSwitch",
                d => d.WheelRpmIndicatorMode,
                "wheel-rpm-indicator-mode",
                getOffset: 1,
                postOffset: 0)
        {
        }
    }
}
