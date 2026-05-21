using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/BrakeNonLinear</c>.
    /// 5-element output curve for the brake axis, backed by
    /// <see cref="MozaData.PedalsBrakeCurve"/> and written via
    /// <c>pedals-brake-y1</c>..<c>pedals-brake-y5</c>.
    /// </summary>
    internal sealed class PedalBrakeNonLinearResource : PedalNonLinearResource
    {
        public PedalBrakeNonLinearResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "BrakeNonLinear", data.PedalsBrakeCurve, "pedals-brake-y")
        {
        }
    }
}
