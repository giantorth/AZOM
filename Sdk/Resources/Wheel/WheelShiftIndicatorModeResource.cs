using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ShiftIndicatorMode</c>.
    /// Maps SDK <c>SteeringWheelShiftIndicatorMode</c> (0/1) to
    /// <see cref="MozaData.WheelRpmDisplayMode"/> + <c>wheel-set-rpm-display-mode</c>.
    /// </summary>
    /// <remarks>
    /// MozaCommandDatabase carries a split read/write pair —
    /// <c>wheel-set-rpm-display-mode</c> (write, group 0xFF/63) and
    /// <c>wheel-get-rpm-display-mode</c> (read, group 64). For SDK POSTs we
    /// use the write side; GET reads MozaData which is populated from the
    /// read side's response parser.
    /// </remarks>
    internal sealed class WheelShiftIndicatorModeResource : WheelScalarResource
    {
        public WheelShiftIndicatorModeResource(MozaData data, HardwareApplier hardware)
            : base(data, hardware,
                "ShiftIndicatorMode",
                d => d.WheelRpmDisplayMode,
                "wheel-set-rpm-display-mode")
        {
        }
    }
}
