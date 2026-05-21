using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/NaturalInertia</c>.
    /// Maps to <see cref="MozaData.NaturalInertia"/> + <c>base-natural-inertia</c>.
    /// </summary>
    internal sealed class MotorNaturalInertiaResource : MotorScalarResource
    {
        public MotorNaturalInertiaResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "NaturalInertia", d => d.NaturalInertia, "base-natural-inertia")
        {
        }
    }
}
