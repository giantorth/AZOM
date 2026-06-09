using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Shared scaffolding for pedal calibration-trigger resources. Each
    /// concrete URI (Clutch/Acc/Brake Calibrate{Strat,Finish}) POSTs to fire
    /// the matching <see cref="HardwareApplier.WriteIfPedalsDetected"/>
    /// command with value <c>1</c>; the payload (4-byte LE int) is logged
    /// for parity but otherwise ignored. GET falls through to base 4.05.
    /// </summary>
    /// <remarks>
    /// The "Strat" typo in <c>Clutch/Acc/Brake CalibrateStrat</c> is preserved
    /// at the URI level — native SDK callers spell it that way and the bug
    /// is part of the ABI. Wire-side command names (<c>pedals-*-cal-start</c>)
    /// use the corrected spelling because that's what the device firmware
    /// accepts.
    /// </remarks>
    internal sealed class PedalCalibrateResource : CoapResourceHandler
    {
        private readonly HardwareApplier _hardware;
        private readonly string _commandName;
        private readonly string _diagnosticName;

        public PedalCalibrateResource(HardwareApplier hardware, string commandName, string diagnosticName)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _commandName = commandName ?? throw new ArgumentNullException(nameof(commandName));
            _diagnosticName = diagnosticName ?? throw new ArgumentNullException(nameof(diagnosticName));
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            // Best-effort payload log: captures show a 4-byte LE int (value 1)
            // but the trigger semantics are "the POST itself happened".
            if (req.HasPayload && PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int v))
                MozaLog.Debug($"[AZOM.Sdk] {_diagnosticName} POST id={req.DeviceId} value={v}");
            else
                MozaLog.Debug($"[AZOM.Sdk] {_diagnosticName} POST id={req.DeviceId} (no/short payload)");

            try
            {
                _hardware.WriteIfPedalsDetected(_commandName, 1);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM.Sdk] {_diagnosticName} write failed: {ex.Message}");
                return CoapResourceResponse.InternalError(ex.Message);
            }
            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
