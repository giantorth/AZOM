namespace MozaPlugin.Sdk.Resources.Discovery
{
    /// <summary>
    /// Wires the Discovery resource handlers (<c>/MOZARacing/ProductDevice</c>
    /// and <c>/MOZARacing/ProductDevice/{id}</c>) into a
    /// <see cref="CoapResourceRegistry"/>. Called once at SDK-server startup
    /// from <see cref="ResourceBindings.RegisterAll"/>.
    /// </summary>
    public static class DiscoveryBindings
    {
        /// <summary>URI suffix for the device-list resource.</summary>
        public const string DeviceListUri = "/MOZARacing/ProductDevice";

        /// <summary>URI suffix template for the per-device manifest resource. <c>{id}</c> resolves against the catalog at lookup time.</summary>
        public const string DeviceManifestUri = "/MOZARacing/ProductDevice/{id}";

        /// <summary>
        /// Register both Discovery handlers on <paramref name="r"/>. The
        /// signature mirrors the Lifecycle/Motor/etc. bindings so future
        /// agents can copy the shape verbatim.
        /// </summary>
        public static void Register(CoapResourceRegistry r, DeviceCatalog catalog, MozaData data)
        {
            // 'data' is accepted for signature uniformity with other binding
            // classes that DO need it (Lifecycle/Motor/etc.). Discovery only
            // reads from the catalog which already wraps MozaData internally.
            _ = data;

            r.Bind(DeviceListUri, new DeviceListResource(catalog));
            r.Bind(DeviceManifestUri, new DeviceManifestResource(catalog));
        }
    }
}
