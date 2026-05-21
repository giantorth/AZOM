using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/SpeedDampingStartPoint</c>.
    /// Maps to <see cref="MozaData.SpeedDampingPoint"/> +
    /// <c>base-speed-damping-point</c> (the command name uses "point" rather
    /// than "start-point" but is the same parameter).
    /// </summary>
    internal sealed class MotorSpeedDampingStartPointResource : MotorScalarResource
    {
        public MotorSpeedDampingStartPointResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "SpeedDampingStartPoint", d => d.SpeedDampingPoint, "base-speed-damping-point")
        {
        }
    }
}
