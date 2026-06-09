using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Handbrake
{
    /// <summary>
    /// Shared scaffolding for handbrake calibration-trigger resources
    /// (<c>HandbrakeCalibrateStart</c> / <c>HandbrakeCalibrateFinish</c>).
    /// POST fires the matching MOZA-protocol command via
    /// <see cref="HardwareApplier.WriteIfHandbrakeDetected"/>; payload is
    /// logged but ignored. GET falls through to base 4.05.
    /// </summary>
    /// <remarks>
    /// Note: handbrake uses <c>Start</c> (not the pedal "Strat" typo) per the
    /// SDK inventory — the URI segment is correctly spelled. Wire commands
    /// are <c>handbrake-cal-start</c> / <c>handbrake-cal-stop</c>.
    /// </remarks>
    internal sealed class HandbrakeCalibrateResource : CoapResourceHandler
    {
        private readonly HardwareApplier _hardware;
        private readonly string _commandName;
        private readonly string _diagnosticName;

        public HandbrakeCalibrateResource(HardwareApplier hardware, string commandName, string diagnosticName)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _commandName = commandName ?? throw new ArgumentNullException(nameof(commandName));
            _diagnosticName = diagnosticName ?? throw new ArgumentNullException(nameof(diagnosticName));
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (req.HasPayload && PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int v))
                MozaLog.Debug($"[AZOM.Sdk] {_diagnosticName} POST id={req.DeviceId} value={v}");
            else
                MozaLog.Debug($"[AZOM.Sdk] {_diagnosticName} POST id={req.DeviceId} (no/short payload)");

            try
            {
                _hardware.WriteIfHandbrakeDetected(_commandName, 1);
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
