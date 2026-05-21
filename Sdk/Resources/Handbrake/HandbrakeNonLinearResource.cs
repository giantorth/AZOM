using System;
using System.Collections.Generic;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Handbrake
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/HandbrakeNonLinear</c>.
    /// 5-element output curve for the handbrake axis, exposed to the SDK as a
    /// CBOR <c>vector&lt;int&gt;</c>. Backed by
    /// <see cref="MozaData.HandbrakeCurve"/>; on the wire each element is its
    /// own MOZA-protocol float command (<c>handbrake-y1</c>..<c>handbrake-y5</c>).
    /// </summary>
    /// <remarks>
    /// Decode is defensive: the payload must be a CBOR array of unsigned ints,
    /// length exactly 5. Anything else returns 4.00 Bad Request — partial /
    /// silent coercion would mask client bugs.
    /// </remarks>
    internal sealed class HandbrakeNonLinearResource : CoapResourceHandler
    {
        private readonly MozaData _data;
        private readonly HardwareApplier _hardware;

        public HandbrakeNonLinearResource(MozaData data, HardwareApplier hardware)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            int[] curve = _data.HandbrakeCurve;
            var snapshot = new int[curve.Length];
            for (int i = 0; i < curve.Length; i++) snapshot[i] = curve[i];

            byte[] payload = CborWriter.WriteArray((IReadOnlyList<int>)snapshot);
            return CoapResourceResponse.Content(payload, PayloadCodec.CFCbor);
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!req.HasPayload)
                return CoapResourceResponse.BadRequest("HandbrakeNonLinear: empty payload");

            object decoded;
            try
            {
                decoded = CborReader.ReadItem(req.Payload);
            }
            catch (CborFormatException ex)
            {
                return CoapResourceResponse.BadRequest($"HandbrakeNonLinear: CBOR decode failed: {ex.Message}");
            }

            if (!(decoded is List<object> list))
                return CoapResourceResponse.BadRequest($"HandbrakeNonLinear: expected CBOR array, got {decoded?.GetType().Name ?? "null"}");

            int[] curve = _data.HandbrakeCurve;
            if (list.Count != curve.Length)
                return CoapResourceResponse.BadRequest(
                    $"HandbrakeNonLinear: expected array of length {curve.Length}, got {list.Count}");

            var values = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                int v;
                switch (list[i])
                {
                    case int i32: v = i32; break;
                    case uint u32:
                        if (u32 > int.MaxValue)
                            return CoapResourceResponse.BadRequest($"HandbrakeNonLinear: element {i} exceeds Int32.MaxValue");
                        v = (int)u32; break;
                    case ulong u64:
                        if (u64 > int.MaxValue)
                            return CoapResourceResponse.BadRequest($"HandbrakeNonLinear: element {i} exceeds Int32.MaxValue");
                        v = (int)u64; break;
                    default:
                        return CoapResourceResponse.BadRequest(
                            $"HandbrakeNonLinear: element {i} is {list[i]?.GetType().Name ?? "null"}, expected unsigned int");
                }
                if (v < 0)
                    return CoapResourceResponse.BadRequest($"HandbrakeNonLinear: element {i} is negative ({v})");
                values[i] = v;
            }

            for (int i = 0; i < values.Length; i++)
            {
                curve[i] = values[i];
                _hardware.WriteFloatIfHandbrakeDetected($"handbrake-y{i + 1}", values[i]);
            }
            return CoapResourceResponse.Valid();
        }
    }
}
