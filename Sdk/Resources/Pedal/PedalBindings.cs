using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Wires every Pedal resource handler into a
    /// <see cref="CoapResourceRegistry"/>. Called once at SDK-server startup
    /// from <see cref="ResourceBindings.RegisterAll"/>.
    /// </summary>
    /// <remarks>
    /// Resources fall into three buckets, all under the
    /// <c>/MOZARacing/ProductDevice/{id}/</c> root:
    /// <list type="bullet">
    ///   <item><description>Scalar — <c>ClutchOutDir</c>, <c>BrakeOutDir</c>,
    ///     <c>AccOutDir</c>, <c>BrakePressCombine</c>. GET = ASCII text,
    ///     POST = 4-byte LE int32.</description></item>
    ///   <item><description>CBOR vector&lt;int&gt; — <c>ClutchNonLinear</c>,
    ///     <c>AccNonLinear</c>, <c>BrakeNonLinear</c>. 5-element output curve.</description></item>
    ///   <item><description>Calibration triggers — <c>Clutch/Acc/Brake
    ///     CalibrateStrat</c> (sic, typo preserved per native ABI) and the
    ///     matching <c>...CalibrateFinish</c>.</description></item>
    /// </list>
    /// </remarks>
    internal static class PedalBindings
    {
        private const string BasePath = "/MOZARacing/ProductDevice/{id}/";

        // Scalar properties.
        public const string ClutchOutDirUri        = BasePath + "ClutchOutDir";
        public const string BrakeOutDirUri         = BasePath + "BrakeOutDir";
        public const string AccOutDirUri           = BasePath + "AccOutDir";
        public const string BrakePressCombineUri   = BasePath + "BrakePressCombine";

        // Non-linear curves (CBOR vector<int>, length 5).
        public const string ClutchNonLinearUri     = BasePath + "ClutchNonLinear";
        public const string AccNonLinearUri        = BasePath + "AccNonLinear";
        public const string BrakeNonLinearUri      = BasePath + "BrakeNonLinear";

        // Calibration triggers. "Strat" is the native typo; preserve verbatim.
        public const string ClutchCalibrateStratUri    = BasePath + "ClutchCalibrateStrat";
        public const string ClutchCalibrateFinishUri   = BasePath + "ClutchCalibrateFinish";
        public const string AccCalibrateStratUri       = BasePath + "AccCalibrateStrat";
        public const string AccCalibrateFinishUri      = BasePath + "AccCalibrateFinish";
        public const string BrakeCalibrateStratUri     = BasePath + "BrakeCalibrateStrat";
        public const string BrakeCalibrateFinishUri    = BasePath + "BrakeCalibrateFinish";

        /// <summary>
        /// Bind every Pedal handler. Signature mirrors the other Phase 6
        /// bindings so the dispatcher in <see cref="ResourceBindings"/>
        /// stays uniform.
        /// </summary>
        public static void Register(CoapResourceRegistry r, MozaData data, HardwareApplier hw)
        {
            // Scalar.
            r.Bind(ClutchOutDirUri,      new PedalClutchOutDirResource(data, hw));
            r.Bind(BrakeOutDirUri,       new PedalBrakeOutDirResource(data, hw));
            r.Bind(AccOutDirUri,         new PedalAccOutDirResource(data, hw));
            r.Bind(BrakePressCombineUri, new PedalBrakePressCombineResource(data, hw));

            // Non-linear curves.
            r.Bind(ClutchNonLinearUri,   new PedalClutchNonLinearResource(data, hw));
            r.Bind(AccNonLinearUri,      new PedalAccNonLinearResource(data, hw));
            r.Bind(BrakeNonLinearUri,    new PedalBrakeNonLinearResource(data, hw));

            // Calibration triggers. URI-segment "Strat" is the native ABI typo.
            r.Bind(ClutchCalibrateStratUri,  new PedalCalibrateResource(hw, "pedals-clutch-cal-start",   "ClutchCalibrateStrat"));
            r.Bind(ClutchCalibrateFinishUri, new PedalCalibrateResource(hw, "pedals-clutch-cal-stop",    "ClutchCalibrateFinish"));
            r.Bind(AccCalibrateStratUri,     new PedalCalibrateResource(hw, "pedals-throttle-cal-start", "AccCalibrateStrat"));
            r.Bind(AccCalibrateFinishUri,    new PedalCalibrateResource(hw, "pedals-throttle-cal-stop",  "AccCalibrateFinish"));
            r.Bind(BrakeCalibrateStratUri,   new PedalCalibrateResource(hw, "pedals-brake-cal-start",    "BrakeCalibrateStrat"));
            r.Bind(BrakeCalibrateFinishUri,  new PedalCalibrateResource(hw, "pedals-brake-cal-stop",     "BrakeCalibrateFinish"));
        }
    }
}
