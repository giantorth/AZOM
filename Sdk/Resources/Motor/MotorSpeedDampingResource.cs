using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/SpeedDamping</c>.
    /// Maps to <see cref="MozaData.SpeedDamping"/> + <c>base-speed-damping</c>.
    /// </summary>
    internal sealed class MotorSpeedDampingResource : MotorScalarResource
    {
        public MotorSpeedDampingResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "SpeedDamping", d => d.SpeedDamping, "base-speed-damping")
        {
        }
    }
}
