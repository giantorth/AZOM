using System;
using System.Linq;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Display;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// FSR V1 display-decode probes plus the live byte-strip visualization feed.
    /// Diagnostic-only: nothing here runs unless the user arms a probe from the
    /// wheel's Dashboard tab or a button action. Extracted from MozaPlugin.
    /// </summary>
    internal sealed class Fsr1ProbeTool
    {
        private readonly MozaPlugin _plugin;

        internal Fsr1ProbeTool(MozaPlugin plugin)
        {
            _plugin = plugin;
        }

        // FSR V1 single-byte probe diagnostic. The driver streams an all-zero record with
        // exactly ONE data byte ramping 0..255, isolating one payload offset at a time so
        // the user can see which on-screen box animates (boundary = where the active box
        // changes; width = the run of consecutive offsets driving the same box; scale =
        // displayed value ÷ byte value). _step is the global step index across the
        // active page's record(s); -1 = probe off. Volatile: UI writes, driver reads.
        private volatile int _step = -1;

        // Page index captured when the byte probe is armed. The wheel streams its
        // "Table 7, Param 6 Written: N" page-report log continuously, which the plugin
        // follows live (NoteFsr1WheelIndex) — so the active index can move WHILE the user
        // steps the probe, scrambling the step→(record,byte) mapping every refresh. Freezing
        // the index for the probe's lifetime keeps stepping stable and contiguous; -1 = no
        // freeze (probe off). See GetActiveFsr1Index.
        private volatile int _frozenIndex = -1;

        /// <summary>Page index the byte probe is locked to while armed, or -1 when the probe
        /// is off — <see cref="Telemetry.Fsr1Cm1MappingCoordinator.GetActiveFsr1Index"/> returns
        /// this (instead of the live, log-followed index) so a stepping sweep can't be
        /// derailed by a mid-probe page-report.</summary>
        internal int FrozenIndex => _step >= 0 ? _frozenIndex : -1;

        /// <summary>True while EITHER FSR V1 probe diagnostic is active — the toolbar
        /// single-byte stepper or the row-driven field-span probe. The two are mutually
        /// exclusive; the driver gates its probe override on this.</summary>
        internal bool Active => _step >= 0 || _fieldProbe != null;

        /// <summary>Current 0-based probe step across the active page's data bytes.</summary>
        internal int StepIndex => _step;

        /// <summary>The record(s) the probe walks — the active page's type(s), or the full
        /// live set as a fallback when the active index is unmapped (mirrors the driver's
        /// own active/fallback selection so the probe targets what is actually streaming).</summary>
        internal Fsr1Dashboard[] Records()
        {
            var active = Fsr1DashboardCatalog.ByIndex(_plugin.GetActiveFsr1Index());
            return active.Length > 0 ? active : Fsr1DashboardCatalog.LiveDashboards;
        }

        /// <summary>Total probe steps = sum of data-byte counts (PayloadLen-5) across the
        /// active page's record(s).</summary>
        internal int StepCount()
        {
            int n = 0;
            foreach (var d in Records())
                n += Math.Max(0, d.PayloadLen - 5);
            return n;
        }

        /// <summary>Map the current step to a (record type, payload offset) target. Returns
        /// <c>(0, -1)</c> when the probe is off or the step is out of range.</summary>
        internal (byte type, int offset) Target()
        {
            int step = _step;
            if (step < 0) return (0, -1);
            foreach (var d in Records())
            {
                int count = Math.Max(0, d.PayloadLen - 5);
                if (step < count) return (d.RecordType, 5 + step);
                step -= count;
            }
            return (0, -1);
        }

        /// <summary>Human-readable description of the byte the probe currently targets,
        /// annotated with the catalog field that — per the CURRENT decode — owns it and
        /// whether the byte is that field's first byte (an assumed field boundary). This
        /// surfaces the hypothesized boundaries while stepping so the user can spot where
        /// the on-screen box disagrees with the catalog's field layout.</summary>
        internal string TargetLabel()
        {
            var (type, off) = Target();
            if (off < 0) return "—";
            string where = $"record 0x{type:X2}, byte {off}  ({_step + 1}/{StepCount()})";
            var dash = Fsr1DashboardCatalog.ByType(type);
            var f = dash?.Fields.FirstOrDefault(x => Array.IndexOf(x.Offsets, off) >= 0);
            if (f == null) return where + "  — unmapped byte";
            bool boundary = f.Offsets.Length > 0 && f.Offsets[0] == off;
            int width = f.Offsets.Length;
            return $"{where}  — {f.FieldId} \"{f.Label}\" " +
                   (boundary ? $"[◀ field start, {width}B]" : "[cont]");
        }

        /// <summary>Toggle the FSR V1 probe (starts at the first data byte, offset 5).
        /// FSR1-only; mutually exclusive with the sweep test pattern.</summary>
        internal void SetProbe(bool on)
        {
            if (on)
            {
                // Capture the live page BEFORE arming (step still -1, so GetActiveFsr1Index
                // returns the real log-followed index, not a stale freeze), then lock to it.
                _frozenIndex = _plugin.GetActiveFsr1Index();
                _step = 0;
                _fieldProbe = null;            // exclusive with the field probe
                _plugin.SetDashboardTestPattern(false);
            }
            else
            {
                _step = -1;
                _frozenIndex = -1;
            }
        }

        /// <summary>Step the probe offset by <paramref name="delta"/>, wrapping within the
        /// active page's total data-byte count. No-op when the probe is off.</summary>
        internal void Step(int delta)
        {
            if (_step < 0) return;
            int total = StepCount();
            if (total <= 0) { _step = 0; return; }
            int s = (_step + delta) % total;
            if (s < 0) s += total;
            _step = s;
        }

        // Row-driven field-span probe. Armed while a field's inline editor is open so the
        // user watches the on-screen box for that field as they step its boundary edges.
        // Distinct from the byte-stepper (_step) and mutually exclusive with it;
        // holds the record + field id and resolves to the field's CURRENT span on demand.
        private sealed class FieldProbe { public string RecordKey = ""; public string FieldId = ""; }
        private volatile FieldProbe? _fieldProbe;

        /// <summary>Arm the row-driven field-span probe on one FSR1 field (disarms the
        /// byte-stepper and the test pattern). Re-call as the field's span changes.</summary>
        internal void SetFieldProbe(string recordKey, string fieldId)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return;
            _step = -1;
            _plugin.SetDashboardTestPattern(false);
            _fieldProbe = new FieldProbe { RecordKey = recordKey, FieldId = fieldId };
        }

        /// <summary>Disarm BOTH probes without touching the test pattern — used by
        /// MozaPlugin.SetDashboardTestPattern, which is mutually exclusive with them
        /// (calling SetProbe/SetFieldProbe from there would re-enter).</summary>
        internal void DisarmAll()
        {
            _step = -1;
            _fieldProbe = null;
        }

        /// <summary>Disarm the field-span probe (row editor closed).</summary>
        internal void ClearFieldProbe() => _fieldProbe = null;

        /// <summary>The field-span probe's CURRENT resolved target — record type, the contiguous
        /// byte span (start..end inclusive), and (for a bit-packed field) its exact bit run — after
        /// applying its user override, or null when not armed / unresolvable. <c>packed</c> selects
        /// the overlay probe (ramp only the field's bits over live data) vs the byte-span probe.</summary>
        internal (byte type, int startOff, int endOff, bool packed, int bitOffset, int bitWidth, bool msbFirst)? FieldProbeTarget()
        {
            var p = _fieldProbe;
            if (p == null) return null;
            var dash = Fsr1DashboardCatalog.ByKey(p.RecordKey);
            if (dash == null) return null;
            // Resolve through the SAME partition the driver emits so the lit span
            // matches the wire exactly.
            foreach (var slot in Fsr1DashboardCatalog.ResolvePartition(dash))
                if (slot.Field.FieldId == p.FieldId)
                    return (dash.RecordType, slot.ByteStart, slot.ByteEnd,
                            !slot.IsByteAligned, slot.BitOffset, slot.BitWidth, slot.MsbFirst);
            return null;
        }

        // ── FSR1 live numeric visualization channel ─────────────────────────
        // When the channel-mapping panel is showing an FSR1 wheel, it asks the driver to
        // publish a per-tick snapshot of the data it streams (each field's resolved span,
        // raw bytes, post-scale value) so the UI can draw a live byte strip. Volatile
        // single-writer (driver) / single-reader (UI 2 Hz timer), matching driver threading.
        private volatile bool _vizActive;
        private volatile Fsr1VizSnapshot? _viz;

        /// <summary>True while the channel-mapping panel wants the FSR1 viz snapshot.</summary>
        internal bool VizActive => _vizActive;

        /// <summary>Arm/disarm FSR1 viz capture (panel load/teardown). Clears the last
        /// snapshot on disarm so a stale strip never lingers.</summary>
        internal void SetVizActive(bool on)
        {
            _vizActive = on;
            if (!on) _viz = null;
        }

        /// <summary>Driver publishes the latest streamed-data snapshot (or null).</summary>
        internal void SetVizSnapshot(Fsr1VizSnapshot? snap) => _viz = snap;

        /// <summary>UI reads the latest FSR1 viz snapshot, or null when none yet.</summary>
        internal Fsr1VizSnapshot? GetVizSnapshot() => _viz;
    }
}
