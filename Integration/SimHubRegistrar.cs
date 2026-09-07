using System;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Display;
using MozaPlugin.Settings;

namespace MozaPlugin.Integration
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
            // Live motor torque in Nm, unsigned — direction is dropped, since
            // torque is torque whichever way the wheel is turning (this is how
            // PitHouse graphs it too). CurrentTorqueRaw is the same reading with
            // the direction sign kept. Both refresh at 5 Hz off
            // MozaPlugin.SampleTorqueHistory, panel open or not.
            _plugin.AttachDelegate("AZOM.CurrentTorque", () => _plugin.Data?.LiveTorqueNm ?? 0.0);
            _plugin.AttachDelegate("AZOM.CurrentTorqueRaw", () => _plugin.Data?.LiveTorqueSignedNm ?? 0.0);
            // Peak |torque| this game session — cleared on each game start by
            // MozaPlugin.DataUpdate, and by the plugin reload a game switch causes.
            _plugin.AttachDelegate("AZOM.MaxTorque", () => _plugin.Data?.LiveTorquePeakNm ?? 0.0);
            // The base model's rated peak, the figure the Base-tab graph scales to.
            // -1 = not established (an unrecognised model). Distinct from
            // AZOM.Torque, which is the user's own output limit as a percentage.
            _plugin.AttachDelegate("AZOM.TorqueLimit", () =>
            {
                double rated = BaseModelInfo.RatedNm(_plugin.Data?.BaseModelName);
                return rated > 0 ? rated : -1.0;
            });
            _plugin.AttachDelegate("AZOM.MaxAngle", () => (_plugin.Data?.MaxAngle ?? 0) * 2);

            // Every wheelbase setting, in the same display units the Base-tab
            // sliders show (AZOM.FfbStrength is one of these — it kept its
            // historical percent scaling). Values track the device read-back,
            // so they read their _data defaults until the base answers its
            // settings sweep. Unsupported settings (EQ bands 7-10 on legacy
            // firmware) report -1 rather than a plausible-looking zero.
            foreach (var s in BaseSettingCatalog.Numeric)
            {
                var def = s; // capture per iteration
                _plugin.AttachDelegate("AZOM." + def.Name, () =>
                {
                    var d = _plugin.Data;
                    if (d == null || !def.IsSupported(d)) return -1;
                    return def.ToDisplay(def.GetRaw(d));
                });
            }
            foreach (var t in BaseSettingCatalog.Toggles)
            {
                var def = t; // capture per iteration
                _plugin.AttachDelegate("AZOM." + def.Name, () =>
                {
                    var d = _plugin.Data;
                    return d != null && def.IsOn(d);
                });
            }
            // Standby: raw register value (0 = running, 1 = standby), matching
            // the existing AZOM.WorkModeOn/Off actions rather than the UI
            // checkbox's inverted "Standby Mode" sense.
            _plugin.AttachDelegate("AZOM.WorkMode", () => _plugin.Data?.WorkMode ?? 0);
            // Road sensitivity as the 0-10 preset index the Base-tab buttons
            // use, not the 10..50 register value. -1 until the base reports it.
            _plugin.AttachDelegate("AZOM.RoadSensitivity",
                () => BaseSettingCatalog.RoadSensitivityPresetFromRaw(_plugin.Data?.RoadSensitivity ?? -1));
            // Clutch split point (issue #125) — the wheel overlay is the source
            // of truth; newer KS-family firmware drops the cmd-9 read-back.
            _plugin.AttachDelegate("AZOM.ClutchSplitPoint", () => CurrentClutchSplitPoint());
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
                _plugin.HardwareApplier.ClearLedsOnHardware();
                MozaLog.Debug("[AZOM] LEDs cleared via action");
            });

            // Step actions mirror the SettingsControl sliders so SimHub button
            // bindings can nudge the same settings. Each registers Up/Down (fine)
            // and UpCoarse/DownCoarse variants; the stepper clamps to the slider's
            // [min,max] range, pushes to hardware exactly where the UI handler does,
            // and persists via SaveSettings(). An open settings panel re-reads the
            // new value on its refresh tick.

            // Every wheelbase setting in BaseSettingCatalog, including the
            // long-standing FfbStrength / Torque / Rotation trio.
            foreach (var s in BaseSettingCatalog.Numeric)
            {
                var def = s; // capture per iteration
                AddStepActions("AZOM." + def.Name, def.Fine, def.Coarse, d => StepBaseSetting(def, d));
            }
            foreach (var t in BaseSettingCatalog.Toggles)
            {
                var def = t; // capture per iteration
                AddToggleActions(def);
            }
            // Road sensitivity steps the 0..10 preset, not the raw register —
            // the preset also rewrites the EQ curve, and moving one without the
            // other leaves the base in a state the Base tab can't represent.
            AddStepActions("AZOM.RoadSensitivity", 1, 2, StepRoadSensitivity);

            // Clutch split point, 0..100 % (issue #125; cf.
            // MozaWheelSettingsControl's WiClutchPointSlider). Only meaningful
            // with Paddles Mode = Combined.
            AddStepActions("AZOM.ClutchSplit", 5, 10, StepClutchSplit);

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
                _plugin.HardwareApplier.WriteIfBaseConnected("main-set-work-mode", 1);
                _plugin.SaveSettings();
                MozaLog.Debug("[AZOM] Work mode off (standby) via action");
            });
            // Turn work mode back on: value 0 is "Standby Mode" off — the base's
            // normal active state.
            _plugin.AddAction("AZOM.WorkModeOn", (a, b) =>
            {
                if (_plugin.Data != null) _plugin.Data.WorkMode = 0;
                _plugin.HardwareApplier.WriteIfBaseConnected("main-set-work-mode", 0);
                _plugin.SaveSettings();
                MozaLog.Debug("[AZOM] Work mode on via action");
            });
            // Flip between the two. Registered by hand rather than through the
            // toggle table so it can't clash with the WorkModeOn/Off names above.
            _plugin.AddAction("AZOM.WorkModeToggle", (a, b) =>
            {
                var data = _plugin.Data;
                if (data == null) return;
                int val = data.WorkMode > 0 ? 0 : 1;
                data.WorkMode = val;
                _plugin.HardwareApplier.WriteIfBaseConnected("main-set-work-mode", val);
                _plugin.SaveSettings();
                MozaLog.Debug($"[AZOM] Work mode {(val == 0 ? "on" : "off (standby)")} via action");
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
                _plugin.Fsr1Probe.SetProbe(!_plugin.Fsr1Probe.Active);
                MozaLog.Debug($"[AZOM] FSR1 byte probe {(_plugin.Fsr1Probe.Active ? "on" : "off")} via action");
            });
            _plugin.AddAction("AZOM.Fsr1ProbeNext", (a, b) => StepFsr1Probe(+1));
            _plugin.AddAction("AZOM.Fsr1ProbePrev", (a, b) => StepFsr1Probe(-1));

            // Re-center the wheelbase (same command as the UI's Calibrate Center
            // button, cf. SettingsControl.BaseCalibrateButton_Click).
            _plugin.AddAction("AZOM.CalibrateCenter", (a, b) =>
            {
                _plugin.HardwareApplier.WriteIfBaseConnected("base-calibration", 1);
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

        /// <summary>
        /// Registers <c>{name}On</c> / <c>{name}Off</c> / <c>{name}Toggle</c>
        /// for a two-state wheelbase setting, mirroring the Base-tab checkbox
        /// commit path (mirror to <c>_data</c>, push, persist).
        /// </summary>
        private void AddToggleActions(BaseSettingCatalog.ToggleSetting def)
        {
            _plugin.AddAction("AZOM." + def.Name + "On",     (a, b) => SetToggle(def, true));
            _plugin.AddAction("AZOM." + def.Name + "Off",    (a, b) => SetToggle(def, false));
            _plugin.AddAction("AZOM." + def.Name + "Toggle", (a, b) =>
            {
                var data = _plugin.Data;
                if (data != null) SetToggle(def, !def.IsOn(data));
            });
        }

        private void SetToggle(BaseSettingCatalog.ToggleSetting def, bool on)
        {
            var data = _plugin.Data;
            if (data == null) return;
            int val = on ? def.OnValue : def.OffValue;
            if (def.Get(data) == val) return; // already there — no flash write
            def.Set(data, val);
            _plugin.HardwareApplier.WriteIfBaseConnected(def.Command, val);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] {def.Name} → {(on ? "on" : "off")} via action");
        }

        private static int ClampStep(int current, int delta, int min, int max)
            => Math.Max(min, Math.Min(max, current + delta));

        /// <summary>
        /// Nudge one wheelbase setting by a signed delta in display units,
        /// mirroring its Base-tab slider commit path exactly: clamp, mirror to
        /// <c>_data</c>, push every command in order, persist via
        /// <c>SaveSettings()</c> (which captures <c>_data</c> into the active
        /// profile). An open settings panel re-reads the value on its refresh tick.
        ///
        /// Every one of these commands is a base parameter-store slot and hits
        /// flash on write, so a value already saturated at a range rail must NOT
        /// be re-written — a held button with key repeat would otherwise burn one
        /// flash write per repeat for no change.
        /// </summary>
        private void StepBaseSetting(BaseSettingCatalog.NumericSetting def, int delta)
        {
            var data = _plugin.Data;
            if (data == null || !def.IsSupported(data)) return;
            int current = def.ToDisplay(def.GetRaw(data));
            int next = ClampStep(current, delta, def.Min, def.EffectiveMax(data));
            if (next == current) return; // saturated — skip the redundant flash write
            int raw = def.ToRaw(next);
            def.SetRaw(data, raw);
            foreach (var cmd in def.Commands)
                _plugin.HardwareApplier.WriteIfBaseConnected(cmd, raw);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] {def.Name} → {next} via action");
        }

        // Road sensitivity: step the 0..10 preset index (cf. the Base tab's
        // sensitivity buttons / EqSensitivity_Click). There is no dedicated
        // sensitivity register — a preset is the 0x0C value plus a canned EQ
        // curve, so both move together or the base ends up in a state the Base
        // tab can't represent. Legacy firmware gets only the six old registers.
        private void StepRoadSensitivity(int delta)
        {
            var data = _plugin.Data;
            if (data == null) return;
            int current = BaseSettingCatalog.RoadSensitivityPresetFromRaw(data.RoadSensitivity);
            // Unknown (base hasn't reported): step in from the appropriate end.
            int next = current < 0
                ? (delta > 0 ? BaseSettingCatalog.RoadSensitivityMinPreset : BaseSettingCatalog.RoadSensitivityMaxPreset)
                : ClampStep(current, delta,
                            BaseSettingCatalog.RoadSensitivityMinPreset,
                            BaseSettingCatalog.RoadSensitivityMaxPreset);
            if (next == current) return; // saturated — skip the redundant flash write

            int sensitivity = BaseSettingCatalog.RoadSensitivityRawFromPreset(next);
            data.RoadSensitivity = sensitivity;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-road-sensitivity", sensitivity);

            int[] preset = BaseSettingCatalog.EqSensitivityPresets[next];
            if (data.BaseSupportsEq10)
            {
                for (int i = 0; i < 10; i++)
                {
                    BaseSettingCatalog.SetEqRegister(data, BaseSettingCatalog.Eq10FreqOrderRegisters[i], preset[i]);
                    _plugin.HardwareApplier.WriteIfBaseConnected(BaseSettingCatalog.Eq10FreqOrderCommands[i], preset[i]);
                }
            }
            else
            {
                for (int i = 0; i < 6; i++)
                {
                    int v = preset[BaseSettingCatalog.Eq6FreqColumns[i]];
                    BaseSettingCatalog.SetEqRegister(data, i, v);
                    _plugin.HardwareApplier.WriteIfBaseConnected(BaseSettingCatalog.EqRegisterCommands[i], v);
                }
            }
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] Road sensitivity → preset {next} (0x0C={sensitivity}) via action");
        }

        // ===== Clutch split point (issue #125) =====

        // Current split point using the same source of truth as the wheel device
        // page: the per-(profile x wheel-page) overlay first, because newer
        // KS-family firmware silently drops the cmd-9 read-back, then the live
        // _data mirror, then the slider's 50 % default. Never returns a
        // sentinel, so the first nudge always moves from a real value.
        private int CurrentClutchSplitPoint()
        {
            var overlay = _plugin.GetCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);
            int v = overlay?.WheelClutchPoint ?? -1;
            if (v < 0) v = _plugin.Data?.WheelClutchPoint ?? -1;
            if (v < 0) v = 50;
            return v > 100 ? 100 : v;
        }

        // Mirror of WiClutchPointSlider_ValueChanged's commit path: _data +
        // wheel overlay + device push + persist. The wheel settings page picks
        // the new value up on its next refresh tick. Wire value == display %.
        private void StepClutchSplit(int delta)
        {
            int current = CurrentClutchSplitPoint();
            int val = ClampStep(current, delta, 0, 100);
            if (val == current) return; // saturated — skip the redundant flash write
            if (_plugin.Data != null) _plugin.Data.WheelClutchPoint = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelClutchPoint = val);
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-clutch-point", val);
            _plugin.SaveSettings();
            MozaLog.Debug($"[AZOM] Clutch split point → {val}% via action");
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

        // The ten mechanical layouts in UI-combo order (cf. SettingsControl's
        // Ab9ModeCombo, which orders by the 0x1F/D3 layout byte): action-name suffix
        // + the invariant label the AZOM.Ab9Layout property reports (matches the
        // English UI strings). Ab9LayoutNext/Prev cycles this list, so every entry
        // here must be a layout a user can safely land on.
        private static readonly (Ab9Mode Mode, string Suffix, string Label)[] Ab9Layouts =
        {
            (Ab9Mode.FivePlusR_L1,  "5R1",        "5+R Layout 1"),
            (Ab9Mode.FivePlusR_L2,  "5R2",        "5+R Layout 2"),
            (Ab9Mode.RPlusFive,     "R5",         "R+5 Layout"),
            (Ab9Mode.RPlusSix,      "R6",         "R+6 Layout"),
            (Ab9Mode.SixPlusR_L1,   "6R1",        "6+R Layout 1"),
            (Ab9Mode.SixPlusR_L2,   "6R2",        "6+R Layout 2"),
            (Ab9Mode.SevenPlusR_L1, "7R1",        "7+R Layout 1"),
            (Ab9Mode.SevenPlusR_L2, "7R2",        "7+R Layout 2"),
            (Ab9Mode.RPlusEight,    "R8",         "R+8 Layout"),
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
            if (!_plugin.Fsr1Probe.Active) _plugin.Fsr1Probe.SetProbe(true);
            else _plugin.Fsr1Probe.Step(delta);
            MozaLog.Debug($"[AZOM] FSR1 byte probe → {_plugin.Fsr1Probe.TargetLabel()} via action");
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
