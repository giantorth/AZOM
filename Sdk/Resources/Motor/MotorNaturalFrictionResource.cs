using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/NaturalFriction</c>.
    /// Maps to <see cref="MozaData.Friction"/> + <c>base-friction</c>.
    /// </summary>
    internal sealed class MotorNaturalFrictionResource : MotorScalarResource
    {
        public MotorNaturalFrictionResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "NaturalFriction", d => d.Friction, "base-friction")
        {
        }
    }
}
