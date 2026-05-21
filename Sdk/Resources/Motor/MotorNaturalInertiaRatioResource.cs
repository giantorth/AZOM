using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/NaturalInertiaRatio</c>.
    /// Both GET and POST are gaps — neither MozaData nor MozaCommandDatabase
    /// has a matching entry as of Phase 6b. Returns 4.04 / 4.05.
    /// </summary>
    internal sealed class MotorNaturalInertiaRatioResource : MotorScalarResource
    {
        public MotorNaturalInertiaRatioResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "NaturalInertiaRatio", read: null, commandName: null)
        {
        }
    }
}
