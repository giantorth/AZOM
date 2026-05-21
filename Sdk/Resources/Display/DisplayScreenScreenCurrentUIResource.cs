using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Display
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/DisplayScreenScreenCurrentUI</c>.
    /// SDK <c>DisplayScreenScreenCurrentUI</c> (int id of currently displayed
    /// layout) for the standalone display.
    /// </summary>
    /// <remarks>
    /// Gap as of Phase 6b — no MozaData field, no MozaCommandDatabase entry.
    /// Same dashboard-upload caveat applies as for the wheel-side
    /// <c>ScreenCurrentUI</c>: see <c>docs/protocol/dashboard-upload/</c>.
    /// Returns 4.04 / 4.05 with a one-shot WARN.
    /// </remarks>
    internal sealed class DisplayScreenScreenCurrentUIResource : DisplayScalarResource
    {
        public DisplayScreenScreenCurrentUIResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "DisplayScreenScreenCurrentUI", read: null, commandName: null)
        {
        }
    }
}
