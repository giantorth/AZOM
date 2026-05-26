using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Buffers inbound 7c:00 session-data chunk bytes from the wheel and parses out
    /// the channel-URL catalog the wheel advertises during preamble (and after dash
    /// switches). The catalog is the canonical "channel idx → URL" mapping the
    /// firmware uses for tier-def lookups; without it, tier-def channels resolve to
    /// idx=0 and the wheel cannot bind them to display elements.
    ///
    /// Bytes are kept in per-session buffers because the wheel may advertise on
    /// session 0x01 (V0 URL-subscription firmware) AND we ALSO collect from the
    /// telemetry session (FlagByte / 0x02) for V2-compact firmware that ships
    /// URLs there. In CS Pro V2-type02 firmware the telemetry session sometimes
    /// carries non-catalog control bytes; merging the two streams into a single
    /// buffer let those control bytes land inside catalog records that span
    /// chunk boundaries and corrupt them. Per-session buffers keep the two
    /// streams independent and the parser runs over each buffer separately,
    /// merging successfully-parsed records into the shared <see cref="Catalog"/>.
    ///
    /// Iteration over sessions during parse is in **wire-arrival order**
    /// (tracked via <see cref="_sessionOrder"/>) so the merge step's
    /// last-write-wins on <c>idx</c> collisions stays deterministic and matches
    /// the byte-arrival ordering the old single-buffer parser saw.
    ///
    /// The parser is intricate because the wire format encodes URLs in three
    /// different forms (full text, prefix-compressed via 0x01, abbreviation-
    /// compressed via 0x5C 0x31) plus back-references (zero-length records that
    /// refer to a prior URL by idx). Dash switches re-index URLs without
    /// re-announcing all of them, so back-refs are heavy at switch time and the
    /// merge step preserves prior idx→URL bindings unless the wheel overrides them.
    ///
    /// All public methods are thread-safe via an internal lock on the buffer map.
    /// The parsed catalog is published via a volatile field so observers see
    /// monotonically-grown lists without external synchronisation.
    /// </summary>
    internal sealed class ChannelCatalogParser
    {
        private readonly object _bufferLock = new object();
        // Per-session byte buffers. Session 0x01 (mgmt) and FlagByte/0x02
        // (telemetry) can both carry catalog chunks depending on firmware.
        // Keeping them split prevents cross-session corruption when one
        // session's payload bytes happen to land mid-record in the other.
        private readonly Dictionary<byte, List<byte>> _buffersBySession = new();
        // First-arrival order of session IDs. Walked by TryParse so iteration
        // matches the order in which the wheel started using each session —
        // makes the merge step's last-write-wins on idx collisions match what
        // the pre-split single-buffer parser produced byte-wise. .NET 4.8's
        // Dictionary explicitly does not guarantee enumeration order; relying
        // on it would let two runs against the same wire bytes produce
        // different catalog content.
        private readonly List<byte> _sessionOrder = new();
        // Per-session highest seq we've already absorbed. The wheel retransmits
        // each catalog chunk multiple times before our ack reaches it (verified
        // 2026-05-09 trace: seqs 5-14 received 3× each). Without dedup, every
        // retransmit gets re-appended, doubling/tripling the buffer for early
        // seqs and corrupting the size-prefixed TLV walk — symptom: tire URLs
        // at indexes 10/11/13/14 parsed as garbage even though the wire bytes
        // were clean per CRC + per-record validation.
        private readonly Dictionary<byte, int> _highestSeqAppended = new();
        private volatile List<string>? _catalog;
        // Cached URL → 1-based idx for the current _catalog. Published
        // together with _catalog and read without locking; FindIdxByUrl is
        // called per string channel per tick (~33 Hz × ~23 channels), so
        // the linear scan it used to do shows up in the hot path.
        private volatile Dictionary<string, int>? _idxByUrl;
        private volatile int _lastActivityMs;
        private int _lastParseLen;
        // u32 value of the most-recently-seen wheel-side END marker
        // (tag 0x06 size 0x00000004 value u32) inside a catalog push.
        // PitHouse echoes this exact value as the END marker of its own
        // tier-def emissions — the wheel uses it as a tier-def version
        // handshake; non-matching END = treated as duplicate / unbound
        // (verified 2026-05-17 from sim/logs/bridge-20260517-070054.jsonl:
        // wheel END=42 → PH tier-def END=42; wheel END=43 → PH tier-def
        // END=43; wheel END=68 → PH retransmits END=68 each time).
        // Updated inside AppendChunkIfNew when a 0x06 marker is observed.
        private volatile uint _lastWheelEndMarker;
        // Tick when the END marker last changed value. Useful for "wait
        // until the wheel has emitted its post-switch END" gating.
        private volatile int _lastWheelEndMarkerTickMs;

        // Live-set tracking: dashboard switches don't drop stale idxs from
        // _catalog (preserving prior bindings is required for back-ref
        // resolution under the wire protocol — see the parser's class
        // docs). For consumers that need "ONLY the channels in the wheel's
        // currently-loaded dashboard" (catalog-only profile synthesis,
        // tier-def channel filter), we additionally track which idxs the
        // wheel touched in the current END-marker generation and publish
        // a masked view of the catalog. _pendingIdxs accumulates each idx
        // (full URL or back-ref) parsed since the last END marker bump;
        // when LastWheelEndMarker advances, _liveCatalog is rebuilt as
        // _catalog with non-pending positions blanked, and _pendingIdxs is
        // cleared for the next generation.
        private readonly HashSet<int> _pendingIdxs = new HashSet<int>();
        private volatile List<string>? _liveCatalog;
        // END-marker value at last commit. Diagnostic only (used in log).
        private uint _committedEndMarker;
        // Set of every markerValue that has already triggered a commit
        // since session start. Per the docs (session-02-channel-catalog.md
        // §"Back-references and END-marker generations"), the wheel emits:
        //   (a) keepalive ENDs at the CURRENT generation's marker (same
        //       value as last commit — handled by the markerValue ==
        //       _committedEndMarker fast path);
        //   (b) historical re-affirmation ENDs at PRIOR generations'
        //       markers, with full-URL records before them re-declaring
        //       the prior dashboard's idxs (so the wheel's back-ref table
        //       stays consistent on the host). These come at DIFFERENT
        //       markerValues from the current generation but are NOT real
        //       switches — they're the wheel replaying past mappings.
        // The doc explicitly states "switch END markers bump to a new
        // value", so any markerValue we've already committed in this
        // session cannot be a real switch — it must be re-affirmation.
        // Tracking the full set lets us drop case (b) cleanly; the prior
        // single-uint check only caught case (a) and let (b) overwrite
        // _liveCatalog with stale prior-dashboard idxs (observed
        // 2026-05-24 on slot=1→slot=0 contracting switches: parse pass
        // produced end=24→3 liveIdxs={1,2,3} followed by end=3→24
        // liveIdxs={1..18}, leaving _liveCatalog with the 18 ETS2-ATS
        // idxs instead of the 3 Simple Rally idxs).
        private readonly HashSet<uint> _committedMarkers = new HashSet<uint>();

        /// <summary>u32 value of the most-recently-seen wheel-side END
        /// marker (tag 0x06) in a catalog push. PitHouse echoes this in
        /// its own tier-def END markers as a version handshake — see
        /// field docs.</summary>
        public uint LastWheelEndMarker => _lastWheelEndMarker;
        public int LastWheelEndMarkerTickMs => _lastWheelEndMarkerTickMs;

        /// <summary>Latest parsed catalog (idx-1 positional, "" for unannounced
        /// gaps). Null until the first parse succeeds. Reads are volatile.
        /// Contains URLs from every dashboard the wheel has ever loaded in
        /// the current session — stale idxs from prior dashes remain so
        /// back-ref resolution keeps working. Callers that need the
        /// current dash's URLs ONLY should use <see cref="LiveCatalog"/>.</summary>
        public IReadOnlyList<string>? Catalog => _catalog;

        /// <summary>Catalog filtered to the wheel's most-recently-committed
        /// END-marker generation: same positional shape as
        /// <see cref="Catalog"/>, but slots not touched in the current
        /// generation are blanked (empty strings). Null until the first
        /// END marker bump observes a non-empty pending set. Use this for
        /// tier-def synthesis and channel-mapping UI — drops stale idxs
        /// from prior dashes after a switch.</summary>
        public IReadOnlyList<string>? LiveCatalog => _liveCatalog;

        /// <summary>Channel count in the latest catalog, or 0 if none.</summary>
        public int Count => _catalog?.Count ?? 0;

        /// <summary>URL → 1-based idx for the current catalog (first-occurrence
        /// wins: stale higher slots ignored after a dashboard switch re-indexes
        /// from 1). Null until the first parse succeeds. Suitable for callers
        /// that need to look up many URLs at once.</summary>
        public IReadOnlyDictionary<string, int>? IdxByUrl => _idxByUrl;

        /// <summary>Build the first-occurrence-wins URL → 1-based idx map from
        /// an arbitrary catalog list. Used by callers that receive the catalog
        /// as a parameter rather than going through the parser instance.</summary>
        public static Dictionary<string, int> BuildIdxByUrl(IReadOnlyList<string> catalog)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (catalog == null) return map;
            for (int i = 0; i < catalog.Count; i++)
            {
                string url = catalog[i];
                if (!string.IsNullOrEmpty(url) && !map.ContainsKey(url))
                    map[url] = i + 1;
            }
            return map;
        }

        /// <summary>Look up a URL's wheel-firmware-canonical channel idx
        /// (1-based) in the latest catalog. Returns -1 if the catalog isn't
        /// populated yet or the URL isn't in it. Case-insensitive match.</summary>
        public int FindIdxByUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return -1;
            var map = _idxByUrl;
            if (map == null) return -1;
            return map.TryGetValue(url, out var ix) ? ix : -1;
        }

        /// <summary>Environment.TickCount of the last AppendChunkIfNew call.
        /// Used by quiet-window waits.</summary>
        public int LastActivityMs => _lastActivityMs;

        /// <summary>Total buffer size across all sessions (for diagnostics and
        /// the "did the buffer grow since last parse?" check). The maximum
        /// individual session buffer size is exposed via
        /// <see cref="MaxSessionBufferLength"/> for overflow guards.</summary>
        public int BufferLength
        {
            get
            {
                lock (_bufferLock)
                {
                    int total = 0;
                    foreach (var s in _sessionOrder)
                        if (_buffersBySession.TryGetValue(s, out var b))
                            total += b.Count;
                    return total;
                }
            }
        }

        /// <summary>Largest individual session buffer size. Used by the
        /// overflow-clear guard so end-marker spam on one session doesn't
        /// trigger a full wipe of another session that still has unparsed
        /// catalog records.</summary>
        public int MaxSessionBufferLength
        {
            get
            {
                lock (_bufferLock)
                {
                    int max = 0;
                    foreach (var s in _sessionOrder)
                        if (_buffersBySession.TryGetValue(s, out var b) && b.Count > max)
                            max = b.Count;
                    return max;
                }
            }
        }

        /// <summary>Seq-aware append: only adds the chunk if seq is greater
        /// than the highest-seen seq for this session. Drops retransmits
        /// silently. Returns true on append, false on dedup.</summary>
        public bool AppendChunkIfNew(byte session, int seq, byte[] chunkBytes, int offset, int length)
        {
            if (chunkBytes == null || length <= 0) return false;
            bool firstChunkOnSession;
            lock (_bufferLock)
            {
                if (_highestSeqAppended.TryGetValue(session, out int prevSeq) && seq <= prevSeq)
                    return false;
                _highestSeqAppended[session] = seq;
                if (!_buffersBySession.TryGetValue(session, out var buf))
                {
                    buf = new List<byte>();
                    _buffersBySession[session] = buf;
                    _sessionOrder.Add(session);
                    firstChunkOnSession = true;
                }
                else
                {
                    firstChunkOnSession = false;
                }
                for (int i = 0; i < length; i++)
                    buf.Add(chunkBytes[offset + i]);
                // Scan the FULL session buffer (not just the new chunk —
                // the END marker may straddle chunk boundaries) for the
                // most recent tag 0x06 size=4 record and capture its u32
                // value. Iterate forward so the LAST seen wins.
                //
                // Compute the scan's final value FIRST, then compare against
                // the prior _lastWheelEndMarker once. The old in-place mutate
                // (set _lastWheelEndMarker inside the loop, stamp the tick
                // on every difference) caused spurious tick updates whenever
                // the buffer contained multiple historical END markers from
                // re-affirmation pushes (e.g., 340 earlier, 416 later, both
                // already seen). Each visit alternated _lastWheelEndMarker
                // between values, so even though the final value was
                // unchanged, the tick stamped at scan time — defeating the
                // HotSwitchCoordinator's "newEndSinceArm" gate (verified
                // 2026-05-25: first hot-switch emission fired at sinceArm=232ms
                // with END value unchanged because the in-loop update
                // re-stamped the tick on the historical 340→416 transition
                // inside one scan).
                uint scanFinalEnd = _lastWheelEndMarker;
                int bcnt = buf.Count;
                for (int ci = 0; ci + 8 < bcnt; ci++)
                {
                    if (buf[ci] != 0x06) continue;
                    if (buf[ci + 1] != 0x04 || buf[ci + 2] != 0 || buf[ci + 3] != 0 || buf[ci + 4] != 0) continue;
                    scanFinalEnd = (uint)(buf[ci + 5]
                        | (buf[ci + 6] << 8)
                        | (buf[ci + 7] << 16)
                        | (buf[ci + 8] << 24));
                }
                if (scanFinalEnd != _lastWheelEndMarker)
                {
                    _lastWheelEndMarker = scanFinalEnd;
                    _lastWheelEndMarkerTickMs = Environment.TickCount;
                }
            }
            _lastActivityMs = Environment.TickCount;
            if (firstChunkOnSession)
            {
                // One-shot log per session-first-use so SimHub.txt records which
                // session(s) the wheel chose for the catalog without needing a
                // wire trace. Cheap (one debug line per session per session-cycle).
                MozaLog.Debug(
                    $"[Moza] Catalog parser: first chunk on sess=0x{session:X2} seq={seq} len={length}");
            }
            return true;
        }

        /// <summary>Reset the per-session buffers (typically on session restart
        /// or before a forced reparse). Does NOT clear the parsed catalog — the
        /// wheel relies on prior idx→URL bindings for back-references after a
        /// dash switch.</summary>
        public void ClearBuffer()
        {
            lock (_bufferLock)
            {
                _buffersBySession.Clear();
                _sessionOrder.Clear();
                // Drop seq-dedup tracking too — fresh buffer means fresh
                // session, retransmit memory should not bleed across.
                _highestSeqAppended.Clear();
            }
            _lastParseLen = 0;
        }

        /// <summary>Hard-limit safety net: wipe any session buffer that
        /// individually exceeds <paramref name="maxPerSession"/> bytes.
        ///
        /// Proactive trimming inside <see cref="TryParse"/> (drop bytes up
        /// to and including the last committed END marker) keeps the buffer
        /// bounded to in-flight bytes in normal operation, so this method
        /// should never fire — it exists only as a defence against
        /// pathological scenarios where the wheel never sends a new END
        /// marker (no committed boundary to trim against). Set the limit
        /// well above the largest plausible in-flight generation so a
        /// long but legitimate burst doesn't get wiped.
        ///
        /// IMPORTANT: this is destructive. Any in-flight (uncommitted)
        /// bytes in the wiped session are lost — the wheel will need to
        /// re-send the generation for the host to catch up. Per-session
        /// seq-dedup entries are dropped so the resend isn't rejected as
        /// a retransmit.
        ///
        /// Returns the number of sessions wiped.</summary>
        public int ClearOverflowingSessions(int maxPerSession)
        {
            int cleared = 0;
            lock (_bufferLock)
            {
                // Walk a snapshot of session keys since we may remove entries.
                for (int i = _sessionOrder.Count - 1; i >= 0; i--)
                {
                    byte sess = _sessionOrder[i];
                    if (!_buffersBySession.TryGetValue(sess, out var buf)) continue;
                    if (buf.Count <= maxPerSession) continue;
                    int wiped = buf.Count;
                    _buffersBySession.Remove(sess);
                    _sessionOrder.RemoveAt(i);
                    _highestSeqAppended.Remove(sess);
                    cleared++;
                    MozaLog.Warn(
                        $"[Moza] Catalog parser: HARD-LIMIT wipe sess=0x{sess:X2} ({wiped} bytes > {maxPerSession}) — " +
                        "proactive trim failed to bound this session (no committed END marker in buffer). " +
                        "Any in-flight catalog data is LOST; wheel must re-send to recover.");
                }
                if (cleared > 0)
                    _lastParseLen = 0;
            }
            return cleared;
        }

        /// <summary>Drop bytes from the front of each session buffer up to
        /// and including the latest END marker whose value is already in
        /// <see cref="_committedMarkers"/>. Called at the end of every
        /// <see cref="TryParse"/> pass to keep buffers bounded to in-flight
        /// content only.
        ///
        /// Safety: the back-ref handler resolves idx → URL from
        /// <see cref="_catalog"/> (the merged historical map), NOT from the
        /// buffer, so dropping the original tag-0x04 / tag-0x06 record
        /// bytes does not break future back-ref resolution. The
        /// <see cref="_committedMarkers"/> set keeps us idempotent: any
        /// already-committed END marker the wheel re-sends as a keepalive
        /// is still dropped (correctly) on the next parse.
        ///
        /// Bytes AFTER the latest committed END (in-flight content for the
        /// next generation that hasn't yet been bounded by a NEW END
        /// marker) are preserved.
        ///
        /// If no committed END marker is present in a session's buffer,
        /// nothing is trimmed for that session — there is no safe boundary,
        /// and all bytes are potentially in-flight content the parser will
        /// need on a future pass.</summary>
        private void TrimProcessedBytes()
        {
            lock (_bufferLock)
            {
                int newTotal = 0;
                foreach (var sess in _sessionOrder)
                {
                    if (!_buffersBySession.TryGetValue(sess, out var buf) || buf.Count == 0)
                        continue;

                    // Forward scan: find offset of byte immediately AFTER
                    // the latest END marker (0x06 0x04 0x00 0x00 0x00 u32)
                    // whose value is in _committedMarkers. Iterating forward
                    // and overwriting `trimTo` means the last match wins,
                    // matching the "latest committed END" requirement.
                    int trimTo = 0;
                    int n = buf.Count;
                    for (int i = 0; i + 8 < n; i++)
                    {
                        if (buf[i] != 0x06) continue;
                        if (buf[i + 1] != 0x04
                            || buf[i + 2] != 0
                            || buf[i + 3] != 0
                            || buf[i + 4] != 0) continue;
                        uint val = (uint)(buf[i + 5]
                            | (buf[i + 6] << 8)
                            | (buf[i + 7] << 16)
                            | (buf[i + 8] << 24));
                        if (_committedMarkers.Contains(val))
                            trimTo = i + 9; // byte immediately past this END
                    }
                    if (trimTo > 0)
                        buf.RemoveRange(0, trimTo);
                    newTotal += buf.Count;
                }
                // Re-stamp _lastParseLen so the "buffer grew since last
                // parse" trigger in TickAbsorbCatalogIfChanged compares
                // against the POST-trim total rather than the pre-trim one
                // (which would otherwise look like a buffer shrink and
                // suppress the next parse even if new bytes arrive).
                _lastParseLen = newTotal;
            }
        }

        /// <summary>Reset both buffers AND parsed catalog. Use only on full session
        /// restart (StartInner) where prior bindings are stale.</summary>
        public void Reset()
        {
            ClearBuffer();
            _catalog = null;
            _idxByUrl = null;
            _lastActivityMs = 0;
            _liveCatalog = null;
            _committedEndMarker = 0;
            _committedMarkers.Clear();
            _pendingIdxs.Clear();
        }

        /// <summary>Returns the total buffer length (across sessions) the last
        /// time TryParse observed it. Caller compares against
        /// <see cref="BufferLength"/> to detect "buffer grew since last parse"
        /// without locking.</summary>
        public int LastParsedBufferLen => _lastParseLen;

        /// <summary>
        /// Scan any session's buffer for a complete catalog: ≥3 plausible 0x04 URL
        /// entries followed by a 0x06 end-marker. Used by the post-startup
        /// quiet-window wait to know when to stop polling. Returns true if any
        /// session's buffer contains a complete catalog.
        /// </summary>
        public bool HasCompleteCatalog()
        {
            lock (_bufferLock)
            {
                foreach (var s in _sessionOrder)
                {
                    if (!_buffersBySession.TryGetValue(s, out var buf)) continue;
                    int cnt = buf.Count;
                    if (cnt <= 30) continue;
                    int urlCount = 0;
                    for (int ci = 0; ci + 5 <= cnt; ci++)
                    {
                        byte b = buf[ci];
                        if (b == 0x04)
                        {
                            uint sz = (uint)(buf[ci + 1]
                                | (buf[ci + 2] << 8)
                                | (buf[ci + 3] << 16)
                                | (buf[ci + 4] << 24));
                            if (sz >= 1 && sz < 200 && ci + 5 + (int)sz <= cnt)
                            {
                                urlCount++;
                                ci += 4 + (int)sz;
                            }
                        }
                        else if (b == 0x06 && urlCount >= 3)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Parse the wheel's channel catalog from buffered 7c:00 session data. The
        /// wheel sends tag 0x04 entries with channel URLs during the preamble.
        /// Each session's buffer is parsed independently and successfully-parsed
        /// records are merged into the shared <see cref="Catalog"/>; no-op if no
        /// buffer contains URL records.
        /// </summary>
        public void TryParse()
        {
            // Snapshot all session buffers under lock, in arrival order, then
            // release the lock before parsing (parsing is purely CPU work on
            // the snapshots and shouldn't block appends).
            List<(byte session, byte[] bytes)> snapshots;
            int totalBytes;
            lock (_bufferLock)
            {
                if (_sessionOrder.Count == 0) return;
                snapshots = new List<(byte, byte[])>(_sessionOrder.Count);
                totalBytes = 0;
                foreach (var s in _sessionOrder)
                {
                    if (!_buffersBySession.TryGetValue(s, out var buf) || buf.Count == 0) continue;
                    snapshots.Add((s, buf.ToArray()));
                    totalBytes += buf.Count;
                }
            }
            _lastParseLen = totalBytes;
            if (snapshots.Count == 0) return;

            // Clear pending at start: the parse loop walks the whole buffer
            // each pass and rebuilds pending in byte order. Carrying pending
            // across passes would let URLs from an in-flight (uncommitted)
            // generation pollute the FIRST commit of the next pass — that
            // commit would erroneously include them in a prior generation's
            // live set. TrimProcessedBytes at the end of each parse keeps
            // post-commit bytes out of the buffer, but pre-commit in-flight
            // bytes are preserved (they sit after the latest committed END
            // marker) — those get re-parsed on the next pass and accumulate
            // correctly into a fresh pending; when their END marker
            // eventually lands they commit cleanly without contamination
            // from prior carry-over.
            _pendingIdxs.Clear();

            var parsed = new Dictionary<int, string>();
            // Per-session diagnostic counters; aggregated at end for the
            // summary log line, but also emitted per-session below so
            // record-loss on one specific session is visible.
            var perSessionStats = new List<(byte session, int full, int prefix, int abbr,
                int backref, int backrefFail, int sizeReject, int plausReject,
                int distinctIdxBefore, int distinctIdxAfter)>();
            int totalFull = 0, totalPrefix = 0, totalAbbr = 0;
            int totalBackref = 0, totalBackrefFail = 0;
            int totalSizeReject = 0, totalPlausReject = 0;
            var existingCatalog = _catalog;

            foreach (var (session, buffer) in snapshots)
            {
                // Pre-scan: any tag=0x04 URL records OR tag=0x06 END markers
                // present? Skip the (expensive) parse + diag logging when the
                // buffer has neither.
                //
                // END markers MUST trigger the walk even without URL records:
                // a chunk that only carries a 0x06 marker still needs to be
                // processed so CommitLiveSet can commit pending content from
                // prior chunks (or, if the marker is new and pending is empty,
                // so subsequent passes accumulate against the correct prior
                // generation). Skipping END-only chunks was the second half
                // of the 2026-05-25 Nebula bug — after the brute-force buffer
                // wipe lost the URL records, the lone END=324 chunk that
                // arrived afterwards never made it into the parse loop and
                // _liveCatalog was frozen on the prior dashboard forever.
                bool hasUrlRecord = false;
                bool hasEndMarker = false;
                for (int b = 0; b + 5 < buffer.Length; b++)
                {
                    if (buffer[b] == 0x04)
                    {
                        uint sz = (uint)(buffer[b + 1] | (buffer[b + 2] << 8)
                                       | (buffer[b + 3] << 16) | (buffer[b + 4] << 24));
                        if (sz >= 1 && sz < 200 && b + 5 + (int)sz <= buffer.Length)
                        {
                            hasUrlRecord = true;
                            break;
                        }
                    }
                    else if (!hasEndMarker
                             && b + 8 < buffer.Length
                             && buffer[b] == 0x06
                             && buffer[b + 1] == 0x04
                             && buffer[b + 2] == 0
                             && buffer[b + 3] == 0
                             && buffer[b + 4] == 0)
                    {
                        hasEndMarker = true;
                        // Don't break — keep looking for URL records so the
                        // diagnostic log line accurately reflects what's
                        // present. URL records short-circuit; END markers
                        // are a fallback.
                    }
                }
                if (!hasUrlRecord && !hasEndMarker) continue;

                int distinctIdxBefore = parsed.Count;
                int sFull = 0, sPrefix = 0, sAbbr = 0;
                int sBackref = 0, sBackrefFail = 0;
                int sSizeReject = 0, sPlausReject = 0;

                // Diagnostic: hex-dump first 128 bytes of this session's buffer.
                // Tagged with session id so diag readers can see whether catalog
                // bytes came from mgmt (0x01) or telemetry (FlagByte) and spot
                // cross-session straddles if they ever happen.
                int dumpLen = Math.Min(buffer.Length, 128);
                var hex = new StringBuilder(dumpLen * 3);
                for (int d = 0; d < dumpLen; d++)
                {
                    if (d > 0) hex.Append('-');
                    hex.Append(buffer[d].ToString("X2"));
                }
                MozaLog.Debug(
                    $"[Moza] Catalog buffer dump sess=0x{session:X2} ({buffer.Length} bytes): {hex}");

                // Scan-forward for `04`-tag URL records. Each record encodes its
                // canonical wheel-firmware idx in the byte at offset i+5 (1-based).
                // Wheel re-indexes URLs per dashboard — same URL gets different idx
                // before/after a dash switch (verified in moza-wire 161929: idx 4 =
                // Gear under Core, idx 4 = TyreTempFrontRight under Grids). Plugin
                // MUST honor the wheel's idx, not parse-order positional.
                //
                // Catalog stored as List<string?> indexed by idx-1; nulls fill
                // unannounced gaps. Merge writes URLs at canonical positions.
                // Live-set commit helper. Called inline whenever the parse
                // loop crosses an END marker (tag 0x06): the records BEFORE
                // the marker belong to one generation, records AFTER belong
                // to the next. By committing at boundaries we cleanly
                // separate the wheel's keepalive re-announcements
                // (back-refs → END → next keepalive) from a real dashboard
                // switch's set, so post-switch live catalogs aren't
                // polluted by stale prior-dash back-refs that landed in
                // _pendingIdxs in the same parse pass.
                // Commit live set for the just-walked END marker.
                // `markerValue` is the u32 value of THIS END marker (taken
                // from buffer[i+5..i+8] at the parse loop's current position),
                // not _lastWheelEndMarker — which holds the LATEST end marker
                // value in the buffer set by the pre-parse wire scan and is
                // wrong for any non-final END in a multi-END buffer.
                //
                // Within a generation, the FIRST commit (markerValue differs
                // from _committedEndMarker) defines that dashboard's channel
                // set. Subsequent commits at the SAME markerValue are
                // current-generation keepalive re-affirmation. Subsequent
                // commits at a DIFFERENT-BUT-ALREADY-SEEN markerValue are
                // PRIOR-GENERATION re-affirmation: the wheel re-declares
                // older dashboards' full-URL mappings (so its back-ref table
                // stays consistent on the host) terminated by the END
                // marker of THAT older generation. Both cases must drop —
                // commits at any previously-committed marker would let
                // stale prior-dashboard idxs overwrite the current
                // dashboard's _liveCatalog. The doc states "switch END
                // markers bump to a new value", so a real switch always
                // brings a markerValue not yet in _committedMarkers.
                // Discarding same-marker bursts also implicitly preserves
                // the FIRST commit's idxs against later subset bursts
                // (e.g. wheel emitting {1,2,3} after first committing
                // {1..9} would otherwise blank channels 4..9 — the
                // dropped-Gear case).
                void CommitLiveSet(uint markerValue)
                {
                    if (_pendingIdxs.Count == 0) return;

                    if (_committedMarkers.Contains(markerValue))
                    {
                        // Either current-generation keepalive (markerValue
                        // == _committedEndMarker) or prior-generation
                        // re-affirmation (markerValue is some earlier
                        // committed marker). Either way the first commit
                        // at this markerValue was authoritative; drop.
                        _pendingIdxs.Clear();
                        return;
                    }

                    var targetIdxs = new HashSet<int>(_pendingIdxs);

                    // No SetEquals dedup here: two real switches CAN produce
                    // identical idx sets with different URL content (e.g.,
                    // dash A uses idxs 1-5 with URLs {Speed, RPM, Gear, Lap,
                    // Fuel}; user switches to dash B which also uses idxs 1-5
                    // but with URLs {SpeedMph, EngineTemp, Gear, BestLap,
                    // FuelPct}). Skipping the commit on idx-set match would
                    // leave _liveCatalog pointing at dash A's URLs even
                    // though the wheel has re-bound those idxs to dash B's
                    // URLs in _catalog (via the full-URL records). The
                    // _committedMarkers.Contains(markerValue) check above
                    // already prevents redundant commits at the SAME end
                    // value; per-pass commits at DIFFERENT end values must
                    // always fire so URL changes propagate.
                    // Build masked positional catalog. URL at each idx
                    // resolves from parsed (latest in this pass) first,
                    // falling back to _catalog (prior generations).
                    int maxIdx = 0;
                    foreach (var ix in targetIdxs) if (ix > maxIdx) maxIdx = ix;
                    int catCount = _catalog?.Count ?? 0;
                    int size = Math.Max(maxIdx, catCount);
                    var masked = new List<string>(size);
                    for (int k = 0; k < size; k++)
                    {
                        int oneIdx = k + 1;
                        if (targetIdxs.Contains(oneIdx))
                        {
                            if (parsed.TryGetValue(oneIdx, out var pu))
                                masked.Add(pu);
                            else if (_catalog != null && k < _catalog.Count)
                                masked.Add(_catalog[k]);
                            else
                                masked.Add("");
                        }
                        else
                        {
                            masked.Add("");
                        }
                    }
                    _liveCatalog = masked;
                    MozaLog.Debug(
                        $"[Moza] Live catalog committed: end={_committedEndMarker}→{markerValue} " +
                        $"liveIdxs={{{string.Join(",", targetIdxs.OrderBy(x => x))}}}");
                    _committedEndMarker = markerValue;
                    _committedMarkers.Add(markerValue);
                    _pendingIdxs.Clear();
                }

                int i = 0;
                while (i + 6 < buffer.Length)
                {
                    byte tag = buffer[i];
                    if (tag == 0x06)
                    {
                        // END marker: tag 0x06, size=4 (LE), u32 value.
                        // Records BEFORE this byte belong to the now-ending
                        // generation; records AFTER belong to the next.
                        if (buffer[i + 1] == 0x04 && buffer[i + 2] == 0
                            && buffer[i + 3] == 0 && buffer[i + 4] == 0)
                        {
                            uint markerValue = (uint)(buffer[i + 5]
                                | (buffer[i + 6] << 8)
                                | (buffer[i + 7] << 16)
                                | (buffer[i + 8] << 24));
                            CommitLiveSet(markerValue);
                            i += 9;
                            continue;
                        }
                    }
                    if (tag != 0x04) { i++; continue; }
                    uint param = (uint)(buffer[i + 1] | (buffer[i + 2] << 8) |
                                 (buffer[i + 3] << 16) | (buffer[i + 4] << 24));
                    if (param < 1 || param >= 200 || i + 5 + (int)param > buffer.Length)
                    {
                        sSizeReject++;
                        i++; continue;
                    }
                    int idx = buffer[i + 5];  // wheel-firmware-canonical idx (1-based)
                    int urlLen = (int)param - 1;
                    int urlStart = i + 6;

                    if (urlLen == 0)
                    {
                        // Backref: payload is just the idx byte. Resolve URL from
                        // the existing catalog at this idx; record the same idx
                        // in our parse map so merge preserves the binding.
                        //
                        // Successful back-refs DO add to _pendingIdxs. Earlier
                        // versions excluded them on the theory that keepalive
                        // bursts re-announce every historical idx via back-ref
                        // and would balloon the live set with stale entries.
                        // That theory was wrong in practice: keepalive bursts
                        // terminate at an ALREADY-COMMITTED end marker, so the
                        // _committedMarkers.Contains(markerValue) drop at the
                        // top of CommitLiveSet clears any pending the keepalive
                        // accumulated before it can affect _liveCatalog.
                        //
                        // Crediting back-refs is REQUIRED for dashboard
                        // switches that re-use existing wheel idxs without
                        // re-announcing them as full URLs (verified 2026-05-25
                        // moza-wire-20260525-204404 trace: Nebula switch sent
                        // back-refs for idxs 5-8 + full URLs for idxs 1-4,
                        // followed by a NEW end marker; the back-ref'd 5-8
                        // were dropped from _pendingIdxs, so the new end
                        // committed only {1..4} instead of {1..8} and the
                        // synthesised profile filter then stripped the back-
                        // ref'd channels as "not in catalog").
                        if (existingCatalog != null && idx >= 1 && idx <= existingCatalog.Count
                            && !string.IsNullOrEmpty(existingCatalog[idx - 1]))
                        {
                            parsed[idx] = existingCatalog[idx - 1];
                            _pendingIdxs.Add(idx);
                            sBackref++;
                        }
                        else
                        {
                            // Cannot resolve to a URL — skip pending so we
                            // don't commit an empty-string slot into the
                            // live set.
                            sBackrefFail++;
                        }
                        i += 5 + (int)param;
                        continue;
                    }

                    // Accepted URL prefixes on the wire:
                    //   "v1/"            literal (no abbreviation)
                    //   0x01             abbreviation for "v1/gameData/"
                    //   0x5C 0x31  "\1"  abbreviation for "v1/gameData/"
                    //   0x5C 0x70  "\p"  abbreviation for "v1/gameData/patch/"
                    // The `\p` form was missing — channels like
                    // `patch/TrackPositionPercent` were emitted by the wheel
                    // as `\pTrackPositionPercent` and rejected here, never
                    // entered _pendingIdxs, and got blanked out of LiveCatalog
                    // on the dashboard-switch commit (the slot was visible to
                    // the wheel but invisible to host synth → channel missing
                    // from the channel-mapping grid and from the live tier-
                    // def we sub back).
                    bool plausible = urlLen >= 3
                        && ((buffer[urlStart] == (byte)'v'
                             && buffer[urlStart + 1] == (byte)'1'
                             && buffer[urlStart + 2] == (byte)'/')
                            || buffer[urlStart] == 0x01
                            || (buffer[urlStart] == 0x5C
                                && urlLen >= 4
                                && (buffer[urlStart + 1] == 0x31
                                    || buffer[urlStart + 1] == 0x70)));
                    if (!plausible) { sPlausReject++; i++; continue; }

                    // Stricter check: validate every byte of the URL body is in
                    // an expected character set. URLs are ASCII printable; the
                    // 0x01 prefix and 0x5C/0x31 abbrev markers are special-cased
                    // but the trailing characters of those forms are still
                    // printable ASCII or the {FL}/{FR}/{RL}/{RR} placeholders.
                    // Without this, a "false 0x04 tag" inside another record's
                    // data — combined with happen-to-pass plausibility on the
                    // first 3 bytes — leaks a corrupt URL into the catalog
                    // (observed 2026-05-09: same Grids switch produced "v1/
                    // gameDa???????t???" deterministically).
                    bool wholeUrlOk = true;
                    int scanStart = urlStart;
                    int scanLen = urlLen;
                    if (buffer[urlStart] == 0x01)
                    {
                        scanStart = urlStart + 1; scanLen = urlLen - 1;
                    }
                    else if (buffer[urlStart] == 0x5C
                             && (buffer[urlStart + 1] == 0x31
                                 || buffer[urlStart + 1] == 0x70))
                    {
                        scanStart = urlStart + 2; scanLen = urlLen - 2;
                    }
                    for (int k = 0; k < scanLen; k++)
                    {
                        byte ch = buffer[scanStart + k];
                        // Allow printable ASCII (0x20..0x7E). Permit tab (0x09)
                        // because some abbreviation expansions retain it briefly.
                        if (ch == 0x09) continue;
                        if (ch < 0x20 || ch > 0x7E) { wholeUrlOk = false; break; }
                    }
                    if (!wholeUrlOk) { sPlausReject++; i++; continue; }
                    string url;
                    if (buffer[urlStart] == 0x01)
                    {
                        url = "v1/gameData/" + Encoding.ASCII.GetString(
                            buffer, urlStart + 1, urlLen - 1);
                        sPrefix++;
                    }
                    else if (buffer[urlStart] == 0x5C && buffer[urlStart + 1] == 0x31)
                    {
                        string suffix = Encoding.ASCII.GetString(
                            buffer, urlStart + 2, urlLen - 2);
                        suffix = suffix
                            .Replace("\\t", "TyreTemp")
                            .Replace("\\P", "TyrePressure")
                            .Replace("{FL}", "FrontLeft")
                            .Replace("{FR}", "FrontRight")
                            .Replace("{RL}", "RearLeft")
                            .Replace("{RR}", "RearRight");
                        url = "v1/gameData/" + suffix;
                        sAbbr++;
                    }
                    else if (buffer[urlStart] == 0x5C && buffer[urlStart + 1] == 0x70)
                    {
                        // \p (0x5C 0x70) abbreviation for "v1/gameData/patch/".
                        // Observed for patch/TrackPositionPercent (track
                        // completion %) and likely other patch/* channels that
                        // Telemetry.json knows about — patch/TrackName,
                        // patch/DisplayTrackName, patch/GameName, etc.
                        url = "v1/gameData/patch/" + Encoding.ASCII.GetString(
                            buffer, urlStart + 2, urlLen - 2);
                        sAbbr++;
                    }
                    else
                    {
                        url = Encoding.ASCII.GetString(buffer, urlStart, urlLen);
                        sFull++;
                    }
                    if (idx >= 1)
                    {
                        parsed[idx] = url;
                        _pendingIdxs.Add(idx);
                    }
                    i += 5 + (int)param;
                }

                int distinctIdxAfter = parsed.Count;
                perSessionStats.Add((session, sFull, sPrefix, sAbbr, sBackref,
                    sBackrefFail, sSizeReject, sPlausReject,
                    distinctIdxBefore, distinctIdxAfter));
                totalFull += sFull; totalPrefix += sPrefix; totalAbbr += sAbbr;
                totalBackref += sBackref; totalBackrefFail += sBackrefFail;
                totalSizeReject += sSizeReject; totalPlausReject += sPlausReject;
            }

            if (perSessionStats.Count > 0)
            {
                // Per-session breakdown — one line per session that contributed URL
                // records. Lets diag readers spot which session lost which records.
                foreach (var s in perSessionStats)
                {
                    int newOnSess = s.distinctIdxAfter - s.distinctIdxBefore;
                    MozaLog.Debug(
                        $"[Moza] Catalog parse sess=0x{s.session:X2}: " +
                        $"full={s.full} prefix={s.prefix} abbr={s.abbr} " +
                        $"backref={s.backref} backrefFail={s.backrefFail} " +
                        $"sizeReject={s.sizeReject} plausReject={s.plausReject} " +
                        $"distinct-idx+={newOnSess}");
                }
                // Aggregate summary line, preserves the pre-split format for
                // anyone grepping for "Catalog parse stats: full=...".
                MozaLog.Debug(
                    $"[Moza] Catalog parse stats: full={totalFull} prefix={totalPrefix} " +
                    $"abbr={totalAbbr} backref={totalBackref} backrefFail={totalBackrefFail} " +
                    $"sizeReject={totalSizeReject} plausReject={totalPlausReject} " +
                    $"distinct-idx={parsed.Count}");
            }

            if (parsed.Count > 0)
            {
                // Build/extend the idx-positional catalog. Latest wheel
                // announcement wins per idx (dashboard switches re-index URLs).
                var prior = _catalog;
                int maxIdx = parsed.Keys.Max();
                int newSize = Math.Max(maxIdx, prior?.Count ?? 0);
                var merged = new List<string>(newSize);
                for (int k = 0; k < newSize; k++)
                {
                    string? entry = (prior != null && k < prior.Count) ? prior[k] : null;
                    if (parsed.TryGetValue(k + 1, out var u)) entry = u;
                    merged.Add(entry ?? "");
                }
                bool changed = prior == null
                    || prior.Count != merged.Count
                    || !prior.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase);
                if (changed)
                {
                    _catalog = merged;
                    _idxByUrl = BuildIdxByUrl(merged);
                    var diff = new StringBuilder();
                    foreach (var kv in parsed.OrderBy(kv => kv.Key))
                    {
                        bool wasDifferent = prior == null
                            || kv.Key - 1 >= prior.Count
                            || !string.Equals(prior[kv.Key - 1], kv.Value, StringComparison.OrdinalIgnoreCase);
                        if (wasDifferent) diff.Append($" [{kv.Key}]={kv.Value}");
                    }
                    MozaLog.Debug(
                        $"[Moza] Wheel channel catalog updated (size {prior?.Count ?? 0}→{merged.Count}):{diff}");
                }
            }

            // Live-set commits happen inline in the parse loop (CommitLiveSet
            // helper above) at each tag-0x06 END marker. By the time we get
            // here, every committed generation has already been published
            // to _liveCatalog. No post-merge commit needed.

            // Proactive buffer trim: drop bytes up to and including the
            // latest END marker we've committed (either just now in this
            // parse pass, or in a prior pass). Keeps each session buffer
            // bounded to in-flight bytes for the next generation. Without
            // this, the append-only buffer grows unbounded as keepalive
            // END markers accumulate, eventually tripping the hard-limit
            // safety wipe which loses any in-flight bytes too. See
            // _committedMarkers / TrimProcessedBytes docs for safety
            // argument.
            //
            // Run unconditionally — committedMarkers may have entries from
            // prior parse passes that older buffer bytes can be trimmed
            // against, even if this particular pass added nothing new.
            TrimProcessedBytes();
        }

        /// <summary>Compare two catalogs for value-equality. Helper used by
        /// renegotiation diagnostics in TelemetrySender.</summary>
        public static bool CatalogEquals(
            IReadOnlyList<string>? a,
            IReadOnlyList<string>? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            return true;
        }
    }
}
