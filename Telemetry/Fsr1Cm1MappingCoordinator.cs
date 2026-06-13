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

        /// <summary>Resolve one FSR1 field's user mapping, or null to use the catalog default.
        /// A synthetic split field returns its inline mapping (it carries an explicit span and
        /// is never stored in the catalog-override dict).</summary>
        internal Fsr1FieldMapping? GetFsr1FieldMapping(string recordKey, string fieldId)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return null;
            var syn = FindSynthetic(recordKey, fieldId);
            if (syn != null) return syn.Mapping;
            var m = GetActiveFsr1Mappings();
            if (m == null) return null;
            return m.TryGetValue(recordKey, out var inner)
                && inner.TryGetValue(fieldId, out var fm) ? fm : null;
        }

        /// <summary>True when a mapping carries no opinion at all — empty property and no
        /// boundary/encoding/gain override — so it should be pruned rather than stored
        /// (dict-missing ≠ explicit-off: a default-only entry must not bloat the profile).</summary>
        private static bool IsDefaultFsr1Mapping(Fsr1FieldMapping? m) =>
            m == null
            || (string.IsNullOrEmpty((m.Property ?? "").Trim())
                && m.StartOffset == null && m.EndOffset == null
                && m.LittleEndian == null && m.Scale == null && m.Bias == null);

        /// <summary>
        /// Persist (or clear) an FSR1 dashboard field assignment (property + input scale +
        /// boundary/encoding/gain overrides). A default-only mapping (see
        /// <see cref="IsDefaultFsr1Mapping"/>) removes the override so the field reverts to
        /// the catalog default. Tidies empty dicts and saves settings. The mapping is cloned
        /// so the stored copy is not aliased to a live UI row.
        /// </summary>
        internal void SetFsr1FieldMapping(string recordKey, string fieldId, Fsr1FieldMapping? mapping)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return;

            // Synthetic split field: replace its inline mapping (never prune — a synthetic
            // always owns an explicit byte span). Preserve the span if the caller omitted it.
            var syn = FindSynthetic(recordKey, fieldId);
            if (syn != null)
            {
                var newMap = (mapping ?? new Fsr1FieldMapping()).Clone();
                if (newMap.StartOffset == null) newMap.StartOffset = syn.Mapping?.StartOffset;
                if (newMap.EndOffset == null) newMap.EndOffset = syn.Mapping?.EndOffset;
                newMap.Property = (newMap.Property ?? "").Trim();
                syn.Mapping = newMap;
                _plugin.SaveSettings();
                return;
            }

            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            if (profile.Fsr1DashboardMappings == null)
                profile.Fsr1DashboardMappings = new Dictionary<Guid, Dictionary<string, Dictionary<string, Fsr1FieldMapping>>>();
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return;

            bool isDefault = IsDefaultFsr1Mapping(mapping);

            // Removal: only touch the dicts if the entry exists; don't allocate empty branches.
            if (isDefault)
            {
                if (profile.Fsr1DashboardMappings.TryGetValue(g.Value, out var mid) && mid != null
                    && mid.TryGetValue(recordKey, out var inr) && inr != null)
                {
                    inr.Remove(fieldId);
                    if (inr.Count == 0) mid.Remove(recordKey);
                    if (mid.Count == 0) profile.Fsr1DashboardMappings.Remove(g.Value);
                    _plugin.SaveSettings();
                }
                return;
            }

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

            var stored = mapping!.Clone();
            stored.Property = (stored.Property ?? "").Trim();
            inner[fieldId] = stored;
            _plugin.SaveSettings();
        }

        // ── FSR V1 synthetic split fields ───────────────────────────────────
        // A user "split" carves a new sub-span out of an existing field; the resulting
        // field is net-new (not in the static catalog) and gets its own channel mapping,
        // stored inline in Fsr1SyntheticField.Mapping (always with an explicit byte span).
        // Get/SetFsr1FieldMapping above are synthetic-aware so the divider editor and the
        // value/scale editors keep working without per-call branching.

        private static readonly List<Fsr1SyntheticField> _emptySynthetic = new List<Fsr1SyntheticField>();

        /// <summary>The synthetic split fields on <paramref name="recordKey"/> for the current
        /// wheel page (live list; empty when none). Read-only callers must not mutate it.</summary>
        internal List<Fsr1SyntheticField> GetSyntheticFields(string recordKey)
        {
            if (string.IsNullOrEmpty(recordKey)) return _emptySynthetic;
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile?.Fsr1SyntheticFields == null) return _emptySynthetic;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return _emptySynthetic;
            if (profile.Fsr1SyntheticFields.TryGetValue(g.Value, out var mid) && mid != null
                && mid.TryGetValue(recordKey, out var list) && list != null)
                return list;
            return _emptySynthetic;
        }

        private Fsr1SyntheticField? FindSynthetic(string recordKey, string fieldId)
        {
            foreach (var s in GetSyntheticFields(recordKey))
                if (s != null && s.FieldId == fieldId) return s;
            return null;
        }

        /// <summary>Next free synthetic field id on the record (e.g. "split1", "split2").</summary>
        internal string NextSyntheticFieldId(string recordKey)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in GetSyntheticFields(recordKey))
                if (s != null) ids.Add(s.FieldId);
            int n = 1;
            while (ids.Contains("split" + n)) n++;
            return "split" + n;
        }

        /// <summary>
        /// Split the field <paramref name="fieldId"/> on <paramref name="recordKey"/> at its
        /// midpoint: the original keeps the low bytes <c>[s, s+⌊w/2⌋-1]</c>, a new synthetic
        /// field takes the high bytes <c>[s+⌊w/2⌋, e]</c> with its own (empty) channel. Requires
        /// the field be ≥ 2 bytes wide. Returns false if it can't be split. The record stays a
        /// gapless partition. Works on a catalog OR a synthetic parent.
        /// </summary>
        internal bool SplitFsr1Field(string recordKey, string fieldId)
        {
            var span = ResolveFieldSpan(recordKey, fieldId);
            if (span == null) return false;
            int s = span.Value.start, e = span.Value.end;
            int w = e - s + 1;
            if (w < 2) return false;
            int half = w / 2;                 // floor
            int parentEnd = s + half - 1;
            int synthStart = s + half;
            int synthEnd = e;

            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return false;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return false;

            // 1. Shrink the parent to the low bytes (catalog → boundary override; synthetic → inline).
            WriteFieldSpan(recordKey, fieldId, s, parentEnd);

            // 2. Add the synthetic owning the high bytes, with its own empty channel mapping.
            string newId = NextSyntheticFieldId(recordKey);
            var list = GetOrCreateSyntheticList(profile, g.Value, recordKey);
            list.Add(new Fsr1SyntheticField
            {
                FieldId = newId,
                Label = "Split " + newId,
                Mapping = new Fsr1FieldMapping { StartOffset = synthStart, EndOffset = synthEnd },
            });
            _plugin.SaveSettings();
            return true;
        }

        /// <summary>
        /// Remove a synthetic split field, reclaiming its bytes into an adjacent field so the
        /// record stays a gapless partition: absorb into the left neighbour if the merged width
        /// ≤ 3, else the right neighbour, else return false (caller surfaces "shrink an adjacent
        /// field first"). Only synthetic fields can be removed.
        /// </summary>
        internal bool RemoveFsr1Split(string recordKey, string fieldId)
        {
            if (FindSynthetic(recordKey, fieldId) == null) return false;
            var span = ResolveFieldSpan(recordKey, fieldId);
            if (span == null) return false;
            int s = span.Value.start, e = span.Value.end;

            var dash = Fsr1DashboardCatalog.ByKey(recordKey);
            if (dash == null) return false;

            // Resolve neighbours across catalog/synthetic via the composed field list.
            string? leftId = null, rightId = null;
            int leftStart = 0, rightEnd = 0;
            foreach (var f in Fsr1FieldComposer.FieldsFor(_plugin, dash))
            {
                if (f.FieldId == fieldId) continue;
                var fs = ResolveFieldSpan(recordKey, f.FieldId);
                if (fs == null) continue;
                if (fs.Value.end == s - 1) { leftId = f.FieldId; leftStart = fs.Value.start; }
                if (fs.Value.start == e + 1) { rightId = f.FieldId; rightEnd = fs.Value.end; }
            }

            if (leftId != null && (e - leftStart + 1) <= 3)
                WriteFieldSpan(recordKey, leftId, leftStart, e);
            else if (rightId != null && (rightEnd - s + 1) <= 3)
                WriteFieldSpan(recordKey, rightId, s, rightEnd);
            else
                return false;   // neither neighbour can absorb the freed bytes

            RemoveSynthetic(recordKey, fieldId);
            _plugin.SaveSettings();
            return true;
        }

        /// <summary>Remove all synthetic split fields on a record (reset-to-defaults).</summary>
        internal void ClearSyntheticFields(string recordKey)
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile?.Fsr1SyntheticFields == null) return;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return;
            if (profile.Fsr1SyntheticFields.TryGetValue(g.Value, out var mid) && mid != null
                && mid.Remove(recordKey))
            {
                if (mid.Count == 0) profile.Fsr1SyntheticFields.Remove(g.Value);
                _plugin.SaveSettings();
            }
        }

        // Resolve a field's effective byte span (catalog or synthetic) through the same
        // ResolveLayout the driver/emitter use, so split math sees the live layout.
        private (int start, int end, int payloadLen)? ResolveFieldSpan(string recordKey, string fieldId)
        {
            var dash = Fsr1DashboardCatalog.ByKey(recordKey);
            if (dash == null) return null;
            var def = Fsr1FieldComposer.FindField(_plugin, recordKey, fieldId);
            if (def == null) return null;
            var m = GetFsr1FieldMapping(recordKey, fieldId);
            var (offsets, _) = Fsr1DashboardCatalog.ResolveLayout(def, m, dash.PayloadLen);
            if (offsets.Length == 0) return null;
            return (offsets[0], offsets[offsets.Length - 1], dash.PayloadLen);
        }

        // Pin a field's byte span. Synthetic → inline mapping; catalog → deviation-only
        // boundary override (offsets equal to the catalog default are nulled so they prune).
        private void WriteFieldSpan(string recordKey, string fieldId, int newStart, int newEnd)
        {
            var syn = FindSynthetic(recordKey, fieldId);
            if (syn != null)
            {
                syn.Mapping ??= new Fsr1FieldMapping();
                syn.Mapping.StartOffset = newStart;
                syn.Mapping.EndOffset = newEnd;
                _plugin.SaveSettings();
                return;
            }

            var dash = Fsr1DashboardCatalog.ByKey(recordKey);
            Fsr1FieldDef? def = null;
            if (dash != null)
                foreach (var f in dash.Fields)
                    if (f.FieldId == fieldId) { def = f; break; }
            int defStart = def != null && def.Offsets.Length > 0 ? def.Offsets[0] : newStart;
            int defEnd = def != null && def.Offsets.Length > 0 ? def.Offsets[def.Offsets.Length - 1] : newEnd;

            var existing = GetFsr1FieldMapping(recordKey, fieldId);
            var map = existing?.Clone() ?? new Fsr1FieldMapping();
            map.StartOffset = newStart == defStart ? (int?)null : newStart;
            map.EndOffset = newEnd == defEnd ? (int?)null : newEnd;
            SetFsr1FieldMapping(recordKey, fieldId, map);
        }

        private void RemoveSynthetic(string recordKey, string fieldId)
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile?.Fsr1SyntheticFields == null) return;
            var g = _plugin.GetCurrentWheelPageGuid();
            if (!g.HasValue) return;
            if (profile.Fsr1SyntheticFields.TryGetValue(g.Value, out var mid) && mid != null
                && mid.TryGetValue(recordKey, out var list) && list != null)
            {
                list.RemoveAll(x => x != null && x.FieldId == fieldId);
                if (list.Count == 0) mid.Remove(recordKey);
                if (mid.Count == 0) profile.Fsr1SyntheticFields.Remove(g.Value);
            }
        }

        private static List<Fsr1SyntheticField> GetOrCreateSyntheticList(
            MozaProfile profile, Guid g, string recordKey)
        {
            if (profile.Fsr1SyntheticFields == null)
                profile.Fsr1SyntheticFields = new Dictionary<Guid, Dictionary<string, List<Fsr1SyntheticField>>>();
            if (!profile.Fsr1SyntheticFields.TryGetValue(g, out var mid) || mid == null)
            {
                mid = new Dictionary<string, List<Fsr1SyntheticField>>(StringComparer.OrdinalIgnoreCase);
                profile.Fsr1SyntheticFields[g] = mid;
            }
            if (!mid.TryGetValue(recordKey, out var list) || list == null)
            {
                list = new List<Fsr1SyntheticField>();
                mid[recordKey] = list;
            }
            return list;
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

        /// <summary>Persist (or clear) a CM1 field assignment (property + optional gain
        /// override). Empty property AND null scale removes the override (field reverts to its
        /// catalog default/constant). Saves settings.</summary>
        internal void SetCm1FieldMapping(string fieldId, string property, double? scale)
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
            if (string.IsNullOrEmpty(trimmed) && scale == null)
            {
                inner.Remove(fieldId);
                if (inner.Count == 0) profile.Cm1FieldMappings.Remove(MozaPlugin.Cm1PageGuid);
            }
            else
            {
                inner[fieldId] = new Fsr1FieldMapping { Property = trimmed, Scale = scale };
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
