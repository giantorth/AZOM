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
    /// Forwarded as <c>main-soft-reboot</c> when a base is detected; 2.03 Valid
    /// means "the request was accepted", including when no base is connected.
    /// </para>
    /// </remarks>
    internal sealed class SoftRebootResource : CoapResourceHandler
    {
        // Wheelbase main-firmware soft reboot (write group 0x01, cmd 0x02,
        // zero payload). See MozaCommandDatabase "main-soft-reboot".
        private const string CommandName = "main-soft-reboot";

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
