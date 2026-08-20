using System;
using System.Collections.Generic;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Per-channel SimHub-property overrides and the plugin-global default
    /// overrides ("master channel mapper"). Extracted from MozaPlugin.
    ///
    /// <para>Resolution order, highest first: per-dashboard override (profile ×
    /// page × dashboard × channel) → global default → <c>simhub_property</c> from
    /// Data/Telemetry.json.</para>
    ///
    /// <para>All the dictionaries here are copy-on-write: the serial-read and
    /// tick threads walk them mid-apply (ApplyUserChannelMappings) and the save
    /// path serializes them, so every level is rebuilt and reference-swapped —
    /// never mutated in place.</para>
    /// </summary>
    internal sealed class ChannelMappingCoordinator
    {
        private readonly MozaPlugin _plugin;

        internal ChannelMappingCoordinator(MozaPlugin plugin)
        {
            _plugin = plugin;
        }

        /// <summary>
        /// Candidate dashboard keys (highest priority first):
        /// <c>wheel:&lt;id&gt;</c>, <c>file:&lt;filename&gt;:&lt;sha1-8&gt;</c>, <c>builtin:&lt;name&gt;</c>.
        /// Caller iterates; primary writer uses index 0.
        /// </summary>
        internal IReadOnlyList<string> GetActiveDashboardKeyCandidates()
        {
            string profileName = _plugin.ActiveTelemetryProfileName;
            string mzdashPath = _plugin.ActiveTelemetryMzdashPath;

            // Cold launch before any selection → fall back to running profile name.
            if (string.IsNullOrEmpty(profileName) && string.IsNullOrEmpty(mzdashPath))
            {
                profileName = _plugin.TelemetrySender?.Profile?.Name ?? "";
            }

            if (string.IsNullOrEmpty(profileName) && string.IsNullOrEmpty(mzdashPath))
                return Array.Empty<string>();

            var result = new List<string>(3);

            // 1) wheel:<id> — match selected name against configJson catalog
            if (!string.IsNullOrEmpty(profileName))
            {
                var state = _plugin.WheelStateForDiagnostics;
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
            var dashCache = _plugin.DashCache;
            if (string.IsNullOrEmpty(keyPath) && dashCache != null && !string.IsNullOrEmpty(profileName))
                keyPath = dashCache.TryGetFolderFilePath(profileName);
            if (!string.IsNullOrEmpty(keyPath))
            {
                // keyPath is non-empty so profile.Name branch is unreachable.
                string fileKey = DashboardProfileStore.GetDashboardKey(keyPath, _plugin.TelemetrySender?.Profile!);
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
        internal void UpdateActive(string channelUrl, string propertyPath, TelemetrySender? sender = null)
        {
            var profile = (sender ?? _plugin.TelemetrySender)?.Profile;
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
        internal void Set(string channelUrl, string propertyPath,
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
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            var g = pageGuid ?? _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return; // no profile/page resolvable yet

            var outer = profile.TelemetryChannelMappings;
            var newMiddle = (outer != null && outer.TryGetValue(g.Value, out var oldMiddle) && oldMiddle != null)
                ? new Dictionary<string, Dictionary<string, string>>(oldMiddle, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var newInner = (newMiddle.TryGetValue(dashKey, out var oldInner) && oldInner != null)
                ? new Dictionary<string, string>(oldInner, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                ? new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>(outer)
                : new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();
            newOuter[g.Value] = newMiddle;
            profile.TelemetryChannelMappings = newOuter;

            // Live-rewire the matching channel on the target sender's profile so the
            // next frame uses the new property. No tier-def restart.
            UpdateActive(channelUrl, trimmed, sender);

            _plugin.SaveSettings();
        }

        /// <summary>Clear all per-channel overrides for a page + its dashboard key(s).
        /// Defaults to the current wheel page across all candidate keys.
        /// COW like <see cref="Set"/> — readers walk these dicts on the
        /// serial-read/tick threads.</summary>
        internal void ClearCurrentDashboard(Guid? pageGuid = null, string? fixedDashKey = null)
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            var outer = profile?.TelemetryChannelMappings;
            if (profile == null || outer == null) return;
            var g = pageGuid ?? _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue || !outer.TryGetValue(g.Value, out var middle) || middle == null) return;

            var newMiddle = new Dictionary<string, Dictionary<string, string>>(middle, StringComparer.OrdinalIgnoreCase);
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

            var newOuter = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>(outer);
            newOuter[g.Value] = newMiddle;
            profile.TelemetryChannelMappings = newOuter;
            _plugin.SaveSettings();
        }

        // ===== Master channel mapper: plugin-global default overrides =====
        // Layer 2 of the mapping resolution — per-dashboard overrides above still
        // win, Telemetry.json's simhub_property is below. Stored flat on
        // MozaPluginSettings; DashboardProfileStore holds the live snapshot the
        // profile builders read.

        /// <summary>Push the persisted global default overrides into the profile store
        /// so subsequent profile builds resolve against them.</summary>
        internal void PushGlobalDefaults()
            => DashboardProfileStore.SetDefaultOverrides(_plugin.Settings?.TelemetryDefaultMappings);

        /// <summary>Set or clear one channel's global default mapping. An empty property
        /// removes the entry (revert to the Telemetry.json default) — same semantics as
        /// <see cref="Set"/>. COW like the per-dashboard map: the tick and serial-read
        /// threads read the store's snapshot, so build fresh and swap.</summary>
        internal void SetGlobalDefault(string channelUrl, string propertyPath)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            var settings = _plugin.Settings;
            if (settings == null) return;

            var old = settings.TelemetryDefaultMappings;
            var next = old != null
                ? new Dictionary<string, string>(old, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string trimmed = (propertyPath ?? "").Trim();
            if (trimmed.Length == 0) next.Remove(channelUrl);
            else next[channelUrl] = trimmed;

            settings.TelemetryDefaultMappings = next;
            PushGlobalDefaults();
            _plugin.SaveSettings();
        }

        /// <summary>Drop every global default override — all channels revert to their
        /// Telemetry.json values.</summary>
        internal void ClearGlobalDefaults()
        {
            var settings = _plugin.Settings;
            if (settings == null) return;
            if (settings.TelemetryDefaultMappings == null || settings.TelemetryDefaultMappings.Count == 0)
                return;
            settings.TelemetryDefaultMappings =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PushGlobalDefaults();
            _plugin.SaveSettings();
        }

        /// <summary>Rebind both display pipelines' live channels to the current
        /// default + per-dashboard resolution. Wire-neutral (only each channel's
        /// SimHubProperty changes; the frame builder reads it live per frame), so a
        /// changed global default reaches the screen without a telemetry restart.
        /// No-ops on a sender whose wheel hasn't committed a catalog generation —
        /// there the change lands on the next profile build.</summary>
        internal void ReResolveAll()
        {
            try { _plugin.TelemetrySender?.ReResolveActiveDashboardMappings(); } catch { }
            try { _plugin.Cm2Sender?.ReResolveActiveDashboardMappings(); } catch { }
        }
    }
}
