using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Sessions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Session 0x09 configJson RPC. Device pushes a compressed JSON state blob
    /// listing enabled / disabled dashboards; host replies with a compressed
    /// `configJson()` object whose `dashboards` array is PitHouse's canonical
    /// library (dashboards the host could upload on demand).
    ///
    /// Without at least one round-trip on this session, PitHouse's Dashboard
    /// Manager UI stays empty even if mzdash uploads succeeded — the UI
    /// reads the wheel's reported state, not its own upload log.
    /// </summary>
    public sealed class ConfigJsonClient
    {
        private readonly SessionDataReassembler _deviceInbox = new();
        private WheelDashboardState? _lastState;

        // Cross-instance cache. The wheel's dashboard library (ConfigJsonList,
        // EnabledDashboards) is stable across plugin reloads in the same SimHub
        // process, so a successful parse in any prior instance is still valid
        // until the wheel is physically swapped. Without this fallback, a
        // sess=0x09 chunk drop on cold-start leaves _lastState null forever
        // for that plugin instance — and that's how the wheel ends up stuck
        // on the wrong dashboard after a game switch, because neither the
        // catalog re-sync probe nor ApplyTelemetryDashboardFromProfile can
        // resolve a wheel:<id> key to a slot without it.
        //
        // Reset by HardReset() — wheel hot-swap, schema upgrade, dispose.
        //
        // One slot per target device: the wheel sender and the CM2 sender each
        // own a ConfigJsonClient, and a single shared slot let one device's
        // TitleId=4 delta merge onto the other's library (and HardReset wipe the
        // other lane). Array element refs are atomic, so readers need no lock.
        private static readonly WheelDashboardState?[] s_cachedByDevice = new WheelDashboardState?[256];

        /// <summary>Target device id (0x17 wheel, 0x14/0x12 CM2) that selects this
        /// client's cross-instance cache slot. Set by the owning sender.</summary>
        public byte CacheKey { get; set; } = MozaProtocol.DeviceWheel;

        private WheelDashboardState? CachedLastState
        {
            get => System.Threading.Volatile.Read(ref s_cachedByDevice[CacheKey]);
            set => System.Threading.Volatile.Write(ref s_cachedByDevice[CacheKey], value);
        }

        /// <summary>Most recent dashboard state parsed from the device.
        /// Falls back to the cross-instance cache when this instance hasn't
        /// completed a parse yet (e.g. mid-cold-start, or a chunk-drop
        /// rendered this session's parse impossible). Callers that need
        /// only library data (ConfigJsonList, EnabledDashboards id→name)
        /// get usable answers; callers that need fresh slot state should
        /// observe TelemetrySender.WheelReportedSlot instead.</summary>
        public WheelDashboardState? LastState => _lastState ?? CachedLastState;

        /// <summary>State decoded from THIS session's wheel push, with NO
        /// cross-session cache fallback (unlike <see cref="LastState"/>). Null until
        /// the wheel actually pushes its configJson on 0x09 this connection — the
        /// signal the sess=0x09 retry needs so it keeps re-requesting until the real
        /// list lands rather than stopping on a stale cache.</summary>
        internal WheelDashboardState? LiveState => _lastState;

        /// <summary>Highest contiguous-received chunk seq, suitable for use
        /// as a cumulative session-ack value. -1 before the first chunk.
        /// Proxy to the internal <see cref="SessionDataReassembler.HighWaterSeq"/>
        /// so the TelemetrySender ack path can stay decoupled from this
        /// client's reassembly state.</summary>
        public int HighWaterSeq => _deviceInbox.HighWaterSeq;

        /// <summary>UTC ticks of the most recent forward gap on the device
        /// inbox, or 0 if none observed. Used by the gap-recovery watchdog
        /// to decide when to escalate from passive (wait for wheel auto-
        /// retransmit) to active (prime+open-request).</summary>
        public long LastForwardGapUtcTicks => _deviceInbox.LastForwardGapUtcTicks;

        /// <summary>Result of a seq-aware <see cref="OnChunk(int, byte[])"/> call.</summary>
        public enum ChunkResult
        {
            /// <summary>Chunk accepted; no complete state yet (more chunks expected).</summary>
            Buffered,
            /// <summary>State decoded — caller can read <see cref="LastState"/>.</summary>
            StateReady,
            /// <summary>Seq gap detected: a prior chunk was dropped on the wire.
            /// Buffer has been cleared. Caller must re-issue the configJson open
            /// request so the wheel re-emits its state burst — otherwise the
            /// handshake is permanently broken for this session lifetime.</summary>
            GapDetected,
        }

        /// <summary>Feed one session 0x09 device→host chunk payload.</summary>
        public WheelDashboardState? OnChunk(byte[] chunkPayload)
        {
            _deviceInbox.AddChunk(chunkPayload);
            byte[]? decomp = _deviceInbox.TryDecompress();
            if (decomp == null) return null;
            var state = WheelStateParser.Parse(decomp, out var missing);
            if (state != null)
            {
                // TitleId=4 pushes are deltas — merge into the prior state so
                // the cached inventory doesn't collapse to the last delta.
                state = WheelDashboardState.Merge(_lastState ?? CachedLastState, state);
                _lastState = state;
                CachedLastState = state;
                // Consume the decoded blob so successive updates (e.g. after
                // a dashboard change) can reassemble a fresh one.
                _deviceInbox.Clear();
                try
                {
                    MozaLog.Debug(
                        $"[AZOM] configJson state received: TitleId={state.TitleId} " +
                        $"displayVersion={state.DisplayVersion} resetVersion={state.ResetVersion} " +
                        $"configJsonList={state.ConfigJsonList.Count} " +
                        $"enabled={state.EnabledDashboards.Count} disabled={state.DisabledDashboards.Count} " +
                        $"imageRefMap={state.ImageRefMap.Count} imagePath={state.ImagePath.Count} " +
                        $"rootDirPath='{state.RootDirPath}'");
                }
                catch { /* logging optional in unit tests */ }
                // Diagnostic: flag firmware-schema drift. 2025-11 firmware
                // emits all 11 top-level fields; older or newer firmware may
                // drop/rename fields and PitHouse's UI flatly rejects the
                // state when any are missing. Log once per distinct shape.
                if (missing.Count > 0)
                {
                    string shape = string.Join(",", missing);
                    if (shape != _lastMissingShape)
                    {
                        _lastMissingShape = shape;
                        try
                        {
                            MozaLog.Debug(
                                $"[AZOM] configJson state missing {missing.Count} expected top-level field(s): {shape}. " +
                                "Firmware may be older than 2025-11 or schema has drifted.");
                        }
                        catch { /* logging optional in unit tests */ }
                    }
                }
            }
            return state;
        }

        private string _lastMissingShape = "";

        /// <summary>
        /// Compact wheel-confirmed deletions out of the cached slot table —
        /// call the moment the wheel's TitleId=4 confirm delta lands
        /// (TelemetrySender.OnConfigJsonStateReadyCheckPendingDelete). The
        /// wheel compacts its own render table (order-preserving) when it
        /// ACCEPTS the host's follow-up list-without-name — mid-session; a
        /// port bounce does NOT rebuild that table (wire-verified 2026-08-17).
        /// Compacting the cache here keeps the host's name→slot mapping in
        /// lockstep with the wheel's imminent compaction.
        /// </summary>
        public void CompactConfirmedDeletes()
        {
            var basis = _lastState ?? CachedLastState;
            if (basis == null || basis.ConfirmedRemovedNames.Count == 0) return;
            var doomed = new HashSet<string>(basis.ConfirmedRemovedNames, StringComparer.Ordinal);
            var table = new List<string>(basis.ConfigJsonList);
            int before = table.Count;
            table.RemoveAll(n => doomed.Contains(n));
            var s = CloneWithList(basis, table);
            s.ConfirmedRemovedNames = Array.Empty<string>();
            _lastState = s;
            CachedLastState = s;
            try
            {
                MozaLog.Info(
                    $"[AZOM] Slot table compacted: {before} → {table.Count} entries " +
                    $"(removed: {string.Join(", ", doomed)})");
            }
            catch { }
        }

        /// <summary>
        /// Apply a host-caused library mutation to the cached slot table. The
        /// wheel maintains its configJsonList itself (ordinal-sorted) and
        /// updates it LIVE on enable/delete — but never re-pushes it
        /// mid-session (full TitleId=1 pushes only happen at connect;
        /// wire-verified 2026-08-16, both plugin and PitHouse sessions). So
        /// the host predicts the wheel's table by applying its OWN deltas to
        /// the wheel-reported list: enabled names insert at their ordinal
        /// position, removed names drop. This is deliberately NOT wholesale
        /// adoption of the declared list (disproven — the wheel's set can
        /// contain entries the enabled-only declaration omits) — only the
        /// names this host just mutated move.
        /// </summary>
        public void ApplyLibraryDelta(string? addName, string? removeName)
        {
            var basis = _lastState ?? CachedLastState;
            if (basis == null) return;
            var list = new List<string>(basis.ConfigJsonList);
            bool changed = false;
            if (!string.IsNullOrEmpty(removeName))
                changed |= list.RemoveAll(n => string.Equals(n, removeName, StringComparison.Ordinal)) > 0;
            if (!string.IsNullOrEmpty(addName)
                && list.FindIndex(n => string.Equals(n, addName, StringComparison.Ordinal)) < 0)
            {
                int at = 0;
                while (at < list.Count && string.CompareOrdinal(list[at], addName) < 0) at++;
                list.Insert(at, addName!);
                changed = true;
            }
            if (!changed) return;
            AdoptList(basis, list);
            try
            {
                MozaLog.Debug(
                    $"[AZOM] Slot-table prediction updated (add='{addName}' remove='{removeName}'): " +
                    $"{list.Count} entries");
            }
            catch { }
        }

        /// <summary>
        /// UNUSED (kept for reference): early 2026-08-16 theory that the wheel
        /// adopts the host's configJson() list as its slot table. Disproven
        /// same day — the wheel maintains its own ordinal-sorted
        /// configJsonList, which can include entries the host's enabled-only
        /// declaration omits; adopting the declared list shifted name→slot
        /// mappings and routed switches to the wrong dash. The wheel-reported
        /// list is the only slot authority; host-caused deltas are applied via
        /// <see cref="ApplyLibraryDelta"/>.
        /// </summary>
        public void AdoptDeclaredLibraryList(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0) return;
            var basis = _lastState ?? CachedLastState;
            if (basis == null) return;
            AdoptList(basis, new List<string>(names));
        }

        /// <summary>Clone <paramref name="basis"/> with a replacement
        /// <see cref="WheelDashboardState.ConfigJsonList"/> and publish it as
        /// the cached state.</summary>
        private void AdoptList(WheelDashboardState basis, List<string> names)
        {
            var s = CloneWithList(basis, names);
            _lastState = s;
            CachedLastState = s;
        }

        private static WheelDashboardState CloneWithList(WheelDashboardState basis, List<string> names)
        {
            return new WheelDashboardState
            {
                TitleId = basis.TitleId,
                DisplayVersion = basis.DisplayVersion,
                ResetVersion = basis.ResetVersion,
                SortTag = basis.SortTag,
                RootDirPath = basis.RootDirPath,
                ConfigJsonList = new List<string>(names),
                EnabledDashboards = basis.EnabledDashboards,
                DisabledDashboards = basis.DisabledDashboards,
                ImageRefMap = basis.ImageRefMap,
                ImagePath = basis.ImagePath,
                FontRefMap = basis.FontRefMap,
                RootPath = basis.RootPath,
                CapturedAt = DateTime.UtcNow,
                EnabledListKind = basis.EnabledListKind,
                DisabledListKind = basis.DisabledListKind,
                EnabledDeletedIds = basis.EnabledDeletedIds,
                DisabledDeletedIds = basis.DisabledDeletedIds,
                ConfirmedRemovedNames = basis.ConfirmedRemovedNames,
            };
        }

        /// <summary>
        /// Seq-aware variant. Detects when a chunk seq is missing and signals the
        /// caller to re-handshake — without this, a single dropped chunk under
        /// Wine SerialPort R/W contention silently corrupts the zlib stream and
        /// the configJson handshake fails for the lifetime of the session.
        /// Returns <see cref="ChunkResult.GapDetected"/> on missing seq, in which
        /// case the buffer has already been cleared and the caller should re-
        /// issue the configJson open request to make the wheel re-emit its burst.
        /// </summary>
        public ChunkResult OnChunk(int seq, byte[] chunkPayload, string tag = "sess=0x09")
        {
            if (!_deviceInbox.AddChunk(seq, chunkPayload, tag))
                return ChunkResult.GapDetected;
            byte[]? decomp = _deviceInbox.TryDecompress();
            if (decomp == null) return ChunkResult.Buffered;
            var state = WheelStateParser.Parse(decomp, out var missing);
            if (state == null) return ChunkResult.Buffered;
            // TitleId=4 pushes are deltas — merge into the prior state so the
            // cached inventory doesn't collapse to the last delta.
            state = WheelDashboardState.Merge(_lastState ?? CachedLastState, state);
            _lastState = state;
            CachedLastState = state;
            _deviceInbox.Clear();
            try
            {
                // Info: the delete/enable acceptance-test monitor line.
                MozaLog.Info(
                    $"[AZOM] configJson state received: TitleId={state.TitleId} " +
                    $"displayVersion={state.DisplayVersion} resetVersion={state.ResetVersion} " +
                    $"configJsonList={state.ConfigJsonList.Count} " +
                    $"enabled={state.EnabledDashboards.Count} disabled={state.DisabledDashboards.Count} " +
                    $"imageRefMap={state.ImageRefMap.Count} imagePath={state.ImagePath.Count} " +
                    $"rootDirPath='{state.RootDirPath}'");
            }
            catch { /* logging optional in unit tests */ }
            if (missing.Count > 0)
            {
                string shape = string.Join(",", missing);
                if (shape != _lastMissingShape)
                {
                    _lastMissingShape = shape;
                    try
                    {
                        MozaLog.Debug(
                            $"[AZOM] configJson state missing {missing.Count} expected top-level field(s): {shape}. " +
                            "Firmware may be older than 2025-11 or schema has drifted.");
                    }
                    catch { /* logging optional in unit tests */ }
                }
            }
            return ChunkResult.StateReady;
        }

        /// <summary>
        /// Clear the in-progress reassembly buffer ONLY. <see cref="LastState"/>
        /// is preserved so a Stop/Start cycle (e.g. dashboard switch) doesn't
        /// drop the dashboard library cache while we're waiting for the wheel
        /// to re-burst. Wire-trace evidence (2026-05-09) showed that a single
        /// b2h chunk drop on sess=0x09 during the post-Start re-burst left the
        /// plugin with no state for the rest of the session — the wheel does
        /// NOT re-burst on a re-issued OpenRequest once it considers the
        /// session initialised, so the only way to repopulate state was a
        /// full plugin restart. Caching the last-good state across Stop/Start
        /// removes that failure mode for any session that has seen at least
        /// one successful burst.
        ///
        /// Use <see cref="HardReset"/> for the full lifecycle reset (plugin
        /// instance dispose / wheel hot-swap).
        /// </summary>
        public void ClearBuffer()
        {
            _deviceInbox.Clear();
        }

        /// <summary>
        /// Full reset: clears reassembly buffer AND <see cref="LastState"/>.
        /// Call only when the cached state is known stale (wheel hot-swap,
        /// plugin instance dispose, mzdash library replacement). Routine
        /// Stop/Start should use <see cref="ClearBuffer"/>.
        /// </summary>
        public void HardReset()
        {
            _deviceInbox.Clear();
            _lastState = null;
            CachedLastState = null;
            _lastMissingShape = "";
        }

        /// <summary>Backwards-compatible alias for <see cref="ClearBuffer"/>.
        /// Old call sites assumed Reset() also cleared LastState; the buffer-
        /// only behaviour is now the safer default. Callers wanting the full
        /// clear should switch to <see cref="HardReset"/>.</summary>
        public void Reset() => ClearBuffer();

        /// <summary>
        /// Build the host's `configJson()` reply. Pass the library names the
        /// host wants to expose to the wheel — PitHouse's 2025-11 capture used
        /// ["Core","Grids","Mono","Nebula","Pulse","Rally V1".."Rally V6"].
        /// The wheel echoes the list back in its next state blob's
        /// <c>configJsonList</c> field.
        /// </summary>
        public static byte[] BuildConfigJsonReply(IReadOnlyList<string> dashboards, int id = 11)
        {
            string json = BuildReplyJson(dashboards, id);
            byte[] uncompressed = Encoding.UTF8.GetBytes(json);
            byte[] compressed = CompressZlib(uncompressed);
            // Same 9-byte envelope as device→host state blobs on session 0x09:
            //   [flag:1B=0x00] [comp_size:u32 LE] [uncomp_size:u32 LE] [zlib]
            // comp_size = zlib length + 4 — PitHouse's convention everywhere in
            // this protocol (also the bundle preamble's total_compressed field).
            // The wheel slices the zlib region as comp_size − 4; emitting the
            // bare zlib length truncates the stream by 4 bytes wheel-side and
            // it silently DISCARDS the reply — which is why the host's library
            // list never drove dashboard enablement (byte-diff vs PitHouse
            // reply, bridge-enable-toggle-20260816: PH comp=208 for a 204 B
            // zlib; ours was comp=263 for 263 B).
            var env = new byte[9 + compressed.Length];
            env[0] = 0x00;
            uint c = (uint)compressed.Length + 4;
            env[1] = (byte)(c & 0xFF); env[2] = (byte)((c >> 8) & 0xFF);
            env[3] = (byte)((c >> 16) & 0xFF); env[4] = (byte)((c >> 24) & 0xFF);
            uint u = (uint)uncompressed.Length;
            env[5] = (byte)(u & 0xFF); env[6] = (byte)((u >> 8) & 0xFF);
            env[7] = (byte)((u >> 16) & 0xFF); env[8] = (byte)((u >> 24) & 0xFF);
            Array.Copy(compressed, 0, env, 9, compressed.Length);
            return env;
        }

        private static string BuildReplyJson(IReadOnlyList<string> dashboards, int id)
        {
            var inner = new JObject
            {
                ["dashboardRootDir"] = "",
                ["dashboards"] = new JArray(dashboards),
                ["fontRootDir"] = "",
                ["fonts"] = new JArray(),
                ["imageRootDir"] = "",
                ["sortTags"] = 0,
            };
            var root = new JObject
            {
                ["configJson()"] = inner,
                ["id"] = id,
            };
            return root.ToString(Formatting.None);
        }

        private static byte[] CompressZlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
