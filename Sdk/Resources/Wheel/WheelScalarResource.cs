using System;
using System.Threading;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Wheel
{
    /// <summary>
    /// Shared scaffolding for Wheel scalar resources that follow the
    /// "GET = ASCII text of a single MozaData int" / "POST = 4-byte LE int → HardwareApplier.WriteIfWheelDetected"
    /// pattern. Mirrors <c>Resources.Motor.MotorScalarResource</c> structurally —
    /// only the dispatcher target differs (wheel-detection gate vs base-connected
    /// gate). When the concrete handler passes a null reader OR a null command,
    /// that side of the request returns 4.04 (read) / 4.05 (write) and a one-shot
    /// <see cref="MozaLog.Warn"/> is emitted per resource per session — the
    /// canonical "gap" shape per Phase 6b spec.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concrete subclasses optionally provide a <c>postValueAdjust</c> hook to
    /// reverse a display→raw offset used by the existing MozaData parsers. For
    /// example, <c>wheel-paddles-mode</c> writes raw 1/2/3 to the wire but
    /// <see cref="MozaData.WheelPaddlesMode"/> stores the offset 0/1/2 form; the
    /// reverse offset is applied here so SDK clients see the canonical 1/2/3
    /// values described in <c>docs/sdk/api-inventory.md</c>.
    /// </para>
    /// </remarks>
    internal abstract class WheelScalarResource : CoapResourceHandler
    {
        private readonly Func<MozaData, int>? _read;
        private readonly string? _commandName;
        private readonly string _diagnosticName;
        private readonly int _getOffset;   // added to MozaData value before encoding for GET (e.g. +1 for 0..2 → 1..3)
        private readonly int _postOffset;  // added to incoming POST value before writing (e.g. 0 for direct, -1 for inverse)
        private int _gapWarned; // 0 = not yet warned, 1 = warned. Interlocked for thread-safety.

        protected readonly MozaData Data;
        protected readonly HardwareApplier Hardware;

        protected WheelScalarResource(
            MozaData data,
            HardwareApplier hardware,
            string diagnosticName,
            Func<MozaData, int>? read,
            string? commandName,
            int getOffset = 0,
            int postOffset = 0)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _diagnosticName = diagnosticName ?? throw new ArgumentNullException(nameof(diagnosticName));
            _read = read;
            _commandName = commandName;
            _getOffset = getOffset;
            _postOffset = postOffset;
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            if (_read == null)
            {
                WarnGapOnce("GET source MozaData field");
                return CoapResourceResponse.NotFound($"{_diagnosticName}: no MozaData source");
            }
            int value = _read(Data) + _getOffset;
            return CoapResourceResponse.Content(
                PayloadCodec.EncodeScalarAsAsciiText(value),
                PayloadCodec.CFOctetStream);
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (_commandName == null)
            {
                WarnGapOnce("POST target command");
                return CoapResourceResponse.MethodNotAllowed($"{_diagnosticName}: no hardware command");
            }
            if (!PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int value))
                return CoapResourceResponse.BadRequest("expected 4-byte LE int32");

            Hardware.WriteIfWheelDetected(_commandName, value + _postOffset);
            return CoapResourceResponse.Valid();
        }

        private void WarnGapOnce(string what)
        {
            // Interlocked CompareExchange ensures exactly one log line per
            // resource per session — even if iRacing hammers the URI before
            // the warn has been observed. The static-field-per-instance shape
            // matches the "cache the warned flag in a static field" spec line:
            // each resource is constructed exactly once at bind time, so the
            // instance field IS the per-session flag.
            if (Interlocked.CompareExchange(ref _gapWarned, 1, 0) == 0)
            {
                MozaLog.Warn($"[Moza.Sdk] Wheel gap: {_diagnosticName} missing {what}");
            }
        }
    }
}
