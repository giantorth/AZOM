using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Downloads dashboard mzdash files from the wheel via session 0x0B.
    /// Replicates PitHouse's full download protocol:
    ///   1. FT-activate command to open session 0x0B for file transfer
    ///   2. 5-section request body (header + remote paths + separator + local paths + sub-msg-1 blocks)
    ///   3. Windowed ack-paced chunk delivery
    ///   4. Response: zlib-compressed mzdash JSON in 4360-byte blocks
    /// See docs/protocol/dashboard-upload/download-session-0x0b.md.
    /// </summary>
    public sealed class DashboardDownloader : ISessionConsumer, IDisposable
    {
        // Idempotent dispose guard. ManualResetEventSlim throws on
        // double-dispose; SimHub plugin reload can drop a downloader and
        // construct a new one, racing the cleanup.
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _downloadComplete.Dispose(); } catch { }
            try { _ackReceived.Dispose(); } catch { }
            try { _sessionOpened.Dispose(); } catch { }
        }

        // Event ops tolerate disposal: a plugin reload can dispose this
        // downloader while Execute is mid-wait on the worker thread, or while an
        // inbound handler fires a Set from the read thread.
        private static bool SafeWait(ManualResetEventSlim e, int ms)
        { try { return e.Wait(ms); } catch (ObjectDisposedException) { return false; } }
        private static void SafeReset(ManualResetEventSlim e)
        { try { e.Reset(); } catch (ObjectDisposedException) { } }
        private static void SafeSet(ManualResetEventSlim e)
        { try { e.Set(); } catch (ObjectDisposedException) { } }
        // Returns true ("done") on disposal so wait-loops exit promptly rather
        // than spinning to their deadline.
        private static bool SafeIsSet(ManualResetEventSlim e)
        { try { return e.IsSet; } catch (ObjectDisposedException) { return true; } }

        private const int WindowSize = 4;
        private byte _session; // dynamic: whichever FT session the wheel device-inits
        public byte ActiveSession => _session;
        /// <summary>
        /// Gate: only attempt downloads when true. Tied to EnableWireTraceFileSink
        /// so download requests don't fire on every launch while the protocol is
        /// still being stabilized.
        /// </summary>
        public bool Enabled { get; set; }
        private readonly MozaSerialConnection _connection;
        private readonly DashboardCache _cache;
        private readonly DashboardProfileStore _store;
        private readonly SessionRetransmitter _retransmitter;
        private readonly SessionDispatcher _dispatcher;

        // Inbound response reassembly
        private readonly List<byte> _responseBuffer = new();
        private volatile bool _receiving;
        private readonly ManualResetEventSlim _downloadComplete = new(false);
        private volatile int _lastChunkTicks;

        // Outbound ack tracking for windowed send
        private volatile int _lastAckedSeq;
        private readonly ManualResetEventSlim _ackReceived = new(false);

        // Session device-init detection
        private readonly ManualResetEventSlim _sessionOpened = new(false);
        private volatile int _deviceInitSeq;

        public DashboardDownloader(
            MozaSerialConnection connection,
            DashboardCache cache,
            DashboardProfileStore store,
            SessionRetransmitter retransmitter,
            SessionDispatcher dispatcher)
        {
            _connection = connection;
            _cache = cache;
            _store = store;
            _retransmitter = retransmitter;
            _dispatcher = dispatcher;
        }

        // ── ISessionConsumer implementation ───────────────────────────────
        // The dispatcher routes frames exclusively to the session owner.
        // No more shared _uploadSession collisions.

        /// <summary>Inbound data chunk on our claimed session.</summary>
        void ISessionConsumer.OnData(byte session, int seq, byte[] payload)
        {
            // TelemetrySender already acks before dispatching — don't double-ack.
            if (!_receiving) return;
            byte[] net = SessionDataReassembler.StripCrcTrailer(payload);
            lock (_responseBuffer)
            {
                _responseBuffer.AddRange(net);
            }
            _lastChunkTicks = Environment.TickCount;
        }

        /// <summary>FC:00 ack on our claimed session. Advances send window.</summary>
        void ISessionConsumer.OnAck(byte session, int ackSeq)
        {
            if (ackSeq > _lastAckedSeq)
                _lastAckedSeq = ackSeq;
            SafeSet(_ackReceived);
        }

        /// <summary>Device-init (type 0x81) on our claimed session.</summary>
        void ISessionConsumer.OnOpen(byte session, int openSeq)
        {
            _session = session;
            _deviceInitSeq = openSeq;
            SafeSet(_sessionOpened);
        }

        /// <summary>Session end marker (type 0x00) on our claimed session.</summary>
        void ISessionConsumer.OnClose(byte session, int ackSeq)
        {
            SafeSet(_downloadComplete);
        }

        /// <summary>
        /// Download dashboards missing from cache. Blocks calling thread.
        /// Returns number of dashboards successfully ingested.
        /// </summary>
        public int Execute(
            WheelDashboardState state,
            IReadOnlyList<string> missingHashes,
            int timeoutMs = 300_000)
        {
            if (!Enabled)
            {
                MozaLog.Debug("[AZOM] DashboardDownloader: skipped (download not enabled)");
                return 0;
            }
            if (state.EnabledDashboards == null || state.EnabledDashboards.Count == 0)
                return 0;

            // Build name→hash lookup for missing dashboards
            var hashByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dash in state.EnabledDashboards)
            {
                if (string.IsNullOrEmpty(dash.DirName) || string.IsNullOrEmpty(dash.Hash))
                    continue;
                if (missingHashes is List<string> list && !list.Contains(dash.Hash))
                    continue;
                hashByName[dash.DirName] = dash.Hash;
            }
            if (hashByName.Count == 0) return 0;

            // ── Phase 1: Claim session 0x0B and request FT device-init ─────
            // Claim 0x0B through the dispatcher so we get exclusive routing.
            // This evicts the tile-server parser from 0x0B during download.
            SafeReset(_sessionOpened);
            SafeReset(_downloadComplete);
            SafeReset(_ackReceived);
            _dispatcher.Claim(0x0B, this);
            MozaLog.Debug("[AZOM] DashboardDownloader: claimed session 0x0B, sending FT activate...");
            // Whole-body try/finally so EVERY exit path (including future ones)
            // releases the dispatcher claim. ReleaseAll(this) covers both the
            // initial 0x0B claim and any session the wheel migrated us to via
            // OnOpen — without enumerating sessions in each return site.
            try
            {
            SendFileTransferActivate(0x0B);

            if (!SafeIsSet(_sessionOpened))
            {
                MozaLog.Debug("[AZOM] DashboardDownloader: waiting for FT session device-init...");
                if (!SafeWait(_sessionOpened, 15_000))
                {
                    MozaLog.Warn("[AZOM] DashboardDownloader: no FT session opened by wheel");
                    return 0;
                }
            }
            MozaLog.Debug($"[AZOM] DashboardDownloader: using session 0x{_session:X2}");

            // ── Phase 3: Build and send download request ──────────────────
            _retransmitter.Clear();

            // DIAGNOSTIC: Replay PitHouse's exact frames to test if the
            // issue is request content vs session setup. If PitHouse bytes
            // get a response, our content is wrong. If not, setup is wrong.
            string replayPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "pithouse_replay.txt");
            if (System.IO.File.Exists(replayPath))
            {
                MozaLog.Debug("[AZOM] DashboardDownloader: REPLAY MODE — sending PitHouse frames");
                var lines = System.IO.File.ReadAllLines(replayPath);
                lock (_responseBuffer) { _responseBuffer.Clear(); }
                SafeReset(_downloadComplete);
                _receiving = true;
                // Device-init ack already sent by TelemetrySender. Don't double-ack.
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    byte[] raw = new byte[line.Length / 2];
                    for (int i = 0; i < raw.Length; i++)
                        raw[i] = Convert.ToByte(line.Substring(i * 2, 2), 16);
                    _connection.Send(raw);
                }
                MozaLog.Debug($"[AZOM] DashboardDownloader: sent {lines.Length} replay frames");
                // Wait for response
                _lastChunkTicks = Environment.TickCount;
                int deadline2 = Environment.TickCount + 60_000;
                while (!SafeIsSet(_downloadComplete) && Environment.TickCount < deadline2)
                {
                    SafeWait(_downloadComplete, 1000);
                    int bufSize; lock (_responseBuffer) { bufSize = _responseBuffer.Count; }
                    int idle = Environment.TickCount - _lastChunkTicks;
                    if (bufSize > 0 && idle > 15_000) break;
                }
                byte[] rd; lock (_responseBuffer) { rd = _responseBuffer.ToArray(); _responseBuffer.Clear(); }
                _receiving = false;
                MozaLog.Debug($"[AZOM] DashboardDownloader: replay response = {rd.Length} bytes");
                return 0;
            }

            byte[] requestBody = BuildRequestBody(state, hashByName, out var dashboardOrder);
            MozaLog.Debug(
                $"[AZOM] DashboardDownloader: built {requestBody.Length} byte request " +
                $"for {hashByName.Count} dashboards on session 0x{_session:X2}");

            lock (_responseBuffer) { _responseBuffer.Clear(); }
            SafeReset(_downloadComplete);
            _receiving = true;

            try
            {
                // Device-init ack is already sent by TelemetrySender's
                // OnMessageDuringPreamble (line ~2060). Do NOT double-ack.

                // Chunk and send with windowed acking.
                // PitHouse uses seq = deviceInitSeq + 3 for the first data chunk.
                // Fresh capture confirms gap=3 (init=11→data=14).
                int seq = _deviceInitSeq + 3;
                var frames = TierDefinitionBuilder.ChunkMessage(
                    requestBody, _session, ref seq);

                MozaLog.Debug(
                    $"[AZOM] DashboardDownloader: sending {frames.Count} chunks " +
                    $"(seq {_deviceInitSeq + 3}..{seq - 1}), windowed");

                int baseSeq = _deviceInitSeq + 3;
                _lastAckedSeq = baseSeq - 1;
                int sent = 0;

                while (sent < frames.Count)
                {
                    int ackedUpTo = _lastAckedSeq - baseSeq + 1;
                    if (ackedUpTo < 0) ackedUpTo = 0;

                    int windowLimit = Math.Min(frames.Count, ackedUpTo + WindowSize);
                    while (sent < windowLimit)
                    {
                        _connection.Send(frames[sent]);
                        _retransmitter.Track(frames[sent]);
                        sent++;
                    }

                    if (sent >= frames.Count) break;

                    SafeReset(_ackReceived);
                    SafeWait(_ackReceived, 500);
                }

                int closeSeq = seq; // seq after last data frame
                MozaLog.Debug(
                    $"[AZOM] DashboardDownloader: all {frames.Count} request chunks sent, " +
                    $"waiting for response (timeout={timeoutMs}ms)");

                // ── Wait for response ──────────────────────────────────────
                // PH receives the wheel's response data BEFORE sending session
                // close. The wheel needs time to process the request and start
                // sending back data. Only close after we have response data or
                // on timeout.
                _lastChunkTicks = Environment.TickCount;
                int deadline = Environment.TickCount + timeoutMs;
                const int InactivityMs = 15_000;
                const int PollMs = 1_000;
                bool closeSent = false;

                while (!SafeIsSet(_downloadComplete) && Environment.TickCount < deadline)
                {
                    SafeWait(_downloadComplete, PollMs);
                    int bufSize;
                    lock (_responseBuffer) { bufSize = _responseBuffer.Count; }

                    // Once we have response data and the wheel goes idle, send
                    // session close and exit. Mirrors PH's close-after-response.
                    int idle = Environment.TickCount - _lastChunkTicks;
                    if (bufSize > 0 && idle > InactivityMs)
                    {
                        MozaLog.Debug(
                            $"[AZOM] DashboardDownloader: {bufSize} bytes, idle {idle}ms — closing");
                        if (!closeSent)
                        {
                            SendSessionClose(_session, (ushort)closeSeq);
                            closeSent = true;
                        }
                        break;
                    }
                }
                if (!closeSent)
                {
                    SendSessionClose(_session, (ushort)closeSeq);
                }
                if (!SafeIsSet(_downloadComplete) && Environment.TickCount >= deadline)
                    MozaLog.Warn("[AZOM] DashboardDownloader: response timeout");
            }
            finally
            {
                _receiving = false;
            }

            // ── Process response ───────────────────────────────────────────
            byte[] responseData;
            lock (_responseBuffer)
            {
                responseData = _responseBuffer.ToArray();
                _responseBuffer.Clear();
            }

            if (responseData.Length == 0)
            {
                MozaLog.Warn("[AZOM] DashboardDownloader: empty response");
                return 0;
            }

            MozaLog.Debug($"[AZOM] DashboardDownloader: received {responseData.Length} bytes");

            byte[]? decompressed = DecompressResponse(responseData);
            if (decompressed == null || decompressed.Length == 0)
            {
                MozaLog.Warn("[AZOM] DashboardDownloader: decompression failed");
                return 0;
            }

            MozaLog.Debug($"[AZOM] DashboardDownloader: decompressed {decompressed.Length} bytes");

            var mzdashFiles = SplitMzdashFiles(decompressed);
            MozaLog.Debug($"[AZOM] DashboardDownloader: found {mzdashFiles.Count} mzdash files");

            int ingested = 0;
            for (int i = 0; i < mzdashFiles.Count && i < dashboardOrder.Count; i++)
            {
                string name = dashboardOrder[i];
                string hash = hashByName[name];
                if (_cache.Ingest(hash, name, mzdashFiles[i]))
                    ingested++;
            }

            MozaLog.Debug(
                $"[AZOM] DashboardDownloader: ingested {ingested}/{mzdashFiles.Count} dashboards");
            return ingested;
            }
            finally
            {
                // Always release every claim we hold — covers initial 0x0B and
                // any session migrated by OnOpen.
                try { _dispatcher.ReleaseAll(this); } catch { }
            }
        }

        // ── Request body construction (5 sections) ─────────────────────────

        private byte[] BuildRequestBody(
            WheelDashboardState state,
            Dictionary<string, string> hashByName,
            out List<string> dashboardOrder)
        {
            long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData).Replace('\\', '/');

            string localBase = $"{localAppData}/MOZA Pit House/_dashes/8ae5d086b2fcad7486dbe208";
            string imageBase = $"{localAppData}/MOZA Pit House/_dashes";

            // Collect all files (dashboards + images). dashName is the dashboard
            // dir-name for dashboard entries, null for images.
            var files = new List<(string remotePath, string localPath, string md5Hex, string? dashName)>();
            foreach (var kvp in hashByName)
            {
                string name = kvp.Key;
                string hash = kvp.Value;
                files.Add((
                    $"/home/moza/resource/dashes/{name}/{name}.mzdash",
                    $"{localBase}/{name}/{name}.mzdash",
                    hash,
                    name));
            }
            if (state.ImagePath != null)
            {
                foreach (var img in state.ImagePath)
                {
                    if (string.IsNullOrEmpty(img.Md5)) continue;
                    // Only include images referenced by enabled dashboards.
                    // ImageRefMap keys are "MD5/<hash>.png" with refcount > 0.
                    string refKey = $"MD5/{img.Md5}.png";
                    if (state.ImageRefMap != null
                        && state.ImageRefMap.TryGetValue(refKey, out int refCount)
                        && refCount > 0)
                    {
                        files.Add((
                            $"/home/moza/resource/images/MD5/{img.Md5}.png",
                            $"{imageBase}/images/MD5/{img.Md5}.png",
                            img.Md5,
                            null));
                    }
                }
            }

            // Sort files alphabetically by remote path to match PitHouse order.
            files.Sort((a, b) => string.Compare(a.remotePath, b.remotePath, StringComparison.Ordinal));

            // The wheel returns file contents in this same (sorted) request order.
            // Capture the dashboard names in that order so the response ingest maps
            // each extracted mzdash file back to the correct dashboard/hash.
            dashboardOrder = new List<string>(files.Count);
            foreach (var f in files)
                if (f.dashName != null) dashboardOrder.Add(f.dashName);

            // Section 2: Remote paths (comma-separated UTF-16LE)
            var remoteSb = new StringBuilder();
            foreach (var f in files)
            {
                if (remoteSb.Length > 0) remoteSb.Append(',');
                remoteSb.Append(f.remotePath);
            }
            byte[] remoteBytes = Encoding.Unicode.GetBytes(remoteSb.ToString());

            // Section 1: Header
            byte[] header = BuildRequestHeader(files.Count, remoteBytes.Length);

            // Section 3: Separator between remote and local paths.
            // Capture 1 (session 0x0B, 2025-11 fw): 00 0F (2 bytes), paths start with ~
            // Capture 2 (session 0x04, 2026-04 fw): 00 0D FA 00 (4 bytes), paths start with C:/
            // Our wheel uses session 0x05 (2026-04 fw) → use 4-byte format.
            // Byte[1] = file_count - 5 (confirmed: cap1=20-5=15=0x0F, cap2=18-5=13=0x0D).
            // Byte[2] = 0xFA for 2026-04 firmware (cap2). Meaning unknown.
            byte sepByte = (byte)Math.Max(0, files.Count - 5);
            byte[] separator1 = new byte[] { 0x00, sepByte, 0xFA, 0x00 };

            // Section 4: Local dest paths (comma-separated UTF-16LE)
            var localSb = new StringBuilder();
            foreach (var f in files)
            {
                if (localSb.Length > 0) localSb.Append(',');
                localSb.Append(f.localPath);
            }
            byte[] localBytesFull = Encoding.Unicode.GetBytes(localSb.ToString());
            // PH's local section is odd-length: the trailing 00 of the last
            // UTF-16LE character is dropped. Verified: PH 3577B = 1789 chars × 2 − 1.
            // The envelope (FF×4 00×8 FF×4) follows immediately after the truncated byte.
            byte[] localBytes = new byte[localBytesFull.Length - 1];
            Array.Copy(localBytesFull, 0, localBytes, 0, localBytes.Length);

            // Section 5: Staging metadata + token tail + TLV manifest entry.
            // Fresh PitHouse capture (0x0B, 2026-05-01) shows the request tail:
            //   Block 1: staging metadata (52-path + MD5 + token + sentinel + checksum)
            //   Block 2: TLV entry (8C-local + 52-remote + MD5 + token + sentinel + checksum)
            // Both blocks share the same staging paths, MD5, and token.
            // DIAGNOSTIC: Use PitHouse's exact MD5 and token. These are stable
            // across cold-starts on the same firmware — device-derived, not random.
            // Verified across 3 captures (bridge-20260501-134243, -153909, -115203).
            byte[] sessionMd5 = new byte[] {
                0x1c, 0xd4, 0xb5, 0xbc, 0x86, 0x1a, 0x0b, 0x16,
                0x53, 0xe7, 0x60, 0x2c, 0xc9, 0x60, 0x8a, 0xae
            };
            string md5Hex = FileTransferBuilder.Md5Hex(sessionMd5);
            string userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile).Replace('\\', '/');
            string localTempPath = $"{userProfile}/_moza_filetransfer_md5_{md5Hex}";
            string remoteStagingPath = $"/tmp/_moza_filetransfer_tmp_{timestampMs}";
            uint tokenValue = 0x0005203d;

            // After local paths: 16-byte envelope, then two TLV entries.
            // No duplicate last path — PH capture has exactly 18 comma-separated
            // paths (17 commas), envelope starts immediately after.
            byte[] envelope = new byte[] {
                0xFF, 0xFF, 0xFF, 0xFF,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0xFF, 0xFF,
            };
            byte[] tlvEntry1 = BuildTlvEntryBlock(localTempPath, remoteStagingPath, sessionMd5, tokenValue, firstEntry: true);
            byte[] tlvEntry2 = BuildTlvEntryBlock(localTempPath, remoteStagingPath, sessionMd5, tokenValue, firstEntry: false);

            // Assemble all sections
            int totalLen = header.Length + remoteBytes.Length + separator1.Length
                         + localBytes.Length
                         + envelope.Length + tlvEntry1.Length + tlvEntry2.Length;
            byte[] body = new byte[totalLen];
            int pos = 0;
            Array.Copy(header, 0, body, pos, header.Length); pos += header.Length;
            Array.Copy(remoteBytes, 0, body, pos, remoteBytes.Length); pos += remoteBytes.Length;
            Array.Copy(separator1, 0, body, pos, separator1.Length); pos += separator1.Length;
            Array.Copy(localBytes, 0, body, pos, localBytes.Length); pos += localBytes.Length;
            Array.Copy(envelope, 0, body, pos, envelope.Length); pos += envelope.Length;
            int entry1Start = pos;
            Array.Copy(tlvEntry1, 0, body, pos, tlvEntry1.Length); pos += tlvEntry1.Length;
            Array.Copy(tlvEntry2, 0, body, pos, tlvEntry2.Length);

            // Patch entry 0 header byte[0]. Formula (from PitHouse capture analysis):
            // hdr = XOR(body[0 .. entry0Start-1]) ^ 0x16
            // Confirmed across fresh capture (bridge-20260501-153909).
            byte xorBefore = 0;
            for (int i = 0; i < entry1Start; i++) xorBefore ^= body[i];
            body[entry1Start] = (byte)(xorBefore ^ 0x16);

            // Patch entry 1 header byte[0]. Same XOR scheme, different constant:
            // hdr = XOR(body[0 .. entry1Start-1]) ^ 0x01
            // Confirmed: fresh capture entry 1 = 0x11 = XOR(before) ^ 0x01.
            int entry2Start = entry1Start + tlvEntry1.Length;
            byte xorBefore2 = 0;
            for (int i = 0; i < entry2Start; i++) xorBefore2 ^= body[i];
            body[entry2Start] = (byte)(xorBefore2 ^ 0x01);

            return body;
        }

        /// <summary>
        /// Build block 1: staging metadata with token/sentinel/checksum.
        /// Format from fresh PitHouse capture:
        ///   [00 00] null + [52 00] + remote path + [00 00] + [10] + MD5 + [00]
        ///   + token[1:3] + token BE32 + FF×4 + checksum
        /// </summary>
        // TODO: dashboard upload (in progress) — do not remove
        private static byte[] BuildStagingBlock(string remoteStagingPath, byte[] md5, uint token)
        {
            byte[] remotePath = Encoding.Unicode.GetBytes(remoteStagingPath);
            var buf = new List<byte>();
            buf.Add(0x00); buf.Add(0x00); // null terminator for local paths
            buf.Add(0x52); buf.Add(0x00); // remote staging path marker
            buf.AddRange(remotePath);
            buf.Add(0x00); buf.Add(0x00); // null terminator
            buf.Add(0x10); // MD5 length
            buf.AddRange(md5);
            buf.Add(0x00); // padding
            // Block 1 checksum is computed over the full assembled request
            // (patched in BuildRequestBody after assembly). Use placeholder 0.
            AppendTokenSentinel(buf, token, -1); // -1 = skip checksum computation
            return buf.ToArray();
        }

        /// <summary>
        /// Build the transfer manifest: 9-byte preamble + N × 268-byte entries.
        /// Each entry declares a 4092-byte offset into the expected response stream.
        /// Format verified across both PitHouse captures:
        ///   Cap1 (session 0x0B, 205 entries): preamble FC 00 [token[1:4]] FF×4
        ///   Cap2 (session 0x04, 82 entries):  preamble FC 00 [token[1:4]] FF×4
        /// Counter: starts at 8184 (2×4092), stride 4092. Last entry counter = token.
        /// </summary>
        // TODO: dashboard upload (in progress) — do not remove
        private static byte[] BuildTransferManifest(
            string localTempPath, string remoteStagingPath, byte[] md5,
            uint token, int entryCount, int chunkStride)
        {
            const int EntrySize = 268;
            const int PreambleSize = 9;
            const int LocalFieldSize = 144;
            const int RemoteFieldSize = 86;

            byte[] localPathBytes = Encoding.Unicode.GetBytes(localTempPath);
            byte[] remotePathBytes = Encoding.Unicode.GetBytes(remoteStagingPath);

            int totalSize = PreambleSize + (entryCount * EntrySize);
            byte[] manifest = new byte[totalSize];
            int pos = 0;

            // 9-byte preamble: FC 00 [token byte1] [token byte2] [token byte3] FF FF FF FF
            manifest[pos++] = 0xFC;
            manifest[pos++] = 0x00;
            manifest[pos++] = (byte)((token >> 16) & 0xFF); // token[1]
            manifest[pos++] = (byte)((token >> 8) & 0xFF);  // token[2]
            manifest[pos++] = (byte)(token & 0xFF);          // token[3]
            manifest[pos++] = 0xFF; manifest[pos++] = 0xFF;
            manifest[pos++] = 0xFF; manifest[pos++] = 0xFF;

            for (int e = 0; e < entryCount; e++)
            {
                int entryStart = pos;
                // Counter: starts at 2*chunkStride (8184), increments by chunkStride.
                // Last entry: counter = token value (marks end of table).
                uint counter = (uint)((e + 2) * chunkStride);
                if (e == entryCount - 1) counter = token;

                // [0] Checksum byte — varies per entry in PitHouse captures.
                // Purpose unknown; both captures show different values per entry.
                // Use 0x00 for now — wheel may ignore this field.
                manifest[pos++] = 0x00;
                // [1] Constant 0x01
                manifest[pos++] = 0x01;
                // [2:4] Type/flags constant 06 01
                manifest[pos++] = 0x06;
                manifest[pos++] = 0x01;
                // [4:9] 5 zero pad
                for (int i = 0; i < 5; i++) manifest[pos++] = 0x00;

                // [9:9+LocalFieldSize] Local path TLV
                manifest[pos++] = 0x8C; // marker
                manifest[pos++] = 0x00; // pad
                int copyLen = Math.Min(localPathBytes.Length, LocalFieldSize - 4); // marker(2) + null(2)
                Array.Copy(localPathBytes, 0, manifest, pos, copyLen);
                pos += LocalFieldSize - 2; // advance past content+null (marker already written)

                // [153:153+RemoteFieldSize] Remote path TLV
                manifest[pos++] = 0x52; // marker (NOT 0x70!)
                manifest[pos++] = 0x00; // pad
                copyLen = Math.Min(remotePathBytes.Length, RemoteFieldSize - 4);
                Array.Copy(remotePathBytes, 0, manifest, pos, copyLen);
                pos += RemoteFieldSize - 2;

                // [239] MD5 length + MD5
                manifest[pos++] = 0x10;
                Array.Copy(md5, 0, manifest, pos, 16);
                pos += 16;

                // [256:260] Counter BE32
                manifest[pos++] = (byte)((counter >> 24) & 0xFF);
                manifest[pos++] = (byte)((counter >> 16) & 0xFF);
                manifest[pos++] = (byte)((counter >> 8) & 0xFF);
                manifest[pos++] = (byte)(counter & 0xFF);

                // [260:264] Token BE32
                manifest[pos++] = (byte)((token >> 24) & 0xFF);
                manifest[pos++] = (byte)((token >> 16) & 0xFF);
                manifest[pos++] = (byte)((token >> 8) & 0xFF);
                manifest[pos++] = (byte)(token & 0xFF);

                // [264:268] Sentinel
                manifest[pos++] = 0xFF; manifest[pos++] = 0xFF;
                manifest[pos++] = 0xFF; manifest[pos++] = 0xFF;

                // Verify entry size
                System.Diagnostics.Debug.Assert(pos - entryStart == EntrySize,
                    $"Entry size mismatch: {pos - entryStart} != {EntrySize}");
            }

            return manifest;
        }

        private static byte[] ParseMd5Hex(string hex)
        {
            byte[] md5 = new byte[16];
            if (string.IsNullOrEmpty(hex)) return md5;
            for (int i = 0; i < 16 && i * 2 + 1 < hex.Length; i++)
                md5[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return md5;
        }

        private static byte[] BuildRequestHeader(int fileCount, int remotePathByteCount)
        {
            int countField = fileCount + 4; // observed: 18 files → 22, 20 files → 24
            return new byte[]
            {
                0x00, 0x00,
                (byte)(countField & 0xFF), (byte)((countField >> 8) & 0xFF), // LE16
                0x00, 0x00, 0x00,
                (byte)((remotePathByteCount >> 8) & 0xFF),  // BE high byte
                (byte)(remotePathByteCount & 0xFF),          // BE low byte
                0x00
            };
        }

        // ── Upload sub-msg-1 handshake ──────────────────────────────────────

        /// <summary>
        /// Build block 2: TLV manifest entry with 8C local + 52 remote paths.
        /// Format from fresh PitHouse capture:
        ///   [checksum] [06 01] [00×5]   — 9-byte entry header
        ///   [8C 00] + local path + [00 00]
        ///   [52 00] + remote path + [00 00]
        ///   [10] + MD5 + [00]
        ///   token[1:3] + token BE32 + FF×4 + checksum
        /// </summary>
        private static byte[] BuildTlvEntryBlock(
            string localTempPath, string remoteStagingPath, byte[] md5, uint token,
            bool firstEntry)
        {
            byte[] localPath = Encoding.Unicode.GetBytes(localTempPath);
            byte[] remotePath = Encoding.Unicode.GetBytes(remoteStagingPath);
            var buf = new List<byte>();
            // Entry 0 (first): 9-byte header [checksum] [01] [06 01] [00×5]
            // Entry 1+ (rest): 8-byte header [checksum] [06 01] [00×5]
            // Both checksums are XOR-based, patched in BuildRequestBody after assembly.
            int hdrIdx = buf.Count;
            if (firstEntry)
            {
                buf.Add(0x00); // checksum placeholder — patched in BuildRequestBody
                buf.Add(0x01); // extra byte only in first entry
            }
            else
            {
                buf.Add(0x00); // checksum placeholder — patched in BuildRequestBody
            }
            buf.Add(0x06); buf.Add(0x01);
            buf.Add(0x00); buf.Add(0x00); buf.Add(0x00); buf.Add(0x00); buf.Add(0x00);
            // 8C local path TLV
            buf.Add(0x8C); buf.Add(0x00);
            buf.AddRange(localPath);
            buf.Add(0x00); buf.Add(0x00); // null terminator
            // 52 remote path TLV
            buf.Add(0x52); buf.Add(0x00);
            buf.AddRange(remotePath);
            buf.Add(0x00); buf.Add(0x00); // null terminator
            // MD5
            buf.Add(0x10);
            buf.AddRange(md5);
            buf.Add(0x00); // padding
            // Tail checksum = XOR from 8C marker to sentinel (inclusive).
            // 8C offset: 9 for first entry (9-byte header), 8 for others (8-byte header).
            int csStart = firstEntry ? 9 : 8;
            AppendTokenSentinel(buf, token, csStart);
            // First entry: header byte[0] depends on XOR of the entire
            // request body BEFORE this entry. Computed in BuildRequestBody
            // after full assembly. Leave as 0x00 placeholder here.
            return buf.ToArray();
        }

        /// <summary>
        /// Append token + sentinel + checksum to a buffer. The checksum for
        /// TLV entry blocks (block 2) = XOR of bytes from the first 0x8C or
        /// 0x52 marker through the FFFFFFFF sentinel (confirmed across captures).
        /// The <paramref name="checksumStartIdx"/> marks where to begin XOR.
        /// </summary>
        private static void AppendTokenSentinel(
            List<byte> buf, uint token, int checksumStartIdx)
        {
            buf.Add((byte)((token >> 16) & 0xFF)); // token[1]
            buf.Add((byte)((token >> 8) & 0xFF));   // token[2]
            buf.Add((byte)(token & 0xFF));           // token[3]
            buf.Add((byte)((token >> 24) & 0xFF));  // token BE32 byte 0
            buf.Add((byte)((token >> 16) & 0xFF));  // token BE32 byte 1
            buf.Add((byte)((token >> 8) & 0xFF));   // token BE32 byte 2
            buf.Add((byte)(token & 0xFF));           // token BE32 byte 3
            buf.Add(0xFF); buf.Add(0xFF); buf.Add(0xFF); buf.Add(0xFF); // sentinel
            // XOR checksum from checksumStartIdx to end of sentinel.
            // If checksumStartIdx < 0, use placeholder 0x00 (patched later).
            if (checksumStartIdx >= 0)
            {
                byte xor = 0;
                for (int i = checksumStartIdx; i < buf.Count; i++)
                    xor ^= buf[i];
                buf.Add(xor);
            }
            else
            {
                buf.Add(0x00); // placeholder
            }
        }

        // ── Wire helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Send the paired session-open commands that PitHouse sends before
        /// each FT session. Fresh capture shows:
        ///   7C 27 0F 80 [port1_lo] 00 [sess1_lo] 00 FE 01  (port-open)
        ///   7C 23 46 80 [port2_lo] 00 [sess2_lo] 00 FE 01  (FT activate)
        /// The last pair before download: port-open(5,3) + FT-activate(0xD,0xB).
        /// </summary>
        private void SendFileTransferActivate(byte ftSession)
        {
            // Port-open command (7C 27 0F 80) — required before FT activate.
            // PitHouse uses port=5, session=3 for the paired management session.
            var portOpen = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x27, 0x0F, 0x80,
                0x05, 0x00,  // port 5
                0x03, 0x00,  // session 3
                0xFE, 0x01,
                0x00
            };
            portOpen[14] = MozaProtocol.CalculateWireChecksum(portOpen);
            _connection.Send(portOpen);

            // FT activate command (7C 23 46 80)
            var ftActivate = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x23, 0x46, 0x80,
                0x0D, 0x00,       // port 0x0D
                ftSession, 0x00,  // session (0x0B)
                0xFE, 0x01,
                0x00
            };
            ftActivate[14] = MozaProtocol.CalculateWireChecksum(ftActivate);
            _connection.Send(ftActivate);
        }

        private void SendSessionAck(byte session, ushort ackSeq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x05,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0xFC, 0x00,
                session,
                (byte)(ackSeq & 0xFF),
                (byte)(ackSeq >> 8),
                0x00
            };
            frame[9] = MozaProtocol.CalculateWireChecksum(frame);
            // Priority lane: matches TelemetrySender.SendSessionAck — fc:00
            // session acks are time-critical (wheel session timeouts ~1 s) and
            // must not get buried behind tier-def or upload-chunk bursts in the
            // one-shot FIFO. See MozaSerialConnection._priorityQueue.
            _connection.SendPriority(frame);
        }

        /// <summary>
        /// Send session close (type=0x00). PitHouse sends this after the upload
        /// sub-msg-1 handshake completes, which triggers the wheel to re-open
        /// the session for the actual download.
        /// Wire: 7C 00 [session] 00 [ack_lo] [ack_hi]
        /// </summary>
        private void SendSessionClose(byte session, ushort ackSeq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session,
                0x00, // type = close
                (byte)(ackSeq & 0xFF),
                (byte)(ackSeq >> 8),
                0x00
            };
            frame[10] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        // ── Response decompression ─────────────────────────────────────────

        private static byte[]? DecompressResponse(byte[] data)
        {
            byte[]? extracted = ExtractContentBlocks(data);
            if (extracted != null && extracted.Length > 0)
            {
                byte[]? result = TryDeflate(extracted);
                if (result != null && result.Length > 0)
                    return result;
            }

            byte[]? byMagic = SessionDataReassembler.TryDecompressByMagic(data);
            if (byMagic != null && byMagic.Length > 0)
                return byMagic;

            return IncrementalDecompress(data);
        }

        private static byte[]? ExtractContentBlocks(byte[] data)
        {
            const int BlockOverhead = 8 + 172;
            const int BlockDataLen = 4180;
            const int BlockTotal = BlockOverhead + BlockDataLen;

            var extracted = new List<byte>();
            bool firstBlock = true;

            for (int i = 0; i + BlockTotal <= data.Length; i++)
            {
                if (data[i] != 0x03) continue;
                if (i + 8 > data.Length) continue;
                if (data[i + 3] != 0x00 || data[i + 4] != 0x00 ||
                    data[i + 5] != 0x00 || data[i + 6] != 0x00 || data[i + 7] != 0x00)
                    continue;

                int blockSize = data[i + 1] | (data[i + 2] << 8);
                if (blockSize < BlockDataLen) continue;

                int dataStart = i + BlockOverhead;
                if (dataStart + BlockDataLen > data.Length) break;

                if (firstBlock)
                {
                    for (int j = 0; j < BlockDataLen - 1; j++)
                    {
                        if (data[dataStart + j] == 0x78 &&
                            (data[dataStart + j + 1] == 0x9C || data[dataStart + j + 1] == 0xDA))
                        {
                            int remaining = BlockDataLen - j;
                            var chunk = new byte[remaining];
                            Array.Copy(data, dataStart + j, chunk, 0, remaining);
                            extracted.AddRange(chunk);
                            break;
                        }
                    }
                    firstBlock = false;
                }
                else
                {
                    var chunk = new byte[BlockDataLen];
                    Array.Copy(data, dataStart, chunk, 0, BlockDataLen);
                    extracted.AddRange(chunk);
                }

                i = i + BlockOverhead + blockSize - 1;
            }

            return extracted.Count > 0 ? extracted.ToArray() : null;
        }

        private static byte[]? TryDeflate(byte[] data)
        {
            int start = 0;
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == 0x78 && (data[i + 1] == 0x9C || data[i + 1] == 0xDA))
                {
                    start = i + 2;
                    break;
                }
            }
            if (start == 0) start = 2;
            return SessionDataReassembler.DecompressZlib(data, start - 2);
        }

        private static byte[] IncrementalDecompress(byte[] data)
        {
            byte[]? result = SessionDataReassembler.TryDecompressByMagic(data);
            return result ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Split decompressed output into individual mzdash files by tracking
        /// JSON brace depth. Each top-level {} is one complete mzdash file.
        /// </summary>
        internal static List<string> SplitMzdashFiles(byte[] decompressed)
        {
            string text = Encoding.UTF8.GetString(decompressed);
            var files = new List<string>();
            int depth = 0;
            int fileStart = -1;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c == '{')
                {
                    if (depth == 0) fileStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && fileStart >= 0)
                    {
                        files.Add(text.Substring(fileStart, i - fileStart + 1));
                        fileStart = -1;
                    }
                }
            }
            return files;
        }
    }
}
