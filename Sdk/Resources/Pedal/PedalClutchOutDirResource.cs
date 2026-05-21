using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ClutchOutDir</c>. Direction
    /// inversion for the clutch axis (0 = normal, 1 = reversed). GET returns
    /// <see cref="MozaData.PedalsClutchDir"/> as ASCII text; POST decodes
    /// 4-byte LE int32 and writes via <c>pedals-clutch-dir</c>.
    /// </summary>
    internal sealed class PedalClutchOutDirResource : PedalScalarResource
    {
        public PedalClutchOutDirResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "ClutchOutDir", d => d.PedalsClutchDir, "pedals-clutch-dir")
        {
        }
    }
}
