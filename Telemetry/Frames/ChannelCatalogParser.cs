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
        private volatile int _lastActivityMs;
        private int _lastParseLen;

        /// <summary>Latest parsed catalog (idx-1 positional, "" for unannounced
        /// gaps). Null until the first parse succeeds. Reads are volatile.</summary>
        public IReadOnlyList<string>? Catalog => _catalog;

        /// <summary>Channel count in the latest catalog, or 0 if none.</summary>
        public int Count => _catalog?.Count ?? 0;

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

        /// <summary>Clear only the session buffers whose individual size
        /// exceeds <paramref name="maxPerSession"/>. Used by the post-
        /// renegotiate overflow guard so that end-marker spam on one session
        /// doesn't wipe another session's still-unparsed catalog records.
        /// Returns the number of sessions cleared. Per-session seq-dedup map
        /// entries are dropped for cleared sessions so a fresh advert can
        /// re-fill the buffer.</summary>
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
                    MozaLog.Debug(
                        $"[Moza] Catalog parser: overflow-clear sess=0x{sess:X2} ({wiped} bytes > {maxPerSession})");
                }
                if (cleared > 0)
                    _lastParseLen = 0;
            }
            return cleared;
        }

        /// <summary>Reset both buffers AND parsed catalog. Use only on full session
        /// restart (StartInner) where prior bindings are stale.</summary>
        public void Reset()
        {
            ClearBuffer();
            _catalog = null;
            _lastActivityMs = 0;
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
                // Pre-scan: any tag=0x04 records present? Buffer fills with end-
                // marker noise (06 04 ... val) post-renegotiate; parsing every tick
                // is wasted work. Skip + suppress logging unless URL records exist.
                bool hasUrlRecord = false;
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
                }
                if (!hasUrlRecord) continue;

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
                int i = 0;
                while (i + 6 < buffer.Length)
                {
                    byte tag = buffer[i];
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
                        if (existingCatalog != null && idx >= 1 && idx <= existingCatalog.Count
                            && !string.IsNullOrEmpty(existingCatalog[idx - 1]))
                        {
                            parsed[idx] = existingCatalog[idx - 1];
                            sBackref++;
                        }
                        else
                        {
                            sBackrefFail++;
                        }
                        i += 5 + (int)param;
                        continue;
                    }

                    bool plausible = urlLen >= 3
                        && ((buffer[urlStart] == (byte)'v'
                             && buffer[urlStart + 1] == (byte)'1'
                             && buffer[urlStart + 2] == (byte)'/')
                            || buffer[urlStart] == 0x01
                            || (buffer[urlStart] == 0x5C
                                && urlLen >= 4
                                && buffer[urlStart + 1] == 0x31));
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
                    else if (buffer[urlStart] == 0x5C && buffer[urlStart + 1] == 0x31)
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
                    else
                    {
                        url = Encoding.ASCII.GetString(buffer, urlStart, urlLen);
                        sFull++;
                    }
                    if (idx >= 1) parsed[idx] = url;
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

            if (perSessionStats.Count == 0) return;

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
