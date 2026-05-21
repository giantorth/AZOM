using System;
using System.Security.Cryptography;
using System.Threading;

namespace MozaPlugin.Sdk.Coap
{
    /// <summary>
    /// Message-ID and token generators. RFC 7252 §4.4 requires MIDs to be
    /// unique within EXCHANGE_LIFETIME (~247 s); a 16-bit counter that wraps
    /// is sufficient for the loopback server use-case. Tokens are random and
    /// per-exchange; the server echoes the client's token on responses, so
    /// generators here are only for messages the server originates (CON
    /// pings, observe notifications).
    ///
    /// All methods are thread-safe.
    /// </summary>
    public static class CoapToken
    {
        // Counter is shifted by a small random offset on first use so
        // restarts of the plugin don't immediately reuse the same MID window
        // a client might still consider live.
        private static int _midCounter = -1;
        private static readonly object _midGate = new object();

        private static readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        /// <summary>
        /// Allocate the next message ID (16-bit, wraps at 0xFFFF). Thread-safe.
        /// </summary>
        public static ushort NextMessageId()
        {
            lock (_midGate)
            {
                if (_midCounter < 0)
                {
                    var seed = new byte[2];
                    _rng.GetBytes(seed);
                    _midCounter = ((seed[0] << 8) | seed[1]) & 0xFFFF;
                }
                ushort mid = (ushort)(_midCounter & 0xFFFF);
                _midCounter = (_midCounter + 1) & 0xFFFF;
                return mid;
            }
        }

        /// <summary>
        /// Generate a random token. <paramref name="length"/> must be in 0..8;
        /// default 1 byte which matches the capture's iRacing client.
        /// </summary>
        public static byte[] NextToken(int length = 1)
        {
            if (length < 0 || length > 8) throw new ArgumentOutOfRangeException(nameof(length));
            if (length == 0) return Array.Empty<byte>();
            var t = new byte[length];
            _rng.GetBytes(t);
            return t;
        }
    }
}
