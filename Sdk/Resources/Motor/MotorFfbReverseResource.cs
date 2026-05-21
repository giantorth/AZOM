using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/FfbReverse</c>.
    /// Maps to <see cref="MozaData.FfbReverse"/> + <c>base-ffb-reverse</c>.
    /// 0/1 toggle in practice.
    /// </summary>
    internal sealed class MotorFfbReverseResource : MotorScalarResource
    {
        public MotorFfbReverseResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "FfbReverse", d => d.FfbReverse, "base-ffb-reverse")
        {
        }
    }
}
