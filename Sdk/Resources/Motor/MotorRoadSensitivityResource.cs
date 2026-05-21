using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/RoadSensitivity</c>.
    /// POST writes via <c>base-road-sensitivity</c> (present in
    /// MozaCommandDatabase). GET returns 4.04 because MozaData has no
    /// RoadSensitivity field as of Phase 6b — adding one is out of scope.
    /// </summary>
    internal sealed class MotorRoadSensitivityResource : MotorScalarResource
    {
        public MotorRoadSensitivityResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "RoadSensitivity", read: null, commandName: "base-road-sensitivity")
        {
        }
    }
}
