using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/RoadSensitivity</c>.
    /// POST writes via <c>base-road-sensitivity</c>; GET returns the
    /// device-read value mirrored in <see cref="MozaData.RoadSensitivity"/>.
    /// </summary>
    internal sealed class MotorRoadSensitivityResource : MotorScalarResource
    {
        public MotorRoadSensitivityResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "RoadSensitivity", read: d => d.RoadSensitivity, commandName: "base-road-sensitivity")
        {
        }
    }
}
