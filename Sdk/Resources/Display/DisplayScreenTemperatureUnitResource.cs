using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Display
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/DisplayScreenTemperatureUnit</c>.
    /// SDK <c>DisplayScreenTemperatureUnit</c> (0=Celsius, 1=Fahrenheit) for
    /// the standalone display.
    /// </summary>
    /// <remarks>
    /// Gap as of Phase 6b — no MozaData field, no MozaCommandDatabase entry.
    /// Returns 4.04 / 4.05 with a one-shot WARN.
    /// </remarks>
    internal sealed class DisplayScreenTemperatureUnitResource : DisplayScalarResource
    {
        public DisplayScreenTemperatureUnitResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "DisplayScreenTemperatureUnit", read: null, commandName: null)
        {
        }
    }
}
