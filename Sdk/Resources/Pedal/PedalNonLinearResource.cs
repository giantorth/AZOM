using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Shared scaffolding for the three pedal "non-linear curve" resources
    /// (<c>ClutchNonLinear</c>, <c>AccNonLinear</c>, <c>BrakeNonLinear</c>).
    /// The SDK surface is a <c>vector&lt;int&gt;</c> of 5 axis-output values
    /// (0..100); on the wire each element is its own MOZA-protocol command
    /// (<c>pedals-{axis}-y{1..5}</c>, 4-byte float). GET emits a CBOR
    /// array-of-unsigned-ints; POST decodes a CBOR array of identical length
    /// and writes each element through <see cref="HardwareApplier.WriteFloatIfPedalsDetected"/>.
    /// </summary>
    /// <remarks>
    /// Decode is defensive: the payload must be a CBOR array of unsigned ints,
    /// length exactly equal to the curve array on <see cref="MozaData"/> (5).
    /// Anything else (wrong type, wrong arity, non-int element, negative value)
    /// returns 4.00 Bad Request — silently coercing a malformed curve into
    /// hardware would mask client bugs.
    /// </remarks>
    internal abstract class PedalNonLinearResource : CoapResourceHandler
    {
        private readonly int[] _curve;            // ref to the 5-element MozaData array (read-side snapshot)
        private readonly string _commandPrefix;   // e.g. "pedals-brake-y" => suffixed 1..5
        private readonly string _diagnosticName;
        private readonly HardwareApplier _hardware;
        private int _gapWarned;

        protected PedalNonLinearResource(
            MozaData data,
            HardwareApplier hardware,
            string diagnosticName,
            int[] curve,
            string commandPrefix)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _diagnosticName = diagnosticName ?? throw new ArgumentNullException(nameof(diagnosticName));
            _curve = curve ?? throw new ArgumentNullException(nameof(curve));
            _commandPrefix = commandPrefix ?? throw new ArgumentNullException(nameof(commandPrefix));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            // Snapshot the live curve to avoid emitting torn values if the UI
            // mutates the array mid-encode. The array is 5 elements so the copy
            // is cheap.
            var snapshot = new int[_curve.Length];
            for (int i = 0; i < _curve.Length; i++) snapshot[i] = _curve[i];

            byte[] payload = CborWriter.WriteArray((IReadOnlyList<int>)snapshot);
            return CoapResourceResponse.Content(payload, PayloadCodec.CFCbor);
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!req.HasPayload)
                return CoapResourceResponse.BadRequest($"{_diagnosticName}: empty payload");

            object decoded;
            try
            {
                decoded = CborReader.ReadItem(req.Payload);
            }
            catch (CborFormatException ex)
            {
                return CoapResourceResponse.BadRequest($"{_diagnosticName}: CBOR decode failed: {ex.Message}");
            }

            if (!(decoded is List<object> list))
                return CoapResourceResponse.BadRequest($"{_diagnosticName}: expected CBOR array, got {decoded?.GetType().Name ?? "null"}");

            if (list.Count != _curve.Length)
                return CoapResourceResponse.BadRequest(
                    $"{_diagnosticName}: expected array of length {_curve.Length}, got {list.Count}");

            // Validate every element before any side effect: a write part-way
            // through a bad array would leave the curve in a half-applied state.
            var values = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                int v;
                switch (list[i])
                {
                    case int i32: v = i32; break;
                    case uint u32:
                        if (u32 > int.MaxValue)
                            return CoapResourceResponse.BadRequest($"{_diagnosticName}: element {i} exceeds Int32.MaxValue");
                        v = (int)u32; break;
                    case ulong u64:
                        if (u64 > int.MaxValue)
                            return CoapResourceResponse.BadRequest($"{_diagnosticName}: element {i} exceeds Int32.MaxValue");
                        v = (int)u64; break;
                    default:
                        return CoapResourceResponse.BadRequest(
                            $"{_diagnosticName}: element {i} is {list[i]?.GetType().Name ?? "null"}, expected unsigned int");
                }
                if (v < 0)
                    return CoapResourceResponse.BadRequest($"{_diagnosticName}: element {i} is negative ({v})");
                values[i] = v;
            }

            // Apply: mirror to MozaData (so subsequent GETs reflect the change
            // without waiting for a device echo) and write each element through
            // the per-index float command. Hardware writes are detection-gated
            // by the HardwareApplier helper.
            for (int i = 0; i < values.Length; i++)
            {
                _curve[i] = values[i];
                _hardware.WriteFloatIfPedalsDetected($"{_commandPrefix}{i + 1}", values[i]);
            }
            return CoapResourceResponse.Valid();
        }

        /// <summary>One-time gap warning hook. Currently unused — kept for symmetry with <see cref="PedalScalarResource"/>; emit when wire-only or data-only paths surface.</summary>
        protected void WarnGapOnce(string what)
        {
            if (Interlocked.CompareExchange(ref _gapWarned, 1, 0) == 0)
                MozaLog.Warn($"[Moza.Sdk] Pedal gap: {_diagnosticName} missing {what}");
        }
    }
}
