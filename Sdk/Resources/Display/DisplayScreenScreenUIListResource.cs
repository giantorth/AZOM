using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Display
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/DisplayScreenScreenUIList</c>.
    /// SDK <c>DisplayScreenScreenUIList</c> — <c>map&lt;int,string&gt;</c> of
    /// dashboard layouts loaded on the standalone display.
    /// </summary>
    /// <remarks>
    /// Per the wheel-side <see cref="Wheel.WheelScreenUIListResource"/>: the
    /// dashboard-upload pipeline does not currently surface a list-back of
    /// loaded layouts, so GET returns an empty CBOR map (0xA0). POST is 4.05
    /// — the on-wire shape is undecoded.
    /// </remarks>
    internal sealed class DisplayScreenScreenUIListResource : CoapResourceHandler
    {
        private static readonly byte[] EmptyMapPayload = new byte[] { 0xA0 };

        public DisplayScreenScreenUIListResource(MozaData data, HardwareApplier hardware)
        {
            _ = data ?? throw new ArgumentNullException(nameof(data));
            _ = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
            => CoapResourceResponse.Content(EmptyMapPayload, PayloadCodec.CFCbor);
    }
}
