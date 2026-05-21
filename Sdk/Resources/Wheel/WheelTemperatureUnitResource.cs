using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/TemperatureUnit</c>.
    /// SDK <c>SteeringWheelTemperatureUnit</c> (0=Celsius, 1=Fahrenheit).
    /// </summary>
    /// <remarks>
    /// Gap as of Phase 6b — <see cref="MozaData.UseFahrenheit"/> exists but
    /// is a plugin-local UI preference, not a wheel-resident setting, and no
    /// matching write command exists in MozaCommandDatabase. Until the
    /// vendor-facing wheel-screen temperature-unit command is decoded (the
    /// wheel firmware almost certainly has one — see PitHouse parity work)
    /// this surface is a pure no-op. Returns 4.04 / 4.05 with a one-shot
    /// WARN.
    /// </remarks>
    internal sealed class WheelTemperatureUnitResource : WheelScalarResource
    {
        public WheelTemperatureUnitResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "TemperatureUnit", read: null, commandName: null)
        {
        }
    }
}
