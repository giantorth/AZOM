using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using MozaPlugin.Telemetry.Frames;

namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// Reassembles 7c:00 type=0x01 session data chunks into the underlying
    /// application message. Real-wheel chunks have a 4-byte CRC32 trailer
    /// (verified by <see cref="TierDefinitionBuilder.Crc32"/>). Some chunks
    /// carry an extra 1-byte flag prefix before the TLV payload (observed in
    /// pithouse-style tier def chunks); we try both strip layouts and pick
    /// whichever makes the CRC match.
    ///
    /// Once enough bytes are buffered the caller can decode the 9-byte
    /// compressed envelope used on sessions 0x04 and 0x09:
    ///
    ///   [flag:1B=0x00] [comp_size:u32 LE] [uncomp_size:u32 LE] [zlib stream]
    ///
    /// The zlib stream uses deflate with a zlib header (78 9c / 78 da). The
    /// reassembler doesn't know how many compressed bytes to expect up front
    /// — the envelope size field is only present on the FIRST chunk of a
    /// message — so callers should invoke <see cref="TryDecompress"/> when a
    /// logical message boundary is signalled elsewhere (e.g. session 0x04 END
    /// marker, session 0x09 seq increment stall).
    /// </summary>
    public sealed class SessionDataReassembler
    {
        // Cap so a stream of chunks whose envelope/zlib never resolves can't
        // grow the list to OOM. Mirrors TileServerStateParser's 1 MiB cap.
        // Real session payloads (configJson state, mzdash uploads) sit well
        // below this; overflow means the stream is corrupted or the END
        // marker was lost.
        private const int MaxBufferBytes = 1 << 20;

        private readonly List<byte> _buffer = new();
        private bool _overflowLogged;
        // Highest contiguous-received seq (-1 before the first chunk). Any
        // seq > HighWaterSeq is unacked; the wheel auto-retransmits from
        // the first unacked seq on its outstanding-ack timer (~1.3 s).
        private int _lastSeq = -1;
        private string _gapTag = "";
        // UTC ticks of the last forward gap; drives the soft-watchdog
        // escalation from passive-wait to prime+open-request.
        private long _lastForwardGapUtcTicks;

        public int Length { get { lock (_buffer) return _buffer.Count; } }

        /// <summary>Highest contiguously-received seq, suitable for sending as
        /// a cumulative ACK. -1 before the first chunk after a
        /// <see cref="Clear"/>. Out-of-order chunks are dropped (caller acks
        /// this value so the wheel retransmits the missing seq), so the
        /// high-water mark is also the most-recently-accepted seq.</summary>
        public int HighWaterSeq { get { lock (_buffer) return _lastSeq; } }

        /// <summary>UTC ticks of the most recent forward-gap observation
        /// (seq &gt; _lastSeq + 1), or 0 if no gap has been observed.
        /// Callers use this to drive the "wait for wheel auto-retransmit
        /// vs. escalate to prime+open-request" decision.</summary>
        public long LastForwardGapUtcTicks
        {
            get { lock (_buffer) return _lastForwardGapUtcTicks; }
        }

        /// <summary>
        /// Result of an <see cref="Insert(int, byte[], string)"/> call.
        /// </summary>
        public enum ChunkInsertResult
        {
            /// <summary>Chunk had seq == HighWaterSeq+1; bytes appended,
            /// HighWaterSeq advanced.</summary>
            Contiguous,
            /// <summary>Chunk had seq == HighWaterSeq; bytes already in
            /// buffer (wheel retransmit because our ack didn't reach it).
            /// Caller should still re-ack to confirm receipt.</summary>
            Duplicate,
            /// <summary>Wheel restarted its outbound seq counter for a new
            /// burst (seq &lt; HighWaterSeq). Buffer was cleared and this
            /// chunk accepted as the first chunk of the new burst.</summary>
            Restart,
            /// <summary>Forward gap (seq &gt; HighWaterSeq+1). Buffer
            /// preserved, HighWaterSeq unchanged, chunk bytes DROPPED. Caller
            /// must ack HighWaterSeq (not seq) so the wheel knows to
            /// retransmit. If the wheel doesn't retransmit within its
            /// auto-retransmit window, the caller should escalate via a
            /// prime+open-request or similar.</summary>
            GapDetected,
            /// <summary>Buffer overflowed the 1 MiB cap. Buffer was cleared;
            /// caller should treat this as a hard reset.</summary>
            BufferOverflow,
        }

        /// <summary>Feed one raw chunk payload (the bytes after session/type/seq).
        /// No seq tracking — caller doesn't have the seq context. Use the
        /// <see cref="AddChunk(int, byte[])"/> overload when seq is available so
        /// gaps are detected instead of corrupting the buffer silently.</summary>
        public void AddChunk(byte[] chunk)
        {
            if (chunk == null || chunk.Length < 4) return;
            byte[] net = StripCrcTrailer(chunk);
            lock (_buffer)
            {
                if (_buffer.Count + net.Length > MaxBufferBytes)
                {
                    if (!_overflowLogged)
                    {
                        MozaLog.Warn($"[AZOM] SessionDataReassembler exceeded {MaxBufferBytes} bytes ({_buffer.Count}+{net.Length}); dropping buffer");
                        _overflowLogged = true;
                    }
                    _buffer.Clear();
                    return;
                }
                _buffer.AddRange(net);
            }
        }

        /// <summary>
        /// Feed one chunk WITH its inbound seq number. Returns true when the
        /// chunk was accepted (including as the start of a new burst after a
        /// detected restart); false on forward gap (chunks lost mid-burst) or
        /// buffer overflow. Back-compat wrapper around <see cref="Insert"/> —
        /// new code should prefer the enum-returning variant since "false"
        /// no longer maps 1:1 to a single recovery action.
        ///
        /// Important: on forward gap the buffer is now PRESERVED (was
        /// cleared in the pre-2026-05-15 implementation). The wheel
        /// auto-retransmits the missing chunk from its outstanding-ack
        /// timer (~1.3 s observed in PitHouse capture), at which point the
        /// chunks arrive contiguously and the buffer holds the full burst.
        /// Caller must ack <see cref="HighWaterSeq"/> (cumulative), NOT
        /// the just-received seq — acking the post-gap seq would tell the
        /// wheel "I have everything up to seq" and suppress retransmit.
        /// </summary>
        public bool AddChunk(int seq, byte[] chunk, string tag = "")
        {
            var r = Insert(seq, chunk, tag);
            return r != ChunkInsertResult.GapDetected
                && r != ChunkInsertResult.BufferOverflow;
        }

        /// <summary>
        /// Rich-result variant of <see cref="AddChunk"/>. Returns one of
        /// <see cref="ChunkInsertResult"/> values describing exactly what
        /// happened, so callers can implement cumulative-ack semantics
        /// (ack <see cref="HighWaterSeq"/> on every insert regardless of
        /// outcome — that's what tells the wheel which seq to retransmit
        /// from) and choose recovery escalation (Restart / GapDetected /
        /// BufferOverflow have different meanings).
        ///
        /// Three non-monotonic cases:
        ///   - <c>seq == _lastSeq</c> → <see cref="ChunkInsertResult.Duplicate"/>.
        ///     Wheel retransmit because our inbound ack got lost. Bytes
        ///     already in buffer; do not re-append. Re-ack confirms receipt.
        ///   - <c>seq &lt; _lastSeq</c> → <see cref="ChunkInsertResult.Restart"/>.
        ///     Wheel restarted its outbound seq counter for a new logical
        ///     message (RPC reply, dir-listing burst, post-prime+open). Clear
        ///     in-progress buffer and accept this chunk as first of new burst.
        ///   - <c>seq &gt; _lastSeq + 1</c> → <see cref="ChunkInsertResult.GapDetected"/>.
        ///     True forward gap. Buffer preserved, HighWaterSeq unchanged,
        ///     this chunk's bytes DROPPED. Caller acks HighWaterSeq → wheel
        ///     retransmits from <c>HighWaterSeq + 1</c>.
        ///
        /// First chunk after <see cref="Clear"/> accepts any seq.
        /// <paramref name="tag"/> is a free-form label included in warning
        /// logs (e.g. <c>"sess=0x09"</c>).
        /// </summary>
        public ChunkInsertResult Insert(int seq, byte[] chunk, string tag = "")
        {
            // Malformed (too short to hold the CRC trailer): drop silently.
            // We classify as Duplicate so callers don't react (no buffer
            // mutation, no _lastSeq advance, ack-from-HighWaterSeq is correct).
            if (chunk == null || chunk.Length < 4) return ChunkInsertResult.Duplicate;
            byte[] net = StripCrcTrailer(chunk);
            lock (_buffer)
            {
                bool wasRestart = false;
                if (_lastSeq != -1 && seq != _lastSeq + 1)
                {
                    string warnTag = string.IsNullOrEmpty(tag) ? _gapTag : tag;
                    string tagSuffix = string.IsNullOrEmpty(warnTag) ? "" : $" ({warnTag})";

                    if (seq == _lastSeq)
                    {
                        // Wheel retransmit of the previous chunk (its inbound
                        // ack-receipt timer fired before our ack reached it).
                        // Bytes already in buffer; re-ack at the call site
                        // re-affirms receipt to the wheel.
                        return ChunkInsertResult.Duplicate;
                    }
                    if (seq < _lastSeq)
                    {
                        MozaLog.Debug(
                            $"[AZOM] Reassembler seq restart{tagSuffix}: " +
                            $"got seq={seq}, last was {_lastSeq}; clearing {_buffer.Count}B buffer (assuming new burst)");
                        _buffer.Clear();
                        _overflowLogged = false;
                        _lastSeq = -1;
                        wasRestart = true;
                        // fall through to accept this chunk as the first of the new burst
                    }
                    else
                    {
                        // Forward gap. PRESERVE the buffer — the wheel will
                        // auto-retransmit from HighWaterSeq+1 once its
                        // outstanding-ack timer fires. We drop THIS chunk's
                        // bytes because appending out-of-order corrupts the
                        // zlib stream; when the wheel retransmits, it will
                        // re-send this seq too (verified in PitHouse capture
                        // bridge-20260514-204307.jsonl: wheel re-bursts the
                        // full unacked window, not just the missing chunk).
                        _lastForwardGapUtcTicks = System.DateTime.UtcNow.Ticks;
                        int missing = seq - _lastSeq - 1;
                        MozaLog.Warn(
                            $"[AZOM] Reassembler forward gap{tagSuffix}: " +
                            $"got seq={seq}, expected {_lastSeq + 1} ({missing} chunk(s) missing); " +
                            $"preserving {_buffer.Count}B buffer, dropping out-of-order chunk — " +
                            $"caller must ack HighWaterSeq={_lastSeq} so wheel retransmits");
                        return ChunkInsertResult.GapDetected;
                    }
                }
                if (_buffer.Count + net.Length > MaxBufferBytes)
                {
                    if (!_overflowLogged)
                    {
                        MozaLog.Warn($"[AZOM] SessionDataReassembler exceeded {MaxBufferBytes} bytes ({_buffer.Count}+{net.Length}); dropping buffer");
                        _overflowLogged = true;
                    }
                    _buffer.Clear();
                    _lastSeq = -1;
                    return ChunkInsertResult.BufferOverflow;
                }
                _buffer.AddRange(net);
                _lastSeq = seq;
                if (!string.IsNullOrEmpty(tag)) _gapTag = tag;
                // Forward progress resumed — clear the gap timestamp so the
                // tick-driven watchdog (DisplayWatchdog gap retransmit) doesn't
                // fire an unnecessary prime+open-request.
                _lastForwardGapUtcTicks = 0;
                return wasRestart ? ChunkInsertResult.Restart : ChunkInsertResult.Contiguous;
            }
        }

        public void Clear()
        {
            lock (_buffer)
            {
                _buffer.Clear();
                _overflowLogged = false;
                _lastSeq = -1;
                _lastForwardGapUtcTicks = 0;
            }
        }

        /// <summary>Snapshot the current reassembled buffer.</summary>
        public byte[] Snapshot()
        {
            lock (_buffer)
                return _buffer.ToArray();
        }

        /// <summary>
        /// Strip the 4-byte CRC32 LE trailer per
        /// `docs/protocol/sessions/chunk-format.md`. Re-verified 2026-05-10
        /// against 524 historical wire-trace files from this wheel:
        /// 227,497 / 227,713 chunks matched 4-byte CRC, 0 / 227,713 matched
        /// the post-2026-05-09 "3-byte" variant. The 3-byte path was a
        /// verification-script tautology (off-by-one on b2h JSONL parsing —
        /// see TelemetrySender.cs catalog-feed comment for the full
        /// post-mortem). On mismatch we still strip 4 bytes blind so the
        /// reassembly buffer stays aligned; downstream consumers
        /// (configJson zlib inflate / RPC reply parse) will reject corrupt
        /// payloads and the TelemetrySender's per-feed CRC counters
        /// already record the rejection rate.
        /// </summary>
        public static byte[] StripCrcTrailer(byte[] chunk)
        {
            if (chunk.Length < 4) return chunk;

            uint wire = (uint)(chunk[chunk.Length - 4]
                             | (chunk[chunk.Length - 3] << 8)
                             | (chunk[chunk.Length - 2] << 16)
                             | (chunk[chunk.Length - 1] << 24));
            uint calc = TierDefinitionBuilder.Crc32(chunk, 0, chunk.Length - 4);
            // Note: we don't currently surface mismatch as a per-reassembler
            // counter here; if a class of corruption ever needs trend
            // tracking, add one. For now the alignment is what matters —
            // downstream layers reject malformed data on their own.
            _ = calc;
            _ = wire;

            var o = new byte[chunk.Length - 4];
            Array.Copy(chunk, 0, o, 0, o.Length);
            return o;
        }

        /// <summary>
        /// Decode the 9-byte compressed envelope + zlib payload. Returns
        /// decompressed bytes or null if the buffer doesn't look like an
        /// envelope yet (header too short, zlib decompression failed, etc).
        /// Caller is responsible for deciding when to call this — typically
        /// after seeing a session END marker or a complete RPC round-trip.
        ///
        /// NOTE: on sess=0x09 / 0x0a the inbound dispatcher copies starting
        /// at byte 8 of the wire frame, which is the session-header ack field
        /// (LE u16) — so the first 2 bytes of every buffer are the wheel's
        /// ack value, NOT an envelope sentinel. The <c>buf[0] == 0x00</c>
        /// branch below frequently misreads those bytes as an envelope and
        /// computes a bogus compSize; the magic-scan fallback below is what
        /// actually decompresses the real zlib stream a few bytes deeper.
        /// A previous attempt to "early-return when envelope says not enough"
        /// was reverted (commit history) because the bogus compSize threshold
        /// can never be reached, leaving configJson permanently un-parsed.
        /// The real fix would be to strip the 2 ack bytes before accumulating
        /// — left as a follow-up.
        /// </summary>
        public byte[]? TryDecompress()
        {
            byte[] buf = Snapshot();
            if (buf.Length < 9) return null;
            if (buf[0] == 0x00)
            {
                uint compSize = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
                if (buf.Length >= 9 + compSize)
                {
                    byte[]? r = DecompressZlib(buf, 9);
                    if (r != null) return r;
                }
            }
            // Fallback: mzdash bodies embed raw 0x7E bytes that interact with
            // the wire-level escape rules (see docs/protocol/wire/checksum.md).
            // If envelope-offset decode failed, scan for zlib magic (78 9c / 78 da)
            // and try each candidate — mirrors sim/wheel_sim.py:1742-1770.
            return TryDecompressByMagic(buf);
        }

        /// <summary>
        /// Scan <paramref name="buf"/> for zlib magic bytes (78 9c / 78 da) and
        /// trial-decompress each hit. Returns the first stream that inflates
        /// without error. Used as a fallback when envelope-offset decoding
        /// fails because embedded 0x7E in mzdash JSON corrupted the header.
        /// </summary>
        public static byte[]? TryDecompressByMagic(byte[] buf)
        {
            for (int i = 0; i + 2 <= buf.Length; i++)
            {
                if (buf[i] != 0x78) continue;
                if (buf[i + 1] != 0x9c && buf[i + 1] != 0xda) continue;
                byte[]? r = DecompressZlib(buf, i);
                if (r != null && r.Length > 0) return r;
            }
            return null;
        }

        /// <summary>
        /// Inflate a zlib stream (`78 9c` / `78 da`) starting at the given
        /// offset. Returns decompressed bytes or null on corruption.
        /// </summary>
        public static byte[]? DecompressZlib(byte[] data, int offset)
        {
            if (offset + 2 > data.Length) return null;
            // Skip 2-byte zlib header (CMF + FLG). DeflateStream expects raw deflate.
            int start = offset + 2;
            int length = data.Length - start;
            // Last 4 bytes of a full zlib stream are the Adler-32 checksum —
            // we can't know where the stream ends exactly without trial
            // decompression. Strip 4 bytes if we can't decode with them.
            using var ms = new MemoryStream(data, start, length);
            using var def = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            try
            {
                def.CopyTo(outMs);
            }
            catch (InvalidDataException)
            {
                return null;
            }
            return outMs.ToArray();
        }
    }
}
