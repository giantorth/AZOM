using System;
using System.Collections.Generic;
using System.Net;

namespace MozaPlugin.Sdk.Coap
{
    /// <summary>
    /// Tracks CoAP Observe (RFC 7641) subscribers indexed by URI suffix. A
    /// subscription is keyed by the (remote endpoint, token) pair — that's
    /// how a server identifies which exchange a notification belongs to.
    ///
    /// The 3-byte Observe sequence number is per-subscription and starts at 1
    /// for the first notification (the initial 2.05 carries seq=0 by RFC 7641
    /// §3.4; we leave that to the resource handler). Each
    /// <see cref="EnumerateSubscriptions"/> call increments and returns the
    /// next seq for every matching subscription, so callers should drain the
    /// enumerable once per notification fan-out.
    ///
    /// Thread-safe: all mutators and the enumerator are serialized on a
    /// single lock. The enumerable returned by <see cref="EnumerateSubscriptions"/>
    /// is a snapshot taken under the lock — safe to iterate without holding
    /// the lock while sending UDP.
    /// </summary>
    public sealed class ObserveRegistry
    {
        private sealed class Subscription
        {
            public IPEndPoint Endpoint;
            public byte[] Token;
            public string UriPath;
            public uint NextSeq; // 24-bit; wraps at 0x1000000

            public Subscription(IPEndPoint endpoint, byte[] token, string uriPath)
            {
                Endpoint = endpoint;
                Token = token;
                UriPath = uriPath;
                // Per RFC 7641: initial response uses 0; first follow-up
                // notification uses 1. We hand out 1 first.
                NextSeq = 1;
            }
        }

        // Subscriptions keyed by uriPath -> list. Lookups by (endpoint, token)
        // for deregister happen by linear scan across all lists; the
        // subscriber count is expected to stay small (one or two iRacing
        // observers at a time).
        private readonly Dictionary<string, List<Subscription>> _byPath
            = new Dictionary<string, List<Subscription>>(StringComparer.Ordinal);

        private readonly object _gate = new object();

        /// <summary>
        /// Add or refresh a subscription. If a subscription with the same
        /// (endpoint, token) already exists, its URI is updated and the seq
        /// counter is preserved (idempotent re-registration).
        /// </summary>
        public void Register(IPEndPoint endpoint, byte[] token, string uriPath)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (token == null) throw new ArgumentNullException(nameof(token));
            if (uriPath == null) throw new ArgumentNullException(nameof(uriPath));

            lock (_gate)
            {
                // Remove any prior subscription for this (endpoint, token) on
                // a different path before inserting.
                RemoveByEndpointAndTokenLocked(endpoint, token, exceptPath: uriPath);

                if (!_byPath.TryGetValue(uriPath, out var list))
                {
                    list = new List<Subscription>();
                    _byPath[uriPath] = list;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    if (MatchesEndpointAndToken(list[i], endpoint, token))
                    {
                        // Already registered for this path; nothing to do.
                        return;
                    }
                }
                list.Add(new Subscription(endpoint, CopyToken(token), uriPath));
            }
        }

        /// <summary>
        /// Drop any subscription matching (endpoint, token). Returns true if
        /// one was removed.
        /// </summary>
        public bool Deregister(IPEndPoint endpoint, byte[] token)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (token == null) throw new ArgumentNullException(nameof(token));

            lock (_gate)
            {
                return RemoveByEndpointAndTokenLocked(endpoint, token, exceptPath: null);
            }
        }

        /// <summary>
        /// Snapshot the subscribers for <paramref name="uriPath"/>, advance
        /// each one's seq counter, and return the (endpoint, token, seq)
        /// triples ready to be turned into notification packets. Returns an
        /// empty enumerable if there are no observers.
        ///
        /// The returned collection is a materialised list — safe to iterate
        /// outside the lock; the underlying registry can mutate concurrently
        /// without invalidating it.
        /// </summary>
        public IEnumerable<(IPEndPoint endpoint, byte[] token, uint seq)> EnumerateSubscriptions(string uriPath)
        {
            if (uriPath == null) throw new ArgumentNullException(nameof(uriPath));

            List<(IPEndPoint, byte[], uint)> snapshot;
            lock (_gate)
            {
                if (!_byPath.TryGetValue(uriPath, out var list) || list.Count == 0)
                    return Array.Empty<(IPEndPoint, byte[], uint)>();

                snapshot = new List<(IPEndPoint, byte[], uint)>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var sub = list[i];
                    uint seq = sub.NextSeq & 0x00FFFFFFu;
                    sub.NextSeq = (sub.NextSeq + 1) & 0x00FFFFFFu;
                    if (sub.NextSeq == 0) sub.NextSeq = 1; // skip 0 on wrap; 0 reserved for the initial response
                    snapshot.Add((sub.Endpoint, CopyToken(sub.Token), seq));
                }
            }
            return snapshot;
        }

        /// <summary>Current subscriber count across all URIs. Diagnostic only.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    foreach (var kv in _byPath) n += kv.Value.Count;
                    return n;
                }
            }
        }

        /// <summary>Wipe all subscriptions. Used during server shutdown.</summary>
        public void Clear()
        {
            lock (_gate) _byPath.Clear();
        }

        // ----- helpers (called under _gate) -----

        private bool RemoveByEndpointAndTokenLocked(IPEndPoint endpoint, byte[] token, string? exceptPath)
        {
            bool removed = false;
            List<string>? emptyKeys = null;
            foreach (var kv in _byPath)
            {
                if (exceptPath != null && string.Equals(kv.Key, exceptPath, StringComparison.Ordinal))
                    continue;
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (MatchesEndpointAndToken(list[i], endpoint, token))
                    {
                        list.RemoveAt(i);
                        removed = true;
                    }
                }
                if (list.Count == 0)
                {
                    if (emptyKeys == null) emptyKeys = new List<string>();
                    emptyKeys.Add(kv.Key);
                }
            }
            if (emptyKeys != null)
            {
                foreach (var k in emptyKeys) _byPath.Remove(k);
            }
            return removed;
        }

        private static bool MatchesEndpointAndToken(Subscription sub, IPEndPoint endpoint, byte[] token)
        {
            if (!sub.Endpoint.Equals(endpoint)) return false;
            if (sub.Token.Length != token.Length) return false;
            for (int i = 0; i < token.Length; i++)
            {
                if (sub.Token[i] != token[i]) return false;
            }
            return true;
        }

        private static byte[] CopyToken(byte[] token)
        {
            if (token.Length == 0) return Array.Empty<byte>();
            var c = new byte[token.Length];
            Buffer.BlockCopy(token, 0, c, 0, token.Length);
            return c;
        }
    }
}
