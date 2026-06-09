using System;
using System.Threading;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Handbrake
{
    /// <summary>
    /// Shared scaffolding for Handbrake scalar resources. GET = ASCII text
    /// of a single <see cref="MozaData"/> int, POST = 4-byte LE int32 -&gt;
    /// <see cref="HardwareApplier.WriteIfHandbrakeDetected"/>. Mirrors
    /// <see cref="Motor.MotorScalarResource"/> /
    /// <see cref="Pedal.PedalScalarResource"/> but gates writes on the
    /// handbrake detection flag.
    /// </summary>
    internal abstract class HandbrakeScalarResource : CoapResourceHandler
    {
        private readonly Func<MozaData, int>? _read;
        private readonly string? _commandName;
        private readonly string _diagnosticName;
        private readonly bool _writeAsFloat;
        private int _gapWarned;

        protected readonly MozaData Data;
        protected readonly HardwareApplier Hardware;

        protected HandbrakeScalarResource(
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
                Hardware.WriteFloatIfHandbrakeDetected(_commandName, value);
            else
                Hardware.WriteIfHandbrakeDetected(_commandName, value);

            return CoapResourceResponse.Valid();
        }

        private void WarnGapOnce(string what)
        {
            if (Interlocked.CompareExchange(ref _gapWarned, 1, 0) == 0)
            {
                MozaLog.Warn($"[AZOM.Sdk] Handbrake gap: {_diagnosticName} missing {what}");
            }
        }
    }
}
