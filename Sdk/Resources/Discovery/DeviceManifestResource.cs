using System;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Discovery
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}</c>. GET returns the CBOR
    /// device manifest for the resolved <paramref name="id"/>; POST is 4.05.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The registry validates that <c>{id}</c> is in the catalogue before
    /// dispatching, so a non-null <see cref="CoapResourceRequest.DeviceId"/>
    /// is normally a guarantee. We still null-check the manifest lookup
    /// because a device can disconnect between resolution and execution
    /// (the catalogue is recomputed on every call from the live MozaData
    /// snapshot).
    /// </para>
    /// <para>
    /// Field order in the response is fixed by
    /// <see cref="DeviceCatalog.ToCborEntries"/> to match the wire capture
    /// byte-for-byte so future diff-of-capture vs diff-of-plugin checks are
    /// trivial.
    /// </para>
    /// </remarks>
    public sealed class DeviceManifestResource : CoapResourceHandler
    {
        private readonly DeviceCatalog _catalog;

        public DeviceManifestResource(DeviceCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            if (string.IsNullOrEmpty(req.DeviceId))
                return CoapResourceResponse.NotFound("Manifest URI requires a device id segment.");

            try
            {
                var manifest = _catalog.GetManifest(req.DeviceId!);
                if (manifest == null)
                    return CoapResourceResponse.NotFound($"No manifest for device id '{req.DeviceId}'.");

                var entries = _catalog.ToCborEntries(manifest);
                byte[] payload = CborWriter.WriteMap(entries);
                return CoapResourceResponse.Content(payload, PayloadCodec.CFCbor);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM.Sdk] DeviceManifestResource GET failed for id '{req.DeviceId}': {ex.Message}");
                return CoapResourceResponse.InternalError(ex.Message);
            }
        }

        // POST falls through to base 4.05.
    }
}
