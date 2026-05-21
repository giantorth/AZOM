using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Handbrake
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/HandbrakeApplicationMode</c>.
    /// Selects analog-axis vs button application (0 = axis, 1 = button).
    /// Backed by <see cref="MozaData.HandbrakeMode"/> and the
    /// <c>handbrake-mode</c> wire command.
    /// </summary>
    internal sealed class HandbrakeApplicationModeResource : HandbrakeScalarResource
    {
        public HandbrakeApplicationModeResource(MozaData data, HardwareApplier hw)
            : base(data, hw, "HandbrakeApplicationMode", d => d.HandbrakeMode, "handbrake-mode")
        {
        }
    }
}
