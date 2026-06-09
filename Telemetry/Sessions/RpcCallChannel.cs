using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Frames;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// Host→wheel JSON RPC machinery on session 0x0a. Wire format mirrors configJson:
    /// 9-byte envelope ([flag=0x00][comp_size:u32 LE][uncomp_size:u32 LE]) wrapping a
    /// zlib stream of <c>{"&lt;method&gt;()": arg, "id": N}</c>. Replies arrive on the
    /// same session in the same envelope and are routed back to the waiter by integer
    /// <c>id</c>.
    ///
    /// Reassembly of the inbound chunk stream is owned by <c>TelemetrySender</c> (it
    /// shares <see cref="SessionDataReassembler"/> with the configJson handler and
    /// other session-0x0a observers); this class consumes already-decompressed blobs
    /// via <see cref="HandleReply"/>.
    /// </summary>
    internal sealed class RpcCallChannel : IDisposable
    {
        private MozaSerialConnection _connection;
        private readonly Func<bool> _shouldAbort;

        /// <summary>Repoint the outbound connection (telemetry sink moved between the
        /// wheelbase and a standalone-USB dashboard connection).</summary>
        public void Rebind(MozaSerialConnection connection) => _connection = connection;

        private int _nextId = 1000;
        private readonly object _lock = new object();
        private readonly Dictionary<int, ManualResetEventSlim> _waiters = new();
        private readonly Dictionary<int, byte[]> _replies = new();

        /// <summary>Outbound seq counter for session 0x0a. Tracks the next seq to use
        /// when chunking an outgoing RPC envelope. TelemetrySender used to keep this
        /// inline; it stays on this class so the channel owns its own seq state.</summary>
        public int OutboundSeq { get; set; }

        // Guards the read-chunk-send-write of OutboundSeq inside Call() so
        // two concurrent RPCs (UI-triggered + auto-test) can't reserve the
        // same seq train and confuse the wheel's per-seq retransmit
        // tracking. Distinct from _lock (which protects the id→waiter
        // dict): RPC replies must be routable while a different RPC is
        // sending, so we use a separate object.
        private readonly object _seqLock = new object();

        private int _disposed;

        /// <param name="connection">Serial connection used to push chunks.</param>
        /// <param name="shouldAbort">Returns true when the caller is shutting down or
        /// not connected; checked between chunk emits so an in-flight Call can bail
        /// without sending half a request. Receives the same semantics as the prior
        /// inline <c>_state == Idle || !_connection.IsConnected</c> guard.</param>
        public RpcCallChannel(MozaSerialConnection connection, Func<bool> shouldAbort)
        {
            _connection = connection;
            _shouldAbort = shouldAbort;
        }

        /// <summary>
        /// Send a host→wheel JSON RPC call on session 0x0a and wait up to
        /// <paramref name="timeoutMs"/> for the wheel's reply. Returns the decoded
        /// reply bytes on success, null on timeout / abort / error.
        /// </summary>
        public byte[]? Call(string method, object arg, int timeoutMs = 2000)
        {
            if (Volatile.Read(ref _disposed) != 0) return null;
            if (!_connection.IsConnected) return null;

            int id;
            var waiter = new ManualResetEventSlim(false);
            lock (_lock)
            {
                id = _nextId++;
                _waiters[id] = waiter;
            }

            byte[] envelope = BuildRpcCallEnvelope(method, arg, id);
            lock (_seqLock)
            {
                int seq = OutboundSeq + 1;
                var frames = TierDefinitionBuilder.ChunkMessage(envelope, 0x0a, ref seq);
                foreach (var frame in frames)
                {
                    if (_shouldAbort()) { CleanupWaiter(id); return null; }
                    _connection.Send(frame);
                }
                OutboundSeq = seq;
            }

            bool acked = waiter.Wait(timeoutMs);
            byte[]? reply = null;
            lock (_lock)
            {
                _replies.TryGetValue(id, out reply);
                _replies.Remove(id);
                _waiters.Remove(id);
            }
            try { waiter.Dispose(); } catch { }
            return acked ? reply : null;
        }

        /// <summary>
        /// Decode a session-0x0a reply blob and route to the waiter by <c>id</c>.
        /// Method name is NOT inspected — replies route solely on integer id, which
        /// accommodates standard <c>{"id": N, "result": ...}</c>, method-keyed
        /// <c>{"&lt;method&gt;()": ..., "id": N}</c>, and empty-method
        /// <c>{"()": "", "id": N}</c> shapes (last seen on reset, 2026-04-21).
        /// </summary>
        public void HandleReply(byte[] uncompressed)
        {
            try
            {
                string json = Encoding.UTF8.GetString(uncompressed);
                var obj = JObject.Parse(json);
                var idTok = obj["id"];
                if (idTok == null) return;
                int id = (int)idTok;
                lock (_lock)
                {
                    // Only store when a waiter is still outstanding. A reply that
                    // arrives after Call() timed out (and removed its waiter+entry)
                    // would otherwise linger in _replies forever.
                    if (_waiters.TryGetValue(id, out var waiter))
                    {
                        _replies[id] = uncompressed;
                        waiter.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] RPC reply parse failed: {ex.Message}");
            }
        }

        /// <summary>Wake every outstanding waiter with a null reply. Called from
        /// TelemetrySender.Stop() and Dispose() so callers blocked in Call() return
        /// promptly instead of hitting their own timeout.</summary>
        public void DrainWaiters()
        {
            ManualResetEventSlim[] waiters;
            lock (_lock)
            {
                if (_waiters.Count == 0) return;
                waiters = new ManualResetEventSlim[_waiters.Count];
                _waiters.Values.CopyTo(waiters, 0);
                // Don't clear the dictionary here — the waiting Call will remove
                // its own entry under the lock and dispose its waiter.
            }
            foreach (var w in waiters)
            {
                try { w.Set(); } catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DrainWaiters();
            // Don't dispose remaining waiters — Call() owns the waiter lifecycle
            // and disposes after its Wait returns. Dropping a reference here is
            // safe because the GC will reclaim them; they cannot be re-acquired.
        }

        private void CleanupWaiter(int id)
        {
            lock (_lock)
            {
                if (_waiters.TryGetValue(id, out var w))
                {
                    _waiters.Remove(id);
                    try { w.Dispose(); } catch { }
                }
            }
        }

        private static byte[] BuildRpcCallEnvelope(string method, object arg, int id)
        {
            var root = new JObject();
            root[$"{method}()"] = JToken.FromObject(arg);
            root["id"] = id;
            string json = root.ToString(Newtonsoft.Json.Formatting.None);
            byte[] uncompressed = Encoding.UTF8.GetBytes(json);
            byte[] compressed = ZlibCompress(uncompressed);
            var env = new byte[9 + compressed.Length];
            env[0] = 0x00;
            uint c = (uint)compressed.Length;
            env[1] = (byte)(c & 0xFF); env[2] = (byte)((c >> 8) & 0xFF);
            env[3] = (byte)((c >> 16) & 0xFF); env[4] = (byte)((c >> 24) & 0xFF);
            uint u = (uint)uncompressed.Length;
            env[5] = (byte)(u & 0xFF); env[6] = (byte)((u >> 8) & 0xFF);
            env[7] = (byte)((u >> 16) & 0xFF); env[8] = (byte)((u >> 24) & 0xFF);
            Array.Copy(compressed, 0, env, 9, compressed.Length);
            return env;
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(
                output, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            uint adler = (b << 16) | a;
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }
    }
}
