using System;
using System.Net.Sockets;

namespace MozaPlugin.Sdk
{
    /// <summary>Socket setup shared by the loopback UDP servers.</summary>
    internal static class LoopbackUdpSocket
    {
        // _WSAIOW(IOC_VENDOR, 12)
        private const int SioUdpConnReset = unchecked((int)0x9800000C);

        /// <summary>
        /// Stop Windows from reporting an ICMP port-unreachable (a reply sent to
        /// a client that already closed its port) as ConnectionReset on the next
        /// Receive. Each such throw reaches SimHub's FirstChanceException log
        /// even though the receive loop catches it. Best-effort: hosts that
        /// reject the ioctl (e.g. Wine) keep the caught-and-continue path.
        /// </summary>
        internal static void DisableConnectionReset(UdpClient udp, string logTag)
        {
            try
            {
                udp.Client.IOControl(SioUdpConnReset, new byte[4], null);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"{logTag} SIO_UDP_CONNRESET not applied: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
