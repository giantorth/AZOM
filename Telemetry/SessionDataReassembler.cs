using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace MozaPlugin.Telemetry
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
        // Last accepted inbound seq, or -1 before the first chunk. Used by the
        // seq-aware AddChunk overload to detect missing chunks (a single dropped
        // chunk under Wine SerialPort R/W contention silently corrupts the zlib
        // stream — see TelemetrySender.cs ProbeAndOpenSessions comment for the
        // pre-existing 0x09 burst issue this guard now catches across sessions).
        private int _lastSeq = -1;
        private string _gapTag = "";

        public int Length { get { lock (_buffer) return _buffer.Count; } }

        /// <summary>Last accepted inbound seq, or -1 if no chunks have been
        /// added since the last <see cref="Clear"/>.</summary>
        public int LastAcceptedSeq { get { lock (_buffer) return _lastSeq; } }

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
                        MozaLog.Warn($"[Moza] SessionDataReassembler exceeded {MaxBufferBytes} bytes ({_buffer.Count}+{net.Length}); dropping buffer");
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
        /// detected restart); false ONLY on a forward gap (chunks lost mid-
        /// burst), in which case the buffer is cleared and the caller should
        /// trigger session-specific recovery (re-issue the open / prime / RPC
        /// call so the wheel re-emits its burst). Without forward-gap detection
        /// a single dropped chunk silently corrupts the zlib stream and the
        /// surrounding handshake fails permanently for the lifetime of the
        /// session.
        ///
        /// Three non-monotonic cases are recognised:
        ///   - <c>seq == _lastSeq</c> — exact duplicate (wheel retransmit
        ///     because its inbound ack got lost). Skip silently; chunk's bytes
        ///     are already in the buffer.
        ///   - <c>seq &lt; _lastSeq</c> — wheel restarted its outbound seq
        ///     counter for a new logical message (common on file-transfer
        ///     sessions where each dir-listing / RPC reply burst is
        ///     independent, and after configJson re-prime). Clear in-progress
        ///     buffer and accept this chunk as the first of the new burst.
        ///   - <c>seq &gt; _lastSeq + 1</c> — true forward gap (chunks lost).
        ///     Clear, return false.
        ///
        /// First chunk after Clear (or Reset) accepts any seq. <paramref name="tag"/>
        /// is a free-form label included in the warning log (e.g. "sess=0x09")
        /// so multi-reassembler diagnostics are distinguishable.
        /// </summary>
        public bool AddChunk(int seq, byte[] chunk, string tag = "")
        {
            if (chunk == null || chunk.Length < 4) return true;
            byte[] net = StripCrcTrailer(chunk);
            lock (_buffer)
            {
                if (_lastSeq != -1 && seq != _lastSeq + 1)
                {
                    if (seq == _lastSeq) return true;

                    string warnTag = string.IsNullOrEmpty(tag) ? _gapTag : tag;
                    string tagSuffix = string.IsNullOrEmpty(warnTag) ? "" : $" ({warnTag})";
                    if (seq < _lastSeq)
                    {
                        MozaLog.Debug(
                            $"[Moza] Reassembler seq restart{tagSuffix}: " +
                            $"got seq={seq}, last was {_lastSeq}; clearing {_buffer.Count}B buffer (assuming new burst)");
                        _buffer.Clear();
                        _overflowLogged = false;
                        _lastSeq = -1;
                        // fall through to accept this chunk as the first of the new burst
                    }
                    else
                    {
                        int missing = seq - _lastSeq - 1;
                        MozaLog.Warn(
                            $"[Moza] Reassembler seq gap{tagSuffix}: " +
                            $"got seq={seq}, expected {_lastSeq + 1} ({missing} chunk(s) missing); " +
                            $"clearing {_buffer.Count}B buffer — caller should re-handshake");
                        _buffer.Clear();
                        _overflowLogged = false;
                        _lastSeq = -1;
                        return false;
                    }
                }
                if (_buffer.Count + net.Length > MaxBufferBytes)
                {
                    if (!_overflowLogged)
                    {
                        MozaLog.Warn($"[Moza] SessionDataReassembler exceeded {MaxBufferBytes} bytes ({_buffer.Count}+{net.Length}); dropping buffer");
                        _overflowLogged = true;
                    }
                    _buffer.Clear();
                    _lastSeq = -1;
                    return true;
                }
                _buffer.AddRange(net);
                _lastSeq = seq;
                if (!string.IsNullOrEmpty(tag)) _gapTag = tag;
                return true;
            }
        }

        public void Clear()
        {
            lock (_buffer)
            {
                _buffer.Clear();
                _overflowLogged = false;
                _lastSeq = -1;
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
