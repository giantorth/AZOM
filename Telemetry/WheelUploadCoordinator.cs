using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
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

        // Wait events for the upload state machine. ManualResetEventSlim so the
        // background upload thread can block briefly between phases.
        private readonly ManualResetEventSlim _sessionOpened = new(false);
        private readonly ManualResetEventSlim _subMsg1Response = new(false);
        private readonly ManualResetEventSlim _subMsg2Response = new(false);
        private readonly ManualResetEventSlim _endReceived = new(false);

        private int _inboundSeq;
        private int _outboundSeq;
        private int _inboundMsgCount;

        // Dir-listing reassembler — wheel pushes a zlib-compressed directory
        // listing on the upload session both before and after upload.
        private readonly SessionDataReassembler _inbox = new();
        private volatile bool _dirListingRefreshed;
        public bool DirListingRefreshed => _dirListingRefreshed;

        // Public surface (mirrors IMozaTelemetry's upload-related properties).
        // TelemetrySender's setters/getters delegate here.
        public byte[]? MzdashContent { get; set; }
        public string MzdashName { get; set; } = "";
        public bool UploadDashboard { get; set; } = true;
        public byte UploadSessionOverride { get; set; } = 0;

        private int _disposed;

        public WheelUploadCoordinator(
            MozaSerialConnection connection,
            Func<bool> shouldAbort,
            Func<EraPolicy> getPolicy,
            Func<WheelDashboardState?> getConfigJsonState,
            Action<byte, ushort> sendSessionAck,
            Action<byte, ushort> sendSessionEnd,
            Action<byte[]> sendAndTrackChunk)
        {
            _connection = connection;
            _shouldAbort = shouldAbort;
            _getPolicy = getPolicy;
            _getConfigJsonState = getConfigJsonState;
            _sendSessionAck = sendSessionAck;
            _sendSessionEnd = sendSessionEnd;
            _sendAndTrackChunk = sendAndTrackChunk;
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
        /// session. Returns true if the chunk was for our session (caller
        /// already sent the 7c:00 ack via the shared SendSessionAck path).</summary>
        public bool NoteInboundChunk(byte session, int seq, byte[] chunkPayload)
        {
            if (session != ActiveSession) return false;
            _inboundSeq = seq;
            _inboundMsgCount++;
            // After ~5 chunks on the upload session from the device, assume a
            // sub-msg reply has fully arrived (capture shows 6 chunks per
            // response). SendDashboardUpload resets the counter to 0 between
            // sub-msg 1 and sub-msg 2, so both thresholds are 5.
            if (_inboundMsgCount >= 5 && !_subMsg1Response.IsSet)
                _subMsg1Response.Set();
            else if (_inboundMsgCount >= 5 && !_subMsg2Response.IsSet)
                _subMsg2Response.Set();

            // 2025-11 firmware also pushes a zlib-compressed directory
            // listing on the upload session (initial + post-upload refresh).
            // Reassemble + decompress so the plugin can confirm the upload
            // landed in the wheel's FS. Same 9-byte envelope as session 0x09
            // configJson state. Seq-aware: detect missing chunks before they
            // corrupt the dir-listing zlib stream.
            bool addOk = _inbox.AddChunk(seq, chunkPayload, $"sess=0x{session:X2} upload");
            byte[]? dirBlob = addOk ? _inbox.TryDecompress() : null;
            if (dirBlob != null)
            {
                _inbox.Clear();
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
            try { _endReceived.Reset(); } catch (ObjectDisposedException) { }
            lock (_ftCandidateSessions) _ftCandidateSessions.Clear();
            ActiveSession = 0x04;
            _inboundSeq = 0;
            _outboundSeq = 0;
            _inboundMsgCount = 0;
            _dirListingRefreshed = false;
            try { _inbox.Clear(); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _sessionOpened.Dispose(); } catch { }
            try { _subMsg1Response.Dispose(); } catch { }
            try { _subMsg2Response.Dispose(); } catch { }
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
            try
            {
                if (_shouldAbort()) return;

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
                    return;
                }

                if (_shouldAbort()) return;
                SendDashboardUpload();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Background dashboard upload failed: {ex.Message}");
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

        private void SendDashboardUpload()
        {
            var content = MzdashContent;
            if (content == null || content.Length == 0) return;
            if (!_connection.IsConnected) return;

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
                return;
            }

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
                DashboardUploader.BuildUpload(content, dashboardName, token, tsMs, policy.UploadWireFormat);

            MozaLog.Debug(
                $"[Moza] Uploading dashboard \"{dashboardName}\" via session 0x{uploadSess:X2} " +
                $"(wire={policy.UploadWireFormat}): " +
                $"raw={upload.UncompressedSize}B md5={upload.Md5Hex} token=0x{token:X8}");

            _subMsg1Response.Reset();
            _subMsg2Response.Reset();
            _endReceived.Reset();
            _inboundMsgCount = 0;

            // Sub-msg 1: path registration. Route through SendAndTrackChunk
            // so each chunk is enqueued for retransmit — if the wheel drops
            // one, the host re-emits on the SessionRetransmitter backoff
            // rather than failing the whole upload after the 2 s wait.
            int seq1 = _outboundSeq + 1;
            var subMsg1Frames = TierDefinitionBuilder.ChunkMessage(
                upload.SubMsg1PathRegistration, uploadSess, ref seq1);
            foreach (var frame in subMsg1Frames)
            {
                if (_shouldAbort()) return;
                _sendAndTrackChunk(frame);
            }
            _outboundSeq = seq1;

            // Wait for device's path echo (capture shows ~6 chunks, arrives within ~200ms).
            if (!_subMsg1Response.Wait(2000))
            {
                if (!policy.AutoFallbackUploadWireFormat)
                {
                    MozaLog.Warn(
                        $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout with " +
                        $"wire={policy.UploadWireFormat} — fallback disabled (era pinned by user)");
                }
                else
                {
                    var fallback = policy.UploadWireFormat == FileTransferWireFormat.New2026_04
                        ? FileTransferWireFormat.Legacy2025_11
                        : FileTransferWireFormat.New2026_04;
                    MozaLog.Warn(
                        $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout with " +
                        $"wire={policy.UploadWireFormat} — retrying with wire={fallback}");

                    policy.UploadWireFormat = fallback;
                    fellBack = true;
                    upload = DashboardUploader.BuildUpload(content, dashboardName, token, tsMs, policy.UploadWireFormat);

                    _subMsg1Response.Reset();
                    _subMsg2Response.Reset();
                    _inboundMsgCount = 0;

                    seq1 = _outboundSeq + 1;
                    subMsg1Frames = TierDefinitionBuilder.ChunkMessage(
                        upload.SubMsg1PathRegistration, uploadSess, ref seq1);
                    foreach (var frame in subMsg1Frames)
                    {
                        if (_shouldAbort()) return;
                        _sendAndTrackChunk(frame);
                    }
                    _outboundSeq = seq1;

                    if (!_subMsg1Response.Wait(2000))
                        MozaLog.Warn(
                            $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout on fallback " +
                            $"wire={policy.UploadWireFormat} — wheel may not be in upload-ready state");
                    else
                        MozaLog.Debug(
                            $"[Moza] Wire format auto-detected: wheel accepts {policy.UploadWireFormat} " +
                            "(cached for this session)");
                }
            }
            // Suppress unused warning when fallback didn't fire.
            _ = fellBack;

            // Sub-msg 2: file content push. May be split across multiple sub-msgs
            // for new-firmware uploads when the body exceeds 0xFFFF bytes (TODO:
            // true multi-sub-msg chunking; today this is single-element for both
            // formats — see FileTransferBuilder.BuildFileContentChunked).
            // Each frame goes through SendAndTrackChunk so retransmits cover
            // wheel-side drops (was bare _connection.Send pre-fix, meaning a
            // single dropped chunk silently aborted the upload).
            _inboundMsgCount = 0;
            int seq2 = _outboundSeq + 1;
            for (int chunkIdx = 0; chunkIdx < upload.SubMsg2Chunks.Count; chunkIdx++)
            {
                var subMsg2 = upload.SubMsg2Chunks[chunkIdx];
                var subMsg2Frames = TierDefinitionBuilder.ChunkMessage(subMsg2, uploadSess, ref seq2);
                foreach (var frame in subMsg2Frames)
                {
                    if (_shouldAbort()) return;
                    _sendAndTrackChunk(frame);
                }
            }
            _outboundSeq = seq2;

            if (!_subMsg2Response.Wait(3000))
            {
                // Sub-msg-2 reply never arrived within the wait window.
                // Retransmits should have re-emitted the unacked chunks via
                // SendAndTrackChunk above; if the wheel still hasn't replied
                // after 3 s, the upload likely failed at the wheel-side
                // filesystem layer (out-of-space, transient FS error) and
                // the end-marker is the only thing left to do — emit it so
                // the wheel cleanly closes the upload session and is ready
                // for the next attempt on the next preamble cycle.
                MozaLog.Warn(
                    $"[Moza] Session 0x{uploadSess:X2} sub-msg 2 response timeout " +
                    "(retransmits did not recover); emitting end marker and aborting");
            }

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
