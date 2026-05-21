using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ScreenUIList</c>.
    /// SDK <c>SteeringWheelScreenUIList</c> — a <c>map&lt;int,string&gt;</c>
    /// listing the dashboard layouts currently loaded on the wheel screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GET returns an empty CBOR map (initial byte 0xA0 — RFC 8949 major type
    /// 5 with arg 0). The plugin uploads .mzdash files to the wheel but the
    /// upload pipeline does not currently expose a list of loaded layouts
    /// back through MozaData, so we emit the empty-map shape rather than
    /// fabricating entries.
    /// </para>
    /// <para>
    /// POST is 4.05 — the SDK header defines a setter but the on-wire shape
    /// for "set the loaded UI list" has not been decoded from PitHouse
    /// captures, and writes to this surface in the wild appear to be
    /// no-ops in practice (the wheel updates its list automatically when
    /// the host pushes .mzdash files).
    /// </para>
    /// </remarks>
    internal sealed class WheelScreenUIListResource : CoapResourceHandler
    {
        // Pre-allocated empty CBOR map (major type 5 with arg 0 → 0xA0).
        private static readonly byte[] EmptyMapPayload = new byte[] { 0xA0 };

        public WheelScreenUIListResource(MozaData data, HardwareApplier hardware)
        {
            // Constructor takes data + hardware for signature parity with the
            // other Wheel resources; both are unused today because the empty
            // map doesn't depend on cached state.
            _ = data ?? throw new ArgumentNullException(nameof(data));
            _ = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
            => CoapResourceResponse.Content(EmptyMapPayload, PayloadCodec.CFCbor);

        // POST falls through to base 4.05.
    }
}
