using System;
using System.Collections.Generic;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// Settings persistence (debounced save, clear/reset) plus the SimHub
    /// profile system: profile-store init/subscription, profile apply, and the
    /// per-wheel-page accessor family (overlay, telemetry enable/name/path,
    /// sleep/idle bundles) with the wheel-reported seed methods.
    /// Settings are read live via <c>_plugin.Settings</c>; only the moved
    /// <see cref="ClearSettings"/> replaces the backing field.
    /// </summary>
    internal sealed class ProfileCoordinator
    {
        private readonly MozaPlugin _plugin;

        internal ProfileCoordinator(MozaPlugin plugin)
        {
            _plugin = plugin;
        }

        internal void SaveSettings()
        {
            // Resolve the current dashboard key (wheel:<id> > file:<...> > builtin:<name>)
            // so the active SimHub profile records which dashboard the user picked.
            // Re-applied on profile load so each game keeps its own dashboard selection.
            string? activeDashKey = null;
            try
            {
                var cands = _plugin.ChannelMapping.GetActiveDashboardKeyCandidates();
                if (cands.Count > 0) activeDashKey = cands[0];
            }
            catch { /* candidate resolver is conservative; ignore early-init errors */ }
            _plugin.Settings.ProfileStore?.CurrentProfile?.CaptureFromCurrent(_plugin.Settings, _plugin.Data, activeDashKey);
            // Single source of truth = profile + overlay. UI handlers write
            // overlay/profile directly; CaptureFromCurrent picks up device-read
            // state. No more legacy slot/UID mirror.
            ScheduleSave();
        }

        internal void PersistSettings()
        {
            ScheduleSave();
        }

        // Trace log helper — emit the active wheel page's sleep bundle state
        // so we can correlate disk-write contents with what the user reported.
        // Cheap (single string format) and only fires at save points, not per-tick.
        private void LogSleepBundleStateForSaveTrace(string trigger)
        {
            try
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue) { MozaLog.Debug($"[AZOM] SLEEP-TRACE [{trigger}]: page guid unresolvable"); return; }
                var dict = _plugin.Settings?.WheelSleepByPageGuid;
                if (dict == null || !dict.TryGetValue(g.Value, out var b) || b == null)
                {
                    MozaLog.Debug($"[AZOM] SLEEP-TRACE [{trigger}]: page={g.Value.ToString().Substring(0,8)} bundle=null");
                    return;
                }
                MozaLog.Info($"[AZOM] SLEEP-TRACE [{trigger}]: page={g.Value.ToString().Substring(0,8)} Mode={b.Mode} TimeoutMin={b.TimeoutMin} SpeedMs={b.SpeedMs}");
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] SLEEP-TRACE failed: {ex.Message}"); }
        }

        private readonly object _saveDebounceLock = new object();

        // Debounce disk writes during rapid slider changes
        private Timer? _saveDebounceTimer;
        private int _saveFailureStreak;

        /// <summary>
        /// Debounce disk writes: restart a 500ms timer on each call.
        /// Prevents dozens of writes per second during rapid slider drags.
        /// </summary>
        internal void ScheduleSave()
        {
            // Lazy-create under a lock — concurrent callers (UI thread + profile-change
            // thread) would otherwise both see null, each create a Timer, and the loser's
            // instance would leak (unstopped, unwatched, still referencing _settings).
            lock (_saveDebounceLock)
            {
                _saveFailureStreak = 0;
                if (_saveDebounceTimer == null)
                {
                    _saveDebounceTimer = new Timer(500) { AutoReset = false };
                    _saveDebounceTimer.Elapsed += OnSaveDebounceElapsed;
                }
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        private void OnSaveDebounceElapsed(object? s, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                LogSleepBundleStateForSaveTrace("debounced-save");
                _plugin.SaveCommonSettings("MozaPluginSettings", _plugin.Settings);
                _saveFailureStreak = 0;
            }
            catch (Exception ex)
            {
                // AutoReset=false + Timer swallowing throws would silently drop
                // the save. Retry a few times, then surface and wait for the
                // next change (ScheduleSave resets the streak).
                if (++_saveFailureStreak <= 3)
                {
                    MozaLog.Warn($"[AZOM] Debounced settings save failed (retry {_saveFailureStreak}/3): {ex.Message}");
                    try { _saveDebounceTimer?.Start(); } catch { }
                }
                else
                    MozaLog.Error($"[AZOM] Debounced settings save failed repeatedly — waiting for next change: {ex.Message}");
            }
        }

        /// <summary>End()/CleanupPartialInit teardown step 1: stop the debounce
        /// timer so no new save callback fires against disposed state.</summary>
        internal void StopSaveDebounceTimer()
        {
            _saveDebounceTimer?.Stop();
        }

        /// <summary>End()/CleanupPartialInit teardown: dispose the debounce timer
        /// after I/O is gone.</summary>
        internal void DisposeSaveDebounceTimer()
        {
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = null;
        }

        internal void ClearSettings()
        {
            _plugin.TelemetrySender?.Stop();
            // Fresh-install defaults, not a bare `new`: reset must land the user
            // where a first-time install would, or "clear all settings" silently
            // hands them a worse configuration than a clean install (dashboard
            // telemetry off for new wheels, wheelbase LFE off ShakeIt).
            _plugin._settings = MozaPluginSettings.CreateForNewInstall();
            _plugin.SaveCommonSettings("MozaPluginSettings", _plugin.Settings);
            // InitProfileSystem re-pushes the master-mapper defaults from the
            // freshly-seeded profile, replacing the cleared set's snapshot.
            InitProfileSystem();
        }

        // Tracks the ProfileStore we subscribed CurrentProfileChanged on, so we can
        // detach when ClearSettings replaces _settings (orphaned subscription would
        // otherwise mutate plugin state via captured `this` from a dead store).
        private MozaProfileStore? _subscribedProfileStore;

        /// <summary>
        /// Initialize the native SimHub profile system.
        /// ProfileSettingsBase.Init() reads the current game from PluginManager and selects the right profile.
        /// </summary>
        internal void InitProfileSystem()
        {
            var store = _plugin.Settings.ProfileStore;

            // Ensure at least one default profile exists. Seed its baselines
            // from the legacy MozaPluginSettings flat fields so pre-refactor
            // users (whose JSON has no profile entries at all) get sane
            // Seed the baseline so first-launch writes (e.g. DashDisplayBrightness)
            // don't sit at the -1 sentinel and leave the display dark.
            if (store.Profiles.Count == 0)
            {
                var defaultProfile = new MozaProfile { Name = "Default" };
                defaultProfile.SeedBaselineFromFlatFields(_plugin.Settings);
                store.Profiles.Add(defaultProfile);
            }

            // Init reads PluginManager.Instance.GameName and selects the matching profile
            store.Init();

            // Detach prior subscription before re-subscribing (ClearSettings replaces _settings).
            if (_subscribedProfileStore != null && !ReferenceEquals(_subscribedProfileStore, store))
                _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;

            // Subscribe to profile changes (game switch, manual selection)
            store.CurrentProfileChanged += OnProfileChanged;
            _subscribedProfileStore = store;

            // The master mapper's channel defaults ride a store of their own. Bring it
            // up here — before the apply below, so the defaults are published ahead of
            // any telemetry-profile build — but keep it off the device-profile
            // lifecycle: switching one must never switch the other.
            InitChannelDefaultsStore();

            // Apply the initially selected profile
            if (store.CurrentProfile != null)
            {
                MozaLog.Debug($"[AZOM] Initial profile: {store.CurrentProfile.Name}");
                if (_plugin.Settings.AutoApplyProfileOnLaunch)
                    ApplyProfile(store.CurrentProfile);
                else
                    MozaLog.Debug("[AZOM] Skipping auto-apply (disabled in Options)");
            }
        }

        // Tracks the channel-defaults store we subscribed on, mirroring
        // _subscribedProfileStore above (ClearSettings replaces both).
        private MozaChannelDefaultsStore? _subscribedChannelDefaultsStore;

        /// <summary>
        /// Bring up the master mapper's own profile store: seed, drain the legacy
        /// plugin-global set into it, let SimHub pick the profile for the running game,
        /// then publish it. Deliberately independent of the device-profile store — the
        /// only thing the two share is this method's call site.
        /// </summary>
        private void InitChannelDefaultsStore()
        {
            var store = _plugin.Settings?.ChannelDefaultsStore;
            if (store == null) return;

            if (store.Profiles.Count == 0)
                store.Profiles.Add(new MozaChannelDefaultsProfile { Name = "Default" });

            // Drain before Init so the profile SimHub selects already carries them.
            MigrateMasterDefaultsToProfiles(store);

            store.Init();

            if (_subscribedChannelDefaultsStore != null
                && !ReferenceEquals(_subscribedChannelDefaultsStore, store))
                _subscribedChannelDefaultsStore.CurrentProfileChanged -= OnChannelDefaultsProfileChanged;
            store.CurrentProfileChanged += OnChannelDefaultsProfileChanged;
            _subscribedChannelDefaultsStore = store;

            // Publish before anything can build a telemetry profile, so the first
            // cold-start tier-def already resolves against these defaults (no dashboard
            // switch needed to pick them up).
            _plugin.ChannelMapping.PushProfileDefaults();
        }

        /// <summary>A different channel-defaults profile became active (the master
        /// mapper's selector, or SimHub switching it for the running game). Republish —
        /// the dashboard store's snapshot is one process-wide static — then rebind the
        /// live senders. No hardware writes: this store holds nothing but mappings.</summary>
        private void OnChannelDefaultsProfileChanged(object sender, EventArgs e)
        {
            var name = _plugin.Settings?.ChannelDefaultsStore?.CurrentProfile?.Name;
            MozaLog.Info($"[AZOM] Channel defaults profile changed: {name ?? "<none>"}");
            _plugin.ChannelMapping.PushProfileDefaults();
            // Wire-neutral (only each channel's SimHubProperty changes), so a live
            // sender picks the new bindings up on its next frame with no tier-def
            // re-emit. No-ops until a catalog generation is committed.
            try { _plugin.ChannelMapping.ReResolveAll(); }
            catch (Exception ex) { MozaLog.Warn("[AZOM] Channel defaults re-resolve failed: " + ex.Message); }
        }

        /// <summary>
        /// One-shot drain of the retired plugin-global master channel defaults
        /// (<c>MozaPluginSettings.TelemetryDefaultMappings</c>) into the channel-defaults
        /// store's first profile. Fill-only: a URL that profile already maps wins, so a
        /// re-run could never clobber a user edit. Clears the legacy dict afterwards so
        /// nothing reads it again.
        ///
        /// <para>Sentinel-guarded rather than keyed on the legacy dict being empty: a
        /// user who drains, then clears those mappings, must not get them re-seeded on
        /// the next launch.</para>
        /// </summary>
        private void MigrateMasterDefaultsToProfiles(MozaChannelDefaultsStore store)
        {
            var settings = _plugin.Settings;
            if (settings == null || settings.MasterDefaultsMigratedToProfiles) return;
            settings.MasterDefaultsMigratedToProfiles = true;

            var legacy = settings.TelemetryDefaultMappings;
            if (legacy == null || legacy.Count == 0) return;

            // Seeded above, so this is the "Default" profile on a first migration; on a
            // store that already has profiles it is whichever sorts first — either way
            // the user's single global set has exactly one sensible landing place.
            var target = store.Profiles.Count > 0 ? store.Profiles[0] : null;
            if (target == null) return;

            // COW: PushProfileDefaults may already have published this dict's reference.
            var next = target.Mappings != null
                ? new Dictionary<string, string>(target.Mappings, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var kv in legacy)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (next.ContainsKey(kv.Key)) continue;
                next[kv.Key] = kv.Value.Trim();
                added++;
            }
            if (added > 0) target.Mappings = next;

            settings.TelemetryDefaultMappings =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MozaLog.Info($"[AZOM] Migrated {added} master channel default(s) "
                         + $"into channel-defaults profile \"{target.Name}\"");
            // Land it now rather than waiting for an unrelated save. ScheduleSave, not
            // SaveSettings — the latter runs CaptureFromCurrent against the DEVICE
            // profile, and a partial device read could write sentinels into it.
            ScheduleSave();
        }

        /// <summary>End()/CleanupPartialInit teardown: detach the CurrentProfileChanged
        /// subscription so an in-flight profile-change callback cannot reach the
        /// plugin during teardown.</summary>
        internal void DetachProfileStore()
        {
            if (_subscribedProfileStore != null)
                _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;
            _subscribedProfileStore = null;
            if (_subscribedChannelDefaultsStore != null)
                _subscribedChannelDefaultsStore.CurrentProfileChanged -= OnChannelDefaultsProfileChanged;
            _subscribedChannelDefaultsStore = null;
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            var profile = _plugin.Settings.ProfileStore.CurrentProfile;
            if (profile != null)
            {
                MozaLog.Info($"[AZOM] Profile changed: {profile.Name}");
                ApplyProfile(profile);
            }
        }

        /// <summary>
        /// One-shot repair: null every saved knob palette (per-knob Active / per-LED
        /// ring, overlay and baseline) whose slots are all black. Those arrays were
        /// laundered from an unseeded <see cref="MozaData"/> mirror by the old
        /// whole-array persist / device-JSON capture paths, and every apply re-wrote
        /// them to the wheel. Runs from Init before the profile system starts.
        /// </summary>
        internal void RepairAllBlackKnobColorArrays()
        {
            var store = _plugin.Settings?.ProfileStore;
            if (store == null) return;
            int cleared = 0;
            foreach (var profile in store.Profiles)
            {
                if (profile == null) continue;
                if (MozaProfile.IsAllBlack(profile.WheelKnobPrimaryColors)) { profile.WheelKnobPrimaryColors = null; cleared++; }
                if (MozaProfile.IsAllBlack(profile.WheelKnobRingColors))    { profile.WheelKnobRingColors    = null; cleared++; }
                var overrides = profile.WheelOverridesByPageGuid;
                if (overrides == null) continue;
                foreach (var ov in overrides.Values)
                {
                    if (ov == null) continue;
                    if (MozaProfile.IsAllBlack(ov.WheelKnobPrimaryColors)) { ov.WheelKnobPrimaryColors = null; cleared++; }
                    if (MozaProfile.IsAllBlack(ov.WheelKnobRingColors))    { ov.WheelKnobRingColors    = null; cleared++; }
                }
            }
            if (cleared > 0)
            {
                MozaLog.Info($"[AZOM] Cleared {cleared} all-black saved knob palette array(s)");
                PersistSettings();
            }
        }

        /// <summary>
        /// Apply a profile by routing through the consolidated Apply*ToHardware
        /// methods. Each method mirrors profile/overlay values into _data (always)
        /// and writes to hardware when the matching device is detected.
        /// </summary>
        internal void ApplyProfile(MozaProfile profile)
        {
            MozaLog.Debug($"[AZOM] Applying profile: {profile.Name}");
            _plugin.HardwareApplier.ApplyProfileHardware(profile);

            // Persist without re-capturing _data — profile already has the values
            // we just applied; concurrent device reads could have overwritten _data
            // before our writes were processed.
            PersistSettings();

            // Apply profile-recorded dashboard preference after wheel settings are
            // in place. Defer to next PollStatus tick when wheel catalog isn't ready.
            if (!string.IsNullOrEmpty(profile.TelemetryDashboardKey))
            {
                bool applied = false;
                try { applied = _plugin.ApplyTelemetryDashboardFromProfile(profile); }
                catch (Exception ex)
                {
                    MozaLog.Warn("[AZOM] ApplyTelemetryDashboardFromProfile threw: " + ex.Message);
                    applied = true;
                }
                if (!applied)
                {
                    _plugin.DashboardBindingCoordinator.SetPendingDashboardKey(profile.TelemetryDashboardKey!);
                    MozaLog.Debug("[AZOM] Profile dashboard apply deferred — wheel state not ready");
                }
                else
                {
                    _plugin.DashboardBindingCoordinator.ClearPendingDashboardKey();
                }
            }

            // Telemetry-enable state is wheel-level, not profile-level — see
            // the design comment on WheelTelemetryEnabledByPageGuid: "Whether
            // telemetry runs for a wheel is a wheel-level decision; the per-
            // game decision (which dashboard, which mzdash) stays on the
            // profile's WheelOverride." A SimHub profile change doesn't
            // change which physical wheel is attached, so re-evaluating
            // ProfileTelemetryEnabled here is incorrect — the state should
            // only change in response to user toggle (SetTelemetryEnabled)
            // or a wheel physically attaching/detaching (StartTelemetryIfReady
            // line 760 syncs on wheel detect; OnSerialDisconnected handles
            // detach via Stop). The prior re-evaluation here caused a silent
            // dash-freeze when a plugin hot-reload ran ApplyProfile before
            // WheelDeviceExtension.Init populated WheelModelName (observed
            // 2026-05-27 CS-Pro bundle: 3 ms race killed value-frame
            // emission until manual re-enable).
            //
            // We still apply telemetry settings (dashboard mapping, mzdash
            // resolution) and kick StartTelemetryIfReady so an inactive
            // sender starts up — but we leave ProfileTelemetryEnabled alone.
            try
            {
                _plugin.ApplyTelemetrySettings();
                _plugin.StartTelemetryIfReady();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Telemetry sync after profile apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Look up the wheel overlay for the currently-connected wheel in the given
        /// profile. Returns null if either the page GUID can't be resolved or the
        /// overlay isn't present.
        /// </summary>
        internal WheelOverride? GetCurrentWheelOverlay(MozaProfile? profile)
        {
            if (profile == null) return null;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            if (profile.WheelOverridesByPageGuid == null) return null;
            return profile.WheelOverridesByPageGuid.TryGetValue(g.Value, out var ov) ? ov : null;
        }

        /// <summary>
        /// Get or create the wheel overlay for the currently-connected wheel.
        /// Returns null only when the wheel hasn't identified itself yet.
        /// </summary>
        internal WheelOverride? GetOrCreateCurrentWheelOverlay(MozaProfile? profile)
        {
            if (profile == null) return null;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            // COW swap, like the sleep/idle bundles: the profile store serializes
            // this dict while the UI and SimHub's SetSettings path insert into it.
            lock (_pageBundleSwapLock)
            {
                var dict = profile.WheelOverridesByPageGuid;
                if (dict != null && dict.TryGetValue(g.Value, out var ov) && ov != null)
                    return ov;
                ov = new WheelOverride();
                var next = dict == null
                    ? new Dictionary<Guid, WheelOverride>()
                    : new Dictionary<Guid, WheelOverride>(dict);
                next[g.Value] = ov;
                profile.WheelOverridesByPageGuid = next;
                return ov;
            }
        }

        /// <summary>
        /// Apply <paramref name="mutator"/> to the active wheel's overlay on the
        /// current profile. No-op if no profile is selected or no wheel is
        /// identified. Used by UI handlers to mirror their edits into the
        /// profile-scoped overlay alongside the legacy flat-field write during
        /// the R4 transition.
        /// </summary>
        internal void UpdateActiveWheelOverlay(Action<WheelOverride> mutator)
        {
            if (mutator == null) return;
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            var overlay = GetOrCreateCurrentWheelOverlay(profile);
            if (overlay == null) return;
            mutator(overlay);
        }

        /// <summary>
        /// Apply <paramref name="mutator"/> to the current profile (or no-op if
        /// no profile is selected). Used by UI handlers that own profile-level
        /// fields (motor/FFB/handbrake/pedals/dash/base-ambient).
        /// </summary>
        internal void UpdateActiveProfile(Action<MozaProfile> mutator)
        {
            if (mutator == null) return;
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            mutator(profile);
        }

        // ===== Active telemetry view — current wheel's overlay accessors =====
        // Returns "telemetry off" defaults when no wheel/profile yet.

        /// <summary>
        /// True iff telemetry is enabled for the current wheel page. Per-wheel-page
        /// (shared across profiles); reads return false when wheel not identified.
        ///
        /// Dict-missing is "no opinion", never a hard off. A wheel with a SCREEN
        /// resolves that to on — it exists to show a dashboard, so it streams until
        /// the user says otherwise, matching <see cref="ActiveDashTelemetryEnabled"/>.
        /// Screenless and unknown models fall back to
        /// <see cref="MozaPluginSettings.TelemetryEnabledDefaultForNewWheels"/>.
        ///
        /// That install-wide flag alone was not enough: it is only set true by the
        /// ReadCommonSettings create-if-not-found factory, so every settings file
        /// written before it existed resolves false — and a user who later attaches a
        /// NEW display wheel gets a silently dark dashboard with no banner and no log
        /// line (bundle NZW8W197, KS Pro on a pre-existing install). The default has
        /// to be scoped to the wheel, not the install.
        /// </summary>
        internal bool ActiveTelemetryEnabled
        {
            get
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue || _plugin.Settings?.WheelTelemetryEnabledByPageGuid == null) return false;
                if (_plugin.Settings.WheelTelemetryEnabledByPageGuid.TryGetValue(g.Value, out var v))
                    return v;
                // IsFsr1DisplayWheel is load-bearing: FSR V1 carries hasDisplay:false
                // in WheelModelInfo (its screen rides the group-0x42 Fsr1DisplayDriver,
                // not the tier-def sender) yet that driver gates on this same property.
                if (_plugin.WheelModelInfo?.HasDisplay == true || _plugin.IsFsr1DisplayWheel)
                    return true;
                return _plugin.Settings.TelemetryEnabledDefaultForNewWheels;
            }
            set
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue) return;
                var s = _plugin.Settings;
                if (s == null) return;
                // COW swap: the save debounce serializes this dict off-thread.
                lock (_pageBundleSwapLock)
                {
                    var next = s.WheelTelemetryEnabledByPageGuid == null
                        ? new Dictionary<Guid, bool>()
                        : new Dictionary<Guid, bool>(s.WheelTelemetryEnabledByPageGuid);
                    next[g.Value] = value;
                    s.WheelTelemetryEnabledByPageGuid = next;
                }
            }
        }

        /// <summary>
        /// True iff dashboard telemetry is enabled for the CM2/CM1 dash pipeline.
        /// A dash is not a wheel: a hub-only or dash-only rig resolves no wheel page
        /// GUID, so <see cref="ActiveTelemetryEnabled"/> reads false and its setter
        /// no-ops there — never gate the dash pipeline on it.
        ///
        /// Resolution order: explicit entry under <see cref="MozaPlugin.Cm2PageGuid"/>
        /// (user toggled it on the dash page) → the wheel page's resolved value while
        /// a wheel IS identified (one shared toggle for wheel+dash rigs, install default
        /// included) → on. Dict-missing is "no opinion", never a hard off.
        ///
        /// The final "on" is deliberate: with no wheel identified there is no wheel
        /// setting to inherit and <see cref="MozaPluginSettings.TelemetryEnabledDefaultForNewWheels"/>
        /// is not a signal about a dash (it is keyed on wheels, and reads false for every
        /// pre-existing install). Falling through to it left a hub-only / dash-only rig
        /// with its only display dark AND — because the dash pipeline is what used to
        /// drive the CM1 discriminator — with its CM1 stuck wearing the speculative CM2
        /// device definition (bundle MGXWJ3YH). A dash on a wheel-less rig IS the display,
        /// so it streams unless the user says otherwise on the dash page.
        /// </summary>
        internal bool ActiveDashTelemetryEnabled
        {
            get
            {
                var s = _plugin.Settings;
                if (s?.WheelTelemetryEnabledByPageGuid == null) return false;
                if (s.WheelTelemetryEnabledByPageGuid.TryGetValue(MozaPlugin.Cm2PageGuid, out var v))
                    return v;
                if (_plugin.GetCurrentWheelPageGuid().HasValue) return ActiveTelemetryEnabled;
                return true;
            }
            set
            {
                var s = _plugin.Settings;
                if (s == null) return;
                lock (_pageBundleSwapLock)
                {
                    var next = s.WheelTelemetryEnabledByPageGuid == null
                        ? new Dictionary<Guid, bool>()
                        : new Dictionary<Guid, bool>(s.WheelTelemetryEnabledByPageGuid);
                    next[MozaPlugin.Cm2PageGuid] = value;
                    s.WheelTelemetryEnabledByPageGuid = next;
                }
            }
        }

        /// <summary>Active wheel's dashboard profile name (cache key / builtin name). "" when unset.</summary>
        internal string ActiveTelemetryProfileName
        {
            get
            {
                var ov = GetCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);
                return ov?.TelemetryProfileName ?? "";
            }
            set
            {
                var ov = GetOrCreateCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);
                if (ov != null) ov.TelemetryProfileName = value ?? "";
            }
        }

        /// <summary>Active wheel's user-loaded .mzdash file path (empty = none).</summary>
        internal string ActiveTelemetryMzdashPath
        {
            get
            {
                var ov = GetCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);
                return ov?.TelemetryMzdashPath ?? "";
            }
            set
            {
                var ov = GetOrCreateCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);
                if (ov != null) ov.TelemetryMzdashPath = value ?? "";
            }
        }

        /// <summary>Mzdash folder for the current wheel page (shared across profiles).</summary>
        internal string ActiveTelemetryMzdashFolder
        {
            get
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue || _plugin.Settings?.WheelMzdashFolderByPageGuid == null) return "";
                return _plugin.Settings.WheelMzdashFolderByPageGuid.TryGetValue(g.Value, out var folder)
                    ? folder ?? "" : "";
            }
            set
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue) return;
                var s = _plugin.Settings;
                if (s == null) return;
                lock (_pageBundleSwapLock)
                {
                    var next = s.WheelMzdashFolderByPageGuid == null
                        ? new Dictionary<Guid, string>()
                        : new Dictionary<Guid, string>(s.WheelMzdashFolderByPageGuid);
                    next[g.Value] = value ?? "";
                    s.WheelMzdashFolderByPageGuid = next;
                }
            }
        }

        /// <summary>
        /// Sleep-light bundle for the current wheel page (shared across profiles).
        /// null means "leave the wheel's stored value alone".
        /// </summary>
        internal WheelSleepSettings? ActiveWheelSleep
        {
            get
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue || _plugin.Settings?.WheelSleepByPageGuid == null) return null;
                return _plugin.Settings.WheelSleepByPageGuid.TryGetValue(g.Value, out var v) ? v : null;
            }
        }

        // Guards the copy-on-write swaps below. The bundles are seeded on the
        // serial read thread while the UI reads the dicts and the save debounce
        // JSON-serializes them — in-place Add would resize a dict mid-enumeration.
        private readonly object _pageBundleSwapLock = new object();

        /// <summary>Get-or-create the per-page sleep bundle. Null only if no wheel identified.</summary>
        internal WheelSleepSettings? GetOrCreateActiveWheelSleep()
        {
            var g = _plugin.GetCurrentWheelPageGuid();
            var settings = _plugin.Settings;
            if (!g.HasValue || settings == null) return null;
            lock (_pageBundleSwapLock)
            {
                var dict = settings.WheelSleepByPageGuid;
                if (dict != null && dict.TryGetValue(g.Value, out var bundle) && bundle != null)
                    return bundle;
                bundle = new WheelSleepSettings();
                var next = dict == null
                    ? new Dictionary<Guid, WheelSleepSettings>()
                    : new Dictionary<Guid, WheelSleepSettings>(dict);
                next[g.Value] = bundle;
                settings.WheelSleepByPageGuid = next;
                return bundle;
            }
        }

        /// <summary>
        /// Idle effect/speed bundle for the current wheel page (shared across profiles).
        /// null means "leave the wheel's stored value alone".
        /// </summary>
        internal WheelIdleSettings? ActiveWheelIdle
        {
            get
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue || _plugin.Settings?.WheelIdleByPageGuid == null) return null;
                return _plugin.Settings.WheelIdleByPageGuid.TryGetValue(g.Value, out var v) ? v : null;
            }
        }

        /// <summary>Get-or-create the per-page idle bundle. Null only if no wheel identified.</summary>
        internal WheelIdleSettings? GetOrCreateActiveWheelIdle()
        {
            var g = _plugin.GetCurrentWheelPageGuid();
            var settings = _plugin.Settings;
            if (!g.HasValue || settings == null) return null;
            lock (_pageBundleSwapLock)
            {
                var dict = settings.WheelIdleByPageGuid;
                if (dict != null && dict.TryGetValue(g.Value, out var bundle) && bundle != null)
                    return bundle;
                bundle = new WheelIdleSettings();
                var next = dict == null
                    ? new Dictionary<Guid, WheelIdleSettings>()
                    : new Dictionary<Guid, WheelIdleSettings>(dict);
                next[g.Value] = bundle;
                settings.WheelIdleByPageGuid = next;
                return bundle;
            }
        }

        /// <summary>
        /// Seed wheel-reported sleep-light + idle-effect/speed values into the
        /// per-page bundles. Only fills sentinel (-1/null) fields — user UI
        /// selections win. Without this, the wheel's current state is mirrored
        /// into _data but never persisted, so on the next launch the bundles
        /// are empty for unset fields and ApplyWheelToHardware leaves the
        /// wheel's mode/speed/color/idle-effect untouched even though we just
        /// observed them.
        /// </summary>
        internal void SeedSleepBundleFromResponse(ParsedResponse r)
        {
            if (r.Name == null) return;
            switch (r.Name)
            {
                case "wheel-idle-mode":
                case "wheel-idle-timeout":
                case "wheel-idle-speed":
                case "wheel-idle-color":
                    SeedSleepBundleField(r);
                    return;
                case "wheel-telemetry-idle-effect":
                case "wheel-buttons-idle-effect":
                case "wheel-knob-idle-effect":
                case "wheel-telemetry-idle-interval":
                case "wheel-buttons-idle-interval":
                case "wheel-knob-idle-interval":
                    SeedIdleBundleField(r);
                    return;
            }
        }

        private void SeedSleepBundleField(ParsedResponse r)
        {
            var bundle = GetOrCreateActiveWheelSleep();
            if (bundle == null) return;
            bool changed = false;
            switch (r.Name)
            {
                case "wheel-idle-mode":
                    if (bundle.Mode < 0 && r.IntValue >= 0)
                    {
                        bundle.Mode = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-idle-timeout":
                    if (bundle.TimeoutMin < 0 && r.IntValue > 0)
                    {
                        MozaLog.Info($"[AZOM] SLEEP-SEED: bundle.TimeoutMin {bundle.TimeoutMin} -> {r.IntValue} (from wheel response)");
                        bundle.TimeoutMin = r.IntValue;
                        changed = true;
                    }
                    else
                    {
                        MozaLog.Debug($"[AZOM] SLEEP-SEED skipped: bundle.TimeoutMin={bundle.TimeoutMin}, wheel reported {r.IntValue}");
                    }
                    break;
                case "wheel-idle-speed":
                    // Payload [mode, ms_msb, ms_lsb] — store only the ms part to
                    // match the slider's single-value contract.
                    if (bundle.SpeedMs < 0 && r.ArrayValue != null && r.ArrayValue.Length >= 3)
                    {
                        int ms = (r.ArrayValue[1] << 8) | r.ArrayValue[2];
                        if (ms > 0)
                        {
                            bundle.SpeedMs = ms;
                            changed = true;
                        }
                    }
                    break;
                case "wheel-idle-color":
                    if (bundle.Color == null && r.ArrayValue != null && r.ArrayValue.Length >= 3)
                    {
                        int packed = (r.ArrayValue[0] << 16) | (r.ArrayValue[1] << 8) | r.ArrayValue[2];
                        bundle.Color = new[] { packed };
                        changed = true;
                    }
                    break;
            }
            if (changed) PersistSettings();
        }

        private void SeedIdleBundleField(ParsedResponse r)
        {
            var bundle = GetOrCreateActiveWheelIdle();
            if (bundle == null) return;
            bool changed = false;
            switch (r.Name)
            {
                case "wheel-telemetry-idle-effect":
                    if (bundle.TelemetryEffect < 0 && r.IntValue >= 0)
                    {
                        bundle.TelemetryEffect = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-buttons-idle-effect":
                    if (bundle.ButtonsEffect < 0 && r.IntValue >= 0)
                    {
                        bundle.ButtonsEffect = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-knob-idle-effect":
                    if (bundle.KnobEffect < 0 && r.IntValue >= 0)
                    {
                        bundle.KnobEffect = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-telemetry-idle-interval":
                case "wheel-buttons-idle-interval":
                case "wheel-knob-idle-interval":
                    // Payload [effect_id, ms_msb, ms_lsb] — store only the ms.
                    if (r.ArrayValue != null && r.ArrayValue.Length >= 3)
                    {
                        int ms = (r.ArrayValue[1] << 8) | r.ArrayValue[2];
                        if (ms > 0)
                        {
                            if (r.Name == "wheel-telemetry-idle-interval" && bundle.TelemetrySpeedMs < 0)
                            {
                                bundle.TelemetrySpeedMs = ms;
                                changed = true;
                            }
                            else if (r.Name == "wheel-buttons-idle-interval" && bundle.ButtonsSpeedMs < 0)
                            {
                                bundle.ButtonsSpeedMs = ms;
                                changed = true;
                            }
                            else if (r.Name == "wheel-knob-idle-interval" && bundle.KnobSpeedMs < 0)
                            {
                                bundle.KnobSpeedMs = ms;
                                changed = true;
                            }
                        }
                    }
                    break;
            }
            if (changed) PersistSettings();
        }

    }
}
