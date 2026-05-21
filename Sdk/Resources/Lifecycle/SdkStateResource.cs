namespace MozaPlugin.Sdk.Resources.Lifecycle
{
    /// <summary>
    /// Handler for <c>/MOZARacing/SdkState</c>.
    /// <list type="bullet">
    ///   <item><description>POST — replies 4.04 Not Found verbatim. This
    ///     matches the observed PitHouse behaviour: iRacing POSTs to this URI
    ///     repeatedly as a liveness probe and tolerates the 4.04 — that exact
    ///     code is what tells iRacing "the server is responding but the
    ///     state-broadcast endpoint isn't implemented", which is how PitHouse
    ///     signals presence without committing to a state contract.</description></item>
    ///   <item><description>GET — 4.05 Method Not Allowed; iRacing never
    ///     reads this resource in the capture.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Do NOT change the 4.04 to 2.03 / 2.05 without re-running the capture
    /// against a real iRacing client; PitHouse's 4.04 here is load-bearing —
    /// changing it has historically broken iRacing's discovery loop because
    /// the client uses the specific code to disambiguate "no PitHouse" from
    /// "PitHouse without SDK state".
    /// </remarks>
    internal sealed class SdkStateResource : CoapResourceHandler
    {
        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
            => CoapResourceResponse.NotFound("SdkState POST is intentionally 4.04 (PitHouse parity).");

        // GET falls through to base 4.05.
    }
}
