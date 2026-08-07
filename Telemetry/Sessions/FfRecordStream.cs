using System.Collections.Generic;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>One decoded FF property record lifted off a session stream.</summary>
    internal readonly struct FfRecord
    {
        public readonly byte Session;
        public readonly uint Kind;
        public readonly byte[] Value;

        public FfRecord(byte session, uint kind, byte[] value)
        {
            Session = session;
            Kind = kind;
            Value = value;
        }
    }

    /// <summary>
    /// Per-session inbound reassembly of FF property records
    /// (<see cref="FfRecordReader"/> layout). A large record — the kind=14
    /// device log payload runs ~3 KB — spans 30+ session-data chunks, and on
    /// sessions 0x01/0x02 those chunks are interleaved with the typed sub-msg /
    /// catalog TLV stream. So this keeps a rolling per-session byte buffer and
    /// scans it for CRC-valid records, skipping anything that isn't one.
    ///
    /// This is a SECOND reader of the same bytes the channel-catalog parser
    /// consumes; it never mutates or consumes on that parser's behalf.
    ///
    /// Thread safety: writers are the serial read thread (one Append per
    /// inbound chunk); <see cref="Clear"/> runs from the sender's start/stop
    /// path. A single leaf lock guards all state — it is never held while
    /// calling out, so it can't stall the read thread's ack path.
    /// </summary>
    internal sealed class FfRecordStream
    {
        // Cap on a single session's rolling buffer. Above this we are clearly
        // not tracking a real record boundary any more (lost chunks, or the
        // session simply never carries FF records), so drop and resync.
        private const int MaxBufferBytes = 256 * 1024;

        // Once the buffer holds this much already-scanned prefix, compact it.
        private const int CompactThreshold = 8 * 1024;

        // A backward seq step at least this large reads as the device restarting
        // its outbound counter rather than retransmitting. Seq is a u16 on the
        // wire, so a 65535→0 wrap presents as a ~65k backward step and lands
        // here as a restart: the buffer is dropped and reassembly resumes on the
        // next record boundary, which is the safe outcome.
        private const int SeqRestartGap = 256;

        // A forward seq jump at least this large means chunks were dropped.
        private const int SeqWrapGap = 256;

        private readonly object _gate = new object();
        private readonly Dictionary<byte, State> _bySession = new Dictionary<byte, State>();

        private sealed class State
        {
            public byte[] Buf = new byte[4096];
            public int Count;
            // Offset of the first byte not yet ruled out as a record start.
            public int Scan;
            public int HighestSeq = -1;
        }

        /// <summary>
        /// Feed one inbound session-data chunk payload. <paramref name="payload"/>
        /// is the raw bytes after the 8-byte session header, INCLUDING the
        /// 4-byte per-chunk CRC32 trailer — the trailer is stripped here so
        /// callers can pass the same array they hand the catalog parser.
        /// Completed records are appended to <paramref name="into"/>.
        /// Retransmits are dropped by seq, mirroring
        /// <c>ChannelCatalogParser.AppendChunkIfNew</c>.
        /// </summary>
        public void Append(byte session, int seq, byte[] payload, List<FfRecord> into)
        {
            if (payload == null || payload.Length <= 4) return;
            int length = payload.Length - 4;

            // Verify the per-chunk CRC32 trailer BEFORE accepting the chunk, and
            // leave HighestSeq alone when it fails. This mirrors the catalog
            // feed in TelemetryInboundDispatcher: a corrupt chunk must not
            // consume its seq, or the wheel's retransmit of that same seq is
            // then discarded as a duplicate and the corruption becomes
            // permanent — losing the whole multi-chunk record rather than the
            // one bad chunk.
            uint wireCrc = (uint)(payload[length]
                                | (payload[length + 1] << 8)
                                | (payload[length + 2] << 16)
                                | (payload[length + 3] << 24));
            if (Frames.TierDefinitionBuilder.Crc32(payload, 0, length) != wireCrc) return;

            lock (_gate)
            {
                if (!_bySession.TryGetValue(session, out var st))
                {
                    st = new State();
                    _bySession[session] = st;
                }
                if (seq <= st.HighestSeq)
                {
                    // A small step back is a retransmit — drop it, the bytes are
                    // already buffered. A big step back is the device restarting
                    // its outbound seq counter, which invalidates the in-progress
                    // buffer; without handling it the stream would dedup
                    // everything forever after a session reopen. (Deliberately
                    // NOT SessionDataReassembler's "any backward step = restart":
                    // these sessions do carry out-of-order retransmits of older
                    // seqs, and treating one as a restart would drop a record
                    // mid-assembly.)
                    if (st.HighestSeq - seq < SeqRestartGap) return;
                    st.Count = 0;
                    st.Scan = 0;
                }
                else if (seq - st.HighestSeq >= SeqWrapGap && st.HighestSeq >= 0)
                {
                    // Forward jump this large means chunks were lost, not that
                    // the stream advanced — whatever is buffered can no longer
                    // be contiguous with what just arrived.
                    st.Count = 0;
                    st.Scan = 0;
                }
                st.HighestSeq = seq;

                if (st.Count + length > MaxBufferBytes)
                {
                    // Nothing coherent is being tracked; start over rather than grow.
                    st.Count = 0;
                    st.Scan = 0;
                }
                EnsureCapacity(st, st.Count + length);
                System.Array.Copy(payload, 0, st.Buf, st.Count, length);
                st.Count += length;

                Drain(session, st, into);
                Compact(st);
            }
        }

        /// <summary>Drop all buffered state. Called on sender start/stop so a
        /// half-record from the previous session can't bleed into the next.</summary>
        public void Clear()
        {
            lock (_gate) _bySession.Clear();
        }

        private static void Drain(byte session, State st, List<FfRecord> into)
        {
            while (st.Scan < st.Count)
            {
                // Find the next candidate sentinel.
                int at = st.Scan;
                while (at < st.Count && st.Buf[at] != 0xFF) at++;
                if (at >= st.Count)
                {
                    // No sentinel left anywhere — everything scanned is dead weight.
                    st.Scan = st.Count;
                    return;
                }

                if (FfRecordReader.TryParse(st.Buf, at, st.Count,
                        out uint kind, out int valueOffset, out int valueLength,
                        out int consumed, out bool needMore))
                {
                    var value = new byte[valueLength];
                    System.Array.Copy(st.Buf, valueOffset, value, 0, valueLength);
                    into.Add(new FfRecord(session, kind, value));
                    st.Scan = at + consumed;
                    continue;
                }

                if (needMore)
                {
                    // Valid-looking header, record not fully buffered yet. Park
                    // the scan ON the sentinel so the next chunk retries here.
                    st.Scan = at;
                    return;
                }

                // False sentinel (a 0xFF byte inside some other record, or a
                // CRC miss from a lost chunk) — step past it and keep looking.
                st.Scan = at + 1;
            }
        }

        private static void Compact(State st)
        {
            if (st.Scan < CompactThreshold) return;
            int remain = st.Count - st.Scan;
            if (remain > 0)
                System.Array.Copy(st.Buf, st.Scan, st.Buf, 0, remain);
            st.Count = remain;
            st.Scan = 0;

            // Hand back a buffer that a one-off giant record (or an overflow
            // reset) grew — otherwise the peak allocation is held for the life
            // of the connection, per session, per sender.
            if (st.Buf.Length > CompactThreshold * 2 && remain < CompactThreshold)
            {
                var shrunk = new byte[CompactThreshold * 2];
                System.Array.Copy(st.Buf, 0, shrunk, 0, remain);
                st.Buf = shrunk;
            }
        }

        private static void EnsureCapacity(State st, int needed)
        {
            if (st.Buf.Length >= needed) return;
            int size = st.Buf.Length;
            while (size < needed) size *= 2;
            var grown = new byte[size];
            System.Array.Copy(st.Buf, 0, grown, 0, st.Count);
            st.Buf = grown;
        }
    }
}
