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

        // FSR V1 single-byte probe diagnostic. The driver streams an all-zero record with
        // exactly ONE data byte ramping 0..255, isolating one payload offset at a time so
        // the user can see which on-screen box animates (boundary = where the active box
        // changes; width = the run of consecutive offsets driving the same box; scale =
        // displayed value ÷ byte value). _fsr1ProbeStep is the global step index across the
        // active page's record(s); -1 = probe off. Volatile: UI writes, driver reads.
        private volatile int _fsr1ProbeStep = -1;

        // Page index captured when the byte probe is armed. The wheel streams its
        // "Table 7, Param 6 Written: N" page-report log continuously, which the plugin
        // follows live (NoteFsr1WheelIndex) — so the active index can move WHILE the user
        // steps the probe, scrambling the step→(record,byte) mapping every refresh. Freezing
        // the index for the probe's lifetime keeps stepping stable and contiguous; -1 = no
        // freeze (probe off). See GetActiveFsr1Index.
        private volatile int _fsr1ProbeFrozenIndex = -1;

        /// <summary>Page index the byte probe is locked to while armed, or -1 when the probe
        /// is off — <see cref="Telemetry.Fsr1Cm1MappingCoordinator.GetActiveFsr1Index"/> returns
        /// this (instead of the live, log-followed index) so a stepping sweep can't be
        /// derailed by a mid-probe page-report.</summary>
        internal int Fsr1ProbeFrozenIndex => _fsr1ProbeStep >= 0 ? _fsr1ProbeFrozenIndex : -1;

        /// <summary>True while EITHER FSR V1 probe diagnostic is active — the toolbar
        /// single-byte stepper or the row-driven field-span probe. The two are mutually
        /// exclusive; the driver gates its probe override on this.</summary>
        internal bool Fsr1ProbeActive => _fsr1ProbeStep >= 0 || _fsr1FieldProbe != null;

        /// <summary>Current 0-based probe step across the active page's data bytes.</summary>
        internal int Fsr1ProbeStepIndex => _fsr1ProbeStep;

        /// <summary>The record(s) the probe walks — the active page's type(s), or the full
        /// live set as a fallback when the active index is unmapped (mirrors the driver's
        /// own active/fallback selection so the probe targets what is actually streaming).</summary>
        internal Telemetry.Fsr1Dashboard[] Fsr1ProbeRecords()
        {
            var active = Telemetry.Fsr1DashboardCatalog.ByIndex(GetActiveFsr1Index());
            return active.Length > 0 ? active : Telemetry.Fsr1DashboardCatalog.LiveDashboards;
        }

        /// <summary>Total probe steps = sum of data-byte counts (PayloadLen-5) across the
        /// active page's record(s).</summary>
        internal int Fsr1ProbeStepCount()
        {
            int n = 0;
            foreach (var d in Fsr1ProbeRecords())
                n += System.Math.Max(0, d.PayloadLen - 5);
            return n;
        }

        /// <summary>Map the current step to a (record type, payload offset) target. Returns
        /// <c>(0, -1)</c> when the probe is off or the step is out of range.</summary>
        internal (byte type, int offset) Fsr1ProbeTarget()
        {
            int step = _fsr1ProbeStep;
            if (step < 0) return (0, -1);
            foreach (var d in Fsr1ProbeRecords())
            {
                int count = System.Math.Max(0, d.PayloadLen - 5);
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
        internal string Fsr1ProbeTargetLabel()
        {
            var (type, off) = Fsr1ProbeTarget();
            if (off < 0) return "—";
            string where = $"record 0x{type:X2}, byte {off}  ({_fsr1ProbeStep + 1}/{Fsr1ProbeStepCount()})";
            var dash = Fsr1DashboardCatalog.ByType(type);
            var f = dash?.Fields.FirstOrDefault(x => System.Array.IndexOf(x.Offsets, off) >= 0);
            if (f == null) return where + "  — unmapped byte";
            bool boundary = f.Offsets.Length > 0 && f.Offsets[0] == off;
            int width = f.Offsets.Length;
            return $"{where}  — {f.FieldId} \"{f.Label}\" " +
                   (boundary ? $"[◀ field start, {width}B]" : "[cont]");
        }

        /// <summary>Toggle the FSR V1 probe (starts at the first data byte, offset 5).
        /// FSR1-only; mutually exclusive with the sweep test pattern.</summary>
        internal void SetFsr1Probe(bool on)
        {
            if (on)
            {
                // Capture the live page BEFORE arming (step still -1, so GetActiveFsr1Index
                // returns the real log-followed index, not a stale freeze), then lock to it.
                _fsr1ProbeFrozenIndex = GetActiveFsr1Index();
                _fsr1ProbeStep = 0;
                _fsr1FieldProbe = null;            // exclusive with the field probe
                SetDashboardTestPattern(false);
            }
            else
            {
                _fsr1ProbeStep = -1;
                _fsr1ProbeFrozenIndex = -1;
            }
        }

        /// <summary>Step the probe offset by <paramref name="delta"/>, wrapping within the
        /// active page's total data-byte count. No-op when the probe is off.</summary>
        internal void StepFsr1Probe(int delta)
        {
            if (_fsr1ProbeStep < 0) return;
            int total = Fsr1ProbeStepCount();
            if (total <= 0) { _fsr1ProbeStep = 0; return; }
            int s = (_fsr1ProbeStep + delta) % total;
            if (s < 0) s += total;
            _fsr1ProbeStep = s;
        }

        // Row-driven field-span probe. Armed while a field's inline editor is open so the
        // user watches the on-screen box for that field as they step its boundary edges.
        // Distinct from the byte-stepper (_fsr1ProbeStep) and mutually exclusive with it;
        // holds the record + field id and resolves to the field's CURRENT span on demand.
        private sealed class Fsr1FieldProbe { public string RecordKey = ""; public string FieldId = ""; }
        private volatile Fsr1FieldProbe? _fsr1FieldProbe;

        /// <summary>Arm the row-driven field-span probe on one FSR1 field (disarms the
        /// byte-stepper and the test pattern). Re-call as the field's span changes.</summary>
        internal void SetFsr1FieldProbe(string recordKey, string fieldId)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return;
            _fsr1ProbeStep = -1;
            SetDashboardTestPattern(false);
            _fsr1FieldProbe = new Fsr1FieldProbe { RecordKey = recordKey, FieldId = fieldId };
        }

        /// <summary>Disarm the field-span probe (row editor closed).</summary>
        internal void ClearFsr1FieldProbe() => _fsr1FieldProbe = null;

        /// <summary>The field-span probe's CURRENT resolved target — record type, the contiguous
        /// byte span (start..end inclusive), and (for a bit-packed field) its exact bit run — after
        /// applying its user override, or null when not armed / unresolvable. <c>packed</c> selects
        /// the overlay probe (ramp only the field's bits over live data) vs the byte-span probe.</summary>
        internal (byte type, int startOff, int endOff, bool packed, int bitOffset, int bitWidth, bool msbFirst)? Fsr1FieldProbeTarget()
        {
            var p = _fsr1FieldProbe;
            if (p == null) return null;
            var dash = Telemetry.Fsr1DashboardCatalog.ByKey(p.RecordKey);
            if (dash == null) return null;
            // Resolve through the SAME partition the driver emits so the lit span
            // matches the wire exactly.
            foreach (var slot in Telemetry.Fsr1DashboardCatalog.ResolvePartition(dash))
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
        private volatile bool _fsr1VizActive;
        private volatile Telemetry.Fsr1VizSnapshot? _fsr1Viz;

        /// <summary>True while the channel-mapping panel wants the FSR1 viz snapshot.</summary>
        internal bool Fsr1VizActive => _fsr1VizActive;

        /// <summary>Arm/disarm FSR1 viz capture (panel load/teardown). Clears the last
        /// snapshot on disarm so a stale strip never lingers.</summary>
        internal void SetFsr1VizActive(bool on)
        {
            _fsr1VizActive = on;
            if (!on) _fsr1Viz = null;
        }

        /// <summary>Driver publishes the latest streamed-data snapshot (or null).</summary>
        internal void SetFsr1VizSnapshot(Telemetry.Fsr1VizSnapshot? snap) => _fsr1Viz = snap;

        /// <summary>UI reads the latest FSR1 viz snapshot, or null when none yet.</summary>
        internal Telemetry.Fsr1VizSnapshot? GetFsr1VizSnapshot() => _fsr1Viz;

        /// <summary>True when some display pipeline is live and can render a test
        /// pattern: a tier-def sender is Active, or a standalone FSR1/CM1 driver runs.</summary>
        internal bool IsAnyDashboardDisplayRunning =>
            (_telemetrySender?.IsActive ?? false)
            || (_cm2Sender?.IsActive ?? false)
            || (_fsr1Driver?.IsRunning ?? false)
            || (_cm1Driver?.IsRunning ?? false);

        /// <summary>True when the FSR V1 standalone 0x42 display driver is running
        /// (connected FSR1 wheel). The tier-def sender never goes Active for an FSR1,
        /// so the dashboard UI gates the selector/status on this instead.</summary>
        internal bool IsFsr1DriverRunning => _fsr1Driver?.IsRunning ?? false;

        /// <summary>True when the CM1 standalone group-0x35 display driver is running.</summary>
        internal bool IsCm1DriverRunning => _cm1Driver?.IsRunning ?? false;

        /// <summary>The dual-display coordinator, for the diagnostics bundle to read the
        /// CM1/CM2 discrimination state. Null before Init wires it.</summary>
        internal Telemetry.DualDisplayCoordinator? DualDisplay => _dualDisplay;

        /// <summary>True when the wheel's OWN screen is driven by the tier-def
        /// <see cref="_telemetrySender"/> (a display wheel like W17/W18) rather than
        /// the standalone FSR1 0x42 driver — so the test button may safely start it.</summary>
        internal bool WheelUsesTierDefDisplaySender =>
            !IsFsr1DisplayWheel && (WheelModelInfo?.HasDisplay == true);

        /// <summary>The sender that drives the CM2 dashboard. DECOUPLED: the CM2 is
        /// ALWAYS driven by the dedicated <see cref="_cm2Sender"/> (created whenever a
        /// CM2 is present, regardless of the wheel), so this is simply that sender —
        /// null when no CM2 is attached. The CM2 dash UI reads its WheelState/
        /// ConfigJsonList. (Previously this fell back to the MAIN sender for a
        /// screenless wheel, because the main sender drove the CM2 then.)</summary>
        internal TelemetrySender? ActiveCm2Sender => _cm2Sender;

        /// <summary>The CM2's selected dashboard name (independent of the wheel's).</summary>
        internal string ActiveCm2DashboardName
        {
            get => _settings?.Cm2SelectedDashboard ?? "";
            set { if (_settings != null) _settings.Cm2SelectedDashboard = value ?? ""; }
        }

        /// <summary>Switch the CM2 dash to a dashboard slot (FF kind=4 on the CM2
        /// sender), independent of the wheel.</summary>
        internal void OnCm2DashboardSwitched(uint slot) =>
            _dashboardBindingCoordinator.OnDashboardSwitched(slot, ActiveCm2Sender);

        // Surface configJson wheel state for the Diagnostics tab.
        internal WheelDashboardState? WheelStateForDiagnostics =>
            _telemetrySender?.WheelState;

        // Tile-server state (b2h session 0x03 parse).
        internal TileServerState? TileServerStateForDiagnostics =>
            _telemetrySender?.TileServerState;

        // Wheel channel catalog.
        internal System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalogForDiagnostics =>
            _telemetrySender?.WheelChannelCatalog;

        // Catalog-parser internals for the diag tab. Surfaces buffer/parse/CRC
        // counters so we can tell at a glance why a missing catalog is missing.
        internal (int BufferBytes, int LastParsedBufferBytes, int CrcRejects, int LastActivityMsAgo)
            CatalogParserDiagnostics
        {
            get
            {
                var s = _telemetrySender;
                if (s == null) return (0, 0, 0, -1);
                int lastAct = s.CatalogLastActivityTickMs;
                int ago = lastAct == 0 ? -1 : Environment.TickCount - lastAct;
                return (s.CatalogBufferLength, s.CatalogLastParsedBufferLen,
                        s.CatalogCrcRejects, ago);
            }
        }

        // Per-session traffic counters (in/out chunk counts).
        internal System.Collections.Generic.IReadOnlyDictionary<byte, (int In, int Out)>? SessionCountsForDiagnostics =>
            _telemetrySender?.SessionCounts;

        // Active telemetry running flag.
        internal bool TelemetryEnabledForDiagnostics =>
            _telemetrySender?.Enabled ?? false;

        // Frame-counter readout.
        internal int FramesSentForDiagnostics =>
            _telemetrySender?.FramesSent ?? 0;

        // Bandwidth + wire-error counters surfaced in the Diagnostics tab.
        internal global::MozaPlugin.Protocol.WriteBudget.Snapshot SerialBudgetForDiagnostics
            => _connection?.CurrentBudget ?? default;
        internal global::MozaPlugin.Protocol.MozaSerialConnection.WireErrorCounters SerialWireErrorsForDiagnostics
            => _connection?.WireErrors ?? default;

        // Subscription diagnostics for the "Subscription" section of the Diagnostics tab.
        internal TelemetrySender.SubscriptionDiagnostics? SubscriptionForDiagnostics =>
            _telemetrySender?.LastSubscription;

        // Inbound s02 chunks captured in 5s window after last subscription send.
        internal System.Collections.Generic.IReadOnlyList<byte[]>? SubscriptionResponseForDiagnostics =>
            _telemetrySender?.LastSubscriptionResponse;
    }
}
