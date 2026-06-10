using System;
using SimHub.Plugins;
using MozaPlugin.Telemetry;

namespace MozaPlugin
{
    /// <summary>
    /// SimHub property + action registration for the plugin: the AZOM.*
    /// property delegates and the button-bindable actions (step/cycle/toggle)
    /// that mirror the SettingsControl sliders. Every delegate reads live
    /// plugin state at invoke time (<c>_plugin.Data?.X</c> etc.) — never a
    /// captured snapshot — because SimHub may invoke getters during plugin
    /// reload windows where the backing fields are unset or mid-teardown.
    /// </summary>
    internal sealed class SimHubRegistrar
    {
        private readonly MozaPlugin _plugin;

        internal SimHubRegistrar(MozaPlugin plugin)
        {
            _plugin = plugin;
        }

        internal void RegisterProperties(PluginManager pluginManager)
        {
            // Null-guard each delegate: SimHub may invoke property getters during
            // plugin reload windows where Data is unset, or after End() left fields
            // intact but mid-teardown. A throw inside a property getter destabilises
            // SimHub's property polling, so each getter returns a sentinel default.
            _plugin.AttachDelegate("AZOM.BaseConnected", () => _plugin.Data?.IsBaseConnected ?? false);
            // PropertyResolver is constructed later in Init than RegisterProperties
            // runs, so guard it too — SimHub may read these before it exists.
            _plugin.AttachDelegate("AZOM.McuTemp", () => (_plugin.Data == null || _plugin.PropertyResolver == null) ? 0.0 : _plugin.PropertyResolver.ConvertTemp(_plugin.Data.McuTemp));
            _plugin.AttachDelegate("AZOM.MosfetTemp", () => (_plugin.Data == null || _plugin.PropertyResolver == null) ? 0.0 : _plugin.PropertyResolver.ConvertTemp(_plugin.Data.MosfetTemp));
            _plugin.AttachDelegate("AZOM.MotorTemp", () => (_plugin.Data == null || _plugin.PropertyResolver == null) ? 0.0 : _plugin.PropertyResolver.ConvertTemp(_plugin.Data.MotorTemp));
            _plugin.AttachDelegate("AZOM.BaseState", () => _plugin.Data?.BaseState ?? 0);
            _plugin.AttachDelegate("AZOM.FfbStrength", () => (_plugin.Data?.FfbStrength ?? 0) / 10);
            _plugin.AttachDelegate("AZOM.MaxAngle", () => (_plugin.Data?.MaxAngle ?? 0) * 2);
            // Telemetry pipeline health, so users can show a degraded/parked state on
            // an overlay. TelemetryState = the PipelinePhase name (Idle/SilenceWait/
            // Starting/Active/HotSwitchBurst/Recovery/Parked). DashboardBound is a
            // best-effort "telemetry actively flowing" flag (Phase==Active) — there is
            // no true wheel-side commit signal yet (see P4), so it can read true while
            // a wheel silently ignores the binding; documented limitation.
            _plugin.AttachDelegate("AZOM.TelemetryState", () => (_plugin.TelemetrySender?.Phase ?? PipelinePhase.Idle).ToString());
            _plugin.AttachDelegate("AZOM.DashboardBound", () => (_plugin.TelemetrySender?.Phase ?? PipelinePhase.Idle) == PipelinePhase.Active);

            // Live physical-input positions read directly from the device HID
            // surface (independent of any game telemetry — these update even with
            // no sim running, see issue #59). HidReader is constructed later in
            // Init than RegisterProperties, so guard it on every getter.
            _plugin.AttachDelegate("AZOM.HidConnected", () => _plugin.Data?.IsHidConnected ?? false);
            // Signed steering angle in degrees: 0 = center, + / - = each lock
            // direction. Scaled by the base's reported max-angle (MaxAngle*2 =
            // full physical range), matching Moza.MaxAngle. Returns 0 until the
            // max-angle and HID range are both known.
            _plugin.AttachDelegate("AZOM.SteeringAngle", () =>
            {
                var hid = _plugin.HidReader;
                int maxAngleDeg = (_plugin.Data?.MaxAngle ?? 0) * 2;
                if (hid == null || maxAngleDeg <= 0) return 0.0;
                return hid.GetCurrentAngleDegrees(maxAngleDeg);
            });
            // Steering as a 0-100 position (0 = full lock one way, 50 = center,
            // 100 = full lock the other). Independent of max-angle. Returns -1
            // when no HID device is connected or the range is unknown.
            _plugin.AttachDelegate("AZOM.SteeringPosition", () => _plugin.HidReader?.GetSteeringPositionPercent() ?? -1.0);
            // Pedal / paddle axes as 0-100 positions.
            _plugin.AttachDelegate("AZOM.Throttle", () => _plugin.Data?.ThrottlePosition ?? 0);
            _plugin.AttachDelegate("AZOM.Brake", () => _plugin.Data?.BrakePosition ?? 0);
            _plugin.AttachDelegate("AZOM.Clutch", () => _plugin.Data?.ClutchPosition ?? 0);
            _plugin.AttachDelegate("AZOM.Handbrake", () => _plugin.Data?.HandbrakePosition ?? 0);
            _plugin.AttachDelegate("AZOM.LeftPaddle", () => _plugin.Data?.LeftPaddlePosition ?? 0);
            _plugin.AttachDelegate("AZOM.RightPaddle", () => _plugin.Data?.RightPaddlePosition ?? 0);
            _plugin.AttachDelegate("AZOM.CombinedPaddle", () => _plugin.Data?.CombinedPaddlePosition ?? 0);
        }

        internal void RegisterActions()
        {
            _plugin.AddAction("AZOM.ClearLeds", (a, b) =>
            {
                _plugin.ClearLedsOnHardware();
                MozaLog.Debug("[AZOM] LEDs cleared via action");
            });

            // Step actions mirror the SettingsControl sliders so SimHub button
            // bindings can nudge the same settings. Each registers Up/Down (fine)
            // and UpCoarse/DownCoarse variants; the stepper clamps to the slider's
            // [min,max] range, pushes to hardware exactly where the UI handler does,
            // and persists via SaveSettings(). An open settings panel re-reads the
            // new value on its refresh tick.

            // Base feel.
            AddStepActions("AZOM.FfbStrength", 5, 10, StepFfbStrength);   // 0..100 %
            AddStepActions("AZOM.Torque",      5, 10, StepTorque);        // 50..100 %
            AddStepActions("AZOM.Rotation",   90, 180, StepRotation);     // 90..2700 deg

            // AB9 shifter vibration.
            AddStepActions("AZOM.Ab9EngineIntensity",    5, 10, StepAb9EngineIntensity);    // 0..100
            AddStepActions("AZOM.Ab9EngineFrequency",   10, 20, StepAb9EngineFrequency);    // 0..200 Hz
            AddStepActions("AZOM.Ab9GearShiftIntensity", 5, 10, StepAb9GearShiftIntensity); // 0..100

            // Cycle the wheel's displayed dashboard (wraparound).
            _plugin.AddAction("AZOM.DashboardNext", (a, b) => CycleDashboard(+1));
            _plugin.AddAction("AZOM.DashboardPrev", (a, b) => CycleDashboard(-1));

            // Dashboard telemetry on/off for the active wheel page.
            _plugin.AddAction("AZOM.DashboardTelemetryToggle", (a, b) => ToggleDashboardTelemetry());
            _plugin.AddAction("AZOM.DashboardTelemetryOn", (a, b) =>
            {
                _plugin.SetTelemetryEnabled(true);
                MozaLog.Debug("[AZOM] Dashboard telemetry on via action");
            });
            _plugin.AddAction("AZOM.DashboardTelemetryOff", (a, b) =>
            {
                _plugin.SetTelemetryEnabled(false);
                MozaLog.Debug("[AZOM] Dashboard telemetry off via action");
            });

            // Wheel screen display brightness, 0..100 % (cf.
            // DashboardManagementControl.WheelDisplayBrightnessSlider). Up/Down
            // nudge ±5, the Coarse variants ±10; the stepper seeds from the
            // wheel's real brightness with the slider's fallback chain so the
            // first press never starts from the -1 sentinel.
            AddStepActions("AZOM.DisplayBrightness", 5, 10, StepDisplayBrightness);

            // Jump straight to a fixed display brightness in 10-% steps
            // (Moza.DisplayBrightness0 .. Moza.DisplayBrightness100).
            for (int pct = 0; pct <= 100; pct += 10)
            {
                int target = pct; // capture per iteration
                _plugin.AddAction($"AZOM.DisplayBrightness{pct}", (a, b) =>
                {
                    SetDisplayBrightness(target);
                    MozaLog.Debug($"[AZOM] Display brightness → {target}% via action");
                });
            }

            // Turn off the base's work mode. The firmware command is
            // main-set-work-mode; value 1 is the state the UI surfaces as
            // "Standby Mode" on (cf. SettingsControl.StandbyCheck_Click), which
            // is what "work mode off" means for the base.
            _plugin.AddAction("AZOM.WorkModeOff", (a, b) =>
            {
                if (_plugin.Data != null) _plugin.Data.WorkMode = 1;
                _plugin.WriteIfBaseConnected("main-set-work-mode", 1);
                _plugin.SaveSettings();
                MozaLog.Debug("[AZOM] Work mode off (standby) via action");
            });
            // Turn work mode back on: value 0 is "Standby Mode" off — the base's
            // normal active state.
            _plugin.AddAction("AZOM.WorkModeOn", (a, b) =>
            {
                if (_plugin.Data != null) _plugin.Data.WorkMode = 0;
                _plugin.WriteIfBaseConnected("main-set-work-mode", 0);
                _plugin.SaveSettings();
                MozaLog.Debug("[AZOM] Work mode on via action");
            });

            // Toggle the wheel screen on/off, remembering the on-brightness so a
            // later toggle-on restores it instead of a fixed default.
            _plugin.AddAction("AZOM.DisplayToggle", (a, b) => ToggleDisplay());

            // Toggle telemetry test mode (synthetic signal sweep) for the active
            // wheel page, mirroring the Test Start/Stop buttons in the UI.
            _plugin.AddAction("AZOM.TestModeToggle", (a, b) => ToggleTestMode());
        }

        // Remembered display brightness from the last DisplayToggle-off, so the
        // next toggle-on restores it. -1 = nothing remembered yet (per-session;
        // not persisted across plugin reload).
        private int _displayBrightnessBeforeBlank = -1;

        // Flip the wheel screen on/off. Off = brightness 0 after stashing the
        // current level; on = restore the stashed level (or 100 if none). "Off"
        // is detected as current brightness 0, matching SetDisplayBrightness's
        // clamp. Reuses the slider commit path so _data, the active profile, the
        // wheel, and settings all stay in sync.
        private void ToggleDisplay()
        {
            int current = CurrentDisplayBrightness();
            if (current > 0)
            {
                _displayBrightnessBeforeBlank = current;
                SetDisplayBrightness(0);
                MozaLog.Debug($"[AZOM] Display off (was {current}%) via action");
            }
            else
            {
                int restore = _displayBrightnessBeforeBlank > 0 ? _displayBrightnessBeforeBlank : 100;
                SetDisplayBrightness(restore);
                MozaLog.Debug($"[AZOM] Display on → {restore}% via action");
            }
        }

        // Flip telemetry test mode for the active wheel page. Mirrors
        // DashboardManagementControl's Test Start/Stop: when the active overlay
        // doesn't already have live telemetry enabled, test mode owns the sender
        // lifecycle (start on a worker thread when turning on, stop when turning
        // off) so the synthetic sweep runs without flipping the persisted
        // per-page telemetry-enabled flag.
        private void ToggleTestMode()
        {
            var active = _plugin.TelemetrySender;
            if (active == null)
            {
                MozaLog.Debug("[AZOM] Test mode toggle ignored: no telemetry sender");
                return;
            }
            bool turningOn = !active.TestMode;
            active.TestMode = turningOn;
            if (!_plugin.ActiveTelemetryEnabled)
            {
                if (turningOn)
                {
                    _plugin.ApplyTelemetrySettings();
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => active.Start());
                }
                else
                {
                    active.Stop();
                }
            }
            MozaLog.Debug($"[AZOM] Test mode → {(turningOn ? "on" : "off")} via action");
        }

        // ===== Display brightness step/set helpers =====

        // Current wheel display brightness using the same fallback chain as the
        // UI slider (DashboardManagementControl.RefreshDisplaySection): live
        // _data → active profile → settings default (100). Never returns the
        // -1 sentinel, so a nudge always moves from the wheel's real value.
        private int CurrentDisplayBrightness()
        {
            int b = _plugin.Data?.DashDisplayBrightness ?? -1;
            if (b < 0)
            {
                var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
                b = profile?.DashDisplayBrightness ?? -1;
                if (b < 0) b = _plugin.Settings?.DashDisplayBrightness ?? 100;
            }
            return b < 0 ? 0 : (b > 100 ? 100 : b);
        }

        // Apply an absolute display brightness, mirroring the slider's commit
        // path: update _data + active profile, push on session 0x02, persist.
        // allowZero: a button bound to a specific value is deliberate intent,
        // same as a slider committed at 0.
        private void SetDisplayBrightness(int val)
        {
            val = val < 0 ? 0 : (val > 100 ? 100 : val);
            if (_plugin.Data != null) _plugin.Data.DashDisplayBrightness = val;
            _plugin.UpdateActiveProfile(p => p.DashDisplayBrightness = val);
            _plugin.TelemetrySender?.SendDashDisplayBrightness(val, allowZero: true);
            _plugin.SaveSettings();
        }

        private void StepDisplayBrightness(int delta)
        {
            int val = ClampStep(CurrentDisplayBrightness(), delta, 0, 100);
            SetDisplayBrightness(val);
            MozaLog.Debug($"[AZOM] Display brightness → {val}% via action");
        }

        /// <summary>
        /// Registers the four button-bindable step variants for a setting:
        /// <c>{name}Up</c>/<c>{name}Down</c> apply ±<paramref name="fine"/>, and
        /// <c>{name}UpCoarse</c>/<c>{name}DownCoarse</c> apply ±<paramref name="coarse"/>.
        /// <paramref name="apply"/> receives the signed delta in display units.
        /// </summary>
        private void AddStepActions(string name, int fine, int coarse, Action<int> apply)
        {
            _plugin.AddAction(name + "Up",         (a, b) => apply(+fine));
            _plugin.AddAction(name + "Down",       (a, b) => apply(-fine));
            _plugin.AddAction(name + "UpCoarse",   (a, b) => apply(+coarse));
            _plugin.AddAction(name + "DownCoarse", (a, b) => apply(-coarse));
        }

        private static int ClampStep(int current, int delta, int min, int max)
            => Math.Max(min, Math.Min(max, current + delta));

        // FFB strength: stored raw = percent * 10 (cf. FfbStrengthSlider_ValueChanged).
        private void StepFfbStrength(int deltaPct)
        {
            var data = _plugin.Data;
            if (data == null) return;
            int pct = ClampStep(data.FfbStrength / 10, deltaPct, 0, 100);
            int raw = pct * 10;
            data.FfbStrength = raw;
            _plugin.WriteIfBaseConnected("base-ffb-strength", raw);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] FFB strength → {pct}% via action");
        }

        // Torque limit: percent, 50..100 (cf. TorqueSlider_ValueChanged).
        private void StepTorque(int deltaPct)
        {
            var data = _plugin.Data;
            if (data == null) return;
            int v = ClampStep(data.Torque, deltaPct, 50, 100);
            data.Torque = v;
            _plugin.WriteIfBaseConnected("base-torque", v);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] Torque → {v}% via action");
        }

        // Steering rotation: display degrees, stored raw = degrees / 2; both
        // base-limit and base-max-angle move together (cf. RotationSlider_ValueChanged).
        private void StepRotation(int deltaDeg)
        {
            var data = _plugin.Data;
            if (data == null) return;
            int deg = ClampStep(data.Limit * 2, deltaDeg, 90, 2700);
            int raw = deg / 2;
            data.Limit = raw;
            data.MaxAngle = raw;
            _plugin.WriteIfBaseConnected("base-limit", raw);
            _plugin.WriteIfBaseConnected("base-max-angle", raw);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] Rotation → {deg}° via action");
        }

        // AB9 engine vibration is host-rendered: the worker thread picks up the
        // new profile value on its next tick, no device write (cf.
        // Ab9EngineVibIntensitySlider_ValueChanged).
        private void StepAb9EngineIntensity(int delta)
        {
            var ab9 = GetOrCreateActiveAb9();
            if (ab9 == null) return;
            ab9.EngineVibrationIntensity = (byte)ClampStep(ab9.EngineVibrationIntensity, delta, 0, 100);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] AB9 engine vibration intensity → {ab9.EngineVibrationIntensity} via action");
        }

        private void StepAb9EngineFrequency(int delta)
        {
            var ab9 = GetOrCreateActiveAb9();
            if (ab9 == null) return;
            ab9.EngineVibrationFrequency = (ushort)ClampStep(ab9.EngineVibrationFrequency, delta, 0, 200);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] AB9 engine vibration frequency → {ab9.EngineVibrationFrequency} Hz via action");
        }

        // AB9 gear-shift vibration: one config write per change so the firmware
        // persists the stored intensity (cf. Ab9GearShiftVibSlider_ValueChanged).
        private void StepAb9GearShiftIntensity(int delta)
        {
            var ab9 = GetOrCreateActiveAb9();
            if (ab9 == null) return;
            int v = ClampStep(ab9.GearShiftVibrationIntensity, delta, 0, 100);
            ab9.GearShiftVibrationIntensity = (byte)v;
            _plugin.Ab9Manager?.SendGearShiftVibrationIntensity(v);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] AB9 gear-shift vibration intensity → {v} via action");
        }

        // Returns the active profile's AB9 block, creating it if absent (matches
        // the UI's GetOrCreateAb9Profile). Null only when no profile is loaded.
        private Ab9Settings? GetOrCreateActiveAb9()
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return null;
            if (profile.Ab9 == null) profile.Ab9 = new Ab9Settings();
            return profile.Ab9;
        }

        // Flip dashboard telemetry for the active wheel page. No-op when no wheel
        // page is identified (ActiveTelemetryEnabled set is a no-op there).
        private void ToggleDashboardTelemetry()
        {
            bool turningOn = !_plugin.ActiveTelemetryEnabled;
            _plugin.SetTelemetryEnabled(turningOn);
            MozaLog.Debug($"[AZOM] Dashboard telemetry → {(turningOn ? "on" : "off")} via action");
        }

        // Cycle the wheel's displayed dashboard to the next/previous enabled slot,
        // wrapping around. Mirrors the DashboardManagementControl combo switch:
        // ConfigJsonList is slot-ordered (dropdown index IS the slot), the wheel's
        // WheelReportedSlot is the ground-truth current slot, and
        // OnDashboardSwitched(slot) routes through SwitchToProfile so FF kind=4 +
        // the pipeline cycle honor the EnableHotRenegotiation flag. delta is +1
        // (next) or -1 (prev). No-op when the wheel has 0 or 1 dashboards.
        private void CycleDashboard(int delta)
        {
            var list = _plugin.WheelStateForDiagnostics?.ConfigJsonList;
            if (list == null || list.Count == 0)
            {
                MozaLog.Debug("[AZOM] Dashboard cycle ignored: no wheel dashboard list");
                return;
            }
            int n = list.Count;
            if (n == 1)
            {
                MozaLog.Debug("[AZOM] Dashboard cycle ignored: only one dashboard");
                return;
            }

            // Prefer the wheel's reported slot; fall back to matching the active
            // profile name against the slot list when the wheel slot is unknown.
            int cur = _plugin.TelemetrySender?.WheelReportedSlot ?? -1;
            if (cur < 0 || cur >= n)
            {
                cur = -1;
                string activeName = _plugin.ActiveTelemetryProfileName;
                if (!string.IsNullOrEmpty(activeName))
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (string.Equals(list[i], activeName, StringComparison.OrdinalIgnoreCase))
                        {
                            cur = i;
                            break;
                        }
                    }
                }
            }

            // Unknown current slot: step in from the appropriate end.
            int target = cur < 0
                ? (delta > 0 ? 0 : n - 1)
                : ((cur + delta) % n + n) % n;

            string selected = list[target];
            _plugin.ActiveTelemetryProfileName = selected;
            _plugin.ActiveTelemetryMzdashPath = "";
            _plugin.SaveSettings();
            _plugin.OnDashboardSwitched((uint)target);
            MozaLog.Debug($"[AZOM] Dashboard cycle {(delta > 0 ? "next" : "prev")} → slot {target} \"{selected}\" via action");
        }
    }
}
