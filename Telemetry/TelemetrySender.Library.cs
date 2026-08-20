using System;
using System.Linq;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;
using MozaPlugin.Telemetry.TestMode;
using MozaPlugin.Telemetry.TileServer;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Telemetry
{
    public partial class TelemetrySender
    {

        /// <summary>
        /// Probe the Display sub-device inside the wheel.
        /// Pithouse sends the same identity commands used for the main wheel
        /// (0x09, 0x04, 0x06, 0x02, 0x05) but via group 0x43 to route them
        /// through the SerialStream to the Display sub-module.
        ///
        /// Responses arrive asynchronously via OnMessageDuringPreamble:
        /// - 0x87 data=01 "Display" → model name (confirms display present)
        /// - 0x89 data=00:01 → presence check (1 sub-device)
        /// - 0x82 data=02 → product type
        /// </summary>

        /// <summary>
        /// Push an empty-state tile-server blob on session 0x03. Matches
        /// PitHouse behaviour observed in 5 captures — PitHouse sends this on
        /// every connect; wheel never pushes back (session 0x03 is host→wheel
        /// only). Envelope is the 12-byte variant (distinct from session
        /// 0x04/0x09 9-byte form). See § Session 0x03 tile-server envelope.
        /// </summary>
        private void SendTileServerState()
        {
            try
            {
                byte[] json = TileServerStateBuilder.BuildEmptyStateJson();
                byte[] payload = TileServerStateBuilder.BuildFullBlob(json);
                int seq = 1;
                var frames = TierDefinitionBuilder.ChunkMessage(payload, 0x03, ref seq, _targetDeviceId);
                foreach (var frame in frames)
                    _connection.Send(frame);
                MozaLog.Debug(
                    $"[AZOM] Sent empty tile-server state on session 0x03: " +
                    $"{json.Length}B JSON → {payload.Length}B (12B env + zlib) → " +
                    $"{frames.Count} chunk(s)");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] SendTileServerState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fire once per session: reply to the wheel's configJson state blob
        /// with a <c>configJson()</c> canonical library list. Wheel uses this
        /// to refresh its <c>configJsonList</c> field, which PitHouse reads
        /// back from <see cref="WheelDashboardState.ConfigJsonList"/>. Sent
        /// on the SAME session the wheel pushed state on (older firmware =
        /// 0x09; KS Pro / 2026-04+ firmware = 0x0a per
        /// usb-capture/ksp/mozahubstartup.pcapng OUT seq=0x0010..0x0017,
        /// decompressed: <c>{"configJson()":{"dashboards":[...]},"id":11}</c>).
        /// </summary>
        // Session the last configJson reply targeted (0x09 on most firmware,
        // 0x0a on KS Pro / 2026-04+). The post-upload enable re-send reuses it.
        private volatile byte _lastConfigJsonReplySession = 0x09;

        // Intentional enable/remove intents with UTC-tick timestamps (10-min
        // TTL). The wire library list is rebuilt from these + the wheel's
        // CURRENT enabled set on every send — see BuildWireLibraryList.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
            _intentionalEnables = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
            _intentionalRemoves = new(StringComparer.Ordinal);
        // Backstop only — intents normally die on the wheel's verdict (see
        // BuildWireLibraryList). Short TTL bounds ghost-manufacture when the
        // wheel never answers at all.
        private const long IntentTtlTicks = TimeSpan.TicksPerMinute * 2;

        /// <summary>
        /// Build the library list actually sent to the wheel. The list is the
        /// wheel's ENABLE AUTHORITY: it enables every named dash and sweeps
        /// unnamed ones — so declaring names the wheel has no files for
        /// creates ghost registry entries that fail with 'dash load error'
        /// when the user cycles onto them (observed 2026-08-16: the plugin's
        /// LOCAL library names — cache + builtins, never uploaded — got
        /// blessed once the reply envelope became parseable). Wire list =
        /// wheel's current enabled dirNames + recent intentional enables
        /// (upload/Enable button; the target exists on the wheel by then) −
        /// recent intentional removes.
        /// </summary>
        private System.Collections.Generic.List<string> BuildWireLibraryList(WheelDashboardState? state)
        {
            long now = DateTime.UtcNow.Ticks;
            foreach (var kv in _intentionalEnables)
                if (now - kv.Value > IntentTtlTicks) _intentionalEnables.TryRemove(kv.Key, out _);
            foreach (var kv in _intentionalRemoves)
                if (now - kv.Value > IntentTtlTicks) _intentionalRemoves.TryRemove(kv.Key, out _);

            // An intent dies the moment the wheel renders a verdict on the
            // name. Enabled → confirmed, the enabled set carries it from here.
            // Disabled → REFUSED (e.g. device-mismatched dash) — declaring a
            // name the wheel won't properly enable manufactures a file-less
            // ghost registry entry ('dash load error' slot) that survives
            // reboots and shifts every later slot (observed 2026-08-16:
            // S09-targeted dash on a W17 ghosted ahead of "Grids").
            if (state != null)
            {
                foreach (var e in state.EnabledDashboards)
                    if (!string.IsNullOrEmpty(e.DirName))
                        _intentionalEnables.TryRemove(e.DirName, out _);
                // Refusal detection needs a settle margin: a fresh upload
                // legitimately sits in disabledManager for a moment before
                // the wheel flips it, and the enable-confirm delta can be
                // delayed past a library-sync reconnect (the port bounce +
                // fresh boot push land ~15-20 s after the intent). A short
                // margin killed healthy enables (2026-08-16 evening: uploads
                // stuck disabled); keep it comfortably beyond one reconnect
                // cycle.
                long refusalMarginTicks = TimeSpan.TicksPerSecond * 45;
                long stateTicks = state.CapturedAt.Ticks;
                foreach (var e in state.DisabledDashboards)
                {
                    if (string.IsNullOrEmpty(e.DirName)) continue;
                    if (_intentionalEnables.TryGetValue(e.DirName, out long intentTicks)
                        && stateTicks > intentTicks + refusalMarginTicks)
                    {
                        _intentionalEnables.TryRemove(e.DirName, out _);
                        MozaLog.Info(
                            $"[AZOM] Enable intent for \"{e.DirName}\" expired — still classified " +
                            "disabled 3s+ after declaration (enable-confirm delta may have been " +
                            "lost, or the wheel declined); click Enable to retry. Intent dropped " +
                            "rather than re-declared to avoid manufacturing a ghost slot");
                    }
                }
            }

            var list = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (state != null)
            {
                foreach (var d in state.EnabledDashboards)
                {
                    if (string.IsNullOrEmpty(d.DirName)) continue;
                    if (_intentionalRemoves.ContainsKey(d.DirName)) continue;
                    if (seen.Add(d.DirName)) list.Add(d.DirName);
                }
            }
            foreach (var name in _intentionalEnables.Keys)
            {
                if (_intentionalRemoves.ContainsKey(name)) continue;
                if (seen.Add(name)) list.Add(name);
            }
            // Ordinal sort — the host's declared list IS the wheel's slot
            // table, and PitHouse always sends it ordinal-sorted ("Core" <
            // "ETS2-ATS" < … < "jrams…" < "porn"; new names slot in sorted,
            // both 2026-08-16 captures). An unsorted list reorders the
            // wheel's slots on every enable, scrambling the selector and any
            // slot-indexed switch.
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private void MaybeSendConfigJsonReply(WheelDashboardState state, byte session)
        {
            _lastConfigJsonReplySession = session;
            if (_session09ReplySent)
            {
                MozaLog.Info(
                    $"[AZOM] configJson reply skipped (already sent this cycle) sess=0x{session:X2}");
                return;
            }

            var wireList = BuildWireLibraryList(state ?? _configJson.LastState);
            if (wireList.Count == 0)
            {
                // Nothing trustworthy to declare yet (no state received) —
                // fall back to echoing the wheel's own list so its
                // configJsonList survives the connect cycle unchanged.
                if (state?.ConfigJsonList == null || state.ConfigJsonList.Count == 0)
                {
                    MozaLog.Info(
                        $"[AZOM] configJson reply skipped (no library names and no wheel list) sess=0x{session:X2}");
                    return;
                }
                wireList = new System.Collections.Generic.List<string>(state.ConfigJsonList);
            }
            CanonicalDashboardList = wireList;

            byte[] reply = ConfigJsonClient.BuildConfigJsonReply(CanonicalDashboardList);
            // Single tracked copy — the retransmitter covers loss, so PitHouse's
            // 2-3 duplicate copies aren't needed.
            const int ReplyCopies = 1;
            int chunkCount;
            // Hold _session09SeqLock across the entire read-chunk-send-write
            // so SendSession09Keepalive (timer thread) can't slip a ++seq in
            // mid-emission and break the wheel's gap detector. Early-return
            // on disconnect still persists the partial advance — any frames
            // already on the wire have consumed those seqs wheel-side.
            lock (_session09SeqLock)
            {
                if (!_session09SeqSeeded)
                    MozaLog.Warn(
                        $"[AZOM] configJson reply sent before sess=0x{session:X2} device-init " +
                        "seeded the outbound seq — the wheel will ignore seqs below its base");
                chunkCount = 0;
                for (int copy = 0; copy < ReplyCopies; copy++)
                {
                    // Counter holds the NEXT seq; ChunkMessage advances it past
                    // the last chunk. Never skip a seq — the wheel's rx ack pins
                    // at the hole and nothing behind it is accepted.
                    int firstSeq = _session09OutboundSeq;
                    int seq = firstSeq;
                    var frames = TierDefinitionBuilder.ChunkMessage(reply, session, ref seq, _targetDeviceId);
                    chunkCount = frames.Count;
                    int sent = 0;
                    foreach (var frame in frames)
                    {
                        if (_state == TelemetryState.Idle || !_connection.IsConnected)
                        {
                            // Only the frames actually sent consumed seqs.
                            _session09OutboundSeq = firstSeq + sent;
                            MozaLog.Info(
                                $"[AZOM] configJson reply ABORTED mid-send sess=0x{session:X2} " +
                                $"(state={_state}, connected={_connection.IsConnected}) — " +
                                "connect-window sweep will not happen this cycle");
                            return;
                        }
                        // Ack-tracked: the wheel fc-acks config-session chunks
                        // (lazily) and retransmits on dup-acks.
                        SendAndTrackConfigChunk(frame);
                        sent++;
                    }
                    _session09OutboundSeq = seq;
                }
            }
            _session09ReplySent = true;
            // Do NOT adopt the declared list into the cached ConfigJsonList.
            // The wheel maintains its OWN ordinal-sorted slot table which can
            // differ from the host's enabled-only declaration (wire-verified
            // 2026-08-16: wheel table kept a dash at slot 3 that the declared
            // list omitted, shifting every later mapping by one — radarrr
            // switched to slot 15 landed on the wrong dash). PitHouse's list
            // merely coincides with the wheel's table because both are
            // ordinal-sorted over the same set. The wheel-reported list is the
            // only slot authority; mutations re-sync it via
            // RequestConfigJsonStateRefresh.
            MozaLog.Info(
                $"[AZOM] Sent configJson() reply on session 0x{session:X2}: " +
                $"{CanonicalDashboardList.Count} dashboards, {chunkCount} chunks");
        }

        /// <summary>
        /// Post-upload enable: uploaded dashboards land in the wheel's
        /// disabledManager and stay out of the on-wheel picker until the host's
        /// configJson() library list names them — the wheel syncs enablement
        /// against that list (ground truth 2026-08-16: PitHouse re-sends its
        /// list after an upload; the uploaded dash present in the list flipped
        /// enabled, the one absent stayed disabled). Adds
        /// <paramref name="dashboardName"/> to <see cref="CanonicalDashboardList"/>
        /// and re-sends the reply on the last-used config session.
        /// </summary>
        internal void EnableUploadedDashboard(string dashboardName)
        {
            if (string.IsNullOrEmpty(dashboardName)) return;

            _intentionalEnables[dashboardName] = DateTime.UtcNow.Ticks;
            _intentionalRemoves.TryRemove(dashboardName, out _);

            var state = _configJson.LastState;
            if (state == null)
            {
                MozaLog.Debug(
                    $"[AZOM] Post-upload enable for \"{dashboardName}\" deferred — " +
                    "no wheel state yet");
                return;
            }

            // Group-0x40 "finalize upload" — PitHouse sends `0b 00` right
            // after the wheel's post-upload state push and before its
            // configJson list reply (ground truth t=328.031); the RE notes
            // (usb-capture/pithouse-re.md § Dashboard upload group 0x40)
            // identify 0x0B as the upload-finalize verb. Without it the
            // uploaded dash stays parked in disabledManager.
            try { _connection.Send(BuildGroup40Bytes(new byte[] { 0x0B, 0x00 })); }
            catch (Exception ex)
            { MozaLog.Debug($"[AZOM] finalize-upload 0x40/0x0B send failed: {ex.Message}"); }

            // Re-send even when the name was already present — the reply
            // itself is part of the enable exchange.
            _session09ReplySent = false;
            MaybeSendConfigJsonReply(state!, _lastConfigJsonReplySession);
            // Predict the wheel's slot table: an accepted list enables the
            // declared name, and the wheel updates its own ordinal-sorted
            // table live but does NOT re-push it mid-session — so the host
            // ordinal-inserts its own delta or the dropdown / name→slot map
            // lacks the new dash until the next connect. Corrected wholesale
            // by the next full push if the wheel declines.
            _configJson.ApplyLibraryDelta(dashboardName, null);
            // No state-refresh nudge: the wheel never answers mid-session
            // prime/open nudges with a full push, and synthetic-seq frames
            // desync the sess=0x09 reassembler.
        }

        // Nonce for post-mutation state-refresh seqs (distinct ranges from the
        // watchdog's gap-recovery nudges at 0x100/0x200).
        private int _stateRefreshNonce;

        // ── Library-sync restart (last-resort reconcile) ─────────────────
        // Rebuilds the wheel's REGISTRY/configJsonList at connect. It does
        // NOT rebuild the render/UI slot table — only an accepted host list
        // compacts that, mid-session — so this is no longer the delete path's
        // reconcile, just the fallback when the wheel never confirms.
        private long _librarySyncRestartDueTicks;
        private int _librarySyncRestartScheduled;
        private long _librarySyncFirstAttemptTicks;

        internal void ScheduleLibrarySyncRestart(string reason)
        {
            Interlocked.Exchange(ref _librarySyncRestartDueTicks,
                DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * 4);
            if (Interlocked.CompareExchange(ref _librarySyncRestartScheduled, 1, 0) != 0) return;
            MozaLog.Info(
                $"[AZOM] Library-sync restart scheduled ({reason}) — reconnect rebuilds the " +
                "wheel's registry/configJsonList (the render table needs an accepted list)");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (true)
                    {
                        long due = Interlocked.Read(ref _librarySyncRestartDueTicks);
                        long now = DateTime.UtcNow.Ticks;
                        if (now >= due)
                        {
                            // Never restart under an in-flight upload — push the
                            // window out and re-check.
                            if (_uploader != null && _uploader.IsUploadInFlight)
                            {
                                Interlocked.Exchange(ref _librarySyncRestartDueTicks,
                                    now + TimeSpan.TicksPerSecond * 4);
                                continue;
                            }
                            // Hold while an enable is awaiting the wheel's
                            // confirming delta — bouncing the port under a
                            // pending enable strands the dash disabled
                            // (observed 2026-08-16 evening). Cap the hold so a
                            // lost confirm can't block the reconcile forever.
                            if (!_intentionalEnables.IsEmpty
                                && now - due < TimeSpan.TicksPerSecond * 20)
                            {
                                Thread.Sleep(500);
                                continue;
                            }
                            // Mid-blackout (a previous reconnect cycle still
                            // settling) is a RETRY, not a bail — silently
                            // returning here lost the reconcile for deletes
                            // issued during the window and their dead slots
                            // persisted. Bounded at 2 minutes of attempts.
                            if (_state == TelemetryState.Idle || !_connection.IsConnected)
                            {
                                if (Interlocked.Read(ref _librarySyncFirstAttemptTicks) == 0)
                                    Interlocked.Exchange(ref _librarySyncFirstAttemptTicks, now);
                                else if (now - Interlocked.Read(ref _librarySyncFirstAttemptTicks)
                                         > TimeSpan.TicksPerMinute * 2)
                                {
                                    MozaLog.Warn(
                                        "[AZOM] Library-sync reconnect abandoned — sender " +
                                        "idle/disconnected for 2min");
                                    return;
                                }
                                Interlocked.Exchange(ref _librarySyncRestartDueTicks,
                                    now + TimeSpan.TicksPerSecond * 5);
                                continue;
                            }
                            break;
                        }
                        long waitMs = (due - now) / TimeSpan.TicksPerMillisecond;
                        Thread.Sleep((int)Math.Min(1000, Math.Max(50, waitMs)));
                    }
                    Interlocked.Exchange(ref _librarySyncFirstAttemptTicks, 0);
                    MozaLog.Info("[AZOM] Library-sync reconnect firing (port-level — rebuilds the " +
                        "wheel's registry/configJsonList; a session Stop/Start does not)");
                    // Keep the cached table in lockstep with the registry the
                    // fresh connect rebuilds (a fast bounce may not re-push it).
                    _configJson.CompactConfirmedDeletes();
                    _connection.ForceReconnect("library-sync reconcile");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Library-sync restart failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _librarySyncRestartScheduled, 0);
                }
            });
        }

        /// <summary>
        /// UNUSED — kept for reference. Mid-session prime + open-request does
        /// NOT make the wheel re-push its full state (full TitleId=1 pushes
        /// happen only at connect), and the synthetic-seq frames desync the
        /// sess=0x09 reassembler (forward-gap storm; config channel dead until
        /// reconnect). Do not call after mutations; the wheel's unprompted
        /// TitleId=4 deltas carry the confirmations.
        /// </summary>
        internal void RequestConfigJsonStateRefresh(string reason)
        {
            byte session = _lastConfigJsonReplySession;
            int n = Interlocked.Increment(ref _stateRefreshNonce) & 0x7F;
            ushort primeSeq = (ushort)(0x300 + n);
            ushort openSeq = (ushort)(0x380 + n);
            MozaLog.Debug(
                $"[AZOM] Requesting fresh configJson state on sess=0x{session:X2} ({reason})");
            try
            {
                SendSessionPrime(session, primeSeq);
                _watchdog.SendConfigJsonOpenRequest(session, openSeq);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] configJson state-refresh request failed: {ex.Message}");
            }
        }

        // ── Pending delete (confirm-gated, mid-session reconcile) ─────────
        // A delete takes two wheel-side steps, both mid-session, neither
        // needing a port bounce: the verb (wheel removes files+registry,
        // confirms with TitleId=4 deletedDashboards deltas), then the wheel
        // ACCEPTING the host's list-without-name — which is what compacts its
        // render/UI slot table. See
        // docs/protocol/dashboard-upload/config-rpc-session-09.md.
        private sealed class PendingDelete
        {
            public string DirName = "";
            public string Id = "";
            public bool ListSent;
        }
        private PendingDelete? _pendingDelete;
        private readonly object _pendingDeleteLock = new object();
        private const int DeleteConfirmTimeoutMs = 5_000;
        private const int DeleteFallbackTimeoutMs = 15_000;

        /// <summary>
        /// Post-delete library sync: register the remove intent and arm the
        /// confirm-gated reconcile. The list re-send fires from the confirm
        /// hook (<see cref="OnConfigJsonStateReadyCheckPendingDelete"/>) the
        /// moment the wheel's TitleId=4 delta lands; the timeout worker sends
        /// it anyway at +5 s and falls back to the port-level reconnect only
        /// at +15 s with no confirm at all.
        /// </summary>
        internal void RemoveDashboardFromLibrary(string dashboardName, string dashboardId)
        {
            if (string.IsNullOrEmpty(dashboardName)) return;

            _intentionalRemoves[dashboardName] = DateTime.UtcNow.Ticks;
            _intentionalEnables.TryRemove(dashboardName, out _);

            var pending = new PendingDelete
            {
                DirName = dashboardName,
                Id = dashboardId ?? "",
            };
            lock (_pendingDeleteLock) { _pendingDelete = pending; }
            ThreadPool.QueueUserWorkItem(_ => PendingDeleteTimeoutWorker(pending));
        }

        private void PendingDeleteTimeoutWorker(PendingDelete pending)
        {
            try
            {
                for (int waited = 0; waited < DeleteFallbackTimeoutMs; waited += 250)
                {
                    Thread.Sleep(250);
                    lock (_pendingDeleteLock)
                        if (!ReferenceEquals(_pendingDelete, pending))
                            return;   // confirmed (hook cleared it) or superseded by a newer delete
                    if (waited + 250 >= DeleteConfirmTimeoutMs && !pending.ListSent)
                    {
                        pending.ListSent = true;
                        MozaLog.Warn(
                            $"[AZOM] Delete \"{pending.DirName}\": no wheel confirm within " +
                            $"{DeleteConfirmTimeoutMs / 1000} s — sending library list without it anyway");
                        var state = _configJson.LastState;
                        if (state != null)
                        {
                            _session09ReplySent = false;
                            MaybeSendConfigJsonReply(state, _lastConfigJsonReplySession);
                        }
                    }
                }
                lock (_pendingDeleteLock)
                {
                    if (!ReferenceEquals(_pendingDelete, pending)) return;
                    _pendingDelete = null;
                }
                MozaLog.Warn(
                    $"[AZOM] Delete \"{pending.DirName}\": no wheel confirm within " +
                    $"{DeleteFallbackTimeoutMs / 1000} s — last-resort library-sync reconnect");
                ScheduleLibrarySyncRestart($"delete \"{pending.DirName}\" unconfirmed");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Pending-delete worker failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from the inbound dispatcher after every StateReady merge,
        /// BEFORE its configJson-reply call. When the freshly-merged state
        /// confirms the pending delete, compact the cached slot table NOW (so
        /// the wheel's post-compaction kind=4 validates against the shortened
        /// list) and re-arm the reply latch — the caller's reply send then
        /// carries the list-without-name whose acceptance makes the wheel
        /// compact its render table mid-session.
        /// </summary>
        internal void OnConfigJsonStateReadyCheckPendingDelete(WheelDashboardState state)
        {
            PendingDelete? p;
            lock (_pendingDeleteLock) p = _pendingDelete;
            if (p == null) return;

            bool confirmed =
                state.ConfirmedRemovedNames.Contains(p.DirName, StringComparer.Ordinal)
                || (!string.IsNullOrEmpty(p.Id)
                    && (state.EnabledDeletedIds.Contains(p.Id, StringComparer.Ordinal)
                        || state.DisabledDeletedIds.Contains(p.Id, StringComparer.Ordinal)));
            if (!confirmed && state.ConfigJsonList.Count > 0)
            {
                // FULL push resets ConfirmedRemovedNames: confirmed iff the
                // entry is gone from the table and both managers.
                confirmed =
                    !state.ConfigJsonList.Contains(p.DirName, StringComparer.Ordinal)
                    && !state.EnabledDashboards.Any(e => string.Equals(e.DirName, p.DirName, StringComparison.Ordinal))
                    && !state.DisabledDashboards.Any(e => string.Equals(e.DirName, p.DirName, StringComparison.Ordinal));
            }
            if (!confirmed) return;

            lock (_pendingDeleteLock)
            {
                if (!ReferenceEquals(_pendingDelete, p)) return;
                _pendingDelete = null;
            }
            MozaLog.Info(
                $"[AZOM] Delete \"{p.DirName}\" confirmed by wheel — compacting slot table; " +
                "list re-send follows (its acceptance compacts the wheel's render table)");
            _configJson.CompactConfirmedDeletes();
            _session09ReplySent = false;
        }

        /// <summary>
        /// Check cache for missing dashboard hashes and trigger background download
        /// via session 0x0B if needed. Called when a new WheelDashboardState arrives.
        /// </summary>
        private void MaybeTriggerDashboardDownload(WheelDashboardState state)
        {
            var cache = _dashboardCache;
            var downloader = _dashboardDownloader;
            if (cache == null) return;

            // Always update the name→hash mapping so TryGetByName works
            // even when downloads are disabled or already triggered.
            var missing = cache.UpdateFromWheelState(state);

            if (downloader == null || _dashboardDownloadTriggered) return;
            if (missing.Count == 0) return;

            _dashboardDownloadTriggered = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int ingested = downloader.Execute(state, missing);
                    if (ingested > 0)
                        MozaLog.Debug(
                            $"[AZOM] Dashboard download complete: {ingested} dashboards cached");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Dashboard download failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Send a host→wheel JSON RPC call on the live configJson session and
        /// wait for the wheel's reply. Kept on TelemetrySender so external
        /// callers (Settings UI, completelyRemove flow) don't need to know
        /// about the helper. A null return means "no direct reply" — the wheel
        /// typically answers management RPCs with a state push instead
        /// (PitHouse's completelyRemove got no textual reply either).
        /// </summary>
        public byte[]? SendRpcCall(string method, object arg, int timeoutMs = 2000)
        {
            if (Volatile.Read(ref _disposed) != 0) return null;
            // RPCs must ride the session the wheel's config machinery listens
            // on (0x09 on most firmware; 0x0a on KS Pro / 2026-04+). The wheel
            // never device-inits 0x0a on 0x09-firmware and silently drops
            // chunks sent there — zero fc-acks (observed completelyRemove
            // attempt, moza-wire-20260816-125328). Share session 0x09's
            // outbound seq counter so RPC chunks can't collide with
            // configJson-reply emissions.
            return _rpc.Call(method, arg, timeoutMs, envelope =>
            {
                byte sess = _lastConfigJsonReplySession;
                MozaLog.Debug(
                    $"[AZOM] RPC \"{method}\" → sess=0x{sess:X2} " +
                    $"({envelope.Length}B envelope, state={_state})");
                lock (_session09SeqLock)
                {
                    // Counter holds the NEXT seq — see MaybeSendConfigJsonReply.
                    int firstSeq = _session09OutboundSeq;
                    int seq = firstSeq;
                    var frames = TierDefinitionBuilder.ChunkMessage(envelope, sess, ref seq, _targetDeviceId);
                    int sent = 0;
                    foreach (var frame in frames)
                    {
                        if (_state == TelemetryState.Idle || !_connection.IsConnected)
                        {
                            _session09OutboundSeq = firstSeq + sent;
                            return false;
                        }
                        SendAndTrackConfigChunk(frame);
                        sent++;
                    }
                    _session09OutboundSeq = seq;
                }
                return true;
            });
        }

        private void SendDisplayProbe()
        {
            if (!_connection.IsConnected) return;

            // Heartbeat/ping first
            _connection.Send(_frames.BuildDisplayFrame(0x00));

            // Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05
            _connection.Send(_frames.BuildDisplayFrame(0x09));
            _connection.Send(_frames.BuildDisplayFrameWithData(0x04, new byte[] { 0x00, 0x00, 0x00, 0x00 }));
            _connection.Send(_frames.BuildDisplayFrame(0x06));
            _connection.Send(_frames.BuildDisplayFrameWithData(0x02, new byte[] { 0x00 }));
            _connection.Send(_frames.BuildDisplayFrameWithData(0x05, new byte[] { 0x00, 0x00, 0x00, 0x00 }));

            // Version queries: 0x07, 0x0F, 0x11, 0x08, 0x10 (sub-device 1)
            _connection.Send(_frames.BuildDisplayFrameWithData(0x07, new byte[] { 0x01 }));
            _connection.Send(_frames.BuildDisplayFrameWithData(0x0F, new byte[] { 0x01 }));
            _connection.Send(_frames.BuildDisplayFrameWithData(0x11, new byte[] { 0x04 }));
            _connection.Send(_frames.BuildDisplayFrameWithData(0x08, new byte[] { 0x01 }));
            _connection.Send(_frames.BuildDisplayFrameWithData(0x10, new byte[] { 0x00 }));
        }

        // ── Preamble message handling ───────────────────────────────────────

        /// <summary>
        /// Handle incoming messages during port probing and the ~1s preamble phase.
        /// Detects fc:00 session acks (for port probing) and acks incoming 7c:00
        /// channel data on the telemetry session.
        /// </summary>
        
        // ── Timer loop ──────────────────────────────────────────────────────

        // Re-entry guard. System.Timers.Timer fires Elapsed on the ThreadPool,
        // so a handler that overruns its interval gets concurrent invocations.
        // Without this, _tickCounter/_slowCounter all race and
        // non-coalesced one-shot frames (heartbeat, display_cfg) fire 2–3× the
        // intended rate. Stream-lane traffic is coalesced so it's immune, but
        // the counter races still skew scheduling. Drop overlapping ticks —
        // the missed tick's data is re-covered by the next tick's fresh
        // snapshot via the latest-wins stream slots.
        private int _tickInProgress;

        // Frame-build / tick failure escalation. The outer catch in
        // OnTimerElapsedInner used to swallow every exception as Warn — a
        // repeatable bug (null resolver entry, malformed channel def, …)
        // would freeze the dashboard with no recovery attempt. Streak counter
        // resets on the first successful tick body; once it hits the
        // threshold we hand off to RecoveryDispatcher (which has its own
        // debounce + rate-cap + park) and reset the counter so the same
        // flap doesn't immediately re-escalate inside the debounce window.
        private int _consecutiveTickFailures;
        private const int TickFailureRestartThreshold = 10;

        // Tracks whether the previous tick observed a live connection.
        // Used to detect the disconnected→connected transition so we can
        // forgive any prior recovery-budget exhaustion: a wheel that just
        // came back is observably a different situation than the one whose
        // earlier failures parked the pipeline.
        private bool _lastTickSawConnected;
    }
}
