using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Display
{
    /// <summary>
    /// Wires the standalone-Display resource handlers (api-inventory §3.6)
    /// into a <see cref="CoapResourceRegistry"/>. Called once at SDK-server
    /// startup from <see cref="ResourceBindings.RegisterAll"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>URI-prefix decision</b>: the API inventory §3.6 lists the standalone
    /// display as a separate family (<c>get/setDisplayScreenSpeedUnit</c>
    /// etc.) — same setting names as the wheel's screen, distinct device.
    /// <see cref="DeviceCatalog"/> assigns a separate device-ID to the
    /// display sub-device when its MCU UID is present (productType
    /// "Display Screen"), so wheel and display URIs share the same
    /// <c>/MOZARacing/ProductDevice/{id}/…</c> template prefix and are
    /// disambiguated purely by which device-ID the client targets.
    /// </para>
    /// <para>
    /// The registry, however, matches schemas by literal segment order — it
    /// cannot disambiguate two schemas that differ only by which device-ID
    /// (wheel vs display) the caller plugs into <c>{id}</c>. If we bound the
    /// same <c>/MOZARacing/ProductDevice/{id}/SpeedUnit</c> twice (once via
    /// WheelBindings, once here), the second <c>Bind</c> would REPLACE the
    /// wheel handler per
    /// <see cref="CoapResourceRegistry.Bind"/>'s re-bind semantics.
    /// </para>
    /// <para>
    /// To keep both surfaces alive simultaneously we prefix the Display
    /// URIs with the SDK C-function-name's <c>DisplayScreen</c> qualifier
    /// (e.g. <c>/MOZARacing/ProductDevice/{id}/DisplayScreenSpeedUnit</c>).
    /// This is unambiguous and reflects the SDK function-name shape used in
    /// <c>docs/sdk/api-inventory.md</c> §3.6. TODO: verify against a real
    /// MOZA SDK client on a Windows box — the live PitHouse capture didn't
    /// include any display URIs, so this prefix shape is a best-guess until
    /// a capture from a wheel-plus-display setup confirms which form the
    /// vendor SDK actually emits.
    /// </para>
    /// </remarks>
    internal static class DisplayBindings
    {
        public const string SpeedUnitUri        = "/MOZARacing/ProductDevice/{id}/DisplayScreenSpeedUnit";
        public const string TemperatureUnitUri  = "/MOZARacing/ProductDevice/{id}/DisplayScreenTemperatureUnit";
        public const string ScreenBrightnessUri = "/MOZARacing/ProductDevice/{id}/DisplayScreenScreenBrightness";
        public const string ScreenCurrentUIUri  = "/MOZARacing/ProductDevice/{id}/DisplayScreenScreenCurrentUI";
        public const string ScreenUIListUri     = "/MOZARacing/ProductDevice/{id}/DisplayScreenScreenUIList";

        /// <summary>
        /// Bind every Display handler on <paramref name="r"/>. Mirrors the
        /// signature of <see cref="Wheel.WheelBindings.Register"/>.
        /// </summary>
        public static void Register(CoapResourceRegistry r, DeviceCatalog catalog, MozaData data, HardwareApplier hw)
        {
            _ = catalog;

            r.Bind(SpeedUnitUri,        new DisplayScreenSpeedUnitResource(data, hw));
            r.Bind(TemperatureUnitUri,  new DisplayScreenTemperatureUnitResource(data, hw));
            r.Bind(ScreenBrightnessUri, new DisplayScreenScreenBrightnessResource(data, hw));
            r.Bind(ScreenCurrentUIUri,  new DisplayScreenScreenCurrentUIResource(data, hw));
            r.Bind(ScreenUIListUri,     new DisplayScreenScreenUIListResource(data, hw));
        }
    }
}
