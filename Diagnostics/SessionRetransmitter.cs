using System;
using System.Collections.Generic;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Reliable-stream retransmit queue for SerialStream session-data chunks
    /// (frames matching <c>7E N 43 17 7C 00 [session] [type=01] [seq_lo seq_hi]
    /// [payload] [crc32]</c>). PitHouse re-emits each unacked chunk continuously
    /// until the wheel acks via <c>fc:00 [session] [ack_seq:u16 LE]</c>; plugin
    /// previously fired-and-forgot, leaving session-02 chunk rate ~70× below
    /// PitHouse on the wire (2026-04-29 nebula diff).
    ///
    /// Usage:
    ///   1. After <c>_connection.Send(frame)</c> for a session-data chunk, call
    ///      <see cref="Track"/> to enqueue it.
    ///   2. In the fc:00 ack handler, call <see cref="Ack"/> with the parsed
    ///      session and ack_seq. All chunks with seq &lt;= ack_seq drop from the
    ///      session's queue.
    ///   3. Periodically call <see cref="DueRetransmits"/> and resend each
    ///      returned frame. Returns frames whose previous send was &gt;=
    ///      <paramref name="intervalMs"/> ago; chunks past
    ///      <paramref name="maxRetries"/> are dropped to bound queue size.
    /// </summary>
    public sealed class SessionRetransmitter
    {
        // Per-chunk exponential backoff. First retry hits fast (catches transient
        // wire drops within ~100ms), subsequent rounds widen so a stuck chunk
        // doesn't keep flooding the link at fixed cadence.
        private const int InitialBackoffMs = 100;
        private const int MaxBackoffMs = 2000;

        // Hard cap so a stalled session (wheel not acking) can't grow the queue
        // unboundedly. Sized for ~4× peak realistic burst: a hot-switch + property-
        // push storm under Grids-class profiles is ~500 unacked chunks; 2048
        // absorbs back-to-back stalls without dropping legitimate in-flight chunks
        // (~720 KB at ~350 B/entry). Eviction is LRU by LastSentTicks — a chunk
        // that just got retx'd is more recently useful than one waiting on first
        // retry, so we drop the staler entry.
        private const int MaxQueueSize = 2048;

        private sealed class Pending
        {
            public byte[] Frame = Array.Empty<byte>();
            public int LastSentTicks;
            public int SendCount;
            public int NextDelayMs;
        }

        private readonly Dictionary<(byte session, int seq), Pending> _queue
            = new Dictionary<(byte, int), Pending>();
        private readonly object _lock = new object();

        // Wraparound watch — fired once per minute when seq approaches the u16
        // limit. Saved monotonically so warning rate is bounded regardless of
        // chunk rate.
        private int _lastWrapWarnTickCount;
        private const int SeqWrapWarnThreshold = 60000;
        private const int WrapWarnIntervalMs = 60000;

        // Throttled eviction warn — same pattern as wrap warn so a backed-up
        // session doesn't spam logs.
        private int _lastEvictWarnTickCount;
        private const int EvictWarnIntervalMs = 60000;

        public int QueueSize { get { lock (_lock) return _queue.Count; } }

        /// <summary>
        /// Inspect <paramref name="frame"/>; if it's a session-data chunk on
        /// group 0x43 dev 0x17, enqueue it for retransmit. No-op otherwise.
        /// Frame must be the unstuffed wire form: <c>7E N 43 17 7C 00 sess
        /// type seq_lo seq_hi …</c>.
        /// </summary>
        public void Track(byte[] frame)
        {
            if (frame == null || frame.Length < 12) return;
            if (frame[0] != 0x7E) return;
            if (frame[2] != 0x43 || frame[3] != 0x17) return;
            if (frame[4] != 0x7C || frame[5] != 0x00) return;
            if (frame[7] != 0x01) return;  // data chunks only — skip type=00 ends and type=81 opens

            byte session = frame[6];
            int seq = frame[8] | (frame[9] << 8);
            var entry = new Pending
            {
                Frame = (byte[])frame.Clone(),
                LastSentTicks = Environment.TickCount,
                SendCount = 1,
                NextDelayMs = InitialBackoffMs,
            };
            bool warn = false;
            int queueSize = 0;
            bool evictWarn = false;
            int evictedSeq = 0;
            byte evictedSession = 0;
            int evictedQueueSize = 0;
            lock (_lock)
            {
                _queue[(session, seq)] = entry;
                if (seq >= SeqWrapWarnThreshold
                    && entry.LastSentTicks - _lastWrapWarnTickCount >= WrapWarnIntervalMs)
                {
                    _lastWrapWarnTickCount = entry.LastSentTicks;
                    warn = true;
                    queueSize = _queue.Count;
                }

                if (_queue.Count > MaxQueueSize)
                {
                    // LRU by LastSentTicks — drop the entry whose last send is
                    // furthest in the past. A chunk that just got retx'd is more
                    // recently useful than one waiting on first retry.
                    (byte, int) victimKey = default;
                    int victimTicks = int.MaxValue;
                    bool haveVictim = false;
                    foreach (var kv in _queue)
                    {
                        if (!haveVictim || kv.Value.LastSentTicks < victimTicks)
                        {
                            victimTicks = kv.Value.LastSentTicks;
                            victimKey = kv.Key;
                            haveVictim = true;
                        }
                    }
                    if (haveVictim)
                    {
                        _queue.Remove(victimKey);
                        evictedSession = victimKey.Item1;
                        evictedSeq = victimKey.Item2;
                        evictedQueueSize = _queue.Count;
                        if (entry.LastSentTicks - _lastEvictWarnTickCount >= EvictWarnIntervalMs)
                        {
                            _lastEvictWarnTickCount = entry.LastSentTicks;
                            evictWarn = true;
                        }
                    }
                }
            }
            if (warn)
            {
                global::MozaPlugin.MozaLog.Warn(
                    $"[AZOM] session 0x{session:X2} seq approaching u16 wrap: {seq} (queue={queueSize})");
            }
            if (evictWarn)
            {
                global::MozaPlugin.MozaLog.Warn(
                    $"[AZOM] retransmit queue over cap {MaxQueueSize}, evicted oldest " +
                    $"sess=0x{evictedSession:X2} seq={evictedSeq} (queue={evictedQueueSize})");
            }
        }

        /// <summary>
        /// Drop all queued chunks for <paramref name="session"/> with seq &lt;=
        /// <paramref name="ackSeq"/>. Mirrors how PitHouse stops retransmitting
        /// on ack.
        /// </summary>
        public void Ack(byte session, int ackSeq)
        {
            lock (_lock)
            {
                var doomed = new List<(byte, int)>();
                foreach (var kv in _queue)
                {
                    if (kv.Key.session == session && kv.Key.seq <= ackSeq)
                        doomed.Add(kv.Key);
                }
                foreach (var k in doomed) _queue.Remove(k);
            }
        }

        /// <summary>
        /// Drop a specific <c>(session, seq)</c> chunk from the queue. Used by
        /// callers that supersede a pending push (e.g. an FF property push of
        /// the same <c>kind</c> replacing an older one) so the older chunk
        /// doesn't keep retransmitting a stale value alongside the new one.
        /// No-op if the entry is absent.
        /// </summary>
        public void Drop(byte session, int seq)
        {
            lock (_lock) _queue.Remove((session, seq));
        }

        /// <summary>True iff the given <c>(session, seq)</c> is still pending
        /// (i.e. enqueued and not yet ack-cleared by <see cref="Ack"/> nor
        /// dropped by <see cref="Drop"/>). Used by the tier-def blind-
        /// retransmit early-exit to detect when the wheel has acked all of
        /// the tracked blind chunks so we can stop blasting.</summary>
        public bool Contains(byte session, int seq)
        {
            lock (_lock) return _queue.ContainsKey((session, seq));
        }

        /// <summary>
        /// Return frames whose per-chunk backoff has elapsed. Chunks past
        /// <paramref name="maxRetries"/> sends are dropped (assume permanent
        /// loss). Each successful retransmit doubles the chunk's next delay
        /// (capped at <see cref="MaxBackoffMs"/>) so a stuck chunk doesn't
        /// keep flooding the link.
        /// </summary>
        public List<byte[]> DueRetransmits(int maxRetries)
        {
            int now = Environment.TickCount;
            var output = new List<byte[]>();
            lock (_lock)
            {
                var doomed = new List<(byte, int)>();
                foreach (var kv in _queue)
                {
                    if (now - kv.Value.LastSentTicks < kv.Value.NextDelayMs) continue;
                    if (kv.Value.SendCount >= maxRetries)
                    {
                        doomed.Add(kv.Key);
                        continue;
                    }
                    output.Add(kv.Value.Frame);
                    kv.Value.LastSentTicks = now;
                    kv.Value.SendCount++;
                    int next = kv.Value.NextDelayMs * 2;
                    kv.Value.NextDelayMs = next > MaxBackoffMs ? MaxBackoffMs : next;
                }
                foreach (var k in doomed) _queue.Remove(k);
            }
            return output;
        }

        public void Clear()
        {
            lock (_lock) _queue.Clear();
        }
    }
}
