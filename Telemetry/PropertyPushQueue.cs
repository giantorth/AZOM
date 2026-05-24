using System.Collections.Generic;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Frames;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Coalescing queue for wheel-integrated dashboard FF property pushes
    /// on session 0x02 (display brightness, standby, etc.). Supersedes a
    /// pending push of the same kind by dropping its prior seqs from the
    /// retransmitter before queuing the new push.
    ///
    /// Without this coalescing, a rapid slider drag (each ValueChanged
    /// fires its own FF chunk that retransmits up to 100×) leaves stale
    /// intermediate values — including any momentary brightness=0 —
    /// interleaving with the latest, blanking the display.
    /// See docs/protocol/findings/2026-04-29-session-01-property-push.md.
    /// </summary>
    internal sealed class PropertyPushQueue
    {
        private readonly TelemetrySender _sender;

        // Latest chunk seqs of the most-recent FF property push per
        // (session, kind). Lookup happens under _sender.Session02SeqLock.
        private readonly Dictionary<(byte session, uint kind), List<int>> _lastSeqs
            = new Dictionary<(byte, uint), List<int>>();

        public PropertyPushQueue(TelemetrySender sender)
        {
            _sender = sender;
        }

        /// <summary>Push a u32-valued property (e.g. brightness 0–100).</summary>
        public void SendU32(uint kind, uint value)
        {
            if (!_sender.ConnectionIsConnected) return;
            byte[] body = SessionPropertyPushBuilder.BuildU32Body(kind, value);
            SendBody(body);
        }

        /// <summary>Push a u64-valued property (e.g. standby in milliseconds).</summary>
        public void SendU64(uint kind, ulong value)
        {
            if (!_sender.ConnectionIsConnected) return;
            byte[] body = SessionPropertyPushBuilder.BuildU64Body(kind, value);
            SendBody(body);
        }

        /// <summary>
        /// Send a pre-built FF property body on session 0x02. All host-side
        /// FF records (init kind=2/7 handshake, dashboard switches kind=4,
        /// brightness/standby kind=1/10) go on sess=0x02; sess=0x01 carries
        /// tier-def TLV traffic only.
        /// </summary>
        public void SendBody(byte[] body)
        {
            if (body == null) return;
            const byte session = 0x02;

            // Body layout from SessionPropertyPushBuilder.WrapFfRecord:
            //   [0]      0xFF
            //   [1..4]   size:u32 LE
            //   [5..8]   inner crc32
            //   [9..12]  kind:u32 LE
            //   [13...]  value bytes
            bool haveKind = body.Length >= 13 && body[0] == 0xFF;
            uint kind = haveKind
                ? (uint)(body[9] | (body[10] << 8) | (body[11] << 16) | (body[12] << 24))
                : 0u;

            lock (_sender.Session02SeqLock)
            {
                // Drop prior seqs for the same kind from the retransmitter
                // before queuing the new chunk. Prevents stale brightness=0
                // retransmits from leaving the display blanked after a quick
                // slider drag.
                if (haveKind && _lastSeqs.TryGetValue((session, kind), out var prevSeqs))
                {
                    foreach (int s in prevSeqs)
                        _sender.Retransmitter.Drop(session, s);
                    prevSeqs.Clear();
                }

                int seq = System.Math.Max(2, _sender.Session02OutboundSeq);
                var frames = TierDefinitionBuilder.ChunkMessage(body, session, ref seq, _sender.TargetDeviceId);
                var newSeqs = haveKind ? new List<int>(frames.Count) : null;
                foreach (var frame in frames)
                {
                    _sender.SendAndTrackChunkInternal(frame);
                    // Frame layout: 7E [N] 43 17 7C 00 [session] [type=01] [seq_lo] [seq_hi] ...
                    if (newSeqs != null && frame.Length >= 10)
                        newSeqs.Add(frame[8] | (frame[9] << 8));
                }
                _sender.Session02OutboundSeq = seq;

                if (haveKind)
                    _lastSeqs[(session, kind)] = newSeqs!;
            }
        }

        /// <summary>Clear the coalescing index. Called on session reset.</summary>
        public void Clear()
        {
            lock (_sender.Session02SeqLock) _lastSeqs.Clear();
        }
    }
}
