using System.Text;
using System.Threading;

namespace MozaPlugin.Sdk.Resources.Shifter
{
    /// <summary>
    /// Generic "gap" resource for the H-pattern shifter URIs the plugin
    /// surfaces but cannot back. The shifter SDK group (auto-blip + calibrate)
    /// has neither a <see cref="MozaData"/> field nor a
    /// <c>MozaCommandDatabase</c> entry as of Phase 6b, so we mimic the native
    /// SDK's ERRORCODE shape:
    /// <list type="bullet">
    ///   <item><description>GET returns 4.04 Not Found with body
    ///     <c>"PITHOUSENOTREADY"</c> — what native callers see when the manager
    ///     is offline.</description></item>
    ///   <item><description>POST returns 4.04 Not Found with body
    ///     <c>"NOINSTALLSDK"</c> — what native callers see when the partner
    ///     library hook isn't installed.</description></item>
    /// </list>
    /// First hit on either verb logs once at Info via <see cref="MozaLog"/>
    /// so we can observe whether real clients actually exercise these.
    /// </summary>
    internal sealed class ShifterGapResource : CoapResourceHandler
    {
        private static readonly byte[] PitHouseNotReady = Encoding.ASCII.GetBytes("PITHOUSENOTREADY");
        private static readonly byte[] NoInstallSdk     = Encoding.ASCII.GetBytes("NOINSTALLSDK");

        private readonly string _diagnosticName;
        private int _hitLogged;

        public ShifterGapResource(string diagnosticName)
        {
            _diagnosticName = diagnosticName ?? "Shifter";
        }

        public override CoapResourceResponse HandleGet(CoapResourceRequest req)
        {
            LogFirstHitOnce("GET");
            // Reason is logger-only; the body bytes are what go on the wire.
            // Clone so callers can't mutate the static singleton.
            return new CoapResourceResponse(
                Coap.CoapCode.NotFound,
                (byte[])PitHouseNotReady.Clone(),
                PayloadCodec.CFOctetStream,
                $"{_diagnosticName}: PitHouse gap (GET)");
        }

        public override CoapResourceResponse HandlePost(CoapResourceRequest req)
        {
            LogFirstHitOnce("POST");
            return new CoapResourceResponse(
                Coap.CoapCode.NotFound,
                (byte[])NoInstallSdk.Clone(),
                PayloadCodec.CFOctetStream,
                $"{_diagnosticName}: SDK gap (POST)");
        }

        private void LogFirstHitOnce(string verb)
        {
            if (Interlocked.CompareExchange(ref _hitLogged, 1, 0) == 0)
            {
                MozaLog.Info($"[Moza.Sdk] Shifter gap exercised: {_diagnosticName} {verb} (further hits suppressed)");
            }
        }
    }
}
