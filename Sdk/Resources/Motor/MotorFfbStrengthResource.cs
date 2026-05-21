using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/FfbStrength</c>.
    /// GET returns <see cref="MozaData.FfbStrength"/> as ASCII text under
    /// Content-Format 42. POST consumes a 4-byte little-endian int32 and
    /// pushes it via <c>base-ffb-strength</c>.
    /// </summary>
    internal sealed class MotorFfbStrengthResource : MotorScalarResource
    {
        public MotorFfbStrengthResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "FfbStrength", d => d.FfbStrength, "base-ffb-strength")
        {
        }
    }
}
