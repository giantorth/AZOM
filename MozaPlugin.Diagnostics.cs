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
        internal (int BufferBytes, int LastParsedBufferBytes, int CrcRejects, int LastActivityMsAgo,
                  int LiveCatalogCount, int MergedCatalogCount)
            CatalogParserDiagnostics
        {
            get
            {
                var s = _telemetrySender;
                if (s == null) return (0, 0, 0, -1, 0, 0);
                int lastAct = s.CatalogLastActivityTickMs;
                int ago = lastAct == 0 ? -1 : Environment.TickCount - lastAct;
                return (s.CatalogBufferLength, s.CatalogLastParsedBufferLen,
                        s.CatalogCrcRejects, ago, s.CatalogLiveCount, s.CatalogCount);
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
