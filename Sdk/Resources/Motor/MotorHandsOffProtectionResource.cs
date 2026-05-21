using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/HandsOffProtection</c>.
    /// Maps to <see cref="MozaData.Protection"/> + <c>base-protection</c>
    /// (PitHouse labels this slider "Hands-off protection" — the underlying
    /// wheelbase parameter is the same one driven by the existing
    /// <c>base-protection</c> command at cmd 0x29 sub 13).
    /// </summary>
    internal sealed class MotorHandsOffProtectionResource : MotorScalarResource
    {
        public MotorHandsOffProtectionResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "HandsOffProtection", d => d.Protection, "base-protection")
        {
        }
    }
}
