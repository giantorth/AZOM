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

        // Gearshift trigger state. Fires base-gearshift-event (grp 0x2D cmd 0x76)
        // on gear-string transitions; null initial value suppresses warm-up.
        private string? _lastGearString;
        private DateTime _lastGearShiftSendUtc = DateTime.MinValue;

        // AB9 per-shift trigger state. Separate gear-string latch and debounce
        // timer from the wheelbase path so both devices can fire independently
        // even if game-side debounce settings change.
        private string? _lastAb9GearString;
        private DateTime _lastAb9GearShiftSendUtc = DateTime.MinValue;

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
                            id.IndexOf(ShakeIt.MozaShakeItDeviceRegistry.WheelbaseDeviceTypeId, StringComparison.OrdinalIgnoreCase) >= 0)
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

        // Fire a one-shot base-gearshift-event on gear change. Gated by
        // GearshiftVibration > 0 and a debounce. By default, transitions
        // *into* neutral don't fire (H-pattern produces two transitions
        // "1"→"N"→"2"; we want the engagement bump only).
        // GearshiftVibrateOnNeutral opts in.
        private void CheckGearshiftEvent(GameData data)
        {
            if (!_data.IsConnected) return;
            string? gear = data?.NewData?.Gear;
            if (string.IsNullOrEmpty(gear)) return;

            // LFE-capable firmware: the complex gearshift (cmd 0x77, LFE channel
            // id 0) handles gear-shift feedback ONLY while that channel is enabled
            // and edge-triggered (OnChange) — then the worker fires it and we skip
            // the classic bump to avoid a double buzz. But if the channel is
            // repurposed as a continuous partial (Level mode, e.g. the Additive
            // Engine preset) or disabled, fall through to the classic bump (cmd
            // 0x76), which coexists with the three LFE channels, so gear shifts are
            // still felt while all three channels drive the engine.
            if (_data.BaseSupportsLfe)
            {
                var lfeGear = _settings?.ProfileStore?.CurrentProfile?.BaseLfe?.Gearshift;
                bool lfeHandlesGearshift = lfeGear != null && lfeGear.Enabled
                    && lfeGear.TriggerMode == BaseLfeTriggerMode.OnChange;
                if (lfeHandlesGearshift) return;
            }

            if (_data.GearshiftVibration <= 0) return;
            if (_lastGearString == null)
            {
                _lastGearString = gear;
                return; // warm-up: record the first observed value, don't fire
            }
            if (gear == _lastGearString) return;
            // Update the latch on every change so we don't compare against a
            // stale value on the next tick. Whether we *fire* is decided after.
            _lastGearString = gear;
            // Skip dis-engagement transitions (anything → neutral) unless the
            // user has opted in. Some games report neutral as "0" instead of
            // "N" — treat both as neutral.
            bool isNeutral = (gear == "N" || gear == "0");
            // Source from the active profile (single source of truth). Falls back
            // to safe defaults when the profile field is sentinel (-1 = unset).
            var gsProfile = _settings?.ProfileStore?.CurrentProfile;
            bool vibrateOnNeutral = gsProfile?.GearshiftVibrateOnNeutral == 1;
            int debounceMs = gsProfile?.GearshiftDebounceMs ?? -1;
            if (debounceMs < 0) debounceMs = 500;
            if (isNeutral && !vibrateOnNeutral) return;
            var now = DateTime.UtcNow;
            if (debounceMs > 0 && (now - _lastGearShiftSendUtc).TotalMilliseconds < debounceMs) return;
            _lastGearShiftSendUtc = now;
            _deviceManager.WriteSetting("base-gearshift-event", 1);
        }

        // Start the AB9's per-shift effects: the ShiftRumble square wave plus
        // EngageForce, or NeutralForce for transitions into neutral. The host owns
        // this — the AB9 does not fire rumble autonomously off its mechanical
        // sensor, and without these starts gear engagement produces zero haptic
        // feedback. See docs/protocol/devices/ab9-shifter.md.
        //
        // Gated by AB9-scoped knobs (Ab9Settings.GearShiftVibrateOnNeutral /
        // GearShiftDebounceMs), separate from the wheelbase gearshift card —
        // users tune the two devices independently (e.g. heavier debounce on
        // the wheelbase to absorb H-pattern double-transitions, but tighter
        // on the AB9 so every gate engagement registers).
        private void CheckAb9GearshiftEvent(GameData data)
        {
            if (_ab9Manager == null || !_ab9Manager.IsConnected) return;
            if (!DetectionState.Ab9Detected) return;
            var ab9Settings = _settings?.ProfileStore?.CurrentProfile?.Ab9;
            if (ab9Settings == null || ab9Settings.GearShiftVibrationIntensity <= 0) return;

            string? gear = data?.NewData?.Gear;
            if (string.IsNullOrEmpty(gear)) return;
            if (_lastAb9GearString == null)
            {
                _lastAb9GearString = gear;
                return; // warm-up: record first value, don't fire
            }
            if (gear == _lastAb9GearString) return;
            _lastAb9GearString = gear;

            bool isNeutral = (gear == "N" || gear == "0");
            bool vibrateOnNeutral = ab9Settings.GearShiftVibrateOnNeutral;
            int debounceMs = ab9Settings.GearShiftDebounceMs;
            if (debounceMs < 0) debounceMs = 0;
            if (isNeutral && !vibrateOnNeutral) return;

            var now = DateTime.UtcNow;
            if (debounceMs > 0 && (now - _lastAb9GearShiftSendUtc).TotalMilliseconds < debounceMs) return;
            _lastAb9GearShiftSendUtc = now;

            // EngageForce for any non-neutral gear, NeutralForce for transitions
            // into neutral; the slider scales the constant-force ramp that precedes it.
            _ab9Manager.SendGearShiftTrigger(engageNotDisengage: !isNeutral,
                                             intensity0to100: ab9Settings.GearShiftVibrationIntensity);
        }
    }
}
