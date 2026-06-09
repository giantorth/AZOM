using System;
using System.Threading;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/Feedforward</c>.
    /// Partner-SDK channel posted once per session as a capability probe.
    /// Forwards the 4-byte LE int32 to the wheelbase via the
    /// <c>base-feedforward</c> CDC command (group 0x2A cmd 0x40), which
    /// persists to EEPROM. Capture-verified 2026-05-23 against PitHouse's
    /// behaviour in <c>iracing-pithouse-{udp,serial}.pcapng</c>.
    /// <para>
    /// TODO: capture iRacing-exit / PitHouse-disengage to see whether
    /// PitHouse re-writes this cell to a "disabled" value on teardown. If it
    /// does, mirror that in the plugin so non-iRacing sims don't inherit
    /// iRacing-mode behaviour. Tracked in docs/protocol/open-questions.md
    /// § "Partner-API teardown on iRacing exit".
    /// </para>
    /// </summary>
    internal sealed class MotorFeedforwardResource : CoapResourceHandler
    {
        // iRacing sends one POST per session today, but the resource was
        // historically sampled in case a future client streams; keep the
        // throttle so an accidental hot-loop doesn't flood the diagnostics
        // buffer.
        private const int LogEvery = 60;

        private readonly HardwareApplier _hardware;
        private int _counter;

        public MotorFeedforwardResource(HardwareApplier hardware)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int value))
                return CoapResourceResponse.BadRequest("expected 4-byte LE int32");

            _hardware.WriteIfBaseConnected("base-feedforward", value);

            int n = Interlocked.Increment(ref _counter);
            if ((n % LogEvery) == 1)
                MozaLog.Debug($"[AZOM.Sdk] Feedforward POST id={req.DeviceId} value={value} (sample 1/{LogEvery})");

            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
