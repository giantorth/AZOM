using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Lifecycle
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/SoftReboot</c>. POST
    /// requests a soft reboot of the wheel firmware; GET is 4.05.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capture's SoftReboot POST carries a 4-byte little-endian int
    /// payload — value <c>1</c> in observed frames. We decode for parity but
    /// the only thing the wheel acts on is "POST happened"; the value is
    /// best-effort logged.
    /// </para>
    /// <para>
    /// The hardware-side command name is provisional. As of Phase 6a the
    /// <c>MozaCommandDatabase</c> does not yet contain a <c>wheel-soft-reboot</c>
    /// entry, so <see cref="HardwareApplier.WriteIfBaseConnected"/> returns
    /// false silently and the wheel does not actually reboot. We still reply
    /// 2.03 Valid because the client's contract is "the request was accepted"
    /// — once the command is added to the DB, no handler change is required.
    /// </para>
    /// </remarks>
    internal sealed class SoftRebootResource : CoapResourceHandler
    {
        // Provisional command name. Update once MozaCommandDatabase grows a
        // real entry — search the DB for "reboot" before changing.
        private const string CommandName = "wheel-soft-reboot";

        private readonly HardwareApplier _hardware;

        public SoftRebootResource(HardwareApplier hardware)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            // Body is optional from our perspective; if present, log the
            // decoded value but don't reject malformed bodies — the client
            // sometimes sends an empty POST (capture showed both shapes).
            if (req.HasPayload && PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int v))
                MozaLog.Debug($"[AZOM.Sdk] SoftReboot POST id={req.DeviceId} value={v}");
            else
                MozaLog.Debug($"[AZOM.Sdk] SoftReboot POST id={req.DeviceId} (no payload)");

            try
            {
                _hardware.WriteIfBaseConnected(CommandName, 1);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM.Sdk] SoftReboot write failed: {ex.Message}");
                return CoapResourceResponse.InternalError(ex.Message);
            }
            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
