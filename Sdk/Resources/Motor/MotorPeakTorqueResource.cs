using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/PeakTorque</c>.
    /// Maps to <see cref="MozaData.Torque"/> + <c>base-torque</c>.
    /// </summary>
    internal sealed class MotorPeakTorqueResource : MotorScalarResource
    {
        public MotorPeakTorqueResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "PeakTorque", d => d.Torque, "base-torque")
        {
        }
    }
}
