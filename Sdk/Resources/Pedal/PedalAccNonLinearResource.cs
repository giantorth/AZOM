using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/AccNonLinear</c>.
    /// 5-element output curve for the accelerator (throttle) axis, backed by
    /// <see cref="MozaData.PedalsThrottleCurve"/> and written via
    /// <c>pedals-throttle-y1</c>..<c>pedals-throttle-y5</c>.
    /// </summary>
    internal sealed class PedalAccNonLinearResource : PedalNonLinearResource
    {
        public PedalAccNonLinearResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "AccNonLinear", data.PedalsThrottleCurve, "pedals-throttle-y")
        {
        }
    }
}
