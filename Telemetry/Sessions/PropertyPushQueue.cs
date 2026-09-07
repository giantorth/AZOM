using System.Collections.Concurrent;
using System.Collections.Generic;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Frames;

namespace MozaPlugin.Telemetry.Sessions
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
        // (session, kind). Concurrent because SendBody runs under whichever seq
        // lock the resolved session selects (Form-B wheels resolve FF to 0x01)
        // while Clear() runs from Stop() — one Dictionary under two different
        // locks is no lock at all. Each List<int> is only touched by the caller
        // that removed or stored it.
        private readonly ConcurrentDictionary<(byte session, uint kind), List<int>> _lastSeqs
            = new ConcurrentDictionary<(byte, uint), List<int>>();

        public PropertyPushQueue(TelemetrySender sender)
        {
            _sender = sender;
        }

        /// <summary>Push a u32-valued property (e.g. brightness 0–100).
        /// <paramref name="sessionOverride"/> pins the record to a specific
        /// session instead of letting this method resolve one.</summary>
        public void SendU32(uint kind, uint value, byte? sessionOverride = null,
                            bool coalesce = true)
        {
            if (!_sender.ConnectionIsConnected) return;
            byte[] body = SessionPropertyPushBuilder.BuildU32Body(kind, value);
            SendBody(body, sessionOverride, coalesce);
        }

        /// <summary>Push a u64-valued property (e.g. standby in milliseconds).</summary>
        public void SendU64(uint kind, ulong value)
        {
            if (!_sender.ConnectionIsConnected) return;
            byte[] body = SessionPropertyPushBuilder.BuildU64Body(kind, value);
            SendBody(body);
        }

        /// <summary>Push a single-byte-valued property (e.g. VGS display-rotation mode 0/1/2).</summary>
        public void SendU8(uint kind, byte value)
        {
            if (!_sender.ConnectionIsConnected) return;
            byte[] body = SessionPropertyPushBuilder.BuildU8Body(kind, value);
            SendBody(body);
        }

        /// <summary>
        /// Send a pre-built FF property body (init kind=2/7 handshake, dashboard
        /// switches kind=4, brightness/standby kind=1/10) on the FF session —
        /// the MIRROR of the tier-def session (<see cref="TelemetrySender.ResolveFfSession"/>).
        /// PitHouse Form A: tier-def on 0x01, FF on 0x02; Form B: tier-def on
        /// 0x02, FF on 0x01. The wheel only acks the FF-init and commits the
        /// tier-def to the display when these land on the expected session.
        /// <paramref name="sessionOverride"/> lets a caller that has already
        /// resolved the session pass it in (the device-log pull does, so its
        /// request and receipt provably ride the same one).
        /// <paramref name="coalesce"/> is the supersede-a-pending-push-of-the-
        /// same-kind behaviour described above. Correct for idempotent values
        /// (brightness, standby) where only the latest matters; WRONG for a
        /// record that advances a device-side cursor by its value — see the
        /// device-log receipt (kind=15).
        /// </summary>
        public void SendBody(byte[] body, byte? sessionOverride = null, bool coalesce = true)
        {
            if (body == null) return;
            byte session = sessionOverride ?? _sender.ResolveFfSession();
            bool onFlagByte = session == _sender.FlagByte && _sender.FlagByte != 0;
            object seqLock = onFlagByte ? _sender.Session02SeqLock : _sender.Session01SeqLock;

            // Body layout from SessionPropertyPushBuilder.WrapFfRecord:
            //   [0]      0xFF
            //   [1..4]   size:u32 LE
            //   [5..8]   inner crc32
            //   [9..12]  kind:u32 LE
            //   [13...]  value bytes
            bool haveKind = coalesce && body.Length >= 13 && body[0] == 0xFF;
            uint kind = haveKind
                ? (uint)(body[9] | (body[10] << 8) | (body[11] << 16) | (body[12] << 24))
                : 0u;

            lock (seqLock)
            {
                // Drop prior seqs for the same kind from the retransmitter
                // before queuing the new chunk. Prevents stale brightness=0
                // retransmits from leaving the display blanked after a quick
                // slider drag.
                if (haveKind && _lastSeqs.TryRemove((session, kind), out var prevSeqs))
                {
                    foreach (int s in prevSeqs)
                        _sender.Retransmitter.Drop(session, s);
                }

                int seq = System.Math.Max(2,
                    onFlagByte ? _sender.Session02OutboundSeq : _sender.Session01OutboundSeq);
                var frames = TierDefinitionBuilder.ChunkMessage(body, session, ref seq, _sender.TargetDeviceId);
                var newSeqs = haveKind ? new List<int>(frames.Count) : null;
                foreach (var frame in frames)
                {
                    _sender.SendAndTrackChunkInternal(frame);
                    // Frame layout: 7E [N] 43 17 7C 00 [session] [type=01] [seq_lo] [seq_hi] ...
                    if (newSeqs != null && frame.Length >= 10)
                        newSeqs.Add(frame[8] | (frame[9] << 8));
                }
                if (onFlagByte) _sender.Session02OutboundSeq = seq;
                else            _sender.Session01OutboundSeq = seq;

                if (haveKind)
                    _lastSeqs[(session, kind)] = newSeqs!;
            }
        }

        /// <summary>Clear the coalescing index. Called on session reset.</summary>
        public void Clear() => _lastSeqs.Clear();
    }
}
