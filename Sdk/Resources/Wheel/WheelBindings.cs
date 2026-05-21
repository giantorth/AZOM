using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Wires the Wheel resource handlers (api-inventory §3.5) into a
    /// <see cref="CoapResourceRegistry"/>. Called once at SDK-server startup
    /// from <see cref="ResourceBindings.RegisterAll"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The URI suffix per resource matches the SDK function-name tail with
    /// the <c>SteeringWheel</c> prefix stripped (per docs/sdk/api-inventory.md
    /// §3.5 — same shape used by <c>FfbStrength</c>, <c>SoftReboot</c>,
    /// <c>CenterWheel</c> elsewhere). <c>CenterWheel</c> itself is intentionally
    /// NOT registered here — it lives in
    /// <see cref="Lifecycle.LifecycleBindings"/> alongside other one-shot
    /// actions per Phase 6a.
    /// </para>
    /// <para>
    /// Resources that are gaps as of Phase 6b (no MozaData field or no
    /// MozaCommandDatabase entry) still get bound — they return 4.04/4.05
    /// with a logged one-shot WARN. This keeps the URI surface complete
    /// from the SDK client's perspective so iRacing's discovery loop doesn't
    /// see missing endpoints.
    /// </para>
    /// </remarks>
    internal static class WheelBindings
    {
        public const string ShiftIndicatorBrightnessUri = "/MOZARacing/ProductDevice/{id}/ShiftIndicatorBrightness";
        public const string ClutchPaddleAxisModeUri     = "/MOZARacing/ProductDevice/{id}/ClutchPaddleAxisMode";
        public const string ClutchPaddleCombinePosUri   = "/MOZARacing/ProductDevice/{id}/ClutchPaddleCombinePos";
        public const string KnobModeUri                 = "/MOZARacing/ProductDevice/{id}/KnobMode";
        public const string JoystickHatswitchModeUri    = "/MOZARacing/ProductDevice/{id}/JoystickHatswitchMode";
        public const string ShiftIndicatorSwitchUri     = "/MOZARacing/ProductDevice/{id}/ShiftIndicatorSwitch";
        public const string ShiftIndicatorModeUri       = "/MOZARacing/ProductDevice/{id}/ShiftIndicatorMode";
        public const string ShiftIndicatorColorUri      = "/MOZARacing/ProductDevice/{id}/ShiftIndicatorColor";
        public const string ShiftIndicatorLightRpmUri   = "/MOZARacing/ProductDevice/{id}/ShiftIndicatorLightRpm";
        public const string SpeedUnitUri                = "/MOZARacing/ProductDevice/{id}/SpeedUnit";
        public const string TemperatureUnitUri          = "/MOZARacing/ProductDevice/{id}/TemperatureUnit";
        public const string ScreenBrightnessUri         = "/MOZARacing/ProductDevice/{id}/ScreenBrightness";
        public const string ScreenUIListUri             = "/MOZARacing/ProductDevice/{id}/ScreenUIList";
        public const string ScreenCurrentUIUri          = "/MOZARacing/ProductDevice/{id}/ScreenCurrentUI";

        /// <summary>
        /// Bind every Wheel handler on <paramref name="r"/>. Mirrors the
        /// signature of <see cref="Lifecycle.LifecycleBindings.Register"/> so
        /// the dispatcher in <see cref="ResourceBindings.RegisterAll"/> can
        /// call each Register the same way.
        /// </summary>
        public static void Register(CoapResourceRegistry r, DeviceCatalog catalog, MozaData data, HardwareApplier hw)
        {
            // 'catalog' is accepted for signature uniformity with peer
            // bindings (Discovery uses it directly). Wheel resources only
            // touch data + hw.
            _ = catalog;

            r.Bind(ShiftIndicatorBrightnessUri, new WheelShiftIndicatorBrightnessResource(data, hw));
            r.Bind(ClutchPaddleAxisModeUri,     new WheelClutchPaddleAxisModeResource(data, hw));
            r.Bind(ClutchPaddleCombinePosUri,   new WheelClutchPaddleCombinePosResource(data, hw));
            r.Bind(KnobModeUri,                 new WheelKnobModeResource(data, hw));
            r.Bind(JoystickHatswitchModeUri,    new WheelJoystickHatswitchModeResource(data, hw));
            r.Bind(ShiftIndicatorSwitchUri,     new WheelShiftIndicatorSwitchResource(data, hw));
            r.Bind(ShiftIndicatorModeUri,       new WheelShiftIndicatorModeResource(data, hw));
            r.Bind(ShiftIndicatorColorUri,      new WheelShiftIndicatorColorResource(data, hw));
            r.Bind(ShiftIndicatorLightRpmUri,   new WheelShiftIndicatorLightRpmResource(data, hw));
            r.Bind(SpeedUnitUri,                new WheelSpeedUnitResource(data, hw));
            r.Bind(TemperatureUnitUri,          new WheelTemperatureUnitResource(data, hw));
            r.Bind(ScreenBrightnessUri,         new WheelScreenBrightnessResource(data, hw));
            r.Bind(ScreenUIListUri,             new WheelScreenUIListResource(data, hw));
            r.Bind(ScreenCurrentUIUri,          new WheelScreenCurrentUIResource(data, hw));
        }
    }
}
