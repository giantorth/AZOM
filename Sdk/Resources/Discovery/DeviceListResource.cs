using System;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Discovery
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice</c>.
    /// <list type="bullet">
    ///   <item><description>GET — returns a CBOR array of currently-known
    ///     device IDs (<see cref="DeviceCatalog.EnumerateDeviceIds"/>),
    ///     Content-Format <c>application/cbor</c>. Empty list is valid and
    ///     is returned when no device has reported an MCU UID yet — clients
    ///     poll for this and tolerate emptiness during disconnects.</description></item>
    ///   <item><description>POST — 4.05 Method Not Allowed. iRacing only
    ///     reads this resource.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// This is a Phase 6a resource. CoAP Observe is NOT supported here yet —
    /// device-list changes are sparse and clients re-poll on reconnect. If
    /// future PitHouse parity work requires push notifications for catalogue
    /// churn, flip <see cref="CoapResourceHandler.SupportsObserve"/> and add
    /// a notifier hook into <see cref="DeviceCatalog"/>.
    /// </remarks>
    public sealed class DeviceListResource : CoapResourceHandler
    {
        private readonly DeviceCatalog _catalog;

        public DeviceListResource(DeviceCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            try
            {
                var ids = _catalog.EnumerateDeviceIds();
                byte[] payload = CborWriter.WriteArray(ids);
                return CoapResourceResponse.Content(payload, PayloadCodec.CFCbor);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza.Sdk] DeviceListResource GET failed: {ex.Message}");
                return CoapResourceResponse.InternalError(ex.Message);
            }
        }

        // POST falls through to base 4.05.
    }
}
