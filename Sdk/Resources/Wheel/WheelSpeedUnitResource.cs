using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/SpeedUnit</c>.
    /// SDK <c>SteeringWheelSpeedUnit</c> (0=metric, 1=imperial).
    /// </summary>
    /// <remarks>
    /// Gap as of Phase 6b — neither MozaData nor MozaCommandDatabase tracks
    /// the wheel-screen speed unit today (UseFahrenheit covers temperature
    /// only, no analogous speed flag). Returns 4.04 / 4.05 with a one-shot
    /// WARN.
    /// </remarks>
    internal sealed class WheelSpeedUnitResource : WheelScalarResource
    {
        public WheelSpeedUnitResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "SpeedUnit", read: null, commandName: null)
        {
        }
    }
}
