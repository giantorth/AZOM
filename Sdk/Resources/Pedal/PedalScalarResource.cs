using System;
using System.Threading;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Pedal
{
    /// <summary>
    /// Shared scaffolding for Pedal scalar resources that follow the
    /// "GET = ASCII text of a single <see cref="MozaData"/> int" /
    /// "POST = 4-byte LE int32 -&gt; <see cref="HardwareApplier"/>" pattern. Each
    /// concrete handler supplies a reader delegate (from <see cref="MozaData"/>)
    /// and a wheelbase command name. A null reader OR null command name marks
    /// that side of the SDK contract as a gap: the missing side returns 4.04
    /// (GET) or 4.05 (POST) and a one-shot <see cref="MozaLog.Warn"/> is emitted
    /// per resource per session.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="Motor.MotorScalarResource"/> but writes via
    /// <see cref="HardwareApplier.WriteIfPedalsDetected"/> instead of the
    /// base-connected variant — the pedals are a distinct USB CDC device and
    /// the wire is silent when no pedal set is enumerated, so writes must be
    /// gated on <c>PedalsDetected</c> rather than <c>IsBaseConnected</c>.
    /// </remarks>
    internal abstract class PedalScalarResource : CoapResourceHandler
    {
        private readonly Func<MozaData, int>? _read;
        private readonly string? _commandName;
        private readonly string _diagnosticName;
        private readonly bool _writeAsFloat;
        private int _gapWarned;

        protected readonly MozaData Data;
        protected readonly HardwareApplier Hardware;

        protected PedalScalarResource(
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
                Hardware.WriteFloatIfPedalsDetected(_commandName, value);
            else
                Hardware.WriteIfPedalsDetected(_commandName, value);

            return CoapResourceResponse.Valid();
        }

        private void WarnGapOnce(string what)
        {
            if (Interlocked.CompareExchange(ref _gapWarned, 1, 0) == 0)
            {
                MozaLog.Warn($"[AZOM.Sdk] Pedal gap: {_diagnosticName} missing {what}");
            }
        }
    }
}
