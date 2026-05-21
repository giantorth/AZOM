namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/motorStopMove</c>.
    /// Vendor-only motion command. GET → 4.05; POST → 4.00 Bad Request.
    /// </summary>
    internal sealed class MotorStopMoveResource : CoapResourceHandler
    {
        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
            => CoapResourceResponse.BadRequest("motorStopMove not supported in v1");

        // GET falls through to base 4.05.
    }
}
