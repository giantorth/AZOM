using System;
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
    /// Callers run <see cref="TryDecompress"/> after each accepted chunk; it
    /// returns (and consumes) a zlib message only once the whole stream is
    /// buffered and verifies. Sessions 0x09/0x0a wrap it in the 9-byte envelope
    ///
    ///   [flag:1B=0x00] [comp_size+4:u32 LE] [uncomp_size:u32 LE] [zlib stream]
    ///
    /// other sessions (0x04 dir listing) use their own prefix.
    /// </summary>
    public sealed class SessionDataReassembler
    {
        // Cap so a stream of chunks whose envelope/zlib never resolves can't
        // grow the list to OOM. Mirrors TileServerStateParser's 1 MiB cap.
        // Real session payloads (configJson state, mzdash uploads) sit well
        // below this; overflow means the stream is corrupted or the END
        // marker was lost.
        private const int MaxBufferBytes = 1 << 20;

        private const int InitialCapacity = 256;
        // A cleared buffer that grew past this is replaced so one large
        // message doesn't pin its peak allocation for the session lifetime.
        private const int ShrinkAboveBytes = 64 * 1024;

        // Guards every field below. TryDecompress inflates under it: the
        // writers are the serial read thread itself, Clear() runs on stop.
        private readonly object _lock = new object();
        private byte[] _buf = new byte[InitialCapacity];
        private int _count;
        private bool _overflowLogged;
        // Buffer length an enveloped message needs before it is complete; 0 = none.
        private int _pendingEnd;
        // Buffer length at the last decode miss; the same bytes can't decode now.
        private int _missLength = -1;
        // Highest contiguous-received seq (-1 before the first chunk). Any
        // seq > HighWaterSeq is unacked; the wheel auto-retransmits from
        // the first unacked seq on its outstanding-ack timer (~1.3 s).
        private int _lastSeq = -1;
        private string _gapTag = "";
        // UTC ticks of the last forward gap; drives the soft-watchdog
        // escalation from passive-wait to prime+open-request.
        private long _lastForwardGapUtcTicks;

        public int Length { get { lock (_lock) return _count; } }

        /// <summary>Highest contiguously-received seq, suitable for sending as
        /// a cumulative ACK. -1 before the first chunk after a
        /// <see cref="Clear"/> or after <see cref="TryDecompress"/> drains the
        /// buffer, so read it before decoding. Out-of-order chunks are dropped (caller acks
        /// this value so the wheel retransmits the missing seq), so the
        /// high-water mark is also the most-recently-accepted seq.</summary>
        public int HighWaterSeq { get { lock (_lock) return _lastSeq; } }

        /// <summary>UTC ticks of the most recent forward-gap observation
        /// (seq &gt; _lastSeq + 1), or 0 if no gap has been observed.
        /// Callers use this to drive the "wait for wheel auto-retransmit
        /// vs. escalate to prime+open-request" decision.</summary>
        public long LastForwardGapUtcTicks
        {
            get { lock (_lock) return _lastForwardGapUtcTicks; }
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
            int netLen = chunk.Length - 4;
            lock (_lock)
            {
                if (_count + netLen > MaxBufferBytes)
                {
                    if (!_overflowLogged)
                    {
                        MozaLog.Warn($"[AZOM] SessionDataReassembler exceeded {MaxBufferBytes} bytes ({_count}+{netLen}); dropping buffer");
                        _overflowLogged = true;
                    }
                    ResetBufferLocked();
                    return;
                }
                AppendLocked(chunk, netLen);
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
            int netLen = chunk.Length - 4;
            lock (_lock)
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
                            $"got seq={seq}, last was {_lastSeq}; clearing {_count}B buffer (assuming new burst)");
                        ResetBufferLocked();
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
                            $"preserving {_count}B buffer, dropping out-of-order chunk — " +
                            $"caller must ack HighWaterSeq={_lastSeq} so wheel retransmits");
                        return ChunkInsertResult.GapDetected;
                    }
                }
                if (_count + netLen > MaxBufferBytes)
                {
                    if (!_overflowLogged)
                    {
                        MozaLog.Warn($"[AZOM] SessionDataReassembler exceeded {MaxBufferBytes} bytes ({_count}+{netLen}); dropping buffer");
                        _overflowLogged = true;
                    }
                    ResetBufferLocked();
                    _lastSeq = -1;
                    return ChunkInsertResult.BufferOverflow;
                }
                AppendLocked(chunk, netLen);
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
            lock (_lock)
            {
                ResetBufferLocked();
                _overflowLogged = false;
                _lastSeq = -1;
                _lastForwardGapUtcTicks = 0;
            }
        }

        private void ResetBufferLocked()
        {
            _count = 0;
            _pendingEnd = 0;
            _missLength = -1;
            if (_buf.Length > ShrinkAboveBytes) _buf = new byte[InitialCapacity];
        }

        private void AppendLocked(byte[] src, int count)
        {
            if (count <= 0) return;
            int needed = _count + count;
            if (needed > _buf.Length)
            {
                int size = _buf.Length;
                while (size < needed) size *= 2;
                var grown = new byte[size];
                Buffer.BlockCopy(_buf, 0, grown, 0, _count);
                _buf = grown;
            }
            Buffer.BlockCopy(src, 0, _buf, _count, count);
            _count = needed;
        }

        private void RemoveHeadLocked(int count)
        {
            int remain = _count - count;
            if (remain > 0) Buffer.BlockCopy(_buf, count, _buf, 0, remain);
            _count = remain;
        }

        /// <summary>Snapshot the current reassembled buffer.</summary>
        public byte[] Snapshot()
        {
            lock (_lock)
            {
                var copy = new byte[_count];
                Buffer.BlockCopy(_buf, 0, copy, 0, _count);
                return copy;
            }
        }

        /// <summary>
        /// Strip the 4-byte CRC32 LE trailer per
        /// `docs/protocol/sessions/chunk-format.md`. Re-verified 2026-05-10
        /// against 524 historical wire-trace files from this wheel:
        /// 227,497 / 227,713 chunks matched 4-byte CRC, 0 / 227,713 matched
        /// the post-2026-05-09 "3-byte" variant. The 3-byte path was a
        /// verification-script tautology (off-by-one on b2h JSONL parsing —
        /// see TelemetrySender.cs catalog-feed comment for the full
        /// post-mortem). The trailer is stripped blind — not verified — so the
        /// reassembly buffer stays aligned; downstream consumers (configJson
        /// zlib inflate / RPC reply parse) reject corrupt payloads and the
        /// TelemetrySender's per-feed CRC counters already record the rejection
        /// rate (the dispatcher verifies the CRC before handing the chunk over).
        /// </summary>
        public static byte[] StripCrcTrailer(byte[] chunk)
        {
            if (chunk.Length < 4) return chunk;

            var o = new byte[chunk.Length - 4];
            Array.Copy(chunk, 0, o, 0, o.Length);
            return o;
        }

        /// <summary>
        /// Return the first COMPLETE zlib message in the buffer and remove its
        /// bytes (plus anything before it); null while no message is complete.
        /// A message counts as complete only when its stream inflates and its
        /// Adler-32 trailer verifies — DeflateStream returns partial output on
        /// truncated input, so a bare inflate can't tell a fragment from a
        /// message. Bytes after the message (the wheel packs the next
        /// message's head into the same chunk) stay buffered; once the buffer
        /// empties, seq tracking restarts as after <see cref="Clear"/>.
        ///
        /// The stream is located by its 78 9c / 78 da magic. When the 9-byte
        /// envelope directly precedes it, the envelope's sizes gate the decode
        /// (no inflate until all comp_size−4 zlib bytes are buffered) and the
        /// inflated length must match uncomp_size. Other prefixes (0x04 dir
        /// listing) fall back to locating the Adler-32 trailer after the stream.
        /// </summary>
        public byte[]? TryDecompress()
        {
            int dropped;
            string tag;
            lock (_lock)
            {
                int n = _count;
                if (n < MinZlibBytes || n == _missLength || n < _pendingEnd) return null;

                byte[]? msg = FindCompleteMessage(_buf, n, out int end, out int pendingEnd, out int dropTo);
                if (msg != null)
                {
                    RemoveHeadLocked(end);
                    _pendingEnd = 0;
                    _missLength = -1;
                    if (_count == 0)
                    {
                        _overflowLogged = false;
                        _lastSeq = -1;
                        _lastForwardGapUtcTicks = 0;
                    }
                    return msg;
                }
                if (dropTo > 0) RemoveHeadLocked(dropTo);
                _pendingEnd = pendingEnd > 0 ? pendingEnd - dropTo : 0;
                _missLength = _count;
                if (dropTo == 0) return null;
                dropped = dropTo;
                tag = _gapTag;
            }
            MozaLog.Debug(
                $"[AZOM] Reassembler{(tag.Length > 0 ? " (" + tag + ")" : "")}: " +
                $"discarded {dropped}B up to an enveloped zlib message that failed verification");
            return null;
        }

        // Smallest zlib stream: 2-byte header + 2-byte empty deflate + Adler-32.
        private const int MinZlibBytes = 8;

        /// <summary>
        /// Scan <c>buf[0, len)</c> for the first complete, verified zlib
        /// message. <paramref name="end"/> is the offset just past it. When an
        /// enveloped message is still incomplete, returns null with
        /// <paramref name="pendingEnd"/> = the buffer length it needs.
        /// <paramref name="dropTo"/> is the offset past the last enveloped
        /// candidate that was complete but failed verification.
        /// </summary>
        private static byte[]? FindCompleteMessage(byte[] buf, int len,
            out int end, out int pendingEnd, out int dropTo)
        {
            end = 0;
            pendingEnd = 0;
            dropTo = 0;
            int tried = 0;
            for (int i = 0; i + 2 <= len; i++)
            {
                if (buf[i] != 0x78) continue;
                if (buf[i + 1] != 0x9c && buf[i + 1] != 0xda) continue;

                if (TryReadEnvelope(buf, i, out int zlibLen, out int uncompLen))
                {
                    int msgEnd = i + zlibLen;
                    if (msgEnd > len)
                    {
                        pendingEnd = msgEnd;
                        return null;
                    }
                    if (++tried > MaxMagicCandidates) return null;
                    byte[]? r = InflateZlibExact(buf, i, zlibLen, uncompLen);
                    if (r != null)
                    {
                        end = msgEnd;
                        return r;
                    }
                    // Sizes may belong to bytes that only look like an envelope.
                    r = InflateZlibToTrailer(buf, i, len, out int streamEnd);
                    if (r != null)
                    {
                        end = streamEnd;
                        return r;
                    }
                    // All its bytes are here and it still doesn't verify: never retry it.
                    dropTo = i + 2;
                    continue;
                }

                if (++tried > MaxMagicCandidates) return null;
                byte[]? s = InflateZlibToTrailer(buf, i, len, out int sEnd);
                if (s != null)
                {
                    end = sEnd;
                    return s;
                }
            }
            return null;
        }

        /// <summary>Read the 9-byte 0x09/0x0a envelope ending right before the
        /// zlib magic at <paramref name="magicAt"/>. False when the bytes there
        /// can't be one (wrong flag, sizes out of range or inconsistent with
        /// deflate's ratio limits — the 0x04 dir-listing prefix has a zero byte
        /// and big-endian sizes in that position).</summary>
        private static bool TryReadEnvelope(byte[] buf, int magicAt, out int zlibLen, out int uncompLen)
        {
            zlibLen = 0;
            uncompLen = 0;
            if (magicAt < 9 || buf[magicAt - 9] != 0x00) return false;
            uint compPlus4 = (uint)(buf[magicAt - 8] | (buf[magicAt - 7] << 8)
                                  | (buf[magicAt - 6] << 16) | (buf[magicAt - 5] << 24));
            uint uncomp = (uint)(buf[magicAt - 4] | (buf[magicAt - 3] << 8)
                               | (buf[magicAt - 2] << 16) | (buf[magicAt - 1] << 24));
            if (compPlus4 < MinZlibBytes + 4 || compPlus4 - 4 > MaxBufferBytes) return false;
            if (uncomp == 0 || uncomp > MaxInflatedBytes) return false;
            long zl = compPlus4 - 4;
            // Deflate tops out near 1032:1; a zlib encoder never exceeds the
            // stored-block size (input + 5 B per 64 KiB block + 6 B framing).
            if (uncomp > zl * 1032 || zl > uncomp + uncomp / 8 + 64) return false;
            zlibLen = (int)zl;
            uncompLen = (int)uncomp;
            return true;
        }

        /// <summary>
        /// Inflate the zlib stream occupying exactly
        /// <c>data[offset, offset + zlibLength)</c> (header + deflate + Adler-32)
        /// and verify it: the Adler-32 trailer must match and, when
        /// <paramref name="expectedLength"/> ≥ 0, so must the inflated length.
        /// Null on any mismatch, corruption, or oversize output.
        /// </summary>
        internal static byte[]? InflateZlibExact(byte[] data, int offset, int zlibLength, int expectedLength)
        {
            if (zlibLength < MinZlibBytes || offset < 0 || offset + zlibLength > data.Length) return null;
            byte[]? r = Inflate(data, offset + 2, zlibLength - 6);
            if (r == null) return null;
            if (expectedLength >= 0 && r.Length != expectedLength) return null;
            return Adler32(r, 0, r.Length) == ReadU32BE(data, offset + zlibLength - 4) ? r : null;
        }

        /// <summary>
        /// Inflate the zlib stream at <paramref name="offset"/> without knowing
        /// its length, and accept it only when the Adler-32 of the output sits
        /// in <c>buf[offset, len)</c> after the stream start — absent for a
        /// truncated stream. <paramref name="end"/> is the offset just past
        /// that trailer.
        /// </summary>
        private static byte[]? InflateZlibToTrailer(byte[] buf, int offset, int len, out int end)
        {
            end = 0;
            byte[]? r = Inflate(buf, offset + 2, len - offset - 2);
            if (r == null || r.Length == 0) return null;
            uint adler = Adler32(r, 0, r.Length);
            for (int p = offset + 4; p + 4 <= len; p++)
            {
                if (ReadU32BE(buf, p) != adler) continue;
                end = p + 4;
                return r;
            }
            return null;
        }

        private static uint ReadU32BE(byte[] b, int at)
            => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

        /// <summary>zlib Adler-32 (RFC 1950).</summary>
        internal static uint Adler32(byte[] data, int offset, int count)
        {
            const uint Mod = 65521;
            // Largest n such that 255n(n+1)/2 + (n+1)(Mod-1) fits in a uint.
            const int Nmax = 5552;
            uint a = 1, b = 0;
            int stop = offset + count;
            while (offset < stop)
            {
                int n = Math.Min(Nmax, stop - offset);
                for (int k = 0; k < n; k++)
                {
                    a += data[offset++];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            return (b << 16) | a;
        }

        // The real stream sits right after its envelope/prefix; a buffer that
        // keeps failing past this many candidates is garbage, and
        // each failed candidate walks the inflater over the whole tail on the
        // serial read thread.
        private const int MaxMagicCandidates = 16;
        // Inflate ceiling. configJson state blobs are tens of KB; anything past
        // this is a corrupt stream, and an unbounded inflate is an OOM risk in
        // the x86 process.
        private const int MaxInflatedBytes = 4 << 20;

        /// <summary>
        /// Scan <paramref name="buf"/> for zlib magic bytes (78 9c / 78 da) and
        /// trial-decompress each hit. Returns the first stream that inflates
        /// without error — no completeness or Adler-32 check, so callers must
        /// pass a whole blob. Serves external callers (DashboardDownloader,
        /// DeviceLogParser); <see cref="TryDecompress"/> does not use it.
        /// </summary>
        public static byte[]? TryDecompressByMagic(byte[] buf)
        {
            int tried = 0;
            for (int i = 0; i + 2 <= buf.Length; i++)
            {
                if (buf[i] != 0x78) continue;
                if (buf[i + 1] != 0x9c && buf[i + 1] != 0xda) continue;
                if (++tried > MaxMagicCandidates) return null;
                byte[]? r = DecompressZlib(buf, i);
                if (r != null && r.Length > 0) return r;
            }
            return null;
        }

        /// <summary>
        /// Inflate a zlib stream (`78 9c` / `78 da`) starting at the given
        /// offset. Returns decompressed bytes or null on corruption or when the
        /// output exceeds <see cref="MaxInflatedBytes"/>.
        /// </summary>
        public static byte[]? DecompressZlib(byte[] data, int offset)
        {
            if (offset + 2 > data.Length) return null;
            // Skip 2-byte zlib header (CMF + FLG). DeflateStream expects raw deflate
            // and stops at the final block, so a trailing Adler-32 is ignored.
            return Inflate(data, offset + 2, data.Length - offset - 2);
        }

        /// <summary>Inflate raw deflate data <c>data[start, start + count)</c>,
        /// bounded by <see cref="MaxInflatedBytes"/>. Null on corruption or
        /// oversize output; truncated input yields partial output.</summary>
        private static byte[]? Inflate(byte[] data, int start, int count)
        {
            if (count < 0) return null;
            using var ms = new MemoryStream(data, start, count);
            using var def = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            var chunk = new byte[16 * 1024];
            try
            {
                int n;
                while ((n = def.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (outMs.Length + n > MaxInflatedBytes) return null;
                    outMs.Write(chunk, 0, n);
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }
            return outMs.ToArray();
        }
    }
}
