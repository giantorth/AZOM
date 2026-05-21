namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/SetMotorRunState</c>.
    /// Low-frequency run-state toggle from the partner SDK (run/stop/idle).
    /// Accepted and logged on every call (caller emits this once on state
    /// transition, not on the telemetry hot path). No CDC emission in v1.
    /// </summary>
    internal sealed class MotorSetMotorRunStateResource : CoapResourceHandler
    {
        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            if (!PayloadCodec.TryDecodeScalarFromLittleEndian(req.Payload, out int value))
                return CoapResourceResponse.BadRequest("expected 4-byte LE int32");

            MozaLog.Debug($"[Moza.Sdk] SetMotorRunState POST id={req.DeviceId} value={value}");
            return CoapResourceResponse.Valid();
        }

        // GET falls through to base 4.05.
    }
}
