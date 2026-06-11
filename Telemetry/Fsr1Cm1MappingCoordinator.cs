using System;
using System.Collections.Generic;
using System.Threading;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// FSR V1 (group-0x42) and CM1 (group-0x35) dashboard field mappings plus
    /// the active dashboard/page index store for both display families. The
    /// FSR1's fixed-schema fields are keyed by record-type + field id per wheel
    /// page; the CM1's flat field set lives under its own dash GUID
    /// (<see cref="MozaPlugin.Cm1PageGuid"/>). Both share the group-0x32/0x81
    /// select command and the "Table 7, Param 6 Written: N" page-report log.
    /// Settings are read live via <c>_plugin.Settings</c> (the field is
    /// replaced by ClearSettings, so it must never be captured).
    /// </summary>
    internal sealed class Fsr1Cm1MappingCoordinator
    {
        private readonly MozaPlugin _plugin;

        internal Fsr1Cm1MappingCoordinator(MozaPlugin plugin)
        {
            _plugin = plugin;
        }

        // ── FSR V1 (group-0x42) dashboard field mappings ────────────────────
        // Mirror the channel-mapping helpers in MozaPlugin but for the FSR V1's
        // fixed-schema dashboard fields (keyed by record-type + field id, value
        // carries scaling).

        /// <summary>Active profile × current wheel page FSR1 field mappings, or null.</summary>
        internal Dictionary<string, Dictionary<string, Fsr1FieldMapping>>? GetActiveFsr1Mappings()
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile?.Fsr1DashboardMappings == null) return null;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            return profile.Fsr1DashboardMappings.TryGetValue(g.Value, out var m) ? m : null;
        }

        /// <summary>Resolve one FSR1 field's user mapping, or null to use the catalog default.</summary>
        internal Fsr1FieldMapping? GetFsr1FieldMapping(string recordKey, string fieldId)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return null;
            var m = GetActiveFsr1Mappings();
            if (m == null) return null;
            return m.TryGetValue(recordKey, out var inner)
                && inner.TryGetValue(fieldId, out var fm) ? fm : null;
        }

        /// <summary>
        /// Persist (or clear) an FSR1 dashboard field assignment. Empty
        /// <paramref name="property"/> removes the override (field reverts to the
        /// catalog default). Tidies empty dicts and saves settings.
        /// </summary>
        internal void SetFsr1FieldMapping(string recordKey, string fieldId, string property, double inMin, double inMax)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return;
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            if (profile.Fsr1DashboardMappings == null)
                profile.Fsr1DashboardMappings = new Dictionary<Guid, Dictionary<string, Dictionary<string, Fsr1FieldMapping>>>();
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return;

            if (!profile.Fsr1DashboardMappings.TryGetValue(g.Value, out var middle) || middle == null)
            {
                middle = new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(StringComparer.OrdinalIgnoreCase);
                profile.Fsr1DashboardMappings[g.Value] = middle;
            }
            if (!middle.TryGetValue(recordKey, out var inner) || inner == null)
            {
                inner = new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase);
                middle[recordKey] = inner;
            }

            string trimmed = (property ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                inner.Remove(fieldId);
                if (inner.Count == 0) middle.Remove(recordKey);
                if (middle.Count == 0) profile.Fsr1DashboardMappings.Remove(g.Value);
            }
            else
            {
                inner[fieldId] = new Fsr1FieldMapping { Property = trimmed, InMin = inMin, InMax = inMax };
            }

            _plugin.SaveSettings();
        }

        // ── FSR V1 active dashboard/page index (0..18) ──────────────────────
        // The FSR V1 has 19 built-in dashboard positions. The plugin switches the
        // wheel by sending the group-0x32 cmd-0x81 index write; the wheel can also
        // switch itself (HID button combo) and reports the new index via its
        // 0x0E "Table 7 Param 6 Written: N" log, which we parse to follow it.

        // Set when the USER selects a dashboard; drained by TelemetrySender which
        // emits the group-0x32/0x81 select command on the next tick. -1 = nothing
        // pending. Wheel-reported (self-switch) updates do NOT set this.
        private int _fsr1PendingSelect = -1;

        /// <summary>Current FSR1 active dashboard index (0..18), default 0.</summary>
        internal int GetActiveFsr1Index()
        {
            var g = _plugin.GetCurrentWheelPageGuid();
            if (g.HasValue && _plugin.Settings?.Fsr1ActiveDashboardByWheelGuid != null
                && _plugin.Settings.Fsr1ActiveDashboardByWheelGuid.TryGetValue(g.Value, out var i))
                return i;
            return 0;
        }

        /// <summary>
        /// Set the active FSR1 dashboard index. <paramref name="sendToWheel"/> true
        /// (user/dropdown) queues the group-0x32/0x81 select command for the sender to
        /// emit; false (wheel self-switch, parsed from the Param 6 log) just records
        /// it. Persists per-wheel and raises <see cref="MozaPlugin.Fsr1ActiveIndexChanged"/>.
        /// </summary>
        internal void SetActiveFsr1Index(int index, bool sendToWheel)
        {
            if (index < 0) index = 0;
            if (index > Fsr1DisplayEmitter.MaxDashboardIndex)
                index = Fsr1DisplayEmitter.MaxDashboardIndex;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (g.HasValue && _plugin.Settings != null)
            {
                if (_plugin.Settings.Fsr1ActiveDashboardByWheelGuid == null)
                    _plugin.Settings.Fsr1ActiveDashboardByWheelGuid = new Dictionary<Guid, int>();
                bool changed = !_plugin.Settings.Fsr1ActiveDashboardByWheelGuid.TryGetValue(g.Value, out var prev) || prev != index;
                _plugin.Settings.Fsr1ActiveDashboardByWheelGuid[g.Value] = index;
                if (changed && !sendToWheel) _plugin.SaveSettings(); // host path saves after queuing below
            }
            if (sendToWheel)
            {
                Interlocked.Exchange(ref _fsr1PendingSelect, index);
                _plugin.SaveSettings();
            }
            _plugin.RaiseFsr1ActiveIndexChanged();
        }

        /// <summary>Sender drains the pending user-select index (or -1). One-shot.</summary>
        internal int TakePendingFsr1Select() => Interlocked.Exchange(ref _fsr1PendingSelect, -1);

        /// <summary>Record a wheel-reported active index parsed from the Param 6 log
        /// (wheel self-switch); follows without re-commanding the wheel.</summary>
        internal void NoteFsr1WheelIndex(int index)
        {
            if (index == GetActiveFsr1Index()) return;
            SetActiveFsr1Index(index, sendToWheel: false);
        }

        // Match "Table 7, Param 6 Written: <N>" in an FSR1 firmware-debug log line
        // and follow the reported dashboard index. Tolerant of surrounding text.
        private static readonly System.Text.RegularExpressions.Regex _fsr1DashLogRe =
            new System.Text.RegularExpressions.Regex(
                @"Table\s*7,\s*Param\s*6\s*Written:\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        internal void TryFollowFsr1DashboardLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var m = _fsr1DashLogRe.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int idx))
                NoteFsr1WheelIndex(idx);
        }

        // CM1 page-report log is byte-identical to the FSR1's (same firmware family),
        // just on dev 0x41. Reuse the regex; follow the dash's self-switch.
        internal void TryFollowCm1DashboardLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var m = _fsr1DashLogRe.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int idx))
                NoteCm1WheelIndex(idx);
        }

        // ===== CM1 base-bridged dash (group-0x35) =====
        // The CM1 is driven by the standalone Cm1DisplayDriver, not a tier-def sender.
        // Its field set is flat (Cm1DashboardCatalog), keyed under its OWN dash GUID
        // (Cm1PageGuid), independent of any wheel. The dashboard-switch command and the
        // Param-6 page-report log are byte-identical to the FSR1's, just on dev 0x14/0x41.

        private Dictionary<string, Fsr1FieldMapping>? GetActiveCm1Mappings()
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile?.Cm1FieldMappings == null) return null;
            return profile.Cm1FieldMappings.TryGetValue(MozaPlugin.Cm1PageGuid, out var m) ? m : null;
        }

        /// <summary>Resolve one CM1 field's user mapping, or null to use the catalog default.</summary>
        internal Fsr1FieldMapping? GetCm1FieldMapping(string fieldId)
        {
            if (string.IsNullOrEmpty(fieldId)) return null;
            var m = GetActiveCm1Mappings();
            return m != null && m.TryGetValue(fieldId, out var fm) ? fm : null;
        }

        /// <summary>Persist (or clear) a CM1 field assignment. Empty property removes the
        /// override (field reverts to its catalog default/constant). Saves settings.</summary>
        internal void SetCm1FieldMapping(string fieldId, string property)
        {
            if (string.IsNullOrEmpty(fieldId)) return;
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            if (profile.Cm1FieldMappings == null)
                profile.Cm1FieldMappings = new Dictionary<Guid, Dictionary<string, Fsr1FieldMapping>>();
            if (!profile.Cm1FieldMappings.TryGetValue(MozaPlugin.Cm1PageGuid, out var inner) || inner == null)
            {
                inner = new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase);
                profile.Cm1FieldMappings[MozaPlugin.Cm1PageGuid] = inner;
            }
            string trimmed = (property ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                inner.Remove(fieldId);
                if (inner.Count == 0) profile.Cm1FieldMappings.Remove(MozaPlugin.Cm1PageGuid);
            }
            else
            {
                inner[fieldId] = new Fsr1FieldMapping { Property = trimmed };
            }
            _plugin.SaveSettings();
        }

        /// <summary>Clear ALL CM1 field mappings (reset-to-defaults).</summary>
        internal void ClearCm1Mappings()
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile?.Cm1FieldMappings == null) return;
            if (profile.Cm1FieldMappings.Remove(MozaPlugin.Cm1PageGuid)) _plugin.SaveSettings();
        }

        private int _cm1PendingSelect = -1;

        /// <summary>Current CM1 active dashboard page (1-based), default 1.</summary>
        internal int GetActiveCm1Index()
        {
            if (_plugin.Settings?.Cm1ActiveDashboardByGuid != null
                && _plugin.Settings.Cm1ActiveDashboardByGuid.TryGetValue(MozaPlugin.Cm1PageGuid, out var i))
                return i;
            return Cm1DisplayEmitter.MinDashboardIndex;
        }

        /// <summary>Set the CM1 active dashboard page. <paramref name="sendToWheel"/> true
        /// queues the group-0x32/0x81 select for the driver to emit; false (dash self-switch
        /// from the Param-6 log) just records it. Persists per dash GUID.</summary>
        internal void SetActiveCm1Index(int index, bool sendToWheel)
        {
            if (index < Cm1DisplayEmitter.MinDashboardIndex)
                index = Cm1DisplayEmitter.MinDashboardIndex;
            if (index > Cm1DisplayEmitter.MaxDashboardIndex)
                index = Cm1DisplayEmitter.MaxDashboardIndex;
            if (_plugin.Settings != null)
            {
                if (_plugin.Settings.Cm1ActiveDashboardByGuid == null)
                    _plugin.Settings.Cm1ActiveDashboardByGuid = new Dictionary<Guid, int>();
                bool changed = !_plugin.Settings.Cm1ActiveDashboardByGuid.TryGetValue(MozaPlugin.Cm1PageGuid, out var prev) || prev != index;
                _plugin.Settings.Cm1ActiveDashboardByGuid[MozaPlugin.Cm1PageGuid] = index;
                if (changed && !sendToWheel) _plugin.SaveSettings();
            }
            if (sendToWheel)
            {
                Interlocked.Exchange(ref _cm1PendingSelect, index);
                _plugin.SaveSettings();
            }
            _plugin.RaiseCm1ActiveIndexChanged();
        }

        /// <summary>Driver drains the pending user-select index (or -1). One-shot.</summary>
        internal int TakePendingCm1Select() => Interlocked.Exchange(ref _cm1PendingSelect, -1);

        /// <summary>Record a dash-reported page index (self-switch via Param-6 log).</summary>
        internal void NoteCm1WheelIndex(int index)
        {
            if (index == GetActiveCm1Index()) return;
            SetActiveCm1Index(index, sendToWheel: false);
        }

        /// <summary>True once this dash is confirmed a CM1 (group-0x35). Persisted per
        /// dash GUID so later boots skip the tier-def probe.</summary>
        internal bool DashIsCm1
        {
            get => _plugin.Settings?.DashIsCm1ByGuid != null
                   && _plugin.Settings.DashIsCm1ByGuid.TryGetValue(MozaPlugin.Cm1PageGuid, out var v) && v;
            set
            {
                if (_plugin.Settings == null) return;
                if (_plugin.Settings.DashIsCm1ByGuid == null)
                    _plugin.Settings.DashIsCm1ByGuid = new Dictionary<Guid, bool>();
                _plugin.Settings.DashIsCm1ByGuid[MozaPlugin.Cm1PageGuid] = value;
            }
        }
    }
}
