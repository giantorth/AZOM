using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Dashboard-binding pipeline: telemetry settings push, kind=4 emission for
    /// profile-driven switches, wheel-initiated switch handling, lifecycle gating.
    /// </summary>
    internal sealed class DashboardBindingCoordinator
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly MozaSerialConnection _connection;
        private readonly DeviceDetectionState _detectionState;

        // ── Pending-apply state ──────────────────────────────────────────────
        //
        // All three fields below are guarded by _stateLock. They were
        // previously bare reference/long fields with no synchronisation
        // even though the writers run on three different threads:
        //   - PollStatus timer thread (TickPendingDashboardRetry)
        //   - Dispatcher / UI thread (manual OnDashboardSwitched* from
        //     combo / load-mzdash / clear-mzdash)
        //   - Profile-change thread (ApplyProfile → SetPendingDashboardKey)
        // Under that race, a manual-click "clear" could be overwritten
        // milliseconds later by a profile-event setter, leaving the
        // system thinking a switch was still pending after the user had
        // explicitly chosen something. The lock is held only for field
        // operations; external calls (sender, plugin, ApplyTelemetrySettings)
        // are made AFTER releasing the lock.
        private readonly object _stateLock = new object();

        /// <summary>Immutable snapshot of an in-flight profile-driven dashboard
        /// apply that deferred because the wheel catalog / sender state wasn't
        /// ready. PollStatus retries via TickPendingDashboardRetry until the
        /// apply succeeds, the profile changes, the user manually switches, or
        /// the deadline elapses. (Plain class rather than a record because
        /// the project targets net48 and records/`with` need IsExternalInit.)</summary>
        private sealed class PendingApply
        {
            public string Key { get; }
            public DateTime DeadlineUtc { get; }
            public int RetryCount { get; }
            public DateTime LastRetryWarnUtc { get; }

            public PendingApply(string key, DateTime deadlineUtc, int retryCount, DateTime lastRetryWarnUtc)
            {
                Key = key;
                DeadlineUtc = deadlineUtc;
                RetryCount = retryCount;
                LastRetryWarnUtc = lastRetryWarnUtc;
            }

            public PendingApply With(int? retryCount = null, DateTime? lastRetryWarnUtc = null)
                => new PendingApply(
                    Key,
                    DeadlineUtc,
                    retryCount ?? RetryCount,
                    lastRetryWarnUtc ?? LastRetryWarnUtc);
        }

        private PendingApply? _pending;

        // Per-plugin-instance memo: avoid re-emitting kind=4 for an already-applied
        // key. Cleared on plugin reload and on ResetWheelDetection.
        private string? _lastAppliedDashboardKey;

        // Throttle for "deferring (key=X): reason" log lines AND text for the
        // UI status label while a pending apply is waiting. Independent of
        // _pending so the very first defer from ApplyProfile (which happens
        // BEFORE SetPendingDashboardKey is called) can dedupe its own log line.
        private string? _lastApplyDeferReason;

        private static readonly TimeSpan PendingProfileKeyTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryWarnInterval = TimeSpan.FromSeconds(30);

        public DashboardBindingCoordinator(
            MozaPlugin plugin,
            MozaData data,
            MozaSerialConnection connection,
            DeviceDetectionState detectionState)
        {
            _plugin = plugin;
            _data = data;
            _connection = connection;
            _detectionState = detectionState;
        }

        public bool IsPendingDashboardApply
        {
            get { lock (_stateLock) { return _pending != null; } }
        }

        /// <summary>Short description of why the pending profile-driven apply is
        /// waiting, for the UI status label. Null when no apply is pending.</summary>
        public string? PendingDashboardApplyDescription
        {
            get
            {
                lock (_stateLock)
                {
                    if (_pending == null) return null;
                    return _lastApplyDeferReason;
                }
            }
        }

        public void SetPendingDashboardKey(string key)
        {
            var now = DateTime.UtcNow;
            lock (_stateLock)
            {
                _pending = new PendingApply(key, now.Add(PendingProfileKeyTimeout), 0, now);
            }
        }

        public void ClearPendingDashboardKey()
        {
            lock (_stateLock) { _pending = null; }
        }

        public void ClearLastAppliedDashboardKey()
        {
            lock (_stateLock) { _lastAppliedDashboardKey = null; }
        }

        // ── Lock-guarded helpers (call ONLY from outside _stateLock) ─────────

        private string? GetLastAppliedKey()
        {
            lock (_stateLock) { return _lastAppliedDashboardKey; }
        }

        private void SetLastAppliedKey(string? key)
        {
            lock (_stateLock) { _lastAppliedDashboardKey = key; }
        }

        /// <summary>Record a defer reason for the log throttle / UI status.
        /// Returns true if the reason changed and the caller should emit a
        /// Debug log line.</summary>
        private bool RecordDeferReason(string reason)
        {
            lock (_stateLock)
            {
                if (_lastApplyDeferReason == reason) return false;
                _lastApplyDeferReason = reason;
                return true;
            }
        }

        private void ClearDeferReason()
        {
            lock (_stateLock) { _lastApplyDeferReason = null; }
        }

        /// <summary>Apply telemetry settings from the active wheel overlay to the TelemetrySender.</summary>
        public void ApplyTelemetrySettings()
        {
            var sender = _plugin.TelemetrySender;
            if (sender == null) return;

            // Standalone dashboard retarget: CM2 (no wheelbase) routes screen
            // telemetry to dev=0x12 (CM2 bridge/main) instead of the wheel's
            // dev=0x17. Inbound dispatcher widens its accepted device fan-in
            // when this mode flag is set.
            bool standaloneDashboard = _plugin.ShouldUseStandaloneDashboardTarget();
            byte targetDeviceId = standaloneDashboard
                ? _plugin.PreferredStandaloneDashboardTargetDeviceId
                : MozaProtocol.DeviceWheel;

            // Point the sender at the connection that owns the screen: the
            // dedicated dashboard connection for a standalone-USB CM2, else the
            // wheelbase connection (wheel-hosted 0x17 / base-bridged CM2 0x12).
            // Rebinding requires Idle; if the sender is mid-session, defer to the
            // next apply (a connection swap mid-stream isn't safe anyway).
            var desired = _plugin.DashboardUsbConnected
                ? _plugin.DashboardConnection
                : _plugin.Connection;
            if (desired != null && sender.StateIsIdle)
                sender.Rebind(desired);

            sender.StandaloneDashboardMode = standaloneDashboard;
            sender.TargetDeviceId = targetDeviceId;
            // The main sender always uses lane base 0. Its strict-inbound / shares-
            // connection flags (set when a co-resident _cm2Sender shares the bus) are
            // owned by MozaPlugin.EnsureCm2Pipeline, not reset here.
            sender.StreamSlotBase = 0;

            // Source from the current wheel's overlay (single source of truth).
            // When no wheel is identified yet, ActiveTelemetry* return defaults
            // → era Auto, paths empty, no profile loaded. The sender stays idle
            // until wheel-model-name resolves the page GUID.
            string telemPath = _plugin.ActiveTelemetryMzdashPath;
            string telemName = _plugin.ActiveTelemetryProfileName;
            MozaWheelEra era = _plugin.ActiveTelemetryWheelEra;

            sender.Policy = EraPolicy.For(era);
            // Upload/download UI is hidden while feature is in development;
            // force both off regardless of saved settings.
            sender.UploadDashboard = false;
            sender.SetDownloadEnabled(false);
            if (_plugin.Settings.EnableAutoTestOnConnect)
                sender.EnableAutoTest(_plugin);

            // Resolve active multi-stream profile and raw mzdash content.
            // Precedence: custom file → cached by name → builtin embedded → null (sender synthesises from catalog).
            MultiStreamProfile? profile = null;
            byte[]? mzdashContent = null;
            string mzdashName = "";

            if (!string.IsNullOrEmpty(telemPath) && File.Exists(telemPath))
            {
                profile = _plugin.DashProfileStore.ParseMzdash(telemPath);
                mzdashContent = File.ReadAllBytes(telemPath);
                mzdashName = Path.GetFileNameWithoutExtension(telemPath);
            }
            else if (!string.IsNullOrEmpty(telemName))
            {
                if (_plugin.DashCache != null)
                {
                    profile = _plugin.DashCache.TryGetByName(telemName);
                    if (profile != null)
                    {
                        mzdashName = profile.Name;
                        mzdashContent = _plugin.DashCache.TryGetRawContent(telemName);
                        MozaLog.Debug($"[AZOM] ApplyTelemetrySettings: found '{telemName}' in cache as '{mzdashName}'");
                    }
                    else
                    {
                        MozaLog.Debug($"[AZOM] ApplyTelemetrySettings: '{telemName}' NOT found in cache (folder={_plugin.DashCache.FolderProfileCount} wheel={_plugin.DashCache.WheelCacheCount})");
                    }
                }

                if (profile == null)
                {
                    var builtins = _plugin.DashProfileStore.BuiltinProfiles;
                    if (builtins.Count > 0)
                    {
                        profile = FindProfile(builtins, telemName);
                        if (profile != null && mzdashContent == null)
                        {
                            mzdashName = profile.Name;
                            string resourceName = $"MozaPlugin.Data.Dashes.{profile.Name.Replace(" ", "_")}.mzdash";
                            var assembly = Assembly.GetExecutingAssembly();
                            using var stream = assembly.GetManifestResourceStream(resourceName);
                            if (stream != null)
                            {
                                using var ms = new MemoryStream();
                                stream.CopyTo(ms);
                                mzdashContent = ms.ToArray();
                            }
                        }
                    }
                }
            }
            // else: catalog-only mode — profile stays null; sender synthesises post-preamble.

            // Telemetry channels ALWAYS come from the wheel's live catalog
            // (catalog-only synthesis). The parsed mzdash profile is never used
            // to drive the subscription: trusting it over the wheel's
            // advertisement misaligned channels and dropped catalog channels the
            // wheel actually wants (e.g. the wheel's "Core" page advertises Rpm
            // at idx 4, but a local "Core" mzdash that omits Rpm left the RPM
            // widget dead). The wheel's catalog is authoritative — Telemetry.json
            // supplies compression/property/test-signal per URL and the synth
            // applies user channel mappings itself (see TelemetrySender catalog
            // synthesis). mzdashContent / mzdashName resolved above are retained
            // ONLY for the upload path. FSR1 uses neither catalog nor mzdash and
            // is driven by its own emitter, so this is a no-op there.
            profile = null;

            sender.PropertyResolver = _plugin.PropertyResolver.ResolveAsDouble;
            sender.PropertyStringResolver = _plugin.PropertyResolver.ResolveAsString;
            int tierCount = profile?.Tiers?.Count ?? 0;
            int chCount = 0;
            if (profile != null)
                foreach (var t in profile.Tiers) chCount += t.Channels.Count;
            // Catalog-only continuity: when the user has no mzdash file for
            // the new dashboard (profile == null) AND the sender already has
            // a synthesised catalog-only profile loaded, KEEP it. The
            // alternative — assigning null and letting MaybeSwap re-synth on
            // the next tick — sends the wheel a torn-down-and-rebuilt tier-
            // def for no semantic gain, which we've observed (2026-05-26
            // moza-wire-...-043633) wedge sess=0x01 in a close/reopen loop
            // when the user rapid-fires dashboard switches. MaybeSwap will
            // still pick up any genuine catalog/endMarker change on a future
            // tick. The non-null case proceeds normally so a user-loaded
            // mzdash always wins over the synth.
            bool keepExistingSynth =
                profile == null
                && sender.Profile != null
                && sender.Profile.Name == TelemetrySender.CatalogProfileName;
            MozaLog.Debug(
                $"[AZOM] ApplyTelemetrySettings: setting profile=" +
                $"{profile?.Name ?? "null"} tiers={tierCount} channels={chCount} " +
                $"mzdash={mzdashName} settingName={telemName}" +
                (keepExistingSynth ? " (keeping existing synth — catalog-only mode unchanged)" : ""));
            if (!keepExistingSynth)
                sender.Profile = profile;
            // Re-point the tier frame builders at the resolver we just assigned.
            // When keepExistingSynth kept the profile (so the Profile setter above
            // did NOT run), the builders still hold whatever resolver they were
            // last built with — on a plugin reload that's the dead old instance's,
            // which freezes the live dashboard at 0 while test mode (TestSignal,
            // not the resolver) keeps working. Self-guards: no-op unless the
            // resolver instance actually changed.
            sender.RebindFrameBuildersToResolver();
            sender.MzdashContent = mzdashContent;
            sender.MzdashName = mzdashName;

            // Source directory: needed so the upload bundle finds sibling PNG widget
            // assets at <dir>/Resource/MD5/<hex>.png. User-picked file → its dir;
            // library-picked → folder profile path; builtins → empty (single-file).
            string mzdashSourceDir = "";
            if (!string.IsNullOrEmpty(telemPath) && File.Exists(telemPath))
            {
                mzdashSourceDir = Path.GetDirectoryName(telemPath) ?? "";
            }
            else if (!string.IsNullOrEmpty(telemName) && _plugin.DashCache != null)
            {
                string? folderPath = _plugin.DashCache.TryGetFolderFilePath(telemName);
                if (!string.IsNullOrEmpty(folderPath))
                    mzdashSourceDir = Path.GetDirectoryName(folderPath!) ?? "";
            }
            sender.MzdashSourceDirectory = mzdashSourceDir;

            // Advertise dashboard library to the wheel on session 0x09.
            // Cache (from wheel download) wins on overlap with builtins.
            var libraryNames = new List<string>();
            if (_plugin.DashCache != null)
            {
                foreach (var name in _plugin.DashCache.CachedNames)
                    libraryNames.Add(name);
            }
            foreach (var p in _plugin.DashProfileStore.BuiltinProfiles)
            {
                if (!libraryNames.Contains(p.Name))
                    libraryNames.Add(p.Name);
            }
            if (!string.IsNullOrEmpty(mzdashName) && !libraryNames.Contains(mzdashName))
                libraryNames.Add(mzdashName);
            sender.CanonicalDashboardList = libraryNames;
        }

        private static MultiStreamProfile? FindProfile(
            IReadOnlyList<MultiStreamProfile> profiles, string name)
        {
            foreach (var p in profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        /// <summary>
        /// Restart the telemetry session with current settings. Used when protocol
        /// version, flag byte mode, or other send options change in the UI.
        /// </summary>
        public void RestartTelemetry()
        {
            var t = _plugin.TelemetrySender;
            if (t == null) return;
            Interlocked.Exchange(ref _plugin._telemetryStartRequested, 0);
            ApplyTelemetrySettings();
            if (!_plugin.ActiveTelemetryEnabled) return;
            // Bypass StartTelemetryIfReady's FramesSent>0 guard which rejects restarts
            // when the sender is running. StartInner's first action is Stop() — true cold-start.
            if (Interlocked.CompareExchange(ref _plugin._telemetryStartRequested, 1, 0) != 0) return;
            MozaLog.Info("[AZOM] Restarting telemetry sender (full cold-start)");
            // Same guard as StartTelemetryIfReady: an unobserved exception on a
            // ThreadPool thread can take down the SimHub host process.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { t.Start(); }
                catch (ObjectDisposedException) { /* plugin disposed mid-start */ }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Telemetry restart failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Apply <see cref="MozaProfile.TelemetryDashboardKey"/>: resolve to a slot,
        /// route through OnDashboardSwitched to emit FF kind=4. Returns false to
        /// defer (PollStatus retries).
        /// </summary>
        public bool ApplyTelemetryDashboardFromProfile(MozaProfile profile)
        {
            if (profile == null) return true;
            string? key = profile.TelemetryDashboardKey;
            if (string.IsNullOrEmpty(key)) return true;

            // Per-plugin-instance no-op: kind=4 already emitted for this key.
            string? lastApplied = GetLastAppliedKey();
            if (lastApplied != null
                && string.Equals(lastApplied, key, StringComparison.OrdinalIgnoreCase))
            {
                MozaLog.Debug("[AZOM] ApplyTelemetryDashboardFromProfile: already applied " +
                              key + " in this plugin instance — no-op");
                return true;
            }

            // Channel-readiness gate: kind=4 before preamble reaches Active is dropped
            // silently; defer during post-emit cooldown too.
            var sender = _plugin.TelemetrySender;
            var state = _plugin.WheelStateForDiagnostics;
            if (sender == null || !sender.IsActive || sender.IsInSilenceCooldown)
            {
                string reason = $"sender={(sender == null ? "null" : (sender.IsActive ? "Active" : "not-Active"))} " +
                                $"cooldown={sender?.IsInSilenceCooldown}";
                if (RecordDeferReason(reason))
                    MozaLog.Debug($"[AZOM] ApplyTelemetryDashboardFromProfile deferring (key={key}): {reason}");
                return false;
            }
            if (state == null || state.ConfigJsonList == null || state.ConfigJsonList.Count == 0)
            {
                string reason = $"state={(state == null ? "null" : $"listCount={state.ConfigJsonList?.Count ?? -1}")}";
                if (RecordDeferReason("wheel state not yet available — " + reason))
                    MozaLog.Debug($"[AZOM] ApplyTelemetryDashboardFromProfile deferring (key={key}): wheel state not yet available — {reason}");
                return false;
            }
            ClearDeferReason();

            // Resolve target dashboard name + branch-specific side data.
            string targetName;
            string mzdashPath = "";
            string sourceTag;

            if (key!.StartsWith("wheel:", StringComparison.OrdinalIgnoreCase))
            {
                string id = key.Substring("wheel:".Length);
                WheelDashboardEntry? match = null;
                if (state.EnabledDashboards != null)
                {
                    foreach (var entry in state.EnabledDashboards)
                    {
                        if (entry != null && string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                        {
                            match = entry;
                            break;
                        }
                    }
                }
                if (match == null)
                {
                    MozaLog.Info("[AZOM] Profile dashboard key not found in current wheel catalog (id=" +
                                 id + "); leaving current selection");
                    return true;
                }
                targetName = match.Title;
                sourceTag = $"wheel:{id} ('{match.Title}')";
            }
            else if (key.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // file:<filename>:<sha1-first-8> — filename → local mzdash + bare name for slot lookup.
                string remainder = key.Substring("file:".Length);
                int colon = remainder.LastIndexOf(':');
                string filename = colon > 0 ? remainder.Substring(0, colon) : remainder;
                string baseName = Path.GetFileNameWithoutExtension(filename);
                string? path = _plugin.DashCache?.TryGetFolderFilePath(baseName);
                bool localOk = !string.IsNullOrEmpty(path) && File.Exists(path);
                targetName = baseName;
                mzdashPath = localOk ? path! : "";
                sourceTag = $"file:{filename}" + (localOk ? $" (local: '{path}')" : " (local file missing)");
            }
            else if (key.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            {
                targetName = key.Substring("builtin:".Length);
                sourceTag = $"builtin:{targetName}";
            }
            else
            {
                MozaLog.Warn("[AZOM] Unknown TelemetryDashboardKey prefix: " + key);
                return true;
            }

            if (string.IsNullOrEmpty(targetName))
            {
                MozaLog.Warn("[AZOM] ApplyTelemetryDashboardFromProfile: empty target name for key " + key);
                return true;
            }

            // Slot lookup in the wheel's ConfigJsonList (slot = index, alphabetical library order).
            // Match by name uniformly across all three key kinds.
            int slot = -1;
            for (int i = 0; i < state.ConfigJsonList.Count; i++)
            {
                var name = state.ConfigJsonList[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    slot = i;
                    break;
                }
            }

            if (slot >= 0)
            {
                // Skip kind=4 if wheel already on target (or we just emitted for it),
                // unless the catalog re-sync probe fired (forces Stop+Start to re-advertise).
                bool wheelOnTargetSlot = sender.WheelReportedSlot == slot;
                bool weEmittedThisSlot = sender.LastEmittedKind4Slot == slot;
                // "Already bound" only holds once the host has emitted at least
                // one kind=4 this session — the display latches our value frames
                // (test or live) only after a host-initiated switch. At first
                // launch the wheel sits on its last slot (wheelOnTargetSlot) but
                // the host has never engaged it (LastEmittedKind4Slot < 0); the
                // old skip there left the dash blank until the user manually
                // switched to a different dashboard. Force the initial kind=4.
                bool hostEverEngaged = sender.LastEmittedKind4Slot >= 0;
                if ((wheelOnTargetSlot || weEmittedThisSlot) && hostEverEngaged)
                {
                    string bindEvidence = wheelOnTargetSlot
                        ? "wheel-reported slot"
                        : "prior host kind=4";
                    if (sender.HasCatalogResyncProbeFired)
                    {
                        MozaLog.Info($"[AZOM] Profile dashboard '{targetName}' (slot {slot}) bound ({bindEvidence}) but probe fired this instance (source: {sourceTag}); re-triggering switch to refresh binding");
                        _plugin.ActiveTelemetryProfileName = targetName;
                        _plugin.ActiveTelemetryMzdashPath = mzdashPath;
                        _plugin.PersistSettings();
                        OnDashboardSwitched((uint)slot);
                        _plugin.RaiseDashboardSelectionChangedInternal();
                        SetLastAppliedKey(key);
                        return true;
                    }
                    MozaLog.Info($"[AZOM] Profile dashboard '{targetName}' (slot {slot}) already bound ({bindEvidence}, no probe this instance, source: {sourceTag}); no wire action needed");
                    _plugin.ActiveTelemetryProfileName = targetName;
                    _plugin.ActiveTelemetryMzdashPath = mzdashPath;
                    _plugin.PersistSettings();
                    ApplyTelemetrySettings();
                    _plugin.RaiseDashboardSelectionChangedInternal();
                    SetLastAppliedKey(key);
                    return true;
                }
                MozaLog.Info($"[AZOM] Applying profile dashboard '{targetName}' via wheel slot {slot} (source: {sourceTag})");
                _plugin.ActiveTelemetryProfileName = targetName;
                _plugin.ActiveTelemetryMzdashPath = mzdashPath;
                _plugin.PersistSettings();
                OnDashboardSwitched((uint)slot);
                _plugin.RaiseDashboardSelectionChangedInternal();
                SetLastAppliedKey(key);
                return true;
            }

            // No matching wheel slot: fall back per branch.
            //   - wheel: not in ConfigJsonList → stop retrying.
            //   - file: local file exists → slotless restart; wheel keeps current binding.
            //   - file: local file missing AND no slot → leave current selection.
            //   - builtin: slotless OnDashboardSwitched restarts against the named builtin.
            if (key.StartsWith("wheel:", StringComparison.OrdinalIgnoreCase))
            {
                MozaLog.Info("[AZOM] Profile dashboard '" + targetName +
                             "' missing from configJsonList; leaving current selection");
                return true;
            }

            if (key.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(mzdashPath))
                {
                    MozaLog.Info("[AZOM] Profile dashboard file not resolvable and not in wheel catalog (" +
                                 targetName + "); leaving current selection");
                    return true;
                }
                MozaLog.Info("[AZOM] Applying profile dashboard (no wheel slot, local file): " + mzdashPath);
                _plugin.ActiveTelemetryMzdashPath = mzdashPath;
                _plugin.ActiveTelemetryProfileName = "";
                _plugin.PersistSettings();
                OnDashboardSwitched();
                _plugin.RaiseDashboardSelectionChangedInternal();
                SetLastAppliedKey(key);
                return true;
            }

            // builtin: fallback.
            MozaLog.Info("[AZOM] Applying profile dashboard (builtin, no wheel slot): " + targetName);
            _plugin.ActiveTelemetryProfileName = targetName;
            _plugin.ActiveTelemetryMzdashPath = "";
            _plugin.PersistSettings();
            OnDashboardSwitched();
            _plugin.RaiseDashboardSelectionChangedInternal();
            SetLastAppliedKey(key);
            return true;
        }

        /// <summary>Slot-aware dashboard switch: emits FF kind=4, awaits echo, then Stop+Start.</summary>
        public void OnDashboardSwitched(uint slot) => OnDashboardSwitched(slot, _plugin.TelemetrySender);

        /// <summary>Switch a specific sender's dashboard to <paramref name="slot"/>
        /// (FF kind=4 + Stop/Start). The wheel sender and the CM2 sender each switch
        /// their own device independently.</summary>
        public void OnDashboardSwitched(uint slot, TelemetrySender? sender)
        {
            if (sender == null || !sender.Enabled) return;
            bool isWheel = ReferenceEquals(sender, _plugin.TelemetrySender);
            MozaLog.Debug(
                $"[AZOM] OnDashboardSwitched(slot={slot}, target={(isWheel ? "wheel" : "cm2")}): scheduling switch + Stop+Start");
            // Stage the target's settings first so the post-Start cold-start builds
            // tier-def from the right channels (wheel: ApplyTelemetrySettings; CM2:
            // EnsureCm2Pipeline re-applies its policy/resolver/mapping target).
            if (isWheel) { ClearPendingDashboardKey(); ApplyTelemetrySettings(); }
            else _plugin.EnsureCm2Pipeline();
            // SwitchToProfile emits FF kind=4 then runs Stop+Start; profile already
            // staged so pass null to keep current.
            sender.SwitchToProfile(slot, null);
        }

        /// <summary>
        /// Slot-less dashboard switch entry point. Used by file-mode, builtin-
        /// fallback paths where ConfigJsonList doesn't expose a slot, and by UI
        /// handlers that change the active dashboard without a wheel slot index
        /// (combo selection of "(none)" / a builtin name, custom-mzdash load,
        /// clear-mzdash). No FF kind=4; sender cycles Stop+Start so the new
        /// profile takes effect. The ~11s silence gate inside StartInner gives
        /// the wheel time to settle its sess=0x09 dashboard-binding state.
        /// </summary>
        public void OnDashboardSwitched()
        {
            // Manual action wins: abandon any pending profile-driven switch.
            ClearPendingDashboardKey();

            var sender = _plugin.TelemetrySender;
            if (sender != null && sender.Enabled)
            {
                MozaLog.Debug("[AZOM] OnDashboardSwitched: scheduling Stop+Start pipeline cycle (no slot)");
                ApplyTelemetrySettings();
                sender.RestartForSwitch();
            }
        }

        /// <summary>
        /// Handler for <see cref="TelemetrySender.DashboardPipelineParked"/>.
        /// Resets the telemetry-start gate so a subsequent hot-swap / user toggle
        /// can re-attempt starting.
        /// </summary>
        public void OnDashboardPipelineParked(object? sender, EventArgs e)
        {
            Interlocked.Exchange(ref _plugin._telemetryStartRequested, 0);
        }

        /// <summary>
        /// Handler for <see cref="TelemetrySender.WheelInitiatedSwitch"/>: clears the
        /// staged profile so the catalog-only synth rebuilds for the new dash. Does
        /// NOT persist — wheel-side nav is transient.
        /// </summary>
        public void OnWheelInitiatedSwitch(int slot)
        {
            try
            {
                var sender = _plugin.TelemetrySender;
                if (sender == null || !sender.Enabled) return;

                var state = _plugin.WheelStateForDiagnostics;
                if (state == null || state.ConfigJsonList == null
                    || slot < 0 || slot >= state.ConfigJsonList.Count)
                {
                    MozaLog.Warn(
                        $"[AZOM] WheelInitiatedSwitch slot={slot}: cannot resolve dashboard name " +
                        $"(state={(state == null ? "null" : "ok")}, " +
                        $"listCount={state?.ConfigJsonList?.Count ?? -1}). " +
                        $"Tier-def burst will use stale profile.");
                    return;
                }

                string newName = state.ConfigJsonList[slot];
                if (string.IsNullOrEmpty(newName))
                {
                    MozaLog.Warn($"[AZOM] WheelInitiatedSwitch slot={slot}: configJsonList entry is empty");
                    return;
                }

                // Wheel-side navigation is transient and ALWAYS catalog-only:
                // rebuild the subscription from the wheel's live catalog for the
                // new dash, exactly like a host switch (ApplyTelemetrySettings
                // forces profile=null — the mzdash never drives channels). This
                // used to resolve a builtin/cache profile and stage it, which
                // (a) sent that profile's channel set instead of the wheel's —
                // an 8-channel builtin "Core" while the wheel's Core catalog has
                // 72 — and (b) stuck, because the synth tick-path refuses to
                // replace a non-synth profile (MaybeSwapProfileForCatalog
                // returns early): after a Core→Marco wheel switch the host kept
                // emitting Core's 8 channels while the wheel displayed Marco.
                //
                // Clear ANY non-synth profile (not only the CatalogProfileName
                // synth) so the tick path rebuilds for the new dash AND a
                // previously-stuck staged profile recovers on the next switch.
                // Safe because the catalog-only path never leaves a user mzdash
                // in sender.Profile (ApplyTelemetrySettings forces it null), so
                // the only non-null value here is the synth or a stale staged
                // one. NO writes to persisted settings — the saved profile
                // preference is the user's intent; wheel nav must not clobber it.
                if (sender.Profile != null)
                    sender.Profile = null;

                MozaLog.Info(
                    $"[AZOM] WheelInitiatedSwitch slot={slot} ('{newName}'): " +
                    $"catalog-only — rebuilding synthesised profile from post-switch wheel catalog");

                // UI dropdown reads sender.WheelReportedSlot directly when building
                // the selection (not ActiveTelemetryProfileName), so the dropdown
                // reflects the wheel's actual current dash without touching the
                // persisted preference.
                _plugin.RaiseDashboardSelectionChangedInternal();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] OnWheelInitiatedSwitch handler error: {ex.Message}");
            }
        }

        public void SetTelemetryEnabled(bool enabled)
        {
            _plugin.ActiveTelemetryEnabled = enabled;
            _plugin.SaveSettings();
            if (enabled)
            {
                // Explicit user re-enable clears any prior park / restart
                // budget so the new attempt starts from a clean slate. Without
                // this, a previously-parked pipeline (e.g. sess=0x09 retry
                // exhausted) refuses subsequent recovery attempts and the
                // user toggle has no visible effect.
                _plugin.TelemetrySender?.Recovery.Reset();
                ApplyTelemetrySettings();
                StartTelemetryIfReady();
            }
            else
            {
                _plugin.TelemetrySender?.Stop();
                // Reset guards so re-enable can start a fresh session — otherwise
                // FramesSent>0 and _telemetryStartRequested==1 cause StartTelemetryIfReady
                // to bail on re-enable.
                Interlocked.Exchange(ref _plugin._telemetryStartRequested, 0);
            }
        }

        /// <summary>Start the telemetry sender when preconditions are met. Dispatched off the read thread.</summary>
        public void StartTelemetryIfReady()
        {
            var t = _plugin.TelemetrySender;
            if (t == null) return;
            if (!_plugin.ActiveTelemetryEnabled) return;
            // A standalone-USB CM2 streams over its own connection while the
            // wheelbase connection may be absent (no base), so accept either.
            if (!_connection.IsConnected && !_plugin.DashboardUsbConnected) return;
            // Standalone dashboard (CM2) drives the pipeline without any wheel
            // attached. Allow start as long as either a wheel detected OR a
            // standalone dashboard is the connection target.
            bool standaloneDashboard = _plugin.ShouldUseStandaloneDashboardTarget();
            if (!_detectionState.NewWheelDetected
                && !_detectionState.OldWheelDetected
                && !standaloneDashboard) return;

            // FSR V1 (group-0x42 display push) is driven by the standalone
            // Telemetry/Fsr1DisplayDriver (started from MozaPlugin), NOT by this
            // tier-def sender — so it is not handled here at all.

            // Capability gate: known displayless wheels never get the dashboard
            // pipeline; unknown models fall back to the runtime probe. CM2
            // standalone is always a dashboard — skip the wheel-display gate.
            if (!standaloneDashboard && !_plugin.ShouldDriveDashboard())
            {
                MozaLog.Info(
                    $"[AZOM] Wheel '{_data?.WheelModelName}' has no display " +
                    $"(HasDisplay={_plugin.WheelModelInfo?.HasDisplay?.ToString() ?? "unknown"}, " +
                    $"probe={_plugin.IsDisplayDetected}) — skipping dashboard telemetry start");
                return;
            }

            // Display-readiness gate. Even when WheelModelInfo says "has
            // display", the display sub-device boots ~18 s after the wheel
            // MCU becomes responsive on hot-attach (verified W17 capture
            // 2026-05-25). Starting the session pipeline while the display
            // is still booting means the wheel never acks our sess=0x01/0x02
            // opens and never engages the catalog push — dashboard layout
            // renders locally on the wheel but no channel data ever reaches
            // it. The display-model-name handler in DeviceProber.cs (line
            // ~512) re-calls StartTelemetryIfReady once the probe completes,
            // and PollStatus re-probes every 5 s while the wheel is
            // detected but the display isn't — so deferring here is safe
            // and self-recovering. Standalone CM2 dashboards skip this gate
            // (they ARE the dashboard target, no separate sub-device boot).
            if (!standaloneDashboard && !_plugin.IsDisplayDetected)
            {
                MozaLog.Debug(
                    $"[AZOM] Display sub-device not yet detected " +
                    $"(HasDisplay={_plugin.WheelModelInfo?.HasDisplay?.ToString() ?? "unknown"}) — " +
                    "deferring telemetry start until display probe completes");
                return;
            }

            // We're past ActiveTelemetryEnabled, so telemetry IS enabled for this
            // wheel. Sync the sender's per-profile flag here so it's correct even
            // when an earlier ApplyProfile fired before the wheel GUID resolved.
            // Done BEFORE the FramesSent short-circuit because the canonical
            // recovery scenario is: persistent sender running (FramesSent>0)
            // with ProfileTelemetryEnabled=false from a race during plugin
            // hot-reload. The old order returned at FramesSent>0 before this
            // flag flipped, leaving emission permanently suppressed until the
            // user manually re-enabled — exactly the 2026-05-27 CS-Pro bundle
            // symptom. Setting it here makes the persistent-sender path heal
            // itself on the next wheel-detect tick instead of needing user
            // intervention.
            t.ProfileTelemetryEnabled = true;

            // Already running — don't restart.
            if (t.FramesSent > 0) return;

            // Prevent duplicate dispatch.
            if (Interlocked.CompareExchange(ref _plugin._telemetryStartRequested, 1, 0) != 0) return;

            MozaLog.Info("[AZOM] Wheel detected and telemetry enabled — starting telemetry sender");
            // Top-level catch: ThreadPool callback exceptions on .NET Framework 4.8
            // can take down the SimHub host process.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { t.Start(); }
                catch (ObjectDisposedException) { /* plugin disposed mid-start */ }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Telemetry start failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// PollStatus tick hook: retry a deferred profile-driven dashboard switch
        /// once the wheel catalog arrives. Stops retrying after
        /// <see cref="PendingProfileKeyTimeout"/>. Emits a Warn every
        /// <see cref="RetryWarnInterval"/> (30s) so silent multi-minute waits
        /// don't go unnoticed in the log.
        /// </summary>
        public void TickPendingDashboardRetry()
        {
            // Snapshot under lock — never touch _pending directly outside the lock.
            PendingApply? snap;
            lock (_stateLock) { snap = _pending; }
            if (snap == null) return;

            var now = DateTime.UtcNow;
            if (now > snap.DeadlineUtc)
            {
                MozaLog.Warn("[AZOM] Pending profile dashboard apply timed out after " +
                             $"{PendingProfileKeyTimeout.TotalMinutes:F0} min and {snap.RetryCount} retries " +
                             $"(key={snap.Key}); giving up — wheel did not advertise dashboards in time");
                lock (_stateLock)
                {
                    // Only clear if it's still the same pending; a concurrent
                    // SetPendingDashboardKey may have replaced it under us.
                    if (ReferenceEquals(_pending, snap)) _pending = null;
                }
                return;
            }

            var profile = _plugin.Settings.ProfileStore?.CurrentProfile;
            if (profile == null ||
                !string.Equals(profile.TelemetryDashboardKey, snap.Key, StringComparison.OrdinalIgnoreCase))
            {
                // Profile changed under us — drop the stale pending key.
                MozaLog.Debug($"[AZOM] Pending profile dashboard apply abandoned — profile/key mismatch " +
                              $"(pending={snap.Key}, current={(profile == null ? "null" : profile.TelemetryDashboardKey ?? "(empty)")})");
                lock (_stateLock)
                {
                    if (ReferenceEquals(_pending, snap)) _pending = null;
                }
                return;
            }

            bool applied;
            try
            {
                applied = ApplyTelemetryDashboardFromProfile(profile);
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] Pending dashboard apply retry threw: " + ex.Message);
                lock (_stateLock)
                {
                    if (ReferenceEquals(_pending, snap)) _pending = null;
                }
                return;
            }

            lock (_stateLock)
            {
                // Concurrent writer may have superseded our snapshot — bail
                // without mutating in that case.
                if (!ReferenceEquals(_pending, snap)) return;

                if (applied)
                {
                    _pending = null;
                    return;
                }

                // Still deferring. Increment retry count and, if it's been
                // RetryWarnInterval since the last warn, emit one. Don't spam
                // every PollStatus tick — that's why the throttle exists.
                int newCount = snap.RetryCount + 1;
                if (now - snap.LastRetryWarnUtc >= RetryWarnInterval)
                {
                    MozaLog.Warn($"[AZOM] Profile dashboard apply still pending after {newCount} retries " +
                                 $"(key={snap.Key}, reason={_lastApplyDeferReason ?? "?"})");
                    _pending = snap.With(retryCount: newCount, lastRetryWarnUtc: now);
                }
                else
                {
                    _pending = snap.With(retryCount: newCount);
                }
            }
        }
    }
}
