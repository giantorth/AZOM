using System.Threading;

namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/HighFrequencyTorque</c>.
    /// Partner-SDK high-rate torque-injection channel — same pattern as
    /// <see cref="MotorFeedforwardResource"/>: accept the 4-byte LE int32,
    /// log once every <see cref="LogEvery"/>th invocation, no CDC emission.
    /// </summary>
    internal sealed class MotorHighFrequencyTorqueResource : CoapResourceHandler
    {
        internal const int LogEvery = 60;

        private int _counter;

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int value))
                return CoapResourceResponse.BadRequest("expected 4-byte LE int32");

            int n = Interlocked.Increment(ref _counter);
            if ((n % LogEvery) == 1)
                MozaLog.Debug($"[Moza.Sdk] HighFrequencyTorque POST id={req.DeviceId} value={value} (sample 1/{LogEvery})");

            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
