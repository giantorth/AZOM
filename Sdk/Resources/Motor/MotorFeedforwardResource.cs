using System.Threading;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/Feedforward</c>.
    /// Partner-SDK high-rate torque-shaping channel. Accepted and logged
    /// once every <see cref="LogEvery"/>th call (iRacing emits these at the
    /// game-engine rate — logging each one would flood the diagnostics
    /// buffer in seconds). No CDC emission in v1 — the wheelbase exposes
    /// no equivalent command, and translating partner API semantics onto
    /// the existing FFB pipeline is out of scope.
    /// </summary>
    internal sealed class MotorFeedforwardResource : CoapResourceHandler
    {
        // Sample 1/60th of calls. Tuned to ~1 line per second at the
        // observed 60 Hz iRacing emit rate.
        private const int LogEvery = 60;

        private int _counter;

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int value))
                return CoapResourceResponse.BadRequest("expected 4-byte LE int32");

            int n = Interlocked.Increment(ref _counter);

            // Log once per LogEvery invocations. Index 1, LogEvery+1, ... so
            // the first call always logs (operators expect at least one line
            // confirming the channel is live).
            if ((n % LogEvery) == 1)
                MozaLog.Debug($"[Moza.Sdk] Feedforward POST id={req.DeviceId} value={value} (sample 1/{LogEvery})");

            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
