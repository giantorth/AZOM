using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/LimitWheelSpeed</c>.
    /// Both GET and POST are gaps — neither MozaData nor MozaCommandDatabase
    /// has a matching entry as of Phase 6b. Returns 4.04 / 4.05 with a single
    /// MozaLog.Warn per session.
    /// </summary>
    internal sealed class MotorLimitWheelSpeedResource : MotorScalarResource
    {
        public MotorLimitWheelSpeedResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware, "LimitWheelSpeed", read: null, commandName: null)
        {
        }
    }
}
