using System.Net;
using System.Net.Sockets;

namespace MozaPlugin.Sdk.PitHouseUdp
{
    /// <summary>
    /// Handler for one PacketId in the PitHouse UDP control protocol.
    /// Registered with <see cref="MozaControlUdpServer"/> at construction
    /// time; the server's receive loop dispatches by
    /// <see cref="PitHousePacket.PacketId"/>.
    /// </summary>
    internal interface IPitHousePacketHandler
    {
        /// <summary>PacketId this handler claims. Must be unique across all registered handlers.</summary>
        int PacketId { get; }

        /// <summary>Human-readable name for logs (e.g. "SteerLock write").</summary>
        string Name { get; }

        /// <summary>
        /// Handle a parsed packet. Replies are sent through
        /// <paramref name="ctx"/>; fire-and-forget handlers leave the
        /// context untouched. Exceptions thrown here are caught by the
        /// server and logged — no need to wrap.
        /// </summary>
        void Handle(PitHousePacket request, PitHouseReplyContext ctx);
    }

    /// <summary>
    /// Context passed to a handler so it can send a reply on the
    /// caller-specified <see cref="PitHousePacket.ReplyPort"/>. The server
    /// owns the <see cref="UdpClient"/> and reuses it for all outbound
    /// replies; handlers should not close it.
    /// </summary>
    internal sealed class PitHouseReplyContext
    {
        public IPEndPoint OriginalSender { get; }
        public int? ReplyPort { get; }
        public UdpClient Server { get; }

        /// <summary>
        /// Short one-line description of what the handler did, surfaced in
        /// the SDK tab's "Recent UDP control requests" list. Handler is
        /// expected to set this before returning so the UI row carries
        /// meaningful detail (e.g. <c>"max=540 limit=540"</c>). Left null
        /// when the handler chose not to describe — the UI falls back to
        /// the handler name alone.
        /// </summary>
        public string? Summary { get; set; }

        public PitHouseReplyContext(IPEndPoint originalSender, int? replyPort, UdpClient server)
        {
            OriginalSender = originalSender;
            ReplyPort = replyPort;
            Server = server;
        }

        /// <summary>
        /// Send a CBOR-encoded reply to the caller's
        /// <see cref="ReplyPort"/> on the original sender's IP. No-op (with
        /// a debug log) when <see cref="ReplyPort"/> is null or 0 — RSF's
        /// client treats a 0 listen-port as "abort this read" and PitHouse
        /// is expected to honour the same convention.
        /// </summary>
        public void SendReply(byte[] cborBytes)
        {
            if (cborBytes == null || cborBytes.Length == 0) return;
            if (ReplyPort == null || ReplyPort.Value <= 0)
            {
                MozaLog.Debug("[PitHouseUdp] reply requested but ReplyPort missing/zero — dropping");
                return;
            }
            var dest = new IPEndPoint(OriginalSender.Address, ReplyPort.Value);
            try
            {
                Server.Send(cborBytes, cborBytes.Length, dest);
            }
            catch (System.ObjectDisposedException)
            {
                // server socket shut down between handler dispatch and send — accept and exit
            }
            catch (SocketException sx)
            {
                MozaLog.Debug($"[PitHouseUdp] reply send {dest}: {sx.SocketErrorCode}: {sx.Message}");
            }
            catch (System.Exception ex)
            {
                MozaLog.Warn($"[PitHouseUdp] reply send failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
