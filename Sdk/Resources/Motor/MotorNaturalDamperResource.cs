using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/NaturalDamper</c>.
    /// Maps to <see cref="MozaData.Damper"/> + <c>base-damper</c>.
    /// </summary>
    internal sealed class MotorNaturalDamperResource : MotorScalarResource
    {
        public MotorNaturalDamperResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "NaturalDamper", d => d.Damper, "base-damper")
        {
        }
    }
}
