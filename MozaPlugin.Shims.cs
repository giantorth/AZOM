using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Hardware;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Display;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.UI.UpdateCheck;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    public partial class MozaPlugin
    {

        // ===== DashboardBindingCoordinator shims (external API surface) =====
        internal void ApplyTelemetrySettings()
        {
            _dashboardBindingCoordinator.ApplyTelemetrySettings();
            EnsureCm2Pipeline();
        }

        /// <summary>
        /// Queue a re-apply of the current profile's saved
        /// <c>TelemetryDashboardKey</c> against the currently-attached
        /// wheel. Called from the wheel-hot-swap path so the new wheel ends
        /// up bound to the user's saved choice instead of whatever slot it
        /// boots to. Tries the apply immediately; if the wheel state isn't
        /// ready yet (configJsonList empty), sets the dashboard-binding
        /// coordinator's pending key so the next PollStatus tick retries.
        /// </summary>
        internal void RequestSavedDashboardReapply()
        {
            try
            {
                var profile = _settings?.ProfileStore?.CurrentProfile;
                if (profile == null) return;
                if (string.IsNullOrEmpty(profile.TelemetryDashboardKey)) return;
                bool applied = false;
                try { applied = ApplyTelemetryDashboardFromProfile(profile); }
                catch (Exception ex)
                {
                    MozaLog.Warn(
                        $"[AZOM] RequestSavedDashboardReapply: apply threw — {ex.Message}");
                    return;
                }
                if (!applied)
                {
                    _dashboardBindingCoordinator.SetPendingDashboardKey(profile.TelemetryDashboardKey!);
                    MozaLog.Debug(
                        $"[AZOM] RequestSavedDashboardReapply: deferred " +
                        $"(key={profile.TelemetryDashboardKey}) — PollStatus retry will fire " +
                        "once wheel state is ready");
                }
                else
                {
                    _dashboardBindingCoordinator.ClearPendingDashboardKey();
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] RequestSavedDashboardReapply: outer error — {ex.Message}");
            }
        }
        internal void RestartTelemetry() => _dashboardBindingCoordinator.RestartTelemetry();
        internal bool ApplyTelemetryDashboardFromProfile(MozaProfile profile) => _dashboardBindingCoordinator.ApplyTelemetryDashboardFromProfile(profile);
        internal void OnDashboardSwitched(uint slot) => _dashboardBindingCoordinator.OnDashboardSwitched(slot);
        internal void OnDashboardSwitched() => _dashboardBindingCoordinator.OnDashboardSwitched();
        internal void SetTelemetryEnabled(bool enabled) => _dashboardBindingCoordinator.SetTelemetryEnabled(enabled);
        internal void SetDashTelemetryEnabled(bool enabled) => _dashboardBindingCoordinator.SetDashTelemetryEnabled(enabled);
        internal void StartTelemetryIfReady()
        {
            // FSR V1 screen runs on its own driver (independent of the tier-def
            // sender), so a CM2 dash can still use the sender concurrently.
            _dualDisplay?.StartFsr1DriverIfNeeded();
            _dashboardBindingCoordinator.StartTelemetryIfReady();
            EnsureCm2Pipeline();
        }

        // ===== DualDisplayCoordinator shim (external API surface) =====
        // CM2/CM1 dual-display coordination lives in Telemetry/DualDisplayCoordinator.cs.
        // Null-guarded: PollStatus/serial callbacks can fire before the coordinator
        // is constructed in Init (same window as _dashboardBindingCoordinator).
        internal void EnsureCm2Pipeline() => _dualDisplay?.EnsureCm2Pipeline();

        /// <summary>
        /// Raised when the active telemetry dashboard selection is updated
        /// programmatically (profile load / deferred retry). Subscribers must
        /// marshal to the UI thread before touching WPF.
        /// </summary>
        public event EventHandler? DashboardSelectionChanged;

        internal void RaiseDashboardSelectionChangedInternal()
        {
            int subs = DashboardSelectionChanged?.GetInvocationList().Length ?? 0;
            MozaLog.Debug(
                $"[AZOM] Raising DashboardSelectionChanged (subscribers={subs}, " +
                $"profileName='{_settings?.TelemetryProfileName}', " +
                $"mzdash='{_settings?.TelemetryMzdashPath}')");
            try { DashboardSelectionChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { MozaLog.Warn("[AZOM] DashboardSelectionChanged subscriber threw: " + ex.Message); }
        }

        /// <summary>Re-apply the current Stalks mode + truck-sim config to the live
        /// controller. Call from the settings UI after editing + SaveSettings().</summary>
        internal void ApplyStalksSettings()
        {
            try { _stalksController?.ApplySettings(_settings.StalksMode, _settings.StalksTruckSim); } catch { }
        }

        // ===== ProfileCoordinator shims (external API surface) =====
        // Settings persistence + profile system live in Settings/ProfileCoordinator.cs.
        internal void SaveSettings() => _profileCoordinator.SaveSettings();
        internal void PersistSettings() => _profileCoordinator.PersistSettings();

        /// <summary>
        /// Requests SimHub to exit and relaunch — used after an in-app plugin
        /// update is installed so the freshly-swapped DLL gets loaded. Drives
        /// the supported SimHub lifecycle hook
        /// <c>PluginManager.RequestApplicationExit(restart: true)</c> (see
        /// docs/simhub.md § Application Lifecycle). Best-effort: logs and
        /// returns false if the call is unavailable or throws, leaving SimHub
        /// running so the user can restart manually.
        /// </summary>
        public bool RestartSimHub()
        {
            // Flush any pending settings synchronously-ish before we ask SimHub
            // to tear down — ScheduleSave is debounced, but SimHub's own
            // shutdown also persists plugin settings, so this is belt-and-braces.
            try { PersistSettings(); } catch { /* best-effort */ }

            var pm = _pluginManager;
            if (pm == null)
            {
                MozaLog.Warn("[UpdateInstall] restart requested but PluginManager is null");
                return false;
            }

            try
            {
                MozaLog.Info("[UpdateInstall] requesting SimHub restart to load updated plugin");
                pm.RequestApplicationExit(true);
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[UpdateInstall] RequestApplicationExit failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Auto-detect connected devices based on response commands.
        ///   - dash-rpm-indicator-mode responds -> dashboard present
        ///   - wheel-telemetry-mode responds -> new protocol wheel (GS/FSR/CS/RS/TSW)
        ///   - wheel-rpm-value1 responds (but not telemetry-mode) -> old protocol wheel (ES)
        /// </summary>
        
        // Hardware-apply entry points moved to HardwareApplier. Profile + WheelOverride
        // are the single source of truth; every write is detection-gated AND
        // sentinel-guarded (no brightness write storm on cold start).

        /// <summary>
        /// Resolve the wheel-page GUID for the currently-connected wheel, or null
        /// if no wheel model is known yet (hardware not identified).
        /// </summary>
        internal Guid? GetCurrentWheelPageGuid()
        {
            var modelName = _data?.WheelModelName;
            if (string.IsNullOrEmpty(modelName)) return null;
            var c = _pageGuidCache;
            if (c != null && string.Equals(c.Model, modelName, StringComparison.Ordinal)) return c.Guid;
            var prefix = WheelModelInfo.ExtractPrefix(modelName!);
            if (string.IsNullOrEmpty(prefix)) return null;
            var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
            if (!Guid.TryParse(guidStr, out var g)) return null;
            _pageGuidCache = new PageGuidCache(modelName!, g);
            return g;
        }

        // Memo of the last resolved (model name → page GUID): every per-page settings
        // accessor and, on FSR1/CM1 wheels, the display tick resolve through here.
        // Immutable snapshot swapped by reference; only a resolved GUID is cached.
        private sealed class PageGuidCache
        {
            public readonly string Model;
            public readonly Guid Guid;
            public PageGuidCache(string model, Guid guid) { Model = model; Guid = guid; }
        }
        private volatile PageGuidCache? _pageGuidCache;

        // ===== ProfileCoordinator accessor shims (external API surface) =====
        // Wheel overlay + per-wheel-page telemetry/sleep/idle/era accessors live
        // in Settings/ProfileCoordinator.cs.
        internal WheelOverride? GetCurrentWheelOverlay(MozaProfile? profile) => _profileCoordinator.GetCurrentWheelOverlay(profile);
        internal WheelOverride? GetOrCreateCurrentWheelOverlay(MozaProfile? profile) => _profileCoordinator.GetOrCreateCurrentWheelOverlay(profile);
        internal void UpdateActiveWheelOverlay(Action<WheelOverride> mutator) => _profileCoordinator.UpdateActiveWheelOverlay(mutator);
        internal void UpdateActiveProfile(Action<MozaProfile> mutator) => _profileCoordinator.UpdateActiveProfile(mutator);
        internal bool ActiveTelemetryEnabled
        {
            get => _profileCoordinator.ActiveTelemetryEnabled;
            set => _profileCoordinator.ActiveTelemetryEnabled = value;
        }
        /// <summary>Telemetry-enable for the CM2/CM1 dash pipeline — resolvable with
        /// no wheel attached, unlike <see cref="ActiveTelemetryEnabled"/>.</summary>
        internal bool ActiveDashTelemetryEnabled
        {
            get => _profileCoordinator.ActiveDashTelemetryEnabled;
            set => _profileCoordinator.ActiveDashTelemetryEnabled = value;
        }
        internal string ActiveTelemetryProfileName
        {
            get => _profileCoordinator.ActiveTelemetryProfileName;
            set => _profileCoordinator.ActiveTelemetryProfileName = value;
        }
        internal string ActiveTelemetryMzdashPath
        {
            get => _profileCoordinator.ActiveTelemetryMzdashPath;
            set => _profileCoordinator.ActiveTelemetryMzdashPath = value;
        }
        internal string ActiveTelemetryMzdashFolder
        {
            get => _profileCoordinator.ActiveTelemetryMzdashFolder;
            set => _profileCoordinator.ActiveTelemetryMzdashFolder = value;
        }
        internal WheelSleepSettings? ActiveWheelSleep => _profileCoordinator.ActiveWheelSleep;
        internal WheelSleepSettings? GetOrCreateActiveWheelSleep() => _profileCoordinator.GetOrCreateActiveWheelSleep();
        internal WheelIdleSettings? ActiveWheelIdle => _profileCoordinator.ActiveWheelIdle;
        internal WheelIdleSettings? GetOrCreateActiveWheelIdle() => _profileCoordinator.GetOrCreateActiveWheelIdle();
        /// <summary>
        /// Channel-mapping dict for the active profile × current wheel page. Null
        /// when no profile/wheel is resolvable. Caller must not mutate returned
        /// dict directly — use the channel-mapping write helpers in MozaPlugin.cs.
        /// </summary>
        // CM2 dash settings are keyed under a fixed page GUID with a single
        // dashboard key, so the CM2's dashboard/channel config is fully independent
        // of the wheel's. pageGuid==null means "the current wheel page".
        // This literal is the GUID of the retired SHDP "MOZA Dashboard" device; it
        // is retained verbatim as the CM2 persistence key so existing users' saved
        // CM2 dashboard/channel mappings (keyed under it) survive the SHDP removal.
        // It is a persistence key only — not a live SimHub device id.
        internal static readonly Guid Cm2PageGuid = Guid.Parse("c97a4d00-a66d-4e2f-a9b4-e7fc348dcc33");
        internal const string Cm2DashKey = "cm2";

        // CM1 base-bridged dash gets its OWN page GUID so its field mappings,
        // active-dashboard selection and the CM1/CM2 discriminator never share a
        // key with the CM2 dash (which uses Cm2PageGuid). A user can run a CM1 and
        // a CM2 simultaneously; keeping the identities disjoint is what lets both
        // persist independently.
        internal static readonly Guid Cm1PageGuid = Guid.Parse(Devices.MozaDeviceConstants.DashCm1Guid);

        internal Dictionary<string, Dictionary<string, string>>? GetActiveChannelMappings(Guid? pageGuid = null)
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile?.TelemetryChannelMappings == null) return null;
            var g = pageGuid ?? GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            return profile.TelemetryChannelMappings.TryGetValue(g.Value, out var m) ? m : null;
        }

        // ===== FSR1/CM1 field-mapping + index shims (external API surface) =====
        // FSR V1 (group-0x42) + CM1 (group-0x35) field mappings and the active
        // dashboard/page index store live in Telemetry/Fsr1Cm1MappingCoordinator.cs.
        internal Fsr1FieldMapping? GetFsr1FieldMapping(string recordKey, string fieldId) => _fsr1Cm1Mapping.GetFsr1FieldMapping(recordKey, fieldId);
        internal Dictionary<string, Dictionary<string, Fsr1FieldMapping>>? GetActiveFsr1Mappings() => _fsr1Cm1Mapping.GetActiveFsr1Mappings();
        internal void SetFsr1FieldMapping(string recordKey, string fieldId, Fsr1FieldMapping? mapping) => _fsr1Cm1Mapping.SetFsr1FieldMapping(recordKey, fieldId, mapping);
        internal int GetActiveFsr1Index() => _fsr1Cm1Mapping.GetActiveFsr1Index();
        internal void SetActiveFsr1Index(int index, bool sendToWheel) => _fsr1Cm1Mapping.SetActiveFsr1Index(index, sendToWheel);
        internal int TakePendingFsr1Select() => _fsr1Cm1Mapping.TakePendingFsr1Select();

        /// <summary>Queue an FSR1 display-brightness push (group-0x32 00/80 write+commit
        /// pair, 0–100). The wheel PERSISTS it to EEPROM (Table 7 Param 5); the driver
        /// emits it behind the shared Table-7 write gate (≥2 s between writes, latest
        /// value wins, same-value repeats dropped) — back-to-back commits preceded the
        /// 2026-08-06 param-store wedges.</summary>
        internal void SendFsr1DisplayBrightness(int percent)
        {
            if (!IsFsr1DisplayWheel) return;
            _fsr1Cm1Mapping.QueueFsr1Brightness(percent);
        }
        internal int TakePendingFsr1Brightness() => _fsr1Cm1Mapping.TakePendingFsr1Brightness();

        internal void ClearFsr1FieldOverrides(string recordKey) => _fsr1Cm1Mapping.ClearFsr1FieldOverrides(recordKey);
        internal Fsr1FieldDef? FindFsr1Field(string recordKey, string fieldId)
        {
            var dash = Fsr1DashboardCatalog.ByKey(recordKey);
            if (dash == null || string.IsNullOrEmpty(fieldId)) return null;
            foreach (var f in dash.Fields)
                if (f.FieldId == fieldId) return f;
            return null;
        }

        internal Fsr1FieldMapping? GetCm1FieldMapping(string fieldId) => _fsr1Cm1Mapping.GetCm1FieldMapping(fieldId);
        internal void SetCm1FieldMapping(string fieldId, string property, double? scale) => _fsr1Cm1Mapping.SetCm1FieldMapping(fieldId, property, scale);
        internal void ClearCm1Mappings() => _fsr1Cm1Mapping.ClearCm1Mappings();
        internal int GetActiveCm1Index() => _fsr1Cm1Mapping.GetActiveCm1Index();
        internal void SetActiveCm1Index(int index, bool sendToWheel) => _fsr1Cm1Mapping.SetActiveCm1Index(index, sendToWheel);
        internal int TakePendingCm1Select() => _fsr1Cm1Mapping.TakePendingCm1Select();

        /// <summary>True once this dash is confirmed a CM1 (group-0x35). Persisted per
        /// dash GUID so later boots skip the tier-def probe.</summary>
        internal bool DashIsCm1
        {
            get => _fsr1Cm1Mapping.DashIsCm1;
            set => _fsr1Cm1Mapping.DashIsCm1 = value;
        }

        /// <summary>Raised when the active FSR1 dashboard index changes (either the
        /// user picked it or the wheel reported a self-switch). UI re-selects.</summary>
        internal event EventHandler? Fsr1ActiveIndexChanged;

        internal void RaiseFsr1ActiveIndexChanged()
        {
            try { Fsr1ActiveIndexChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        /// <summary>Raised when the active CM1 dashboard index changes (user pick or the
        /// dash reported a self-switch via its Param-6 log). UI re-selects.</summary>
        internal event EventHandler? Cm1ActiveIndexChanged;

        internal void RaiseCm1ActiveIndexChanged()
        {
            try { Cm1ActiveIndexChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        // Match "steer_connected <N>" in a main-bridge firmware-debug line. The
        // wheel-bus firmware emits this on the edge of the steering-wheel (rim)
        // attach state: "steer_connected 1" when a rim is seated on the
        // quick-release, "steer_connected 0" when it's pulled off (alongside
        // "Gpw Wheel Disconnected").
        private static readonly System.Text.RegularExpressions.Regex _steerConnectedRe =
            new System.Text.RegularExpressions.Regex(
                @"steer_connected\s+(\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Last rim attach-state parsed from the main bridge firmware-debug log;
        // -1 = not yet observed. Read/written only on the serial read thread
        // (OnMessageReceived), so no synchronisation needed.
        private int _lastSteerConnected = -1;

        // Tear down cached wheel/display identity + channel catalog when the rim
        // is detached. A rim pull keeps the wheelbase COM port open and the base
        // keeps answering wheel-model-name on the locked ID (see PollStatus
        // hot-swap notes), so the poll-miss path never fires and the diagnostics
        // tab / dashboard gating would otherwise report a phantom wheel
        // indefinitely. We act only on the 1→0 (or unknown→0) falling edge;
        // reseating the rim re-detects automatically via the PollStatus
        // ProbeWheelDetection loop, so no connect-edge handling is needed.
        private void TryHandleWheelConnectionLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var m = _steerConnectedRe.Match(text);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int state)) return;

            int prev = _lastSteerConnected;
            _lastSteerConnected = state;
            if (state == prev || state != 0) return;   // ignore re-prints and the attach edge

            if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected)
                ResetWheelDetection(
                    "Rim detached (firmware steer_connected 0) — resetting wheel detection");
        }

        /// <summary>Apply a profile via the consolidated Apply*ToHardware methods —
        /// logic lives in Settings/ProfileCoordinator.cs.</summary>
        internal void ApplyProfile(MozaProfile profile) => _profileCoordinator.ApplyProfile(profile);
    }
}
