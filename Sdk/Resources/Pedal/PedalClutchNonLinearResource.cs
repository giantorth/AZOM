using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ClutchNonLinear</c>.
    /// 5-element output curve for the clutch axis, backed by
    /// <see cref="MozaData.PedalsClutchCurve"/> and written via
    /// <c>pedals-clutch-y1</c>..<c>pedals-clutch-y5</c>.
    /// </summary>
    internal sealed class PedalClutchNonLinearResource : PedalNonLinearResource
    {
        public PedalClutchNonLinearResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "ClutchNonLinear", data.PedalsClutchCurve, "pedals-clutch-y")
        {
        }
    }
}
