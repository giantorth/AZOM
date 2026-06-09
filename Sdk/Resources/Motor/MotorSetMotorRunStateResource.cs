using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/SetMotorRunState</c>.
    /// Partner-SDK channel posted once per session as a capability probe.
    /// Forwards the 4-byte LE int32 to the wheelbase via the
    /// <c>base-motor-run-state</c> CDC command (group 0x2C cmd 0x01), which
    /// also flips the firmware's <c>input_appmode</c> and
    /// <c>motor_mode debug_mode</c> state per firmware log echoes.
    /// <para>TODO (open-questions.md § "Partner-API teardown"): confirm whether
    /// PitHouse re-writes this cell to a disabled value on iRacing exit. This
    /// one is the highest-stakes of the three — if "1" means "engage iRacing
    /// motor mode", leaving it set after iRacing exits could persist iRacing
    /// behaviour into other sims.</para>
    /// </summary>
    internal sealed class MotorSetMotorRunStateResource : CoapResourceHandler
    {
        private readonly HardwareApplier _hardware;

        public MotorSetMotorRunStateResource(HardwareApplier hardware)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int value))
                return CoapResourceResponse.BadRequest("expected 4-byte LE int32");

            _hardware.WriteIfBaseConnected("base-motor-run-state", value);

            MozaLog.Debug($"[AZOM.Sdk] SetMotorRunState POST id={req.DeviceId} value={value}");
            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
