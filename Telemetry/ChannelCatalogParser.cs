using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Buffers inbound 7c:00 session-data chunk bytes from the wheel and parses out
    /// the channel-URL catalog the wheel advertises during preamble (and after dash
    /// switches). The catalog is the canonical "channel idx → URL" mapping the
    /// firmware uses for tier-def lookups; without it, tier-def channels resolve to
    /// idx=0 and the wheel cannot bind them to display elements.
    ///
    /// The parser is intricate because the wire format encodes URLs in three
    /// different forms (full text, prefix-compressed via 0x01, abbreviation-
    /// compressed via 0x5C 0x31) plus back-references (zero-length records that
    /// refer to a prior URL by idx). Dash switches re-index URLs without
    /// re-announcing all of them, so back-refs are heavy at switch time and the
    /// merge step preserves prior idx→URL bindings unless the wheel overrides them.
    ///
    /// All public methods are thread-safe via an internal lock on the byte buffer.
    /// The parsed catalog is published via a volatile field so observers see
    /// monotonically-grown lists without external synchronisation.
    /// </summary>
    internal sealed class ChannelCatalogParser
    {
        private readonly object _bufferLock = new object();
        private readonly List<byte> _buffer = new();
        // Per-session highest seq we've already absorbed into _buffer. The
        // wheel retransmits each catalog chunk multiple times before our ack
        // reaches it (verified 2026-05-09 trace: seqs 5-14 received 3× each).
        // Without dedup, every retransmit gets re-appended, doubling/tripling
        // the buffer for early seqs and corrupting the size-prefixed TLV walk
        // — symptom: tire URLs at indexes 10/11/13/14 parsed as garbage even
        // though the wire bytes were clean per CRC + per-record validation.
        private readonly Dictionary<byte, int> _highestSeqAppended = new();
        private volatile List<string>? _catalog;
        private volatile int _lastActivityMs;
        private int _lastParseLen;

        /// <summary>Latest parsed catalog (idx-1 positional, "" for unannounced
        /// gaps). Null until the first parse succeeds. Reads are volatile.</summary>
        public IReadOnlyList<string>? Catalog => _catalog;

        /// <summary>Channel count in the latest catalog, or 0 if none.</summary>
        public int Count => _catalog?.Count ?? 0;

        /// <summary>Environment.TickCount of the last AppendChunk call. Used by
        /// quiet-window waits.</summary>
        public int LastActivityMs => _lastActivityMs;

        /// <summary>Current buffer size in bytes (for diagnostics).</summary>
        public int BufferLength
        {
            get { lock (_bufferLock) return _buffer.Count; }
        }

        /// <summary>Append inbound chunk bytes to the rolling buffer. Records
        /// activity timestamp so quiet-window waits can detect end-of-burst.
        ///
        /// Legacy seq-unaware overload — use the (session, seq, …) variant
        /// when possible to dedup retransmits.</summary>
        public void AppendChunk(byte[] chunkBytes, int offset, int length)
        {
            if (chunkBytes == null || length <= 0) return;
            lock (_bufferLock)
            {
                for (int i = 0; i < length; i++)
                    _buffer.Add(chunkBytes[offset + i]);
            }
            _lastActivityMs = Environment.TickCount;
        }

        /// <summary>Seq-aware append: only adds the chunk if seq is greater
        /// than the highest-seen seq for this session. Drops retransmits
        /// silently. Returns true on append, false on dedup.</summary>
        public bool AppendChunkIfNew(byte session, int seq, byte[] chunkBytes, int offset, int length)
        {
            if (chunkBytes == null || length <= 0) return false;
            lock (_bufferLock)
            {
                if (_highestSeqAppended.TryGetValue(session, out int prevSeq) && seq <= prevSeq)
                    return false;
                _highestSeqAppended[session] = seq;
                for (int i = 0; i < length; i++)
                    _buffer.Add(chunkBytes[offset + i]);
            }
            _lastActivityMs = Environment.TickCount;
            return true;
        }

        /// <summary>Append a single byte. Helper for TelemetrySender.OnMessageDuringPreamble
        /// which iterates the chunk payload byte-by-byte.</summary>
        public void AppendByte(byte b)
        {
            lock (_bufferLock) _buffer.Add(b);
            _lastActivityMs = Environment.TickCount;
        }

        /// <summary>Reset the buffer (typically on session restart or before a
        /// forced reparse). Does NOT clear the parsed catalog — the wheel relies
        /// on prior idx→URL bindings for back-references after a dash switch.</summary>
        public void ClearBuffer()
        {
            lock (_bufferLock)
            {
                _buffer.Clear();
                // Drop seq-dedup tracking too — fresh buffer means fresh
                // session, retransmit memory should not bleed across.
                _highestSeqAppended.Clear();
            }
            _lastParseLen = 0;
        }

        /// <summary>Reset both buffer AND parsed catalog. Use only on full session
        /// restart (StartInner) where prior bindings are stale.</summary>
        public void Reset()
        {
            ClearBuffer();
            _catalog = null;
            _lastActivityMs = 0;
        }

        /// <summary>Returns the current buffer length the last time TryParse
        /// observed it. Caller compares against <see cref="BufferLength"/> to
        /// detect "buffer grew since last parse" without locking.</summary>
        public int LastParsedBufferLen => _lastParseLen;

        /// <summary>
        /// Scan the buffer for a complete catalog: ≥3 plausible 0x04 URL entries
        /// followed by a 0x06 end-marker. Used by the post-startup quiet-window
        /// wait to know when to stop polling.
        /// </summary>
        public bool HasCompleteCatalog()
        {
            lock (_bufferLock)
            {
                int cnt = _buffer.Count;
                if (cnt <= 30) return false;
                int urlCount = 0;
                for (int ci = 0; ci + 5 <= cnt; ci++)
                {
                    byte b = _buffer[ci];
                    if (b == 0x04)
                    {
                        uint sz = (uint)(_buffer[ci + 1]
                            | (_buffer[ci + 2] << 8)
                            | (_buffer[ci + 3] << 16)
                            | (_buffer[ci + 4] << 24));
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
                return false;
            }
        }

        /// <summary>
        /// Parse the wheel's channel catalog from buffered 7c:00 session data. The
        /// wheel sends tag 0x04 entries with channel URLs during the preamble.
        /// Updates <see cref="Catalog"/> if new URLs were observed; no-op if the
        /// buffer contains no URL records.
        /// </summary>
        public void TryParse()
        {
            byte[] buffer;
            lock (_bufferLock)
            {
                if (_buffer.Count == 0) return;
                buffer = _buffer.ToArray();
                _lastParseLen = _buffer.Count;
            }

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
            if (!hasUrlRecord) return;

            // Diagnostic: hex-dump first 128 bytes of catalog buffer
            int dumpLen = Math.Min(buffer.Length, 128);
            var hex = new StringBuilder(dumpLen * 3);
            for (int d = 0; d < dumpLen; d++)
            {
                if (d > 0) hex.Append('-');
                hex.Append(buffer[d].ToString("X2"));
            }
            MozaLog.Debug($"[Moza] Catalog buffer dump ({buffer.Length} bytes): {hex}");

            // Scan-forward for `04`-tag URL records. Each record encodes its
            // canonical wheel-firmware idx in the byte at offset i+5 (1-based).
            // Wheel re-indexes URLs per dashboard — same URL gets different idx
            // before/after a dash switch (verified in moza-wire 161929: idx 4 =
            // Gear under Core, idx 4 = TyreTempFrontRight under Grids). Plugin
            // MUST honor the wheel's idx, not parse-order positional.
            //
            // Catalog stored as List<string?> indexed by idx-1; nulls fill
            // unannounced gaps. Merge writes URLs at canonical positions.
            var parsed = new Dictionary<int, string>();
            int i = 0;
            int diagFullUrl = 0, diagPrefixUrl = 0, diagAbbrUrl = 0;
            int diagBackRef = 0, diagBackRefFail = 0;
            int diagSizeReject = 0, diagPlausReject = 0;
            var existingCatalog = _catalog;
            while (i + 6 < buffer.Length)
            {
                byte tag = buffer[i];
                if (tag != 0x04) { i++; continue; }
                uint param = (uint)(buffer[i + 1] | (buffer[i + 2] << 8) |
                             (buffer[i + 3] << 16) | (buffer[i + 4] << 24));
                if (param < 1 || param >= 200 || i + 5 + (int)param > buffer.Length)
                {
                    diagSizeReject++;
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
                        diagBackRef++;
                    }
                    else
                    {
                        diagBackRefFail++;
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
                if (!plausible) { diagPlausReject++; i++; continue; }

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
                if (!wholeUrlOk) { diagPlausReject++; i++; continue; }
                string url;
                if (buffer[urlStart] == 0x01)
                {
                    url = "v1/gameData/" + Encoding.ASCII.GetString(
                        buffer, urlStart + 1, urlLen - 1);
                    diagPrefixUrl++;
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
                    diagAbbrUrl++;
                }
                else
                {
                    url = Encoding.ASCII.GetString(buffer, urlStart, urlLen);
                    diagFullUrl++;
                }
                if (idx >= 1) parsed[idx] = url;
                i += 5 + (int)param;
            }
            MozaLog.Debug(
                $"[Moza] Catalog parse stats: full={diagFullUrl} prefix={diagPrefixUrl} " +
                $"abbr={diagAbbrUrl} backref={diagBackRef} backrefFail={diagBackRefFail} " +
                $"sizeReject={diagSizeReject} plausReject={diagPlausReject} " +
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
