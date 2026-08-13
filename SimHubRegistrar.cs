using System;
using SimHub.Plugins;
using MozaPlugin.Devices;
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

            // AB9 active shifter presence + current mechanical layout (issue #112).
            // The active profile's Ab9 block is authoritative for the layout — the
            // UI combo and the layout actions write it, and HardwareApplier pushes
            // it on connect; the device offers no parsed layout read-back. The label
            // is the invariant English UI string so dashboards can key on the value
            // regardless of UI language.
            _plugin.AttachDelegate("AZOM.Ab9Connected", () => (_plugin.Ab9Manager?.IsConnected ?? false) || _plugin.IsAb9Detected);
            _plugin.AttachDelegate("AZOM.Ab9Layout", () => Ab9LayoutLabel(CurrentAb9Layout()));
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

            // AB9 mechanical layout (issue #112): Next/Prev cycle the six layouts
            // in UI-combo order (wraparound), plus one direct-set action per
            // layout. With the AB9 offline the choice still lands in the profile
            // and applies on the next connect.
            _plugin.AddAction("AZOM.Ab9LayoutNext", (a, b) => CycleAb9Layout(+1));
            _plugin.AddAction("AZOM.Ab9LayoutPrev", (a, b) => CycleAb9Layout(-1));
            foreach (var layout in Ab9Layouts)
            {
                var target = layout.Mode; // capture per iteration
                _plugin.AddAction("AZOM.Ab9Layout" + layout.Suffix, (a, b) => SetAb9Layout(target));
            }

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

            // FSR V1 single-byte probe diagnostic: step the probed payload offset with
            // wheel buttons so the boundaries can be walked hands-on while watching the
            // screen (mirrors the ◀/▶ buttons on the Dashboard Telemetry card).
            _plugin.AddAction("AZOM.Fsr1ProbeToggle", (a, b) =>
            {
                _plugin.SetFsr1Probe(!_plugin.Fsr1ProbeActive);
                MozaLog.Debug($"[AZOM] FSR1 byte probe {(_plugin.Fsr1ProbeActive ? "on" : "off")} via action");
            });
            _plugin.AddAction("AZOM.Fsr1ProbeNext", (a, b) => StepFsr1Probe(+1));
            _plugin.AddAction("AZOM.Fsr1ProbePrev", (a, b) => StepFsr1Probe(-1));

            // Re-center the wheelbase (same command as the UI's Calibrate Center
            // button, cf. SettingsControl.BaseCalibrateButton_Click).
            _plugin.AddAction("AZOM.CalibrateCenter", (a, b) =>
            {
                _plugin.WriteIfBaseConnected("base-calibration", 1);
                MozaLog.Debug("[AZOM] Base center calibration via action");
            });
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
            // FSR1 wheel display: brightness is a group-0x32 EEPROM param write.
            if (_plugin.IsFsr1DisplayWheel && _plugin.ActiveCm2Sender == null)
                _plugin.SendFsr1DisplayBrightness(val);
            else
                // Decoupled: target the CM2's own sender when a CM2 is present (it drives
                // the CM2 screen); fall back to the wheel-screen main sender otherwise.
                (_plugin.ActiveCm2Sender ?? _plugin.TelemetrySender)?.SendDashDisplayBrightness(val, allowZero: true);
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
            int deg = ClampStep(data.Limit * 2, deltaDeg, 60, 2700);
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

        // The six mechanical layouts in UI-combo order (cf. SettingsControl's
        // Ab9ModeCombo): action-name suffix + the invariant label the
        // AZOM.Ab9Layout property reports (matches the English UI strings).
        private static readonly (Ab9Mode Mode, string Suffix, string Label)[] Ab9Layouts =
        {
            (Ab9Mode.FivePlusR_L1,  "5R1",        "5+R Layout 1"),
            (Ab9Mode.SixPlusR_L1,   "6R1",        "6+R Layout 1"),
            (Ab9Mode.SixPlusR_L2,   "6R2",        "6+R Layout 2"),
            (Ab9Mode.SevenPlusR_L1, "7R1",        "7+R Layout 1"),
            (Ab9Mode.SevenPlusR_L2, "7R2",        "7+R Layout 2"),
            (Ab9Mode.Sequential,    "Sequential", "Sequential"),
        };

        private static string Ab9LayoutLabel(Ab9Mode mode)
        {
            foreach (var l in Ab9Layouts)
                if (l.Mode == mode) return l.Label;
            return mode.ToString();
        }

        // Active profile's layout without creating the Ab9 block (property getters
        // must not mutate). SevenPlusR_L1 mirrors the Ab9Settings default so a
        // missing block reads the same as the UI's defaults.
        private Ab9Mode CurrentAb9Layout()
            => _plugin.Settings?.ProfileStore?.CurrentProfile?.Ab9?.Mode ?? Ab9Mode.SevenPlusR_L1;

        private void CycleAb9Layout(int delta)
        {
            var current = CurrentAb9Layout();
            int cur = -1;
            for (int i = 0; i < Ab9Layouts.Length; i++)
                if (Ab9Layouts[i].Mode == current) { cur = i; break; }
            int n = Ab9Layouts.Length;
            // Unrecognised persisted value: step in from the appropriate end.
            int target = cur < 0
                ? (delta > 0 ? 0 : n - 1)
                : ((cur + delta) % n + n) % n;
            SetAb9Layout(Ab9Layouts[target].Mode);
        }

        // Mirror of Ab9ModeCombo_Changed's commit path: profile + device push +
        // persist. An open settings panel re-reads the combo on its refresh tick.
        private void SetAb9Layout(Ab9Mode mode)
        {
            var ab9 = GetOrCreateActiveAb9();
            if (ab9 == null) return;
            ab9.Mode = mode;
            _plugin.Ab9Manager?.SendMode(mode);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] AB9 layout → {Ab9LayoutLabel(mode)} via action");
        }

        // Flip dashboard telemetry for the active wheel page. With no wheel identified
        // the wheel-page flag is unwritable, so target the dash pipeline instead —
        // otherwise the action does nothing at all on a hub-only / dash-only rig.
        private void ToggleDashboardTelemetry()
        {
            if (!_plugin.GetCurrentWheelPageGuid().HasValue)
            {
                bool dashOn = !_plugin.ActiveDashTelemetryEnabled;
                _plugin.SetDashTelemetryEnabled(dashOn);
                MozaLog.Debug($"[AZOM] Dash telemetry → {(dashOn ? "on" : "off")} via action (no wheel)");
                return;
            }
            bool turningOn = !_plugin.ActiveTelemetryEnabled;
            _plugin.SetTelemetryEnabled(turningOn);
            MozaLog.Debug($"[AZOM] Dashboard telemetry → {(turningOn ? "on" : "off")} via action");
        }

        // Step the FSR V1 byte-probe offset, auto-starting the probe on first press so a
        // single bound button both begins and walks the diagnostic. No-op on non-FSR1.
        private void StepFsr1Probe(int delta)
        {
            if (!_plugin.IsFsr1DisplayWheel) return;
            if (!_plugin.Fsr1ProbeActive) _plugin.SetFsr1Probe(true);
            else _plugin.StepFsr1Probe(delta);
            MozaLog.Debug($"[AZOM] FSR1 byte probe → {_plugin.Fsr1ProbeTargetLabel()} via action");
        }

        // Cycle the displayed dashboard to the next/previous page, wrapping around.
        // Branches by display family the same way DashboardManagementControl's combo
        // does, because FSR1/CM1 don't speak tier-def and carry no ConfigJsonList:
        //   FSR V1 wheel  → group-0x32/0x81 select over its 19 fixed pages (0..18)
        //   CM1 bus dash  → group-0x32/0x81 select over its 13 fixed pages (1..13)
        //   tier-def wheel → ConfigJsonList slot cycle (below)
        // The wheel's own screen wins over a secondary CM1 dash, so the CM1 branch is
        // the fall-through for a screenless wheel. delta is +1 (next) or -1 (prev).
        private void CycleDashboard(int delta)
        {
            // FSR V1 wheel: its own screen has 19 fixed hardware pages. Step and wrap,
            // emitting the same select the dash dropdown does (drained by the driver).
            if (_plugin.IsFsr1DisplayWheel)
            {
                int nf = Fsr1DisplayEmitter.MaxDashboardIndex + 1;              // 19
                int targetf = ((_plugin.GetActiveFsr1Index() + delta) % nf + nf) % nf;
                _plugin.SetActiveFsr1Index(targetf, sendToWheel: true);
                MozaLog.Debug($"[AZOM] FSR1 dashboard cycle {(delta > 0 ? "next" : "prev")} → page {targetf} via action");
                return;
            }

            var list = _plugin.WheelStateForDiagnostics?.ConfigJsonList;
            if (list == null || list.Count == 0)
            {
                // No tier-def wheel screen: fall back to a CM1 base-bridged dash if present.
                if (_plugin.DashIsCm1)
                {
                    int min = Cm1DisplayEmitter.MinDashboardIndex;             // 1
                    int nc = Cm1DisplayEmitter.MaxDashboardIndex - min + 1;    // 13
                    int curc = _plugin.GetActiveCm1Index() - min;
                    int targetc = ((curc + delta) % nc + nc) % nc + min;
                    _plugin.SetActiveCm1Index(targetc, sendToWheel: true);
                    MozaLog.Debug($"[AZOM] CM1 dashboard cycle {(delta > 0 ? "next" : "prev")} → page {targetc} via action");
                    return;
                }
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
