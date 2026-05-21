using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ScreenCurrentUI</c>.
    /// SDK <c>SteeringWheelScreenCurrentUI</c> (int id of currently-displayed
    /// dashboard layout).
    /// </summary>
    /// <remarks>
    /// Gap as of Phase 6b — the plugin uploads .mzdash files via the
    /// dashboard-upload channel but has no MozaData field tracking the
    /// active layout id, and no MozaCommandDatabase write command to select
    /// one. Returns 4.04 / 4.05 with a one-shot WARN. See
    /// <c>docs/protocol/dashboard-upload/</c> for the upload-side work that
    /// will eventually feed this resource.
    /// </remarks>
    internal sealed class WheelScreenCurrentUIResource : WheelScalarResource
    {
        public WheelScreenCurrentUIResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "ScreenCurrentUI", read: null, commandName: null)
        {
        }
    }
}
