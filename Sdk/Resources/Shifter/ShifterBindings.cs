using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Shifter
{
    /// <summary>
    /// Wires every H-pattern shifter resource (per the SDK auto-blip group)
    /// into a <see cref="CoapResourceRegistry"/>. All entries here are
    /// <see cref="ShifterGapResource"/> placeholders — neither
    /// <see cref="MozaData"/> nor <c>MozaCommandDatabase</c> currently expose
    /// the shifter auto-blip / calibrate surface, so every URI replies with
    /// the native ERRORCODE strings on each verb (see
    /// <see cref="ShifterGapResource"/>).
    /// </summary>
    /// <remarks>
    /// The signature still accepts <see cref="MozaData"/> and
    /// <see cref="HardwareApplier"/> (currently ignored) to keep the
    /// Phase-6 binding-registration contract uniform; when shifter data /
    /// commands land, drop in real handlers without touching
    /// <see cref="ResourceBindings"/>.
    /// </remarks>
    internal static class ShifterBindings
    {
        private const string BasePath = "/MOZARacing/ProductDevice/{id}/";

        public const string HandingShifterAutoBlipOutputUri    = BasePath + "HandingShifterAutoBlipOutput";
        public const string HandingShifterAutoBlipDurationUri  = BasePath + "HandingShifterAutoBlipDuration";
        public const string HandingShifterAutoBlipSwitchUri    = BasePath + "HandingShifterAutoBlipSwitch";
        public const string ShifterCalibrateStartUri           = BasePath + "ShifterCalibrateStart";
        public const string ShifterCalibrateFinishUri          = BasePath + "ShifterCalibrateFinish";

        /// <summary>Bind every shifter gap handler.</summary>
        public static void Register(CoapResourceRegistry r, MozaData data, HardwareApplier hw)
        {
            // 'data' / 'hw' currently unused — see class remarks.
            _ = data;
            _ = hw;

            r.Bind(HandingShifterAutoBlipOutputUri,   new ShifterGapResource("HandingShifterAutoBlipOutput"));
            r.Bind(HandingShifterAutoBlipDurationUri, new ShifterGapResource("HandingShifterAutoBlipDuration"));
            r.Bind(HandingShifterAutoBlipSwitchUri,   new ShifterGapResource("HandingShifterAutoBlipSwitch"));
            r.Bind(ShifterCalibrateStartUri,          new ShifterGapResource("ShifterCalibrateStart"));
            r.Bind(ShifterCalibrateFinishUri,         new ShifterGapResource("ShifterCalibrateFinish"));
        }
    }
}
