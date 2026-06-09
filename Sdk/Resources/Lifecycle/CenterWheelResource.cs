using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Lifecycle
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/CenterWheel</c>. POST
    /// requests that the wheelbase recenter its zero position; GET is 4.05.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Body shape matches SoftReboot: 4-byte little-endian int, value <c>1</c>
    /// in captures. We decode best-effort and pass through.
    /// </para>
    /// <para>
    /// The hardware command name is provisional — <c>MozaCommandDatabase</c>
    /// does not yet have a center-wheel entry as of Phase 6a, so this is a
    /// silent no-op on the wire. The handler still returns 2.03 Valid so the
    /// SDK contract holds; once the DB grows a real command, no resource
    /// code needs to change.
    /// </para>
    /// </remarks>
    internal sealed class CenterWheelResource : CoapResourceHandler
    {
        // Provisional. Search MozaCommandDatabase for "center"/"recenter"
        // before changing.
        private const string CommandName = "base-center-wheel";

        private readonly HardwareApplier _hardware;

        public CenterWheelResource(HardwareApplier hardware)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (req.HasPayload && PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int v))
                MozaLog.Debug($"[AZOM.Sdk] CenterWheel POST id={req.DeviceId} value={v}");
            else
                MozaLog.Debug($"[AZOM.Sdk] CenterWheel POST id={req.DeviceId} (no payload)");

            try
            {
                _hardware.WriteIfBaseConnected(CommandName, 1);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM.Sdk] CenterWheel write failed: {ex.Message}");
                return CoapResourceResponse.InternalError(ex.Message);
            }
            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
