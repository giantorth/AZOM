using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Telemetry.Display;
using MozaPlugin.Settings;

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

        /// <summary>The attached wheel's current-profile FSR1 field mappings, or null.</summary>
        internal Dictionary<string, Dictionary<string, Fsr1FieldMapping>>? GetActiveFsr1Mappings()
            => _plugin.ActiveWheelProfile?.Fsr1Mappings;

        /// <summary>Resolve one FSR1 field's user mapping, or null to use the catalog default.</summary>
        internal Fsr1FieldMapping? GetFsr1FieldMapping(string recordKey, string fieldId)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return null;
            var m = GetActiveFsr1Mappings();
            if (m == null) return null;
            return m.TryGetValue(recordKey, out var inner)
                && inner.TryGetValue(fieldId, out var fm) ? fm : null;
        }

        /// <summary>True when a mapping carries no opinion at all — empty property and no
        /// gain override — so it should be pruned rather than stored
        /// (dict-missing ≠ explicit-off: a default-only entry must not bloat the profile).</summary>
        private static bool IsDefaultFsr1Mapping(Fsr1FieldMapping? m) =>
            m == null
            || (string.IsNullOrEmpty((m.Property ?? "").Trim())
                && m.Scale == null && m.Bias == null);

        /// <summary>
        /// Persist (or clear) an FSR1 dashboard field assignment (property + input scale +
        /// gain overrides). A default-only mapping (see
        /// <see cref="IsDefaultFsr1Mapping"/>) removes the override so the field reverts to
        /// the catalog default. Tidies empty dicts and saves settings. The mapping is cloned
        /// so the stored copy is not aliased to a live UI row.
        /// </summary>
        internal void SetFsr1FieldMapping(string recordKey, string fieldId, Fsr1FieldMapping? mapping)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return;

            var profile = _plugin.ActiveWheelProfile;
            if (profile == null) return;

            // Copy-on-write at every level: the 20 ms display tick reads these dicts
            // (GetActiveFsr1Mappings) while the UI edits them, and the save debounce
            // serializes them — a published dict is never mutated in place.
            var mid = profile.Fsr1Mappings;
            Dictionary<string, Fsr1FieldMapping>? inner = null;
            if (mid != null && mid.TryGetValue(recordKey, out var inner0)) inner = inner0;

            if (IsDefaultFsr1Mapping(mapping))
            {
                // Removal: only touch the dicts if the entry exists; don't allocate empty branches.
                if (inner == null || !inner.ContainsKey(fieldId)) return;
                var innerNext = new Dictionary<string, Fsr1FieldMapping>(inner, StringComparer.OrdinalIgnoreCase);
                innerNext.Remove(fieldId);
                var midNext = new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(mid!, StringComparer.OrdinalIgnoreCase);
                if (innerNext.Count == 0) midNext.Remove(recordKey); else midNext[recordKey] = innerNext;
                profile.Fsr1Mappings = midNext;
                _plugin.SaveSettings();
                return;
            }

            var stored = mapping!.Clone();
            stored.Property = (stored.Property ?? "").Trim();
            var innerSet = inner == null
                ? new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Fsr1FieldMapping>(inner, StringComparer.OrdinalIgnoreCase);
            innerSet[fieldId] = stored;
            var midSet = mid == null
                ? new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(mid, StringComparer.OrdinalIgnoreCase);
            midSet[recordKey] = innerSet;
            profile.Fsr1Mappings = midSet;
            _plugin.SaveSettings();
        }

        /// <summary>Drop ALL per-field overrides on a record so it reverts to the
        /// catalog defaults. Used by reset-to-defaults.</summary>
        internal void ClearFsr1FieldOverrides(string recordKey)
        {
            var profile = _plugin.ActiveWheelProfile;
            var mid = profile?.Fsr1Mappings;
            if (profile == null || mid == null || !mid.ContainsKey(recordKey)) return;
            // COW (see SetFsr1FieldMapping).
            var midNext = new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(mid, StringComparer.OrdinalIgnoreCase);
            midNext.Remove(recordKey);
            profile.Fsr1Mappings = midNext;
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
            // While the byte probe is armed it pins the page so a mid-sweep page-report
            // can't scramble stepping (see MozaPlugin.Fsr1Probe.FrozenIndex). The driver,
            // probe target, channel UI, and label all read through here, so they stay in
            // agreement on the frozen page for the probe's lifetime.
            int frozen = _plugin.Fsr1Probe.FrozenIndex;
            if (frozen >= 0) return frozen;
            // The idle page while idle; the stored page is the driving pick.
            int idle = _plugin.IdleDashboards?.Fsr1OverrideIndex ?? -1;
            if (idle >= 0) return idle;
            return RawActiveFsr1Index();
        }

        /// <summary>The stored page for the current wheel page (the in-game pick).</summary>
        internal int StoredFsr1Index() => RawActiveFsr1Index();

        /// <summary>Queue a select without touching the stored page (idle dashboard).</summary>
        internal void QueueFsr1Select(int index) => Interlocked.Exchange(ref _fsr1PendingSelect, index);

        /// <summary>The persisted active index for the current wheel page, ignoring any probe
        /// freeze and idle page — the in-game pick used for change detection.</summary>
        private int RawActiveFsr1Index()
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
            // A user pick ends the idle page; the wheel is on that page, not the stored one.
            bool idleWasShowing = sendToWheel && (_plugin.IdleDashboards?.YieldFsr1() ?? false);
            bool changed = true;   // unknown wheel/settings → can't dedupe, emit
            var g = _plugin.GetCurrentWheelPageGuid();
            if (g.HasValue && _plugin.Settings != null)
            {
                // COW: written from the UI and the serial read thread (Param-6 follow)
                // while the save debounce serializes the dict.
                var cur = _plugin.Settings.Fsr1ActiveDashboardByWheelGuid;
                changed = cur == null || !cur.TryGetValue(g.Value, out var prev) || prev != index;
                if (changed)
                {
                    var next = cur == null ? new Dictionary<Guid, int>() : new Dictionary<Guid, int>(cur);
                    next[g.Value] = index;
                    _plugin.Settings.Fsr1ActiveDashboardByWheelGuid = next;
                }
                if (changed && !sendToWheel) _plugin.SaveSettings(); // host path saves after queuing below
            }
            // Dedupe: the wheel PERSISTS the index to EEPROM (Table 7 Param 6) on every
            // select, so a re-pick of the page it is already on must not hit the wire.
            // The stored index tracks wheel self-switches (Param-6 log follow), so it is
            // an accurate mirror of the wheel's current page.
            if (sendToWheel && (changed || idleWasShowing))
            {
                Interlocked.Exchange(ref _fsr1PendingSelect, index);
                _plugin.SaveSettings();
            }
            _plugin.RaiseFsr1ActiveIndexChanged();
        }

        /// <summary>Sender drains the pending user-select index (or -1). One-shot.</summary>
        internal int TakePendingFsr1Select() => Interlocked.Exchange(ref _fsr1PendingSelect, -1);

        // Pending display-brightness percent (-1 = none). Same pattern as the pending
        // select: the slot holds only the LATEST value so a slider drag coalesces to a
        // single wheel EEPROM write (Table 7 Param 5), drained by the driver behind the
        // shared Table-7 write gate.
        private int _fsr1PendingBrightness = -1;
        private int _fsr1LastSentBrightness = -1;

        /// <summary>Queue an FSR1 display-brightness push (0–100). Same-value repeats
        /// are dropped so a slider returning to its start writes nothing.</summary>
        internal void QueueFsr1Brightness(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (percent == _fsr1LastSentBrightness) return;
            Interlocked.Exchange(ref _fsr1PendingBrightness, percent);
        }

        /// <summary>Driver drains the pending brightness (or -1). One-shot; records the
        /// drained value as last-sent for the same-value dedupe.</summary>
        internal int TakePendingFsr1Brightness()
        {
            int v = Interlocked.Exchange(ref _fsr1PendingBrightness, -1);
            if (v >= 0) _fsr1LastSentBrightness = v;
            return v;
        }

        /// <summary>Record a wheel-reported active index parsed from the Param 6 log
        /// (wheel self-switch); follows without re-commanding the wheel.</summary>
        internal void NoteFsr1WheelIndex(int index)
        {
            // Compare against the RAW stored index, not GetActiveFsr1Index — the latter is
            // pinned during a probe, which would suppress real wheel switches and break the
            // change log used to diagnose page-report spam.
            // Our own idle-page select coming back — not a switch made on the wheel.
            if (_plugin.IdleDashboards?.IsFsr1IdleEcho(index) == true) return;
            bool idleWasShowing = _plugin.IdleDashboards?.YieldFsr1() ?? false;
            int prev = RawActiveFsr1Index();
            if (index == prev)
            {
                if (idleWasShowing) _plugin.RaiseFsr1ActiveIndexChanged();
                return;
            }
            MozaLog.Info($"[AZOM] FSR1 wheel-reported page index {prev} → {index} (Param-6 follow)");
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

        /// <summary>The CM1 dash's current-profile field mappings (copy-on-write snapshot), or null.</summary>
        internal Dictionary<string, Fsr1FieldMapping>? GetActiveCm1Mappings()
            => _plugin.DeviceProfiles?.DashProfileFor(MozaPlugin.Cm1PageGuid)?.Cm1Mappings;

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
            var profile = _plugin.DeviceProfiles?.DashProfileFor(MozaPlugin.Cm1PageGuid);
            if (profile == null) return;
            // COW (see SetFsr1FieldMapping): the 50 ms CM1 driver tick reads these.
            var inner = profile.Cm1Mappings;
            string trimmed = (property ?? "").Trim();
            var innerNext = inner == null
                ? new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Fsr1FieldMapping>(inner, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(trimmed) && scale == null)
                innerNext.Remove(fieldId);
            else
                innerNext[fieldId] = new Fsr1FieldMapping { Property = trimmed, Scale = scale };
            profile.Cm1Mappings = innerNext;
            _plugin.SaveSettings();
        }

        /// <summary>Clear ALL CM1 field mappings (reset-to-defaults).</summary>
        internal void ClearCm1Mappings()
        {
            var profile = _plugin.DeviceProfiles?.DashProfileFor(MozaPlugin.Cm1PageGuid, seed: false);
            if (profile?.Cm1Mappings == null || profile.Cm1Mappings.Count == 0) return;
            profile.Cm1Mappings = new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase);
            _plugin.SaveSettings();
        }

        private int _cm1PendingSelect = -1;

        /// <summary>Current CM1 active dashboard page (1-based), default 1: the idle page
        /// while no game runs, else the stored pick.</summary>
        internal int GetActiveCm1Index()
        {
            int idle = _plugin.IdleDashboards?.Cm1OverrideIndex ?? -1;
            return idle >= 0 ? idle : StoredCm1Index();
        }

        /// <summary>Queue a CM1 select without touching the stored page (idle dashboard).</summary>
        internal void QueueCm1Select(int index) => Interlocked.Exchange(ref _cm1PendingSelect, index);

        /// <summary>The stored CM1 page (the in-game pick), default 1.</summary>
        internal int StoredCm1Index()
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
            if (sendToWheel) _plugin.IdleDashboards?.YieldCm1();
            if (_plugin.Settings != null)
            {
                var cur = _plugin.Settings.Cm1ActiveDashboardByGuid;
                bool changed = cur == null || !cur.TryGetValue(MozaPlugin.Cm1PageGuid, out var prev) || prev != index;
                if (changed)
                {
                    // COW, as for the FSR1 index above.
                    var next = cur == null ? new Dictionary<Guid, int>() : new Dictionary<Guid, int>(cur);
                    next[MozaPlugin.Cm1PageGuid] = index;
                    _plugin.Settings.Cm1ActiveDashboardByGuid = next;
                }
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
            // Our own idle-page select coming back — not a switch made on the dash.
            if (_plugin.IdleDashboards?.IsCm1IdleEcho(index) == true) return;
            bool idleWasShowing = _plugin.IdleDashboards?.YieldCm1() ?? false;
            if (index == StoredCm1Index())
            {
                if (idleWasShowing) _plugin.RaiseCm1ActiveIndexChanged();
                return;
            }
            SetActiveCm1Index(index, sendToWheel: false);
        }

        // True once THIS session's bus dash has been confirmed a CM1 (group-0x35)
        // by the live discriminator. Deliberately SESSION-ONLY (in-memory): the
        // CM1-vs-CM2 verdict is a statement about what's physically on the bus
        // right now and MUST be re-derived each boot. It used to be persisted "so
        // later boots skip the tier-def probe", keyed by the constant Cm1PageGuid —
        // i.e. one global bit, not per-device — so a single mis-latch (e.g. a real
        // CM2 whose cold-start was starved before the dual-bus start-gate fix)
        // turned EVERY future bus dash into a CM1 permanently, across reboots and
        // hardware swaps (CS Pro + bus CM2 bundle 2026-06-18: a real CM2 stuck on
        // the CM1 driver — telemetry + dashboard tab dead, only LEDs alive). The
        // discriminator re-latches a genuine CM1 within ~5 s via the 0x8E fast path,
        // so re-deriving each boot is cheap and correct.
        private volatile bool _dashIsCm1;

        /// <summary>True once this session's bus dash is confirmed a CM1 (group-0x35).
        /// Session-only — never persisted; re-derived each boot by the discriminator.</summary>
        internal bool DashIsCm1
        {
            get => _dashIsCm1;
            set => _dashIsCm1 = value;
        }
    }
}
