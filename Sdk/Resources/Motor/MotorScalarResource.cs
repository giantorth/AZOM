using System;
using System.Threading;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Shared scaffolding for Motor resources that follow the
    /// "GET = ASCII text of a single MozaData int" / "POST = 4-byte LE int → HardwareApplier"
    /// pattern. Each concrete handler supplies a reader delegate (from
    /// <see cref="MozaData"/>) and a wheelbase command name. When the
    /// concrete subclass passes a null reader OR a null command, that side of
    /// the request returns 4.04 (read) / 4.05 (write) and a one-shot
    /// <see cref="MozaLog.Warn"/> is emitted per resource per session — this
    /// is the canonical "gap" shape per Phase 6b spec.
    /// </summary>
    internal abstract class MotorScalarResource : CoapResourceHandler
    {
        private readonly Func<MozaData, int>? _read;
        private readonly string? _commandName;
        private readonly string _diagnosticName;
        private readonly bool _writeAsFloat;
        private int _gapWarned; // 0 = not yet warned, 1 = warned. Interlocked for thread-safety.

        protected readonly MozaData Data;
        protected readonly HardwareApplier Hardware;

        protected MotorScalarResource(
            MozaData data,
            HardwareApplier hardware,
            string diagnosticName,
            Func<MozaData, int>? read,
            string? commandName,
            bool writeAsFloat = false)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _diagnosticName = diagnosticName ?? throw new ArgumentNullException(nameof(diagnosticName));
            _read = read;
            _commandName = commandName;
            _writeAsFloat = writeAsFloat;
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            if (_read == null)
            {
                WarnGapOnce("GET source MozaData field");
                return CoapResourceResponse.NotFound($"{_diagnosticName}: no MozaData source");
            }
            int value = _read(Data);
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

            if (_writeAsFloat)
                Hardware.WriteFloatIfBaseConnected(_commandName, value);
            else
                Hardware.WriteIfBaseConnected(_commandName, value);

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
                MozaLog.Warn($"[Moza.Sdk] Motor gap: {_diagnosticName} missing {what}");
            }
        }
    }
}
