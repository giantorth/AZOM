using System;
using System.Collections.Generic;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Per-channel SimHub-property overrides and the master channel defaults.
    /// Extracted from MozaPlugin.
    ///
    /// <para>Resolution order, highest first: per-dashboard override (device profile ×
    /// page × dashboard × channel) → the active channel-defaults profile →
    /// <c>simhub_property</c> from
    /// Data/Telemetry.json. Either override level also forces the channel's scale to
    /// 1 — Telemetry.json's <c>simhub_scale</c> is calibrated for its own property, so
    /// inheriting it silently zeroed integer channels and saturated the percent ones
    /// (see <c>DashboardProfileStore.ResolveDefaultBinding</c>). A user whose source
    /// unit differs converts with a formula, e.g. <c>[MyPlugin.Boost0to1]*100</c>.</para>
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
        /// Live-rewire a channel's <see cref="ChannelDefinition.SimHubProperty"/> and
        /// scale in place; new values apply on the next telemetry frame. Safe while
        /// running.
        ///
        /// <para>An override drops the Telemetry.json scale (it is calibrated for the
        /// JSON property — see <c>DashboardProfileStore.ResolveDefaultBinding</c>). An
        /// EMPTY <paramref name="propertyPath"/> means revert-to-default and restores
        /// the channel's default binding, property AND scale, instead of blanking the
        /// property — blanking dropped the channel onto the <c>SimHubField</c> snapshot
        /// path in the wrong unit until the next profile build (ErsState → ErsPercent
        /// 0-100 into a 4-bit field).</para>
        /// </summary>
        internal void UpdateActive(string channelUrl, string propertyPath, TelemetrySender? sender = null)
        {
            var profile = (sender ?? _plugin.TelemetrySender)?.Profile;
            if (profile == null || string.IsNullOrEmpty(channelUrl)) return;
            string trimmed = (propertyPath ?? "").Trim();
            double scale = 1.0;
            if (trimmed.Length == 0
                && _plugin.DashProfileStore.TryResolveDefaultBinding(
                    channelUrl, out var defaultProperty, out var defaultScale))
            {
                trimmed = defaultProperty;
                scale = defaultScale;
            }
            foreach (var tier in profile.Tiers)
            {
                foreach (var ch in tier.Channels)
                {
                    if (!string.Equals(ch.Url, channelUrl, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Scale first — see ChannelDefinition.SimHubPropertyScale.
                    ch.SimHubPropertyScale = scale;
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

        // ===== Master channel mapper: the channel-defaults profile =====
        // Layer 2 of the mapping resolution — per-dashboard overrides above still
        // win, Telemetry.json's simhub_property is below. Stored on the active
        // MozaChannelDefaultsProfile, a store of its own that is independent of the
        // device profiles; DashboardProfileStore holds the live snapshot the profile
        // builders read. That snapshot is a single static, so every push republishes
        // the CURRENT set — see PushProfileDefaults' call sites in ProfileCoordinator.

        /// <summary>The channel-defaults profile in force. Null before the store has
        /// initialised.</summary>
        private MozaChannelDefaultsProfile? ActiveProfile
            => _plugin.Settings?.ChannelDefaultsStore?.CurrentProfile;

        /// <summary>Publish the active channel-defaults profile into the dashboard
        /// profile store so subsequent profile builds resolve against it. Must run on
        /// every switch — the published snapshot is global, the source is not.</summary>
        internal void PushProfileDefaults()
            => DashboardProfileStore.SetDefaultOverrides(ActiveProfile?.Mappings);

        /// <summary>Set or clear one channel's default mapping on the active
        /// channel-defaults profile. An empty property removes the entry (revert to the
        /// Telemetry.json default) — same semantics as <see cref="Set"/>. COW like the
        /// per-dashboard map: the tick and serial-read threads read the store's
        /// snapshot, so build fresh and swap.</summary>
        internal void SetProfileDefault(string channelUrl, string propertyPath)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            var profile = ActiveProfile;
            if (profile == null) return;

            var old = profile.Mappings;
            var next = old != null
                ? new Dictionary<string, string>(old, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string trimmed = (propertyPath ?? "").Trim();
            if (trimmed.Length == 0) next.Remove(channelUrl);
            else next[channelUrl] = trimmed;

            profile.Mappings = next;
            PushProfileDefaults();
            _plugin.SaveSettings();
        }

        /// <summary>Drop every default override on the active channel-defaults profile —
        /// all its channels revert to their Telemetry.json values. Other profiles in the
        /// store are untouched.</summary>
        internal void ClearProfileDefaults()
        {
            var profile = ActiveProfile;
            if (profile == null) return;
            if (profile.Mappings == null || profile.Mappings.Count == 0)
                return;
            profile.Mappings =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PushProfileDefaults();
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
