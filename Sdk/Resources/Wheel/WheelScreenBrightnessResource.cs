using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ScreenBrightness</c>.
    /// SDK <c>SteeringWheelScreenBrightness</c> (0..100) — the WHEEL's onboard
    /// screen, distinct from the standalone dash (<c>DashDisplayBrightness</c>)
    /// and the standalone display sub-device (<c>DisplayScreen*</c>).
    /// </summary>
    /// <remarks>
    /// Gap as of Phase 6b — no <c>wheel-screen-brightness</c> entry in the
    /// MozaCommandDatabase and no corresponding MozaData field. The KS Pro
    /// wheel exposes a screen brightness slider in PitHouse so the wire
    /// command is known to exist; it simply hasn't been added to the
    /// plugin's command DB yet. Returns 4.04 / 4.05 with a one-shot WARN.
    /// </remarks>
    internal sealed class WheelScreenBrightnessResource : WheelScalarResource
    {
        public WheelScreenBrightnessResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "ScreenBrightness", read: null, commandName: null)
        {
        }
    }
}
