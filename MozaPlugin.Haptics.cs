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


        // mBooster per-shift edge state. Separate gear-string latch from the
        // wheelbase's/AB9's own — only the raw "did the gear string change
        // this tick" edge + whether the new gear is neutral are computed
        // here (once, globally); each mBooster device's own Gear Shift
        // effect applies its own VibrateOnNeutral/DebounceMs on top of that
        // raw edge in MBoosterEffectWorker.UpdateGearShiftRequest, so no
        // debounce timestamp is needed at this layer.
        private string? _lastMBoosterGearString;
        // Monotonic shift counter for the mBooster Gear Shift effect — see
        // MBoosterTelemetrySnapshot.GearShiftSeq. Advanced once per detected
        // gear-string change; the per-device workers each track the last
        // value they acted on so none can miss a shift the way a one-tick
        // bool edge would when sampled by their slower ~20ms timer.
        private int _mboosterShiftSeq;

        // Wheelbase LFE momentary test triggers (from the UI test buttons). No-op
        // when the firmware doesn't support LFE. Each plays a fixed pattern:
        // engine = 2 s sweep, ABS = 1 s burst, gearshift = two rapid bumps.
        public void TriggerBaseLfeEngineTest() { if (_data.BaseSupportsLfe) _baseLfeWorker?.PostEngineTest(); }
        public void TriggerBaseLfeAbsTest() { if (_data.BaseSupportsLfe) _baseLfeWorker?.PostAbsTest(); }
        public void TriggerBaseLfeGearshiftTest() { if (_data.BaseSupportsLfe) _baseLfeWorker?.PostGearshiftTest(); }

        // ── ShakeIt haptics bridge (ShakeIt/) ─────────────────────────────────
        // The provider is constructed by SimHub (generic new()) and reaches the
        // live plugin through Instance; these forwarders keep the worker the
        // single wire owner.

        /// <summary>True when the wheelbase can accept ShakeIt-driven LFE frames (drives the haptics device's connected state).</summary>
        internal bool IsBaseLfeHapticsReady =>
            _baseLfeWorker != null && _data.BaseSupportsLfe
            && DetectionState.BaseDetected && _deviceManager?.IsConnected == true;

        /// <summary>True when a "MOZA Wheelbase LFE" haptics device instance is deployed in SimHub's device list, regardless of enable/game state — the LFE tab hides while it is, so the two sources can't both edit the base. UI-thread callers only (enumerates SimHub's WPF-owned device collection).</summary>
        internal bool IsShakeItLfeDeviceDeployed
        {
            get
            {
                try
                {
                    var dp = _pluginManager?.GetPlugin<SimHub.Plugins.Devices.DevicesPlugin>();
                    if (dp == null) return false;
                    foreach (var d in dp.GetDevices())
                    {
                        if (d?.DeviceDescriptor?.DeviceTypeID is string id && id.Length != 0 &&
                            id.IndexOf(Integration.MozaShakeItDeviceRegistry.WheelbaseDeviceTypeId, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                    return false;
                }
                catch { return false; }
            }
        }

        /// <summary>Latest ShakeIt per-oscillator (gain 0..1, freq Hz) for the three summed LFE slots — from the provider on the SimHub data thread.</summary>
        internal void PostShakeItLfeChannels(double g0, double f0, double g1, double f1, double g2, double f2)
            => _baseLfeWorker?.PostShakeItChannels(g0, f0, g1, f1, g2, f2);

        internal void ClearShakeItLfeChannels() => _baseLfeWorker?.ClearShakeItChannels();

        /// <summary>Latest (carrier freq Hz, amplitude 0..1) for the 3 LFE slots — drives the settings scope.</summary>
        public (double freq, double amp)[] GetLfeScopeSamples()
        {
            var w = _baseLfeWorker;
            if (w == null) return new[] { (0.0, 0.0), (0.0, 0.0), (0.0, 0.0) };
            return new[] { (w.ScopeEngineFreq, w.ScopeEngineAmp), (w.ScopeAbsFreq, w.ScopeAbsAmp), (w.ScopeGearFreq, w.ScopeGearAmp) };
        }

    }
}
