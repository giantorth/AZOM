using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Lifecycle
{
    /// <summary>
    /// Wires the Lifecycle resource handlers into a
    /// <see cref="CoapResourceRegistry"/>. Called once at SDK-server startup
    /// from <see cref="ResourceBindings.RegisterAll"/>.
    /// </summary>
    /// <remarks>
    /// Lifecycle URIs cover the "what is this thing" half of the SDK
    /// surface (the other half being per-property reads/writes that
    /// Motor/Wheel/Display etc. agents add): server presence
    /// (<c>SdkState</c>) and one-shot device actions (<c>SoftReboot</c>,
    /// <c>CenterWheel</c>).
    /// </remarks>
    internal static class LifecycleBindings
    {
        /// <summary>URI suffix for the SDK-state probe (PitHouse parity replies 4.04 to POST).</summary>
        public const string SdkStateUri = "/MOZARacing/SdkState";

        /// <summary>URI suffix template for the soft-reboot action.</summary>
        public const string SoftRebootUri = "/MOZARacing/ProductDevice/{id}/SoftReboot";

        /// <summary>URI suffix template for the center-wheel action.</summary>
        public const string CenterWheelUri = "/MOZARacing/ProductDevice/{id}/CenterWheel";

        /// <summary>
        /// Bind all Lifecycle handlers on <paramref name="r"/>. The
        /// signature deliberately mirrors
        /// <see cref="Discovery.DiscoveryBindings.Register"/> plus the
        /// HardwareApplier argument that future agents will also need.
        /// </summary>
        public static void Register(CoapResourceRegistry r, DeviceCatalog catalog, MozaData data, HardwareApplier hw)
        {
            // 'catalog' and 'data' are accepted for signature uniformity
            // with future bindings (Motor/Wheel/etc.) that DO need them.
            _ = catalog;
            _ = data;

            r.Bind(SdkStateUri,     new SdkStateResource());
            r.Bind(SoftRebootUri,   new SoftRebootResource(hw));
            r.Bind(CenterWheelUri,  new CenterWheelResource(hw));
        }
    }
}
