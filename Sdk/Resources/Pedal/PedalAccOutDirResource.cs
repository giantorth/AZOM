using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/AccOutDir</c>. Direction
    /// inversion for the accelerator (throttle) axis (0 = normal, 1 = reversed).
    /// SDK names the throttle "Acc"; the underlying plugin field is
    /// <see cref="MozaData.PedalsThrottleDir"/> and the wire command is
    /// <c>pedals-throttle-dir</c>.
    /// </summary>
    internal sealed class PedalAccOutDirResource : PedalScalarResource
    {
        public PedalAccOutDirResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "AccOutDir", d => d.PedalsThrottleDir, "pedals-throttle-dir")
        {
        }
    }
}
