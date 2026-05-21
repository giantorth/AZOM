using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/JoystickHatswitchMode</c>.
    /// SDK <c>SteeringWheelJoystickHatswitchMode</c> (0/1).
    /// </summary>
    /// <remarks>
    /// Both GET and POST are gaps — neither MozaData nor MozaCommandDatabase
    /// surface a hatswitch toggle today (the existing <c>wheel-stick-mode</c>
    /// covers thumbstick output; "hatswitch" appears to be a distinct vendor
    /// concept covered only by the SDK). Returns 4.04 / 4.05 with a one-shot
    /// WARN.
    /// </remarks>
    internal sealed class WheelJoystickHatswitchModeResource : WheelScalarResource
    {
        public WheelJoystickHatswitchModeResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "JoystickHatswitchMode", read: null, commandName: null)
        {
        }
    }
}
