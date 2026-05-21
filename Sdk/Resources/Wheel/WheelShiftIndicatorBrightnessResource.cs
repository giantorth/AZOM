using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ShiftIndicatorBrightness</c>.
    /// Maps the SDK <c>SteeringWheelShiftIndicatorBrightness</c> (0–100) to
    /// <see cref="MozaData.WheelRpmBrightness"/> + <c>wheel-rpm-brightness</c>.
    /// </summary>
    /// <remarks>
    /// The "shift indicator" in MOZA's SDK terminology is what the plugin calls
    /// the wheel's RPM LED bar — same physical LEDs, vendor renamed the surface.
    /// </remarks>
    internal sealed class WheelShiftIndicatorBrightnessResource : WheelScalarResource
    {
        public WheelShiftIndicatorBrightnessResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "ShiftIndicatorBrightness", d => d.WheelRpmBrightness, "wheel-rpm-brightness")
        {
        }
    }
}
