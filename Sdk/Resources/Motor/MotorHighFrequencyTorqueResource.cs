using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/HighFrequencyTorque</c>.
    /// Partner-SDK channel posted once per session as a capability probe.
    /// Forwards the 4-byte LE int32 to the wheelbase via the
    /// <c>base-high-freq-torque</c> CDC command (group 0x2A cmd 0x41), which
    /// persists to EEPROM Table 11 Params 13/14 (firmware log echo confirms).
    /// <para>TODO (open-questions.md § "Partner-API teardown"): confirm whether
    /// PitHouse re-writes this cell to a disabled value on iRacing exit.</para>
    /// </summary>
    internal sealed class MotorHighFrequencyTorqueResource : CoapResourceHandler
    {
        // Logged (sampled) by MozaSdkCoapServer.LogRequest.
        private readonly HardwareApplier _hardware;

        public MotorHighFrequencyTorqueResource(HardwareApplier hardware)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int value))
                return CoapResourceResponse.BadRequest("expected 4-byte LE int32");

            _hardware.WriteIfBaseConnected("base-high-freq-torque", value);
            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
