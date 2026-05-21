using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/SpringStrength</c>.
    /// Maps to <see cref="MozaData.Spring"/> + <c>base-spring</c>.
    /// </summary>
    internal sealed class MotorSpringStrengthResource : MotorScalarResource
    {
        public MotorSpringStrengthResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "SpringStrength", d => d.Spring, "base-spring")
        {
        }
    }
}
