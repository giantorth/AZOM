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
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.UI.UpdateCheck;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    public partial class MozaPlugin
    {

        /// <summary>Apply telemetry settings from the active wheel overlay to the TelemetrySender.</summary>
        
        public IReadOnlyList<string> GetAllSimHubPropertyNames() => _propertyResolver.GetAllSimHubPropertyNames();
        public object? GetPropertyValueForDisplay(string? path) => _propertyResolver.GetValueForDisplay(path);
        internal string CurrentWheelKey() => _propertyResolver.CurrentWheelKey();

        /// <summary>
        /// Build a formula/property → double resolver for a 50 Hz haptics worker
        /// (LFE, mBooster). Same dialect and property resolution as the telemetry
        /// channel-mapper, but formulas evaluate on the returned closure's OWN
        /// engine instance — SimHub's NCalcEngineBase is not safe for concurrent
        /// evaluation, so each evaluator serializes internally, and a private
        /// instance keeps haptics ticks from queueing behind the 30 Hz telemetry
        /// evaluations (see SimHubPropertyResolver.ResolveAsDouble overload).
        /// Late-binds <see cref="_propertyResolver"/>: workers are constructed
        /// before the resolver exists.
        /// </summary>
        private Func<string?, double> CreateHapticsFormulaResolver()
        {
            var formula = new Telemetry.NCalcExpressionEvaluator();
            return f =>
            {
                if (string.IsNullOrWhiteSpace(f)) return 0.0;
                var resolver = _propertyResolver;
                if (resolver == null) return 0.0;
                return resolver.ResolveAsDouble(f!, formula);
            };
        }

        /// <summary>SimHub's shared formula engine for the channel-mapper's formula
        /// picker; null if engine construction failed (formulas then read as default).</summary>
        internal SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon.NCalcEngineBase? ChannelFormulaEngine
            => _propertyResolver?.FormulaEngine;

        // UI-thread formula preview (LFE "current value" readouts). Own evaluator
        // instance so it never contends with the haptics worker's engine; UI-thread
        // only, so no locking needed beyond ResolveAsDouble's internal serialization.
        private Telemetry.NCalcExpressionEvaluator? _uiFormulaEvaluator;

        /// <summary>Evaluate a haptics formula/property to a double for a UI preview. 0 if unavailable.</summary>
        internal double EvalHapticsFormula(string? formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return 0.0;
            var resolver = _propertyResolver;
            if (resolver == null) return 0.0;
            _uiFormulaEvaluator ??= new Telemetry.NCalcExpressionEvaluator();
            return resolver.ResolveAsDouble(formula!, _uiFormulaEvaluator);
        }

        /// <summary>
        /// Candidate dashboard keys (highest priority first):
        /// <c>wheel:&lt;id&gt;</c>, <c>file:&lt;filename&gt;:&lt;sha1-8&gt;</c>, <c>builtin:&lt;name&gt;</c>.
        /// Caller iterates; primary writer uses index 0.
        /// </summary>
        internal IReadOnlyList<string> GetActiveDashboardKeyCandidates()
        {
            string profileName = ActiveTelemetryProfileName;
            string mzdashPath = ActiveTelemetryMzdashPath;

            // Cold launch before any selection → fall back to running profile name.
            if (string.IsNullOrEmpty(profileName) && string.IsNullOrEmpty(mzdashPath))
            {
                profileName = _telemetrySender?.Profile?.Name ?? "";
            }

            if (string.IsNullOrEmpty(profileName) && string.IsNullOrEmpty(mzdashPath))
                return Array.Empty<string>();

            var result = new List<string>(3);

            // 1) wheel:<id> — match selected name against configJson catalog
            if (!string.IsNullOrEmpty(profileName))
            {
                var state = WheelStateForDiagnostics;
                if (state != null && state.EnabledDashboards != null)
                {
                    foreach (var entry in state.EnabledDashboards)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                        bool nameMatch =
                            string.Equals(entry.Title, profileName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.DirName, profileName, StringComparison.OrdinalIgnoreCase);
                        if (nameMatch)
                        {
                            result.Add("wheel:" + entry.Id);
                            break;
                        }
                    }
                }
            }

            // 2) file:<filename>:<sha1>
            string? keyPath = mzdashPath;
            if (string.IsNullOrEmpty(keyPath) && DashCache != null && !string.IsNullOrEmpty(profileName))
                keyPath = DashCache.TryGetFolderFilePath(profileName);
            if (!string.IsNullOrEmpty(keyPath))
            {
                // keyPath is non-empty so profile.Name branch is unreachable.
                string fileKey = DashboardProfileStore.GetDashboardKey(keyPath, _telemetrySender?.Profile!);
                if (!string.IsNullOrEmpty(fileKey) && !result.Contains(fileKey))
                    result.Add(fileKey);
            }

            // 3) builtin:<name>
            if (!string.IsNullOrEmpty(profileName))
            {
                string builtinKey = "builtin:" + profileName;
                if (!result.Contains(builtinKey))
                    result.Add(builtinKey);
            }

            return result;
        }

        /// <summary>
        /// Live-rewire a channel's <see cref="ChannelDefinition.SimHubProperty"/>
        /// in place; new value applies on the next telemetry frame. Safe while running.
        /// </summary>
        internal void UpdateActiveChannelMapping(string channelUrl, string propertyPath, TelemetrySender? sender = null)
        {
            var profile = (sender ?? _telemetrySender)?.Profile;
            if (profile == null || string.IsNullOrEmpty(channelUrl)) return;
            string trimmed = (propertyPath ?? "").Trim();
            foreach (var tier in profile.Tiers)
            {
                foreach (var ch in tier.Channels)
                {
                    if (string.Equals(ch.Url, channelUrl, StringComparison.OrdinalIgnoreCase))
                        ch.SimHubProperty = trimmed;
                }
            }
        }

        /// <summary>Set or clear a per-channel SimHub property override. Defaults to the
        /// current wheel + active dashboard; the CM2 page passes its own page GUID +
        /// fixed key + sender so its config is independent of the wheel's.</summary>
        internal void SetChannelMapping(string channelUrl, string propertyPath,
            Guid? pageGuid = null, string? fixedDashKey = null, TelemetrySender? sender = null)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            string dashKey;
            if (!string.IsNullOrEmpty(fixedDashKey))
            {
                dashKey = fixedDashKey!;
            }
            else
            {
                var candidates = GetActiveDashboardKeyCandidates();
                if (candidates.Count == 0) return;
                dashKey = candidates[0]; // write to the highest-priority key
            }

            // Profile × page × dashboard × channel → SimHub property path.
            // COW: the serial-read/tick threads walk these dicts mid-apply
            // (ApplyUserChannelMappings) and the save path serializes them —
            // rebuild each level and reference-swap, never mutate in place.
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            var g = pageGuid ?? GetCurrentWheelPageGuid();
            if (!g.HasValue) return; // no profile/page resolvable yet

            var outer = profile.TelemetryChannelMappings;
            var newMiddle = (outer != null && outer.TryGetValue(g.Value, out var oldMiddle) && oldMiddle != null)
                ? new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>(oldMiddle, StringComparer.OrdinalIgnoreCase)
                : new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var newInner = (newMiddle.TryGetValue(dashKey, out var oldInner) && oldInner != null)
                ? new System.Collections.Generic.Dictionary<string, string>(oldInner, StringComparer.OrdinalIgnoreCase)
                : new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string trimmed = (propertyPath ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                newInner.Remove(channelUrl);
                // Tidy: drop empty inner dict so the JSON doesn't accumulate
                // empty objects after every reset-to-default.
                if (newInner.Count == 0) newMiddle.Remove(dashKey);
                else newMiddle[dashKey] = newInner;
            }
            else
            {
                newInner[channelUrl] = trimmed;
                newMiddle[dashKey] = newInner;
            }

            var newOuter = outer != null
                ? new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>>(outer)
                : new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>>();
            newOuter[g.Value] = newMiddle;
            profile.TelemetryChannelMappings = newOuter;

            // Live-rewire the matching channel on the target sender's profile so the
            // next frame uses the new property. No tier-def restart.
            UpdateActiveChannelMapping(channelUrl, trimmed, sender);

            SaveSettings();
        }

        /// <summary>Clear all per-channel overrides for a page + its dashboard key(s).
        /// Defaults to the current wheel page across all candidate keys.
        /// COW like <see cref="SetChannelMapping"/> — readers walk these dicts
        /// on the serial-read/tick threads.</summary>
        internal void ClearCurrentDashboardMappings(Guid? pageGuid = null, string? fixedDashKey = null)
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            var outer = profile?.TelemetryChannelMappings;
            if (profile == null || outer == null) return;
            var g = pageGuid ?? GetCurrentWheelPageGuid();
            if (!g.HasValue || !outer.TryGetValue(g.Value, out var middle) || middle == null) return;

            var newMiddle = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>(middle, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            if (!string.IsNullOrEmpty(fixedDashKey))
            {
                if (newMiddle.Remove(fixedDashKey!)) changed = true;
            }
            else
            {
                foreach (var key in GetActiveDashboardKeyCandidates())
                    if (newMiddle.Remove(key)) changed = true;
            }
            if (!changed) return;

            var newOuter = new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>>(outer);
            newOuter[g.Value] = newMiddle;
            profile.TelemetryChannelMappings = newOuter;
            SaveSettings();
        }

        // ===== Master channel mapper: plugin-global default overrides =====
        // Layer 2 of the mapping resolution — per-dashboard overrides above still
        // win, Telemetry.json's simhub_property is below. Stored flat on
        // MozaPluginSettings; DashboardProfileStore holds the live snapshot the
        // profile builders read.

        /// <summary>Push the persisted global default overrides into the profile store
        /// so subsequent profile builds resolve against them.</summary>
        internal void PushGlobalChannelDefaults()
            => DashboardProfileStore.SetDefaultOverrides(_settings?.TelemetryDefaultMappings);

        /// <summary>Set or clear one channel's global default mapping. An empty property
        /// removes the entry (revert to the Telemetry.json default) — same semantics as
        /// <see cref="SetChannelMapping"/>. COW like the per-dashboard map: the tick and
        /// serial-read threads read the store's snapshot, so build fresh and swap.</summary>
        internal void SetGlobalChannelDefault(string channelUrl, string propertyPath)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            var settings = _settings;
            if (settings == null) return;

            var old = settings.TelemetryDefaultMappings;
            var next = old != null
                ? new System.Collections.Generic.Dictionary<string, string>(old, StringComparer.OrdinalIgnoreCase)
                : new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string trimmed = (propertyPath ?? "").Trim();
            if (trimmed.Length == 0) next.Remove(channelUrl);
            else next[channelUrl] = trimmed;

            settings.TelemetryDefaultMappings = next;
            PushGlobalChannelDefaults();
            SaveSettings();
        }

        /// <summary>Drop every global default override — all channels revert to their
        /// Telemetry.json values.</summary>
        internal void ClearGlobalChannelDefaults()
        {
            var settings = _settings;
            if (settings == null) return;
            if (settings.TelemetryDefaultMappings == null || settings.TelemetryDefaultMappings.Count == 0)
                return;
            settings.TelemetryDefaultMappings =
                new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PushGlobalChannelDefaults();
            SaveSettings();
        }

        /// <summary>Rebind both display pipelines' live channels to the current
        /// default + per-dashboard resolution. Wire-neutral (only each channel's
        /// SimHubProperty changes; the frame builder reads it live per frame), so a
        /// changed global default reaches the screen without a telemetry restart.
        /// No-ops on a sender whose wheel hasn't committed a catalog generation —
        /// there the change lands on the next profile build.</summary>
        internal void ReResolveAllChannelMappings()
        {
            try { _telemetrySender?.ReResolveActiveDashboardMappings(); } catch { }
            try { _cm2Sender?.ReResolveActiveDashboardMappings(); } catch { }
        }

        // Dashboard binding state moved to DashboardBindingCoordinator.
        internal bool IsPendingDashboardApply => _dashboardBindingCoordinator?.IsPendingDashboardApply ?? false;
        internal string? PendingDashboardApplyDescription => _dashboardBindingCoordinator?.PendingDashboardApplyDescription;

        // ===== ConnectionCoordinator forwarders =====
        // Multi-connection management + hub/base pipes live in
        // Devices/ConnectionCoordinator.cs. These 1-line private handlers keep
        // Init's event-subscription order untouched (the hub/base managers
        // subscribe before the coordinator exists) and null-guard that window.
        private void OnHubMessageReceived(byte[] data) => _connectionCoordinator?.OnHubMessageReceived(data);
        private void OnHubDisconnected() => _connectionCoordinator?.OnHubDisconnected();
        private void OnBaseMessageReceived(byte[] data) => _connectionCoordinator?.OnBaseMessageReceived(data);
        private void OnBaseDisconnected() => _connectionCoordinator?.OnBaseDisconnected();

        /// <summary>Inbound from the dashboard connection — same command-parse path as
        /// the wheelbase. (The telemetry inbound dispatcher follows the sender's
        /// Rebind, so dashboard session frames reach it once the sender is bound here.)</summary>
        private void OnDashboardMessageReceived(byte[] data) => OnMessageReceived(data, fromDashboard: true);

        /// <summary>Dashboard USB unplugged — pause the sender so the next tick rebinds
        /// it back to the wheelbase (and the base-bridged 0x14 path takes over if present).</summary>
        private void OnDashboardDisconnected()
        {
            if (IsShuttingDown) return;
            try { _telemetrySender?.Pause(); } catch { }
            DetectionState.DashDetected = false;
            _data.IsDashboardConnected = false;
            // Same reasoning as OnSerialDisconnected: pending reads for a port
            // that's gone will never be answered, and their sunsets must not
            // carry over to whatever enumerates next.
            try { _dashboardManager?.PendingResponses.Clear(); } catch { }
        }

        private const int WheelMissThreshold = 3;

        // wheel-model-name recheck cadence once identity is resolved; per-tick
        // liveness then comes from the 0x00 presence ACK. Kept strictly below
        // WheelMissThreshold so that even if a wheel model never ACKs 0x00 and
        // emits no 0x0E logs, the model-name response still resets the miss
        // counter before a false re-detect. Unresolved wheels read every tick
        // (fast identity). See the hot-swap block in PollStatus.
        private const int WheelModelRecheckInterval = WheelMissThreshold - 1;
        private int _wheelModelRecheckTick;

        // Flash-backed wheel settings whose readback value can seed the write cache
        // 1:1 (scalar int, same encoding on the write path), so an apply that matches
        // what the wheel already holds writes nothing to its parameter flash.
        // Deliberately excludes the composite-key params (idle-speed = mode<<32|ms,
        // idle-color = packed RGB) and every colour ARRAY — a mis-encoded prime there
        // would silently swallow a real user edit.
        private static readonly System.Collections.Generic.HashSet<string> s_primableWheelCfg =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "wheel-idle-mode", "wheel-idle-timeout",
                "wheel-telemetry-idle-effect", "wheel-buttons-idle-effect", "wheel-knob-idle-effect",
                "wheel-telemetry-mode", "wheel-buttons-led-mode", "wheel-knob-led-mode",
                "wheel-rpm-brightness", "wheel-buttons-brightness", "wheel-knob-ring-brightness",
                "wheel-rpm-indicator-mode", "wheel-rpm-display-mode",
            };

        private static bool IsPrimableWheelCfg(string name) => s_primableWheelCfg.Contains(name);
        // One-shot log edge for the param-storm suspend (see PollStatusCore).
        private bool _paramStormLogged;
    }
}
