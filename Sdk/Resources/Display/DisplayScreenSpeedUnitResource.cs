using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Display
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/DisplayScreenSpeedUnit</c>.
    /// SDK <c>DisplayScreenSpeedUnit</c> (0=metric, 1=imperial) — applies to
    /// the standalone display sub-device (productType "Display Screen").
    /// </summary>
    /// <remarks>
    /// Gap as of Phase 6b — MozaData has no <c>DashSpeedUnit</c> field and
    /// MozaCommandDatabase has no matching write command. Returns 4.04 / 4.05
    /// with a one-shot WARN.
    /// </remarks>
    internal sealed class DisplayScreenSpeedUnitResource : DisplayScalarResource
    {
        public DisplayScreenSpeedUnitResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "DisplayScreenSpeedUnit", read: null, commandName: null)
        {
        }
    }
}
