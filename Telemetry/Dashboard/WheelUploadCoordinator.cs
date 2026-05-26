using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Owns the wheel-side mzdash upload session lifecycle: detecting which FT
    /// session the wheel device-inits (0x04..0x0b), waiting for the device-init
    /// burst, sending sub-msg-1 (path registration) + sub-msg-2 (file content) +
    /// end-marker, and consuming the post-upload directory listing the wheel
    /// pushes back. Also handles MD5-based skip-if-already-loaded and the
    /// 2025-11 ↔ 2026-04 wire-format auto-fallback.
    ///
    /// The chunk-handler in TelemetrySender forwards every relevant device event
    /// here via <see cref="NoteDeviceInit"/>, <see cref="NoteInboundChunk"/>,
    /// and <see cref="NoteEndMarker"/>. The coordinator owns the upload-state
    /// fields (sessions set, wait events, seq counters, dir-listing reassembler)
    /// rather than scattering them across TelemetrySender.
    ///
    /// In-progress upload code (BuildStagingBlock / BuildTransferManifest in
    /// DashboardDownloader.cs and any helpers used here) is preserved verbatim
    /// — this class is a MOVE not a redesign.
    /// </summary>
    internal sealed class WheelUploadCoordinator : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly Func<bool> _shouldAbort;
        private readonly Func<EraPolicy> _getPolicy;
        private readonly Func<WheelDashboardState?> _getConfigJsonState;
        private readonly Action<byte, ushort> _sendSessionAck;
        private readonly Action<byte, ushort> _sendSessionEnd;
        /// <summary>
        /// Send a host-initiated session-open frame
        /// (<c>7c 00 &lt;sess&gt; 81 &lt;port:2 LE&gt; &lt;port:2 LE&gt; fd 02</c>). Without
        /// this, the wheel-side ack session (typically 0x04) is left half-open —
        /// the wheel device-inits its side but the host never opens its side,
        /// so the wheel can't emit ack sub-msgs back to the host. PitHouse
        /// always opens sess=0x04 from the host side before an upload; we
        /// must too. Verified 2026-05-16 against PitHouse bridge capture.
        /// </summary>
        private readonly Action<byte, byte> _sendSessionOpen;
        // Reliable-stream chunk emitter. Pushes the frame onto the wire AND
        // registers it with the host-side retransmit queue so unacked
        // session-data chunks get re-emitted by TelemetrySender's
        // TickEmitRetransmits. Used for sub-msg 1 and sub-msg 2 so that if
        // the wheel drops one mid-burst, the retransmit fires automatically
        // instead of leaving the upload silently incomplete.
        private readonly Action<byte[]> _sendAndTrackChunk;

        // FT-eligible sessions the wheel device-inited. ChooseUploadSession
        // prefers 0x04 (legacy), then walks up looking for the first match.
        private readonly HashSet<byte> _ftCandidateSessions = new();
        // Session number currently in use for upload. Updated by NoteDeviceInit
        // for legacy non-09/0a sessions (matches prior TelemetrySender behavior)
        // and by SendDashboardUpload at upload-start.
        public byte ActiveSession { get; private set; } = 0x04;

        /// <summary>
        /// Session the wheel uses for sub-msg acks (type=0x01 progress,
        /// type=0x11 complete) regardless of which session the host opened
        /// for the upload. Verified 2026-05-15 across two PitHouse uploads
        /// in <c>sim/logs/bridge-20260514-170002.jsonl</c> — see
        /// <c>docs/protocol/dashboard-upload/upload-handshake-2026-04.md</c>
        /// §"Wheel-side ack session ≠ host upload session".
        /// </summary>
        private const byte UploadAckSession = 0x04;

        // Wait events for the upload state machine. ManualResetEventSlim so the
        // background upload thread can block briefly between phases.
        private readonly ManualResetEventSlim _sessionOpened = new(false);
        private readonly ManualResetEventSlim _subMsg1Response = new(false);
        private readonly ManualResetEventSlim _subMsg2Response = new(false);
        /// <summary>
        /// Fired by the ack walker every time a fresh wheel-side ack sub-msg
        /// (type=0x01 progress OR type=0x11 complete) lands. Used to implement
        /// PitHouse's per-round flow-control: after each type=0x03 content
        /// sub-msg, reset this event and wait on it before sending the next.
        /// Blasting chunks without waiting saturates the wheel's serial
        /// input and the wheel never engages the file-transfer state machine.
        /// </summary>
        private readonly ManualResetEventSlim _ackProgress = new(false);
        private readonly ManualResetEventSlim _endReceived = new(false);

        private int _inboundSeq;
        private int _outboundSeq;
        private int _inboundMsgCount;

        // Dir-listing reassembler — wheel pushes a zlib-compressed directory
        // listing on the upload session both before and after upload.
        private readonly SessionDataReassembler _inbox = new();
        private volatile bool _dirListingRefreshed;
        public bool DirListingRefreshed => _dirListingRefreshed;

        // ── Cross-session ack stream (b2h sess=0x04 during in-flight upload) ──
        // The wheel acks the upload on sess=0x04 even when the host opened a
        // different session (0x05/0x07/...) for the upload itself. We reassemble
        // those chunks separately and walk them with the 6-byte sub-msg parser
        // so type=0x01 (progress / ready) and type=0x11 (complete) acks fire
        // _subMsg1Response / _subMsg2Response correctly instead of the prior
        // chunk-count heuristic. Walker offsets are tracked per-buffer so
        // legacy firmware (acks on ActiveSession, _inbox path) and new
        // firmware (acks on UploadAckSession, _ackInbox path) don't collide
        // when both reassemblers happen to see traffic for the same upload.
        private readonly SessionDataReassembler _ackInbox = new();
        private int _ackInboxWalkOffset;
        private int _inboxAckWalkOffset;
        private volatile bool _isUploadInFlight;
        public bool IsUploadInFlight => _isUploadInFlight;

        /// <summary>
        /// Latest <c>bytes_written:u32 BE</c> decoded from a wheel-side ack
        /// sub-msg (type=0x01 progress or type=0x11 complete). Zero before any
        /// ack arrives; equals <see cref="LastTotalSize"/> on a clean complete.
        /// </summary>
        public uint LastBytesWritten { get; private set; }
        /// <summary>
        /// Latest <c>total_size:u32 BE</c> decoded from a wheel-side ack sub-msg.
        /// Echoes the host's metadata total_size field (= compressed payload
        /// byte count).
        /// </summary>
        public uint LastTotalSize { get; private set; }
        /// <summary>
        /// Last trailing XOR status byte from an ack sub-msg. Stable per
        /// upload phase: known values include <c>0x6B</c> (in-progress) and
        /// <c>0x25</c> (complete) on legacy firmware; varies on Type02.
        /// </summary>
        public byte LastStatusByte { get; private set; }

        // Upload-related properties. TelemetrySender's setters/getters delegate here.
        public byte[]? MzdashContent { get; set; }
        public string MzdashName { get; set; } = "";
        /// <summary>
        /// Directory the active mzdash was loaded from, used to find sibling
        /// PNG assets at <c>&lt;dir&gt;/Resource/MD5/&lt;hex&gt;.png</c> when
        /// building the multi-file upload bundle. Empty when the mzdash came
        /// from an embedded resource (builtin) — those uploads ship as
        /// <c>file_count=1</c> (mzdash only) since no co-located PNG store
        /// exists.
        /// </summary>
        public string MzdashSourceDirectory { get; set; } = "";
        public bool UploadDashboard { get; set; } = true;
        public byte UploadSessionOverride { get; set; } = 0;

        /// <summary>
        /// Outcome of the most recent upload attempt — what actually
        /// happened from the wheel's perspective. Surfaced via
        /// <see cref="UploadCompleted"/> so callers (TelemetrySender,
        /// diagnostics) can see when an upload silently failed instead of
        /// having to scan log files for the right Warn line.
        /// </summary>
        public enum UploadOutcome
        {
            /// <summary>Wheel acked the final type=0x03 chunk.</summary>
            Succeeded,
            /// <summary>Wheel already has the same MD5 — no upload needed.</summary>
            SkippedHashMatch,
            /// <summary>Wheel never device-inited an FT session inside the 60 s window.</summary>
            NoFtSession,
            /// <summary>Wheel never acked the path-registration sub-msg (sub-msg 1).</summary>
            SubMsg1AckTimeout,
            /// <summary>Wheel acked sub-msg 1 but stopped acking content chunks.</summary>
            SubMsg2AckTimeout,
            /// <summary>An exception unwound the upload thread.</summary>
            ExceptionThrown,
            /// <summary>TelemetrySender flipped to Idle while the upload was in flight.</summary>
            Aborted,
        }

        /// <summary>
        /// Fires once per <see cref="RunBackgroundUpload"/> attempt with the
        /// terminal outcome. Subscribers should be fast and exception-safe;
        /// the event is invoked on the upload worker thread.
        /// </summary>
        public event Action<UploadOutcome>? UploadCompleted;

        private void FireUploadCompleted(UploadOutcome outcome)
        {
            try { UploadCompleted?.Invoke(outcome); }
            catch (Exception ex)
            {
                MozaLog.Warn(
                    $"[Moza] UploadCompleted subscriber threw: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private int _disposed;

        public WheelUploadCoordinator(
            MozaSerialConnection connection,
            Func<bool> shouldAbort,
            Func<EraPolicy> getPolicy,
            Func<WheelDashboardState?> getConfigJsonState,
            Action<byte, ushort> sendSessionAck,
            Action<byte, ushort> sendSessionEnd,
            Action<byte[]> sendAndTrackChunk,
            Action<byte, byte> sendSessionOpen)
        {
            _connection = connection;
            _shouldAbort = shouldAbort;
            _getPolicy = getPolicy;
            _getConfigJsonState = getConfigJsonState;
            _sendSessionAck = sendSessionAck;
            _sendSessionEnd = sendSessionEnd;
            _sendAndTrackChunk = sendAndTrackChunk;
            _sendSessionOpen = sendSessionOpen;
        }

        /// <summary>Notify the coordinator that the wheel device-inited a
        /// session in 0x04..0x0b. Tracks it as an FT candidate and wakes any
        /// thread waiting in <see cref="RunBackgroundUpload"/>.</summary>
        public void NoteDeviceInit(byte session)
        {
            if (session < 0x04 || session > 0x0b) return;
            lock (_ftCandidateSessions) _ftCandidateSessions.Add(session);
            try { _sessionOpened.Set(); } catch (ObjectDisposedException) { }
            // Legacy: also update ActiveSession for non-configJson candidates so
            // a wheel firmware that opens 0x05/0x07 (KS Pro on Universal Hub)
            // gets routed for inbound chunks even before SendDashboardUpload runs.
            if (session != 0x09 && session != 0x0a)
                ActiveSession = session;
        }

        /// <summary>Notify the coordinator of an inbound chunk on the upload
        /// session or the wheel's cross-session ack channel
        /// (<see cref="UploadAckSession"/>). Returns true if the chunk was
        /// consumed by the coordinator (caller already sent the 7c:00 ack via
        /// the shared SendSessionAck path).
        ///
        /// Three routing cases:
        ///   1. <c>session == UploadAckSession</c> AND upload in flight AND
        ///      that session is NOT the host's active upload session →
        ///      route to <see cref="_ackInbox"/>, walk for sub-msgs only.
        ///   2. <c>session == ActiveSession</c> → existing dir-listing path
        ///      via <see cref="_inbox"/>. If ActiveSession also happens to be
        ///      UploadAckSession (legacy firmware case), the same buffer is
        ///      walked for sub-msg acks alongside dir-listing decompression.
        ///   3. Otherwise → ignore (return false).
        /// </summary>
        public bool NoteInboundChunk(byte session, int seq, byte[] chunkPayload)
        {
            bool isAckSession = session == UploadAckSession && _isUploadInFlight;
            bool isActive = session == ActiveSession;
            if (!isAckSession && !isActive) return false;

            _inboundSeq = seq;
            _inboundMsgCount++;

            // Cross-session ack routing: sess=0x04 b2h during in-flight upload
            // when the host opened a different session (0x05/0x07/...) for the
            // upload. The wheel still acks on sess=0x04 — we accumulate the
            // chunks in a dedicated reassembler and walk them with the 6-byte
            // sub-msg parser. Dir-listing decompression is NOT run on this
            // buffer because the cross-session 0x04 stream during upload
            // carries only ack sub-msgs (verified in
            // sim/logs/bridge-20260514-170002.jsonl).
            if (isAckSession && !isActive)
            {
                int prevLen = _ackInbox.Length;
                _ackInbox.AddChunk(seq, chunkPayload, $"sess=0x{session:X2} ack");
                // Restart / BufferOverflow shrinks the buffer; reset the walker.
                if (_ackInbox.Length < prevLen) _ackInboxWalkOffset = 0;
                WalkAckSubMsgs(_ackInbox, ref _ackInboxWalkOffset);
                return true;
            }

            // ActiveSession path. Dir-listing reassembler — wheel pushes a
            // zlib-compressed directory listing on the upload session both
            // before and after upload. Seq-aware: detect missing chunks
            // before they corrupt the dir-listing zlib stream.
            int prevInboxLen = _inbox.Length;
            bool addOk = _inbox.AddChunk(seq, chunkPayload, $"sess=0x{session:X2} upload");

            // During an in-flight upload, walk the ActiveSession buffer for
            // ack sub-msgs regardless of which session it is. Two firmware
            // shapes need this:
            //   • Legacy 2025-11: wheel acks on the SAME session as upload
            //     (ActiveSession ∈ {0x05, 0x06, ...}, NOT 0x04). Without this
            //     branch the walker never runs for legacy firmware.
            //   • New 2026-04+ where ActiveSession happens to be 0x04: walker
            //     finds sub-msgs interleaved with dir-listing on the same
            //     buffer.
            // The walker only fires events on type=0x01/0x11, so running it
            // on dir-listing-only buffers is a cheap no-op (bad pad bytes
            // make it break out of the loop early).
            if (_isUploadInFlight)
            {
                if (_inbox.Length < prevInboxLen) _inboxAckWalkOffset = 0;
                WalkAckSubMsgs(_inbox, ref _inboxAckWalkOffset);
            }

            byte[]? dirBlob = addOk ? _inbox.TryDecompress() : null;
            if (dirBlob != null)
            {
                _inbox.Clear();
                // Dir-listing decompress clears _inbox; reset the ack-walker
                // offset for this buffer so subsequent sub-msgs after the
                // listing get parsed from the new buffer start.
                _inboxAckWalkOffset = 0;
                _dirListingRefreshed = true;
                try
                {
                    string json = System.Text.Encoding.UTF8.GetString(dirBlob);
                    MozaLog.Debug(
                        $"[Moza] Session 0x{session:X2} dir listing: {dirBlob.Length} bytes, " +
                        $"children≈{CountOccurrences(json, "\"name\"")}");
                }
                catch (Exception ex)
                {
                    MozaLog.Debug(
                        $"[Moza] Session 0x{session:X2} dir listing decode: {ex.Message}");
                }
            }
            return true;
        }

        /// <summary>
        /// Walk the buffered b2h ack-stream bytes with the 6-byte sub-msg
        /// parser, advancing <see cref="_ackWalkOffset"/> past consumed
        /// messages. Fires <see cref="_subMsg1Response"/> / <see cref="_subMsg2Response"/>
        /// on observed type=0x01 / type=0x11 boundaries (or type=0x01 with
        /// bytes_written == total_size, the complete-via-progress firmware
        /// variant). Decodes the body trailer to surface
        /// <see cref="LastBytesWritten"/>, <see cref="LastTotalSize"/>, and
        /// <see cref="LastStatusByte"/>.
        ///
        /// Header layout per
        /// <c>docs/protocol/dashboard-upload/6-byte-submsg-header.md</c>:
        /// <c>[type:1][size_LE:u16][pad:3=00 00 00]</c>. Next sub-msg starts
        /// at <c>offset + 6 + size</c>.
        /// </summary>
        private void WalkAckSubMsgs(SessionDataReassembler reassembler, ref int walkOffset)
        {
            byte[] buf = reassembler.Snapshot();
            while (walkOffset + 6 <= buf.Length)
            {
                // Validate the 3 pad bytes. Bad alignment usually means the
                // reassembled buffer is dir-listing zlib data (no sub-msg
                // header at this offset) — stop walking; the dir-listing
                // decompressor handles that case separately.
                if (buf[walkOffset + 3] != 0
                    || buf[walkOffset + 4] != 0
                    || buf[walkOffset + 5] != 0)
                    break;

                byte type = buf[walkOffset];
                int size = buf[walkOffset + 1] | (buf[walkOffset + 2] << 8);
                int total = 6 + size;
                if (walkOffset + total > buf.Length) break; // partial; wait for more chunks

                int bodyStart = walkOffset + 6;
                OnAckSubMsg(type, buf, bodyStart, size);
                walkOffset += total;
            }
        }

        /// <summary>
        /// Decode one ack sub-msg body and fire the appropriate wait events.
        /// Body trailer layout (Type02 firmware, verified byte-exact 2026-05-15):
        /// <c>[bytes_written:u32 BE][total_size:u32 BE][ff ff ff ff sentinel][status:u8]</c>
        /// — i.e. the last 13 bytes of the body. fc:00 chunk-level acks do
        /// NOT reach this path (they're a different wire-level cmd, never
        /// routed through NoteInboundChunk).
        /// </summary>
        private void OnAckSubMsg(byte type, byte[] buf, int bodyStart, int size)
        {
            if (size >= 13 && (type == 0x01 || type == 0x11))
            {
                int trailerOff = bodyStart + size - 13;
                uint bw = ReadUInt32BE(buf, trailerOff);
                uint ts = ReadUInt32BE(buf, trailerOff + 4);
                byte status = buf[bodyStart + size - 1];
                LastBytesWritten = bw;
                LastTotalSize = ts;
                LastStatusByte = status;
                MozaLog.Debug(
                    $"[Moza] Upload ack type=0x{type:X2} bw={bw} total={ts} status=0x{status:X2}");
            }

            if (type == 0x01)
            {
                if (!_subMsg1Response.IsSet)
                {
                    try { _subMsg1Response.Set(); } catch (ObjectDisposedException) { }
                }
                // Every type=0x01 (progress or ready) advances PitHouse's
                // per-round flow control — the upload thread waits on this
                // between content chunks.
                try { _ackProgress.Set(); } catch (ObjectDisposedException) { }
                // Firmware variant: type=0x01 with bytes_written == total_size
                // is the "complete via progress" signal — wheel never emits a
                // separate type=0x11. Treat as sub-msg 2 complete.
                if (LastTotalSize != 0
                    && LastBytesWritten == LastTotalSize
                    && !_subMsg2Response.IsSet)
                {
                    try { _subMsg2Response.Set(); } catch (ObjectDisposedException) { }
                }
            }
            else if (type == 0x11)
            {
                if (!_subMsg1Response.IsSet)
                {
                    // Some firmwares skip type=0x01 and go straight to 0x11;
                    // unblock sub-msg 1's waiter too so the upload thread can
                    // proceed past the metadata-ack stage.
                    try { _subMsg1Response.Set(); } catch (ObjectDisposedException) { }
                }
                try { _ackProgress.Set(); } catch (ObjectDisposedException) { }
                if (!_subMsg2Response.IsSet)
                {
                    try { _subMsg2Response.Set(); } catch (ObjectDisposedException) { }
                }
            }
            // type=0x02 / 0x03 are host-emitted (we should never see them on b2h)
            // and type=0x08 / 0x0a are dir-listing probes/replies — ignore.
        }

        private static uint ReadUInt32BE(byte[] buf, int off)
        {
            return ((uint)buf[off] << 24)
                 | ((uint)buf[off + 1] << 16)
                 | ((uint)buf[off + 2] << 8)
                 | buf[off + 3];
        }

        /// <summary>Notify the coordinator of a session end-marker (type=0x00).
        /// Wakes the upload thread so it can complete the
        /// <see cref="RunBackgroundUpload"/> call.</summary>
        public void NoteEndMarker(byte session)
        {
            if (session == ActiveSession)
            {
                try { _endReceived.Set(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Reset all upload state. Called by TelemetrySender.Stop()
        /// alongside the rest of its session-state reset.</summary>
        public void Reset()
        {
            try { _sessionOpened.Reset(); } catch (ObjectDisposedException) { }
            try { _subMsg1Response.Reset(); } catch (ObjectDisposedException) { }
            try { _subMsg2Response.Reset(); } catch (ObjectDisposedException) { }
            try { _ackProgress.Reset(); } catch (ObjectDisposedException) { }
            try { _endReceived.Reset(); } catch (ObjectDisposedException) { }
            lock (_ftCandidateSessions) _ftCandidateSessions.Clear();
            ActiveSession = 0x04;
            _inboundSeq = 0;
            _outboundSeq = 0;
            _inboundMsgCount = 0;
            _dirListingRefreshed = false;
            try { _inbox.Clear(); } catch { }
            try { _ackInbox.Clear(); } catch { }
            _ackInboxWalkOffset = 0;
            _inboxAckWalkOffset = 0;
            _isUploadInFlight = false;
            LastBytesWritten = 0;
            LastTotalSize = 0;
            LastStatusByte = 0;
            // Note: MzdashSourceDirectory NOT cleared — it's a config-like
            // property set by ApplyTelemetrySettings, not per-attempt state.
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _sessionOpened.Dispose(); } catch { }
            try { _subMsg1Response.Dispose(); } catch { }
            try { _subMsg2Response.Dispose(); } catch { }
            try { _ackProgress.Dispose(); } catch { }
            try { _endReceived.Dispose(); } catch { }
        }

        /// <summary>
        /// Background upload entry point. Runs on a worker thread so a slow-to-
        /// open file-transfer session (KS Pro on RS21-W18-MC SW: ~11 s) doesn't
        /// stall tier def + telemetry start. Waits up to 60 s for the wheel to
        /// device-init any session in 0x04..0x0a, then runs the legacy upload
        /// path. If the wait expires, logs and bails — the wheel will render a
        /// previously-cached dashboard, or nothing if it has none.
        ///
        /// Aborts cleanly if the caller's <see cref="_shouldAbort"/> turns true
        /// (TelemetrySender.Stop flips _state to Idle and the next checkpoint
        /// here exits).
        /// </summary>
        public void RunBackgroundUpload()
        {
            UploadOutcome outcome = UploadOutcome.Aborted;
            try
            {
                if (_shouldAbort()) { outcome = UploadOutcome.Aborted; return; }

                // 60 s ceiling: covers the slowest firmware observed (~11 s) with
                // headroom. If the wheel hasn't opened an FT session by then it
                // either doesn't support uploads on this firmware or is wedged —
                // either way, retrying won't help and host-opening 0x04 races the
                // wheel's eventual late burst (closes session 0x02, kills telemetry).
                const int FtBurstWaitMs = 60000;
                if (!_sessionOpened.Wait(FtBurstWaitMs))
                {
                    MozaLog.Warn(
                        $"[Moza] No file-transfer session device-opened within " +
                        $"{FtBurstWaitMs}ms — skipping dashboard upload. " +
                        "Wheel may render previously-cached dashboard.");
                    outcome = UploadOutcome.NoFtSession;
                    return;
                }

                if (_shouldAbort()) { outcome = UploadOutcome.Aborted; return; }
                outcome = SendDashboardUpload();
            }
            catch (Exception ex)
            {
                outcome = UploadOutcome.ExceptionThrown;
                MozaLog.Warn($"[Moza] Background dashboard upload failed: {ex.Message}");
            }
            finally
            {
                FireUploadCompleted(outcome);
            }
        }

        /// <summary>
        /// Pick the file-transfer session number to upload on. Priority:
        /// (1) <see cref="UploadSessionOverride"/> if non-zero;
        /// (2) 0x04 if the wheel device-initiated it (legacy);
        /// (3) The first session in 0x04..0x0a the wheel device-initiated;
        /// (4) 0x04 fallback if no candidate seen yet.
        /// </summary>
        public byte ChooseUploadSession()
        {
            if (UploadSessionOverride != 0) return UploadSessionOverride;
            lock (_ftCandidateSessions)
            {
                if (_ftCandidateSessions.Contains((byte)0x04)) return 0x04;
                foreach (byte b in new byte[] { 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a })
                    if (_ftCandidateSessions.Contains(b)) return b;
            }
            return 0x04;
        }

        private UploadOutcome SendDashboardUpload()
        {
            var content = MzdashContent;
            // No content / no link is "nothing to do", not a failure. Treat
            // as Aborted so the caller can distinguish from a real attempt.
            if (content == null || content.Length == 0) return UploadOutcome.Aborted;
            if (!_connection.IsConnected) return UploadOutcome.Aborted;

            // Pick the upload session from the wheel's device-init burst.
            byte uploadSess = ChooseUploadSession();
            ActiveSession = uploadSess;

            // Skip-if-unchanged: if the wheel already reported this dashboard
            // as loaded (via session 0x09 state) and the MD5 matches, don't
            // re-upload. Saves ~1 s of handshake per reconnect.
            if (CanSkipUpload(content))
            {
                MozaLog.Debug(
                    $"[Moza] Dashboard \"{MzdashName}\" already loaded on wheel (hash match) — skipping upload");
                return UploadOutcome.SkippedHashMatch;
            }

            // Arm the cross-session ack stream. _isUploadInFlight gates b2h
            // sess=0x04 routing in NoteInboundChunk; clear the dedicated ack
            // reassembler + walker so stale state from a prior upload doesn't
            // confuse this attempt. Cleared in the finally below.
            _ackInbox.Clear();
            _ackInboxWalkOffset = 0;
            _inboxAckWalkOffset = 0;
            LastBytesWritten = 0;
            LastTotalSize = 0;
            LastStatusByte = 0;
            _isUploadInFlight = true;
            try
            {
                return SendDashboardUploadInner(content, uploadSess);
            }
            finally
            {
                _isUploadInFlight = false;
            }
        }

        /// <summary>
        /// Chunk a single sub-msg (metadata or one type=0x03 content slice)
        /// through the session-data framer and emit each wire frame via the
        /// retransmit-tracked send path, with a per-frame delay to keep the
        /// host's outbound rate below the wheel's serial budget
        /// (~12 kB/s observed peak). Updates <paramref name="seq"/> in place
        /// so the caller can advance <see cref="_outboundSeq"/>.
        /// </summary>
        private void EmitSubMsg(byte[] subMsg, byte sess, ref int seq, int interFrameDelayMs)
        {
            var frames = TierDefinitionBuilder.ChunkMessage(subMsg, sess, ref seq);
            foreach (var frame in frames)
            {
                if (_shouldAbort()) return;
                _sendAndTrackChunk(frame);
                if (interFrameDelayMs > 0) Thread.Sleep(interFrameDelayMs);
            }
        }

        /// <summary>
        /// Variant of <see cref="EmitSubMsg"/> that returns the pre-built wire
        /// frames so the caller can retransmit them with identical seq numbers
        /// while waiting for the wheel's type=0x01 ack. PitHouse pattern: emit
        /// the burst, then re-emit the SAME frames (same seq numbers) every
        /// ~1.9 s until the wheel acks. The wheel treats duplicate-seq chunks
        /// as no-ops, but the retransmissions keep its file-transfer state
        /// machine engaged. Verified against
        /// sim/logs/bridge-20260514-170002.jsonl upload #1: PitHouse emits
        /// seq=7-13 at +0 ms, re-emits seq=7-13 at +102 ms, re-emits just
        /// seq=13 at +1965 ms, wheel acks at +2018 ms (b2h sess=04 seq=06).
        /// </summary>
        private List<byte[]> EmitSubMsgCapturing(byte[] subMsg, byte sess, ref int seq, int interFrameDelayMs)
        {
            var frames = TierDefinitionBuilder.ChunkMessage(subMsg, sess, ref seq);
            foreach (var frame in frames)
            {
                if (_shouldAbort()) return frames;
                _sendAndTrackChunk(frame);
                if (interFrameDelayMs > 0) Thread.Sleep(interFrameDelayMs);
            }
            return frames;
        }

        /// <summary>
        /// Re-emit pre-built wire frames at the same per-frame cadence as
        /// the initial emission. Used between <see cref="_subMsg1Response"/>
        /// waits to nudge the wheel into emitting its type=0x01 ready ack.
        /// Bypasses retransmit tracking — the frames are byte-identical
        /// (same seq) so the retransmitter shouldn't re-register them.
        /// </summary>
        private void ReemitFrames(IReadOnlyList<byte[]> frames, int interFrameDelayMs)
        {
            foreach (var frame in frames)
            {
                if (_shouldAbort()) return;
                if (_subMsg1Response.IsSet) return; // ack arrived mid-retransmit; stop early
                _connection.Send(frame);
                if (interFrameDelayMs > 0) Thread.Sleep(interFrameDelayMs);
            }
        }

        private UploadOutcome SendDashboardUploadInner(byte[] content, byte uploadSess)
        {

            string dashboardName = !string.IsNullOrEmpty(MzdashName) ? MzdashName : "dashboard";
            uint token = DashboardUploader.PickToken();
            long tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Probe-based wire-format selection. Older wheels (VGS, GS V2P,
            // CS V2.1, etc.) accept Legacy2025_11; newer wheels (W17 CS Pro /
            // W18 KS Pro / W13 FSR V2 on 2026-04+ firmware) silently drop
            // Legacy and only ack New2026_04. Identity probes carry no
            // build/version field, so we can't pick from a string match. Try
            // the user-configured format first (default New2026_04), and on
            // sub-msg 1 ack timeout, fall back to the other format.
            var policy = _getPolicy();
            bool fellBack = false;
            DashboardUploader.UploadPayload upload =
                DashboardUploader.BuildUpload(content, dashboardName, token, tsMs,
                    policy.UploadWireFormat, MzdashSourceDirectory);

            MozaLog.Debug(
                $"[Moza] Uploading dashboard \"{dashboardName}\" via session 0x{uploadSess:X2} " +
                $"(wire={policy.UploadWireFormat}): " +
                $"raw={upload.UncompressedSize}B md5={upload.Md5Hex} " +
                $"compressed={upload.TotalCompressedSize}B chunks={upload.SubMsg2Chunks.Count} " +
                $"pngs={upload.BundledPngCount} token=0x{token:X8}");

            _subMsg1Response.Reset();
            _subMsg2Response.Reset();
            _ackProgress.Reset();
            _endReceived.Reset();
            _inboundMsgCount = 0;

            // Open sess=0x04 from the host side. The wheel uses sess=0x04 as
            // its ack channel (`type=0x01` ready + progress acks, `type=0x11`
            // complete), but it can only emit on that session if BOTH sides
            // have opened it. The wheel device-inits its end early in the
            // connect handshake; the host must send its own session-open
            // (`7c 00 04 81 <port:2> <port:2> fd 02`) to complete the
            // bidirectional handshake. PitHouse always does this before any
            // upload — verified 2026-05-16 against bridge capture
            // sim/logs/bridge-20260514-170002.jsonl (host emits session-open
            // for sess=0x04 with port=0x0e, then dir-listing probe, then
            // type=0x02 metadata, all on sess=0x04/0x05). Without it, the
            // wheel silently drops the upload because there's no return path
            // for the ack.
            const byte AckSessionPort = 0x0E;
            _sendSessionOpen(UploadAckSession, AckSessionPort);
            // Small settle delay so the wheel processes the open before we
            // start blasting metadata. Same scale as the inter-sub-msg pause.
            Thread.Sleep(50);

            // Per-frame throttle keeps host serial output under the wheel's
            // ~12 kB/s budget. At 64 wire bytes per chunk → 6 ms/frame ≈
            // 10.7 kB/s, ~85 % of budget while leaving headroom for the
            // telemetry sender's parallel traffic. Verified necessary against
            // CS Pro: prior unthrottled blast hit 110 % budget consistently
            // and the wheel never emitted a sub-msg ack.
            const int InterFrameDelayMs = 6;
            // PitHouse's per-round flow control: after each type=0x03 sub-msg
            // the wheel emits a type=0x01 progress ack (with bytes_written
            // advancing). The host MUST wait for it before sending the next
            // sub-msg — without that wait the wheel's file-transfer state
            // machine never engages. Timeout is generous because per-round
            // processing can take 25-28 s on large uploads (decompression
            // + filesystem write).
            //
            // Sm1AckTimeoutMs is the TOTAL wait budget for the ready-ack;
            // matched to the progress-ack timeout because the wheel applies
            // the same processing latency to sub-msg 1 as to subsequent
            // sub-msgs (verified against PitHouse upload #1 which receives
            // its sub-msg 1 ack at +2018 ms, but slower wheels — same
            // firmware — can defer up to ~28 s).
            const int InitialBurstRetransmitDelayMs = 100;
            const int Sm1RetransmitIntervalMs = 1800;
            const int Sm1AckTimeoutMs = 30000;
            const int ProgressAckTimeoutMs = 30000;
            const int CompleteAckTimeoutMs = 30000;

            int seq1 = _outboundSeq + 1;
            var sm1Frames = EmitSubMsgCapturing(upload.SubMsg1PathRegistration, uploadSess, ref seq1, InterFrameDelayMs);
            _outboundSeq = seq1;

            // Sub-msg 1 wait: type=0x01 ready-ack from wheel.
            // PitHouse pattern: emit-then-immediately-re-emit-the-whole-burst
            // at ~100 ms (defensive against drops), then re-emit every
            // ~1.9 s until the wheel acks. Without these retransmits the
            // CS Pro wheel sat for 5 s with no sub-msg ack despite
            // chunk-acking the bytes — wheel's state machine needs the
            // periodic nudge to flush its ready-ack.
            if (!_subMsg1Response.IsSet && InitialBurstRetransmitDelayMs > 0)
            {
                Thread.Sleep(InitialBurstRetransmitDelayMs);
                if (!_subMsg1Response.IsSet)
                {
                    ReemitFrames(sm1Frames, InterFrameDelayMs);
                }
            }

            DateTime sm1Deadline = DateTime.UtcNow.AddMilliseconds(Sm1AckTimeoutMs);
            while (!_subMsg1Response.IsSet && DateTime.UtcNow < sm1Deadline && !_shouldAbort())
            {
                int waitMs = Math.Min(Sm1RetransmitIntervalMs,
                    (int)Math.Max(50, (sm1Deadline - DateTime.UtcNow).TotalMilliseconds));
                if (_subMsg1Response.Wait(waitMs)) break;
                if (_shouldAbort()) break;
                // No ack yet — retransmit the metadata burst with same seq numbers
                ReemitFrames(sm1Frames, InterFrameDelayMs);
            }

            // No ack → ABORT.
            // Optionally retry once with the fallback wire format (era policy).
            if (!_subMsg1Response.IsSet)
            {
                bool retried = false;
                if (policy.AutoFallbackUploadWireFormat)
                {
                    var fallback = policy.UploadWireFormat == FileTransferWireFormat.New2026_04
                        ? FileTransferWireFormat.Legacy2025_11
                        : FileTransferWireFormat.New2026_04;
                    MozaLog.Warn(
                        $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout with " +
                        $"wire={policy.UploadWireFormat} — retrying with wire={fallback}");

                    policy.UploadWireFormat = fallback;
                    fellBack = true;
                    upload = DashboardUploader.BuildUpload(content, dashboardName, token, tsMs,
                        policy.UploadWireFormat, MzdashSourceDirectory);

                    _subMsg1Response.Reset();
                    _subMsg2Response.Reset();
                    _ackProgress.Reset();
                    _inboundMsgCount = 0;

                    seq1 = _outboundSeq + 1;
                    var sm1FallbackFrames = EmitSubMsgCapturing(upload.SubMsg1PathRegistration, uploadSess, ref seq1, InterFrameDelayMs);
                    _outboundSeq = seq1;

                    if (!_subMsg1Response.IsSet && InitialBurstRetransmitDelayMs > 0)
                    {
                        Thread.Sleep(InitialBurstRetransmitDelayMs);
                        if (!_subMsg1Response.IsSet)
                            ReemitFrames(sm1FallbackFrames, InterFrameDelayMs);
                    }
                    DateTime fbDeadline = DateTime.UtcNow.AddMilliseconds(Sm1AckTimeoutMs);
                    while (!_subMsg1Response.IsSet && DateTime.UtcNow < fbDeadline && !_shouldAbort())
                    {
                        int waitMs = Math.Min(Sm1RetransmitIntervalMs,
                            (int)Math.Max(50, (fbDeadline - DateTime.UtcNow).TotalMilliseconds));
                        if (_subMsg1Response.Wait(waitMs)) break;
                        if (_shouldAbort()) break;
                        ReemitFrames(sm1FallbackFrames, InterFrameDelayMs);
                    }

                    if (_subMsg1Response.IsSet)
                    {
                        MozaLog.Debug(
                            $"[Moza] Wire format auto-detected: wheel accepts {policy.UploadWireFormat} " +
                            "(cached for this session)");
                        retried = true;
                    }
                    else
                    {
                        MozaLog.Warn(
                            $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout on fallback " +
                            $"wire={policy.UploadWireFormat} — aborting upload, no content sent");
                    }
                }
                else
                {
                    MozaLog.Warn(
                        $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout with " +
                        $"wire={policy.UploadWireFormat} — fallback disabled, aborting upload");
                }

                if (!retried)
                {
                    // No ready-ack → wheel hasn't created the staging file.
                    // Sending content is guaranteed to fail; just close the
                    // session cleanly so the wheel doesn't sit in a half-open
                    // upload state.
                    _sendSessionEnd(uploadSess, (ushort)_outboundSeq);
                    return UploadOutcome.SubMsg1AckTimeout;
                }
            }
            _ = fellBack;

            MozaLog.Debug(
                $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack received " +
                $"(bytes_written={LastBytesWritten} total={LastTotalSize} status=0x{LastStatusByte:X2}) — " +
                $"sending {upload.SubMsg2Chunks.Count} type=0x03 sub-msg(s)");

            // Sub-msg 2: file content. PitHouse pacing — emit one type=0x03,
            // wait for the wheel's type=0x01 progress-ack (or type=0x11
            // complete on the last chunk), then emit the next. Reset
            // _ackProgress before each emit so we wait for a FRESH ack, not
            // a stale set from the sub-msg-1 ready-ack.
            int seq2 = _outboundSeq + 1;
            uint lastBwSeen = LastBytesWritten;
            for (int chunkIdx = 0; chunkIdx < upload.SubMsg2Chunks.Count; chunkIdx++)
            {
                bool isLast = chunkIdx == upload.SubMsg2Chunks.Count - 1;
                _ackProgress.Reset();
                _subMsg2Response.Reset();

                EmitSubMsg(upload.SubMsg2Chunks[chunkIdx], uploadSess, ref seq2, InterFrameDelayMs);

                // Wait for the wheel's response: progress ack on intermediate
                // chunks (bytes_written must advance), complete ack on last.
                int waitMs = isLast ? CompleteAckTimeoutMs : ProgressAckTimeoutMs;
                bool gotAck;
                if (isLast)
                {
                    // Complete: type=0x11 OR type=0x01 with bw==total (the
                    // walker fires _subMsg2Response in both cases).
                    gotAck = _subMsg2Response.Wait(waitMs);
                }
                else
                {
                    // Progress: ANY new type=0x01 (advancing bytes_written).
                    gotAck = _ackProgress.Wait(waitMs);
                }

                if (!gotAck)
                {
                    MozaLog.Warn(
                        $"[Moza] Session 0x{uploadSess:X2} sub-msg 2 chunk {chunkIdx + 1}/{upload.SubMsg2Chunks.Count} " +
                        $"{(isLast ? "complete" : "progress")} ack timeout " +
                        $"(last bw={LastBytesWritten} total={LastTotalSize}) — aborting upload");
                    _outboundSeq = seq2;
                    _sendSessionEnd(uploadSess, (ushort)_outboundSeq);
                    return UploadOutcome.SubMsg2AckTimeout;
                }

                // Sanity check on intermediate chunks: bytes_written should
                // have advanced. If it didn't, the wheel is acking but not
                // making progress — log but keep going (the wheel could be
                // late with the actual progress count).
                if (!isLast && LastBytesWritten <= lastBwSeen)
                {
                    MozaLog.Debug(
                        $"[Moza] Session 0x{uploadSess:X2} progress ack arrived but " +
                        $"bytes_written did not advance (was {lastBwSeen}, is {LastBytesWritten})");
                }
                lastBwSeen = LastBytesWritten;
            }
            _outboundSeq = seq2;

            MozaLog.Debug(
                $"[Moza] Session 0x{uploadSess:X2} sub-msg 2 complete-ack received " +
                $"(bytes_written={LastBytesWritten} total={LastTotalSize} status=0x{LastStatusByte:X2})");

            // End marker on the upload session.
            _sendSessionEnd(uploadSess, (ushort)_outboundSeq);

            if (_endReceived.Wait(1000))
                MozaLog.Debug($"[Moza] Dashboard upload complete (session 0x{uploadSess:X2} closed by device)");
            else
                MozaLog.Debug("[Moza] Dashboard upload finished; device did not echo end marker within 1s");

            // Wheel's 2025-11 firmware fires a post-upload state refresh on
            // the upload session (updated directory listing) and session 0x09
            // (updated configJson state blob including the newly-uploaded
            // dashboard). Continue pumping so OnMessageDuringPreamble can ack
            // + consume those chunks before the preamble phase ends.
            int preRefreshCount = _inboundMsgCount;
            Thread.Sleep(500);
            int refreshChunks = _inboundMsgCount - preRefreshCount;
            if (refreshChunks > 0)
                MozaLog.Debug(
                    $"[Moza] Session 0x{uploadSess:X2} post-upload state refresh: {refreshChunks} chunks");

            return UploadOutcome.Succeeded;
        }

        /// <summary>
        /// Compare the active mzdash MD5 against the wheel's reported hash from
        /// its last session 0x09 state blob. Wheel stores hash as ASCII-hex of
        /// ASCII-hex of MD5. Returns true when the wheel already has this exact
        /// dashboard loaded in enableManager.
        /// </summary>
        private bool CanSkipUpload(byte[] content)
        {
            var state = _getConfigJsonState();
            if (state == null || state.EnabledDashboards.Count == 0) return false;
            byte[] md5 = FileTransferBuilder.ComputeMd5(content);
            string md5Hex = FileTransferBuilder.Md5Hex(md5);
            string wireHash = AsciiHexOfAsciiHex(md5Hex);
            foreach (var entry in state.EnabledDashboards)
            {
                if (string.Equals(entry.Hash, wireHash, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string AsciiHexOfAsciiHex(string ascii)
        {
            var sb = new System.Text.StringBuilder(ascii.Length * 2);
            foreach (var c in ascii) sb.Append(((byte)c).ToString("x2"));
            return sb.ToString();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
