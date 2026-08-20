using System;
using GameReaderCommon;
using MozaPlugin.UI;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Gear-change edge detection for the one-shot shift effects on the
    /// wheelbase and the AB9 active shifter. Extracted from MozaPlugin; called
    /// once per DataUpdate tick.
    ///
    /// <para>The two devices keep independent gear-string latches and debounce
    /// timers so both fire on their own schedule even when the user tunes their
    /// debounce differently. The mBooster shift edge is computed separately in
    /// the mBooster snapshot path — its per-device workers apply their own
    /// neutral/debounce policy on top of a raw edge.</para>
    /// </summary>
    internal sealed class GearshiftDetector
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly MozaDeviceManager _deviceManager;
        private readonly MozaAb9DeviceManager _ab9Manager;
        private readonly DeviceDetectionState _detectionState;

        // Gearshift trigger state. Fires base-gearshift-event (grp 0x2D cmd 0x76)
        // on gear-string transitions; null initial value suppresses warm-up.
        private string? _lastGearString;
        private DateTime _lastGearShiftSendUtc = DateTime.MinValue;

        // AB9 per-shift trigger state. Separate gear-string latch and debounce
        // timer from the wheelbase path so both devices can fire independently
        // even if game-side debounce settings change.
        private string? _lastAb9GearString;
        private DateTime _lastAb9GearShiftSendUtc = DateTime.MinValue;

        internal GearshiftDetector(
            MozaPlugin plugin,
            MozaData data,
            MozaDeviceManager deviceManager,
            MozaAb9DeviceManager ab9Manager,
            DeviceDetectionState detectionState)
        {
            _plugin = plugin;
            _data = data;
            _deviceManager = deviceManager;
            _ab9Manager = ab9Manager;
            _detectionState = detectionState;
        }

        // Settings are read live, never cached: ProfileCoordinator.ClearSettings
        // replaces the whole MozaPluginSettings instance.
        private MozaProfile? CurrentProfile => _plugin.Settings?.ProfileStore?.CurrentProfile;

        /// <summary>Run both device paths for this tick.</summary>
        internal void Tick(GameData data)
        {
            CheckWheelbase(data);
            CheckAb9(data);
        }

        // Fire a one-shot base-gearshift-event on gear change. Gated by
        // GearshiftVibration > 0 and a debounce. By default, transitions
        // *into* neutral don't fire (H-pattern produces two transitions
        // "1"→"N"→"2"; we want the engagement bump only).
        // GearshiftVibrateOnNeutral opts in.
        private void CheckWheelbase(GameData data)
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
                var lfeGear = CurrentProfile?.BaseLfe?.Gearshift;
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
            var gsProfile = CurrentProfile;
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
        private void CheckAb9(GameData data)
        {
            if (_ab9Manager == null || !_ab9Manager.IsConnected) return;
            if (!_detectionState.Ab9Detected) return;
            var ab9Settings = CurrentProfile?.Ab9;
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
