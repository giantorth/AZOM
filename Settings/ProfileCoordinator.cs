using System;
using System.Collections.Generic;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Era;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// Settings persistence (debounced save, clear/reset) plus the SimHub
    /// profile system: profile-store init/subscription, profile apply, and the
    /// per-wheel-page accessor family (overlay, telemetry enable/name/path,
    /// sleep/idle bundles, firmware era) with the wheel-reported seed methods.
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
                var cands = _plugin.GetActiveDashboardKeyCandidates();
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
                if (_saveDebounceTimer == null)
                {
                    _saveDebounceTimer = new Timer(500) { AutoReset = false };
                    _saveDebounceTimer.Elapsed += (s, e) =>
                    {
                        LogSleepBundleStateForSaveTrace("debounced-save");
                        _plugin.SaveCommonSettings("MozaPluginSettings", _plugin.Settings);
                    };
                }
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
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
            _plugin._settings = new MozaPluginSettings();
            _plugin.SaveCommonSettings("MozaPluginSettings", _plugin.Settings);
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
                new SettingsMigrator(_plugin.Settings).SeedProfileBaselineFromFlatFields(defaultProfile);
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

        /// <summary>End()/CleanupPartialInit teardown: detach the CurrentProfileChanged
        /// subscription so an in-flight profile-change callback cannot reach the
        /// plugin during teardown.</summary>
        internal void DetachProfileStore()
        {
            if (_subscribedProfileStore != null)
                _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;
            _subscribedProfileStore = null;
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
            if (profile.WheelOverridesByPageGuid == null)
                profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
            if (!profile.WheelOverridesByPageGuid.TryGetValue(g.Value, out var ov) || ov == null)
            {
                ov = new WheelOverride();
                profile.WheelOverridesByPageGuid[g.Value] = ov;
            }
            return ov;
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
        /// </summary>
        internal bool ActiveTelemetryEnabled
        {
            get
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue || _plugin.Settings?.WheelTelemetryEnabledByPageGuid == null) return false;
                return _plugin.Settings.WheelTelemetryEnabledByPageGuid.TryGetValue(g.Value, out var v) && v;
            }
            set
            {
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue) return;
                if (_plugin.Settings == null) return;
                if (_plugin.Settings.WheelTelemetryEnabledByPageGuid == null)
                    _plugin.Settings.WheelTelemetryEnabledByPageGuid = new Dictionary<Guid, bool>();
                _plugin.Settings.WheelTelemetryEnabledByPageGuid[g.Value] = value;
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
                if (_plugin.Settings == null) return;
                if (_plugin.Settings.WheelMzdashFolderByPageGuid == null)
                    _plugin.Settings.WheelMzdashFolderByPageGuid = new Dictionary<Guid, string>();
                _plugin.Settings.WheelMzdashFolderByPageGuid[g.Value] = value ?? "";
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

        /// <summary>Get-or-create the per-page sleep bundle. Null only if no wheel identified.</summary>
        internal WheelSleepSettings? GetOrCreateActiveWheelSleep()
        {
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue || _plugin.Settings == null) return null;
            if (_plugin.Settings.WheelSleepByPageGuid == null)
                _plugin.Settings.WheelSleepByPageGuid = new Dictionary<Guid, WheelSleepSettings>();
            if (!_plugin.Settings.WheelSleepByPageGuid.TryGetValue(g.Value, out var bundle) || bundle == null)
            {
                bundle = new WheelSleepSettings();
                _plugin.Settings.WheelSleepByPageGuid[g.Value] = bundle;
            }
            return bundle;
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
            if (!g.HasValue || _plugin.Settings == null) return null;
            if (_plugin.Settings.WheelIdleByPageGuid == null)
                _plugin.Settings.WheelIdleByPageGuid = new Dictionary<Guid, WheelIdleSettings>();
            if (!_plugin.Settings.WheelIdleByPageGuid.TryGetValue(g.Value, out var bundle) || bundle == null)
            {
                bundle = new WheelIdleSettings();
                _plugin.Settings.WheelIdleByPageGuid[g.Value] = bundle;
            }
            return bundle;
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

        /// <summary>
        /// Firmware era for the current wheel page. Reads the per-page-GUID
        /// override for the connected wheel; when no wheel has identified yet
        /// (UI opened before hardware came up), falls back to the
        /// <see cref="MozaDeviceConstants.WheelGenericGuid"/> bucket so the
        /// user's pick made before the wheel was visible still applies.
        /// Returns <see cref="MozaWheelEra.Auto"/> only when neither bucket
        /// holds an explicit value.
        /// </summary>
        internal MozaWheelEra ActiveTelemetryWheelEra
        {
            get
            {
                if (_plugin.Settings?.WheelTelemetryEraByPageGuid == null) return MozaWheelEra.Auto;
                var g = _plugin.GetCurrentWheelPageGuid();
                if (g.HasValue
                    && _plugin.Settings.WheelTelemetryEraByPageGuid.TryGetValue(g.Value, out var v)
                    && v >= 0)
                    return MigrateStoredEra(v);
                if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var generic)
                    && _plugin.Settings.WheelTelemetryEraByPageGuid.TryGetValue(generic, out var gv)
                    && gv >= 0)
                    return MigrateStoredEra(gv);
                return MozaWheelEra.Auto;
            }
            set
            {
                if (_plugin.Settings == null) return;
                if (_plugin.Settings.WheelTelemetryEraByPageGuid == null)
                    _plugin.Settings.WheelTelemetryEraByPageGuid = new Dictionary<Guid, int>();
                // Specific wheel identified → write the per-wheel override.
                // Otherwise stash under WheelGenericGuid so the user's pick
                // survives until the wheel shows up; the getter falls back
                // to this bucket when the per-wheel entry is missing.
                var g = _plugin.GetCurrentWheelPageGuid();
                if (!g.HasValue
                    && Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var generic))
                    g = generic;
                if (!g.HasValue) return;
                _plugin.Settings.WheelTelemetryEraByPageGuid[g.Value] = (int)value;
            }
        }

        /// <summary>
        /// Map a persisted era int onto the current <see cref="MozaWheelEra"/>
        /// values. The defunct Era2025 was stored as 2 (now a retired hole) and
        /// is migrated to <see cref="MozaWheelEra.Auto"/> so the wheel is
        /// re-probed rather than pinned to a hallucinated era. Existing
        /// Era2024 (1) and Era2026 (3) picks are preserved; anything else
        /// (including 0 and the retired 2) falls back to Auto.
        /// </summary>
        private static MozaWheelEra MigrateStoredEra(int stored)
        {
            switch (stored)
            {
                case (int)MozaWheelEra.Era2024: return MozaWheelEra.Era2024;
                case (int)MozaWheelEra.Era2026: return MozaWheelEra.Era2026;
                default: return MozaWheelEra.Auto;
            }
        }
    }
}
