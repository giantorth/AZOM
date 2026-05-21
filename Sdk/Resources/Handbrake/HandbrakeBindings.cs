using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Handbrake
{
    /// <summary>
    /// Wires every Handbrake resource handler into a
    /// <see cref="CoapResourceRegistry"/>. Called once at SDK-server startup
    /// from <see cref="ResourceBindings.RegisterAll"/>.
    /// </summary>
    internal static class HandbrakeBindings
    {
        private const string BasePath = "/MOZARacing/ProductDevice/{id}/";

        public const string HandbrakeOutDirUri           = BasePath + "HandbrakeOutDir";
        public const string HandbrakeApplicationModeUri  = BasePath + "HandbrakeApplicationMode";
        public const string HandbrakeNonLinearUri        = BasePath + "HandbrakeNonLinear";
        public const string HandbrakeCalibrateStartUri   = BasePath + "HandbrakeCalibrateStart";
        public const string HandbrakeCalibrateFinishUri  = BasePath + "HandbrakeCalibrateFinish";

        /// <summary>Bind every Handbrake handler.</summary>
        public static void Register(CoapResourceRegistry r, MozaData data, HardwareApplier hw)
        {
            r.Bind(HandbrakeOutDirUri,           new HandbrakeOutDirResource(data, hw));
            r.Bind(HandbrakeApplicationModeUri,  new HandbrakeApplicationModeResource(data, hw));
            r.Bind(HandbrakeNonLinearUri,        new HandbrakeNonLinearResource(data, hw));

            r.Bind(HandbrakeCalibrateStartUri,   new HandbrakeCalibrateResource(hw, "handbrake-cal-start", "HandbrakeCalibrateStart"));
            r.Bind(HandbrakeCalibrateFinishUri,  new HandbrakeCalibrateResource(hw, "handbrake-cal-stop",  "HandbrakeCalibrateFinish"));
        }
    }
}
