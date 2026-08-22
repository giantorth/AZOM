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
        // Maps the READBACK command name to the cfg-cache key (== the WRITE command name).
        // They're the same for most settings, but not all: rpm display mode reads on
        // 'wheel-get-rpm-display-mode' and writes on 'wheel-set-rpm-display-mode', so a
        // set keyed on either name alone silently primes nothing.
        private static readonly System.Collections.Generic.Dictionary<string, string> s_primableWheelCfg =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["wheel-idle-mode"]               = "wheel-idle-mode",
                ["wheel-idle-timeout"]            = "wheel-idle-timeout",
                ["wheel-telemetry-idle-effect"]   = "wheel-telemetry-idle-effect",
                ["wheel-buttons-idle-effect"]     = "wheel-buttons-idle-effect",
                ["wheel-knob-idle-effect"]        = "wheel-knob-idle-effect",
                ["wheel-telemetry-mode"]          = "wheel-telemetry-mode",
                ["wheel-buttons-led-mode"]        = "wheel-buttons-led-mode",
                ["wheel-knob-led-mode"]           = "wheel-knob-led-mode",
                ["wheel-rpm-brightness"]          = "wheel-rpm-brightness",
                ["wheel-buttons-brightness"]      = "wheel-buttons-brightness",
                ["wheel-knob-brightness"]         = "wheel-knob-brightness",
                ["wheel-rpm-indicator-mode"]      = "wheel-rpm-indicator-mode",
                ["wheel-get-rpm-display-mode"]    = "wheel-set-rpm-display-mode",
            };

        private static bool TryPrimableWheelCfgKey(string name, out string cacheKey) =>
            s_primableWheelCfg.TryGetValue(name, out cacheKey);
        // One-shot log edge for the param-storm suspend (see PollStatusCore).
        private bool _paramStormLogged;
    }
}
