using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Wires every Motor resource handler into a
    /// <see cref="CoapResourceRegistry"/>. Called once at SDK-server startup
    /// from <see cref="ResourceBindings.RegisterAll"/>.
    /// </summary>
    /// <remarks>
    /// Resources fall into four buckets:
    /// <list type="bullet">
    ///   <item><description>Scalar properties (GET ASCII text, POST LE int32) — most of the table.</description></item>
    ///   <item><description>CBOR pair / map — <c>LimitAngle</c>, <c>EqualizerAmp</c>.</description></item>
    ///   <item><description>Vendor gaps — <c>motorMoveTo</c>, <c>motorStopMove</c>: GET 4.05 / POST 4.00.</description></item>
    ///   <item><description>Partner API one-shot probes — <c>Feedforward</c>, <c>HighFrequencyTorque</c>, <c>SetMotorRunState</c>: forwarded to base via CDC (group 0x2A / 0x2C writes; persists to EEPROM Tables 11/5).</description></item>
    /// </list>
    /// <see cref="Lifecycle.LifecycleBindings"/> already owns <c>SoftReboot</c>
    /// and <c>CenterWheel</c>; do not duplicate them here.
    /// </remarks>
    internal static class MotorBindings
    {
        // URI suffix constants — kept here (not in each resource file) so the
        // listener-side trace can grep one place when a property changes name.
        private const string BasePath = "/MOZARacing/ProductDevice/{id}/";

        public const string FfbStrengthUri              = BasePath + "FfbStrength";
        public const string RoadSensitivityUri          = BasePath + "RoadSensitivity";
        public const string LimitWheelSpeedUri          = BasePath + "LimitWheelSpeed";
        public const string SpringStrengthUri           = BasePath + "SpringStrength";
        public const string NaturalDamperUri            = BasePath + "NaturalDamper";
        public const string NaturalFrictionUri          = BasePath + "NaturalFriction";
        public const string SpeedDampingUri             = BasePath + "SpeedDamping";
        public const string NaturalInertiaUri           = BasePath + "NaturalInertia";
        public const string NaturalInertiaRatioUri      = BasePath + "NaturalInertiaRatio";
        public const string SpeedDampingStartPointUri   = BasePath + "SpeedDampingStartPoint";
        public const string HandsOffProtectionUri       = BasePath + "HandsOffProtection";
        public const string FfbReverseUri               = BasePath + "FfbReverse";
        public const string PeakTorqueUri               = BasePath + "PeakTorque";
        public const string LimitAngleUri               = BasePath + "LimitAngle";
        public const string EqualizerAmpUri             = BasePath + "EqualizerAmp";
        public const string MotorMoveToUri              = BasePath + "motorMoveTo";
        public const string MotorStopMoveUri            = BasePath + "motorStopMove";
        public const string FeedforwardUri              = BasePath + "Feedforward";
        public const string HighFrequencyTorqueUri      = BasePath + "HighFrequencyTorque";
        public const string SetMotorRunStateUri         = BasePath + "SetMotorRunState";

        /// <summary>
        /// Bind every Motor handler. Signature mirrors the other Phase 6
        /// bindings so the dispatcher in <see cref="ResourceBindings"/>
        /// stays uniform.
        /// </summary>
        public static void Register(CoapResourceRegistry r, MozaData data, HardwareApplier hw)
        {
            // Scalar properties.
            r.Bind(FfbStrengthUri,            new MotorFfbStrengthResource(data, hw));
            r.Bind(RoadSensitivityUri,        new MotorRoadSensitivityResource(data, hw));
            r.Bind(LimitWheelSpeedUri,        new MotorLimitWheelSpeedResource(data, hw));
            r.Bind(SpringStrengthUri,         new MotorSpringStrengthResource(data, hw));
            r.Bind(NaturalDamperUri,          new MotorNaturalDamperResource(data, hw));
            r.Bind(NaturalFrictionUri,        new MotorNaturalFrictionResource(data, hw));
            r.Bind(SpeedDampingUri,           new MotorSpeedDampingResource(data, hw));
            r.Bind(NaturalInertiaUri,         new MotorNaturalInertiaResource(data, hw));
            r.Bind(NaturalInertiaRatioUri,    new MotorNaturalInertiaRatioResource(data, hw));
            r.Bind(SpeedDampingStartPointUri, new MotorSpeedDampingStartPointResource(data, hw));
            r.Bind(HandsOffProtectionUri,     new MotorHandsOffProtectionResource(data, hw));
            r.Bind(FfbReverseUri,             new MotorFfbReverseResource(data, hw));
            r.Bind(PeakTorqueUri,             new MotorPeakTorqueResource(data, hw));

            // CBOR map properties.
            r.Bind(LimitAngleUri,             new MotorLimitAngleResource(data, hw));
            r.Bind(EqualizerAmpUri,           new MotorEqualizerAmpResource(data, hw));

            // Vendor gaps — explicit BadRequest on POST.
            r.Bind(MotorMoveToUri,            new MotorMoveToResource());
            r.Bind(MotorStopMoveUri,          new MotorStopMoveResource());

            // Partner-API channels — one-shot capability probes that PitHouse
            // forwards to wheelbase EEPROM. See class docs for CDC mapping.
            r.Bind(FeedforwardUri,            new MotorFeedforwardResource(hw));
            r.Bind(HighFrequencyTorqueUri,    new MotorHighFrequencyTorqueResource(hw));
            r.Bind(SetMotorRunStateUri,       new MotorSetMotorRunStateResource(hw));
        }
    }
}
