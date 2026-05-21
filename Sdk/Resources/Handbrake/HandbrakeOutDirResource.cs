using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Handbrake
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/HandbrakeOutDir</c>.
    /// Direction inversion for the handbrake axis (0 = normal, 1 = reversed).
    /// Backed by <see cref="MozaData.HandbrakeDirection"/> and the
    /// <c>handbrake-direction</c> wire command.
    /// </summary>
    internal sealed class HandbrakeOutDirResource : HandbrakeScalarResource
    {
        public HandbrakeOutDirResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "HandbrakeOutDir", d => d.HandbrakeDirection, "handbrake-direction")
        {
        }
    }
}
