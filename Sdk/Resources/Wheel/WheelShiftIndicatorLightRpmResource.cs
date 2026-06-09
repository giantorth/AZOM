using System;
using System.Collections.Generic;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/ShiftIndicatorLightRpm</c>.
    /// SDK <c>SteeringWheelShiftIndicatorLightRpm</c> — a <c>vector&lt;int&gt;</c>
    /// of per-LED RPM thresholds (the RPM at which each shift-indicator LED
    /// switches on).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Up to 10 thresholds, matching <c>wheel-rpm-value1..wheel-rpm-value10</c>
    /// in <c>MozaCommandDatabase</c>. POST decodes a CBOR array of unsigned
    /// ints and writes each via the per-LED command.
    /// </para>
    /// <para>
    /// GET is a gap as of Phase 6b — MozaData has no field caching the
    /// read-back of these thresholds. The per-LED read commands exist
    /// (group 64 cmd 24[i]) but their responses are not currently parsed
    /// into MozaData. Returns 4.04 with a one-shot WARN; iRacing typically
    /// only POSTs this surface so the gap should be invisible in practice.
    /// </para>
    /// </remarks>
    internal sealed class WheelShiftIndicatorLightRpmResource : CoapResourceHandler
    {
        // Per-LED command count matches the for-loop bound in
        // MozaCommandDatabase that registers wheel-rpm-value{1..10}.
        private const int MaxLedCount = 10;

        private readonly MozaData _data;
        private readonly HardwareApplier _hardware;
        private int _getWarned;

        public WheelShiftIndicatorLightRpmResource(MozaData data, HardwareApplier hardware)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _getWarned, 1, 0) == 0)
            {
                MozaLog.Warn(
                    "[AZOM.Sdk] WheelShiftIndicatorLightRpm GET: MozaData has no cached " +
                    "per-LED thresholds (wheel-rpm-value{N} read responses not parsed).");
            }
            return CoapResourceResponse.NotFound("ShiftIndicatorLightRpm: no MozaData source");
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!req.HasPayload)
                return CoapResourceResponse.BadRequest("empty CBOR payload");

            object item;
            try
            {
                item = CborReader.ReadItem(req.Payload);
            }
            catch (CborFormatException ex)
            {
                return CoapResourceResponse.BadRequest($"malformed CBOR: {ex.Message}");
            }
            if (!(item is List<object> list))
                return CoapResourceResponse.BadRequest("expected CBOR array");

            int writeCount = Math.Min(list.Count, MaxLedCount);
            for (int i = 0; i < writeCount; i++)
            {
                if (!TryAsInt(list[i], out int threshold) || threshold < 0)
                    return CoapResourceResponse.BadRequest($"array entry {i} is not an unsigned int");
                _hardware.WriteIfWheelDetected($"wheel-rpm-value{i + 1}", threshold);
            }
            return CoapResourceResponse.Valid();
        }

        /// <summary>
        /// CBOR's reader returns int/uint/ulong depending on magnitude. The
        /// 16-bit threshold range fits comfortably in int32, so we accept any
        /// of the three but reject ulong values that exceed int.MaxValue
        /// (defensive — won't happen with real payloads but keeps the
        /// contract tight).
        /// </summary>
        private static bool TryAsInt(object boxed, out int value)
        {
            switch (boxed)
            {
                case int i32: value = i32; return true;
                case uint u32:
                    if (u32 <= int.MaxValue) { value = (int)u32; return true; }
                    break;
                case ulong u64:
                    if (u64 <= int.MaxValue) { value = (int)u64; return true; }
                    break;
            }
            value = 0;
            return false;
        }
    }
}
