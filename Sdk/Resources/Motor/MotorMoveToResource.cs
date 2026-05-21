namespace MozaPlugin.Sdk.Resources.Motor
{
    /// <summary>
    /// Handler for <c>/MOZARacing/ProductDevice/{id}/motorMoveTo</c>.
    /// Vendor-only motion command not supported in v1 of the plugin's SDK
    /// emulation. GET → 4.05; POST → 4.00 Bad Request with a stable reason
    /// string so a client can distinguish "not implemented here" from
    /// "malformed payload".
    /// </summary>
    internal sealed class MotorMoveToResource : CoapResourceHandler
    {
        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
            => CoapResourceResponse.BadRequest("motorMoveTo not supported in v1");

        // GET falls through to base 4.05.
    }
}
