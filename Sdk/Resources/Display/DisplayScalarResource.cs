using System;
using System.Threading;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Display
{
    /// <summary>
    /// Shared scaffolding for standalone-Display scalar resources. Identical
    /// in shape to <see cref="Wheel.WheelScalarResource"/> /
    /// <see cref="Motor.MotorScalarResource"/>; the only difference is that
    /// POSTs route through <see cref="HardwareApplier.WriteIfDashDetected"/>
    /// (dash-detection gate) and reads come from the Dash-side MozaData
    /// fields.
    /// </summary>
    /// <remarks>
    /// When the concrete handler passes a null reader OR a null command, that
    /// side of the request returns 4.04 (read) / 4.05 (write) and a one-shot
    /// <see cref="MozaLog.Warn"/> is emitted per resource per session.
    /// </remarks>
    internal abstract class DisplayScalarResource : CoapResourceHandler
    {
        private readonly Func<MozaData, int>? _read;
        private readonly string? _commandName;
        private readonly string _diagnosticName;
        private int _gapWarned;

        protected readonly MozaData Data;
        protected readonly HardwareApplier Hardware;

        protected DisplayScalarResource(
            MozaData data,
            HardwareApplier hardware,
            string diagnosticName,
            Func<MozaData, int>? read,
            string? commandName)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _diagnosticName = diagnosticName ?? throw new ArgumentNullException(nameof(diagnosticName));
            _read = read;
            _commandName = commandName;
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

            Hardware.WriteIfDashDetected(_commandName, value);
            return CoapResourceResponse.Valid();
        }

        private void WarnGapOnce(string what)
        {
            if (Interlocked.CompareExchange(ref _gapWarned, 1, 0) == 0)
            {
                MozaLog.Warn($"[AZOM.Sdk] Display gap: {_diagnosticName} missing {what}");
            }
        }
    }
}
