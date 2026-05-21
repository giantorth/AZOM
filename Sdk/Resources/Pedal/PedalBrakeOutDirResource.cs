using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/BrakeOutDir</c>. Direction
    /// inversion for the brake axis (0 = normal, 1 = reversed). GET returns
    /// <see cref="MozaData.PedalsBrakeDir"/> as ASCII text; POST decodes
    /// 4-byte LE int32 and writes via <c>pedals-brake-dir</c>.
    /// </summary>
    internal sealed class PedalBrakeOutDirResource : PedalScalarResource
    {
        public PedalBrakeOutDirResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "BrakeOutDir", d => d.PedalsBrakeDir, "pedals-brake-dir")
        {
        }
    }
}
