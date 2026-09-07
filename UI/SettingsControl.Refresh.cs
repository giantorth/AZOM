using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MozaPlugin.Devices;
using MozaPlugin.Resources;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.UI;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;
using static MozaPlugin.UI.UiHelpers;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;
using CaptureRedactor = MozaPlugin.Diagnostics.CaptureRedactor;
using MozaPlugin.Devices.Ui;

namespace MozaPlugin.UI
{
    public partial class SettingsControl : UserControl
    {


        // ===== Refresh =====

        private void RequestAllSettings()
        {
            _device.ReadSettings(
                "base-limit", "base-max-angle", "base-ffb-strength", "base-torque", "base-speed",
                "base-damper", "base-friction", "base-inertia", "base-spring",
                "main-get-damper-gain", "main-get-friction-gain",
                "main-get-inertia-gain", "main-get-spring-gain",
                "base-protection", "base-natural-inertia",
                "base-speed-damping", "base-speed-damping-point",
                "base-soft-limit-stiffness", "base-soft-limit-retain",
                "base-ffb-reverse", "main-get-work-mode", "main-get-led-status",
                "main-get-ble-mode", "main-get-compat-mode",
                "base-mcu-temp", "base-mosfet-temp", "base-motor-temp"
            );
        }

        private int _shakeItProbeTick;

        private void RefreshDisplay(object sender, EventArgs e)
        {
            // All top-of-pane banners (status hints + update + SDK nudge) are
            // owned by the self-refreshing PluginBanners control now.

            try
            {
                using (_suppressor.Begin())
                {
                    RefreshBaseTab();
                    RefreshHandbrakeTab();
                    RefreshPedalsTab();
                    RefreshHgpTab();
                    RefreshSgpTab();
                    RefreshHubTab();
                    RefreshAb9Tab();
                    RefreshStalksTab();
                    RefreshMBoosterTab();
                    RefreshOptionsTab();
                    InitTelemetryTab();
                    RefreshSdkStatusTick();
                    // Last: the per-tab refreshes above each set their own tab's
                    // Visibility from detection, so the override only sticks if it
                    // runs after them. Turning it back off needs no undo here —
                    // those same assignments re-collapse on the next tick.
                    ApplyShowAllTabs();
                }
            }
            catch (Exception ex)
            {
                // Unguarded, a throwing tab reaches SimHub's dispatcher handler, which
                // logs an Error and flushes the log (up to 2 s) — twice a second.
                MozaLog.DebugIfChanged("ui-tick-settings", $"[AZOM] Settings refresh tick failed: {ex}");
            }
        }

        // Every tab the pane can hide: the detection-gated device tabs, plus the
        // ones hidden unconditionally in XAML — the wheel tab (migrated to the
        // per-wheel device page) and the dashboard upload / wheel files tabs
        // (feature still in development). Nothing else drives the latter three's
        // Visibility, so ShowAllTabs is the only thing that surfaces them.
        private TabItem[] HideableTabs => new[]
        {
            BaseLfeTab, HandbrakeTab, PedalsTab, Ab9Tab,
            HgpTab, SgpTab, MBoosterTab, HubTab, StalksTab,
        };

        private void ApplyShowAllTabs()
        {
            if (_plugin?.Settings?.ShowAllTabs != true) return;

            foreach (var tab in HideableTabs)
            {
                if (tab != null)
                    tab.Visibility = Visibility.Visible;
            }
        }

        private void ShowAllTabsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.ShowAllTabs = ShowAllTabsCheck.IsChecked == true;
            _plugin.SaveSettings();
            // Show immediately; hiding falls out of the next tick's per-tab pass.
            ApplyShowAllTabs();
        }

        private void UpdateHidInputDisplays()
        {
            var hidReader = _plugin.HidReader;
            bool connected = hidReader != null && _data.IsHidConnected;

            if (connected && _data.MaxAngle > 0)
            {
                double deg = hidReader!.GetCurrentAngleDegrees(_data.MaxAngle * 2);
                SteeringAngleLabel.Text = $"{deg:0;-0;0}°";
                UpdateRedesignSteeringAngle(deg, valid: true);
            }
            else
            {
                SteeringAngleLabel.Text = "--";
                UpdateRedesignSteeringAngle(0, valid: false);
            }

            HandbrakeBar.Value = connected ? _data.HandbrakePosition : 0;

            // Pedals-tab live-input trace (replaced the throttle/brake/clutch
            // bars). Pushed every frame so the sparkline scrolls whether or not a
            // pedal is moving; same merged 0-100 values as the mBooster trace.
            PushTraceSample(_pedalBrakeTraceSamples,    connected ? _data.BrakePosition : 0);
            PushTraceSample(_pedalThrottleTraceSamples, connected ? _data.ThrottlePosition : 0);
            PushTraceSample(_pedalClutchTraceSamples,   connected ? _data.ClutchPosition : 0);

            UpdateHandbrakeButtonStatus(connected);
            UpdateMBoosterCurveMarkers(connected);

            // Phase 6: fan out live HID data to the per-device wheel control's
            // Inputs sub-tab so its paddle bars + active-button text update at
            // the same 30 Hz cadence as the (now hidden) plugin-pane controls.
            try { global::MozaPlugin.Devices.Ui.MozaWheelSettingsControl.Instance?.PushInputsLiveData(_data); }
            catch { }
        }

        // Live position markers on the mBooster tab's two curve editors,
        // driven by the currently selected device's latest HID position.
        // Runs at the same 30 Hz cadence as the standard pedal bars above
        // instead of the 500ms general refresh — that felt sluggish for
        // direct pedal feedback.
        private void UpdateMBoosterCurveMarkers(bool hidConnected)
        {
            if (MBoosterDevicePanel.Visibility != Visibility.Visible) return;
            var selected = _plugin?.MBoosterRegistry?.FindByIdentity(_mboosterSelectedIdentity ?? "");
            if (selected == null) return;
            // Track the SELECTED pedal's own axis, not always the master's —
            // otherwise every pedal page showed the master (throttle) input.
            int idx = _mboosterEffectPedalIndex;
            double preCurve = (idx >= 0 && idx < selected.LastAxisRawPercentPreCurve.Length)
                ? selected.LastAxisRawPercentPreCurve[idx] : selected.LastRawPercentPreCurve;
            // TRUE raw reading — % of Max Force's own hardware ceiling, i.e.
            // the physical force the user is actually applying to the pedal,
            // captured in OnHidAxisUpdate BEFORE the host-side Max Threshold
            // rescale. This is the Pedal Feel curve's real input domain
            // (Deadzone-Max Force span) and what "Input Force" should show —
            // preCurve above is post-Threshold-rescale now (Sim Input
            // Mapping's own, different domain), not this pedal's raw input.
            double rawInput = (idx >= 0 && idx < selected.LastAxisRawPercentPreThreshold.Length)
                ? selected.LastAxisRawPercentPreThreshold[idx] : 0.0;

            // Pedal Feel's curve is now a REAL hardware effect (see
            // MozaMBoosterRegistry.ComputeFeelCurveX) — the device reshapes
            // the raw force before this HID read ever sees it, so AZOM has
            // no live TRUE "input to that curve" value to plot (that would
            // need the raw pre-reshape force, which AZOM never receives).
            // Best available proxy: rawInput — positionally correct against
            // the curve's own Deadzone-Max Force X axis (unlike preCurve,
            // which is now Threshold-rescaled and belongs to a different
            // domain), even though it can't reflect the device's own
            // internal reshaping.
            // The Sim Input Mapping curve is the opposite: purely host-side
            // (see EvaluateCurveArbitraryX), so its live marker uses
            // preCurve exactly — the already-hardware-shaped, Threshold-
            // rescaled position that's actually fed INTO this curve, not
            // pct (which is the curve's own output).
            MBoosterInputCurveEditor.LiveX = hidConnected ? rawInput : double.NaN;
            MBoosterCurveEditor.LiveX = preCurve;

            // Live "position % · kg force" readout above the Pedal Feel
            // curve editor (MBoosterPedalFeelLiveLabel) — the raw force the
            // user is applying to the pedal, independent of Max Threshold's
            // sim-facing output scaling. kg is an estimate, not a directly-
            // read sensor value: rawInput/100 * Max Force's own kg ceiling
            // (the reference its own 100% represents — see OnHidAxisUpdate),
            // falling back to 200kg if Max Force itself is unset.
            if (hidConnected)
            {
                var cfg = PeekMBoosterEffectTarget();
                double fullScaleKg = (cfg != null && cfg.MaxForceKg >= 0) ? cfg.MaxForceKg : 200.0;
                double kg = rawInput / 100.0 * fullScaleKg;
                MBoosterPedalFeelLiveLabel.Text = $"{Strings.Label_InputForce}: {rawInput:F0}% · {kg:F1} kg";
            }
            else
            {
                MBoosterPedalFeelLiveLabel.Text = Strings.Label_InputForce;
            }

            // Effects card pedal trace — same 30 Hz cadence as the Inputs
            // tab's pedal bars above, and the same merged 0-100 values
            // (_data.*Position), so it shows every connected pedal's live
            // position — mBooster or not, whichever device currently holds
            // each role — not just an mBooster's own HID reading.
            PushTraceSample(_mboosterBrakeTraceSamples, hidConnected ? _data.BrakePosition : 0);
            PushTraceSample(_mboosterThrottleTraceSamples, hidConnected ? _data.ThrottlePosition : 0);
            PushTraceSample(_mboosterClutchTraceSamples, hidConnected ? _data.ClutchPosition : 0);
        }

        // Append one sample to a rolling trace buffer, trimming to PedalTraceSamples.
        // Shared by the mBooster page trace and the Pedals-tab trace.
        private static void PushTraceSample(ObservableCollection<double> samples, double value)
        {
            samples.Add(value);
            while (samples.Count > PedalTraceSamples)
                samples.RemoveAt(0);
        }

        private void UpdateHandbrakeButtonStatus(bool connected)
        {
            if (_data.HandbrakeMode != 1) return;

            bool pressed = connected && _data.HandbrakeButtonPressed;
            HandbrakeButtonStatus.Text = pressed ? "Pressed" : "Released";
            HandbrakeButtonStatus.FontWeight = pressed ? FontWeights.Bold : FontWeights.Normal;
            HandbrakeButtonStatus.Foreground = pressed ? Brushes.White : Brushes.Gray;
        }

        private void RefreshBaseTab()
        {
            ConnectionIndicator.Fill = _data.IsConnected ? Brushes.LimeGreen : Brushes.Gray;
            ConnectionLabel.Text = _data.IsConnected ? "Connected" : "Disconnected";
            UpdateRedesignLiveDisplays();

            string tempUnit = _data.UseFahrenheit ? "°F" : "°C";
            McuTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.McuTemp):F0} {tempUnit}" : "--";
            MosfetTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MosfetTemp):F0} {tempUnit}" : "--";
            MotorTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MotorTemp):F0} {tempUnit}" : "--";

            // Reverse expression: *2 (raw → display degrees)
            // Floor 60° = measured firmware clamp (PitHouse stops at 90°);
            // must stay even — the wire's half-degree raw makes odd degrees
            // unrepresentable — and matched to the XAML slider Minimum.
            double rot = Clamp(_data.Limit * 2.0, 60, 2700);
            RotationSlider.Value = rot;
            SetValueText(RotationValue, $"{rot:F0}°");

            double ffb = Clamp(_data.FfbStrength / 10.0, 0, 100);
            FfbStrengthSlider.Value = ffb;
            SetValueText(FfbStrengthValue, $"{ffb:F0}%");

            double interp = Clamp(_data.Interpolation / 10.0, 0, 10);   // wire 0-100 -> display 0-10
            InterpolationSlider.Value = interp;
            SetValueText(InterpolationValue, $"{interp:F0}");

            double torque = Clamp(_data.Torque, 50, 100);
            TorqueSlider.Value = torque;
            SetValueText(TorqueValue, $"{torque:F0}%");

            // Performance output (cmd 0x1E base = TempStrategy): 0 = Reserved, 1 = Full
            int perf = _data.TempStrategy;
            if (perf >= 0 && perf < PerformanceOutputCombo.Items.Count)
                PerformanceOutputCombo.SelectedIndex = perf;
            // Gearshift vibration intensity (cmd 0x2E base): 0..5
            int gs = _data.GearshiftVibration;
            if (gs < 0) gs = 0;
            if (gs > 5) gs = 5;
            GearshiftVibrationSlider.Value = gs;
            SetValueText(GearshiftVibrationValue, gs.ToString());

            // Plugin-side gearshift event coalescing — per-profile (so each
            // game can pick its own tuning) with flat-field fallback. Refreshed
            // on every tick so a profile-switch with the settings tab open
            // pulls the new game's values; without this, the controls held
            // stale values from the previous profile and any user edit
            // silently overwrote the new active profile's stored values.
            // Suppressor on the surrounding RefreshDisplay scope swallows the
            // ValueChanged events these assignments raise.
            var gsProfile = _plugin.Settings.ProfileStore?.CurrentProfile;
            bool von = gsProfile?.GearshiftVibrateOnNeutral == 1
                || (gsProfile?.GearshiftVibrateOnNeutral == -1 && _plugin.Settings.GearshiftVibrateOnNeutral);
            GearshiftVibrateOnNeutralCheck.IsChecked = von;
            int dbMs = gsProfile?.GearshiftDebounceMs ?? -1;
            if (dbMs < 0) dbMs = _plugin.Settings.GearshiftDebounceMs;
            if (dbMs < 0) dbMs = 500;
            if (dbMs > 1000) dbMs = 1000;
            dbMs = ((dbMs + 25) / 50) * 50;
            GearshiftDebounceSlider.Value = dbMs;
            GearshiftDebounceValue.Text = $"{dbMs} ms";

            // Wheelbase LFE effects (base fw >= 1.2.10.10). On LFE-capable
            // firmware the complex effects card is shown and the classic
            // gearshift card hidden (the complex gearshift replaces it);
            // otherwise the reverse. Re-evaluated each tick so the card appears
            // within one refresh of the firmware-version read landing.
            // The classic gearshift card stays visible on all firmware (its bump
            // command coexists with the LFE channels); the LFE card is shown
            // additionally, full-width below, only on LFE-capable firmware.
            // Hide the LFE tab when the user routed LFE to SimHub's ShakeIt haptics
            // — that owns the output, and the two must not both edit the base.
            // Stamp the cache while we're on the UI thread — the diagnostics dump
            // (built off the bundle writer's thread) can't enumerate SimHub's
            // device collection itself. A walk of SimHub's whole device list, so
            // every ~10 s rather than every 500 ms tick; it feeds a diagnostics line.
            if (_plugin != null && (_shakeItProbeTick++ % 20) == 0)
                _plugin.ShakeItLfeDeviceDeployedCached = _plugin.IsShakeItLfeDeviceDeployed;
            bool lfeSupported = _data.BaseSupportsLfe && _plugin?.WheelbaseLfeRoutedToShakeIt != true;
            BaseLfeTab.Visibility = lfeSupported
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (lfeSupported)
                SeedBaseLfeControls(gsProfile?.BaseLfe);

            // FFB EQ band mode (6 legacy bands vs 10-band fw >= 1.2.10.10) —
            // re-evaluated each tick like the LFE gate above.
            bool eq10 = _data.BaseSupportsEq10;
            ApplyEqBandMode(eq10);

            double spd = Clamp(_data.Speed / 10.0, 0, 200);
            SpeedSlider.Value = spd;
            SetValueText(SpeedValue, $"{spd:F0}%");

            SetSliderPercent(DamperSlider, DamperValue, _data.Damper / 10.0, 0, 100);
            SetSliderPercent(FrictionSlider, FrictionValue, _data.Friction / 10.0, 0, 100);
            double inertia = Clamp(_data.Inertia / 10.0, 100, 500);
            InertiaSlider.Value = inertia;
            SetValueText(InertiaValue, $"{inertia:F0}");
            SetSliderPercent(SpringSlider, SpringValue, _data.Spring / 10.0, 0, 100);

            FfbReverseCheck.IsChecked = _data.FfbReverse > 0;

            SetSliderPercent(GameDamperSlider, GameDamperValue, _data.GameDamper / 2.55, 0, 100);
            SetSliderPercent(GameFrictionSlider, GameFrictionValue, _data.GameFriction / 2.55, 0, 100);
            SetSliderPercent(GameInertiaSlider, GameInertiaValue, _data.GameInertia / 2.55, 0, 100);
            SetSliderPercent(GameSpringSlider, GameSpringValue, _data.GameSpring / 2.55, 0, 100);

            double spdDamp = Clamp(_data.SpeedDamping, 0, 100);
            SpeedDampingSlider.Value = spdDamp;
            SetValueText(SpeedDampingValue, $"{spdDamp:F0}%");
            double spdDampPt = Clamp(_data.SpeedDampingPoint, 0, 400);
            SpeedDampingPointSlider.Value = spdDampPt;
            SetValueText(SpeedDampingPointValue, $"{spdDampPt:F0} kph");

            ProtectionCheck.IsChecked = _data.Protection > 0;
            double natInertia = Clamp(_data.NaturalInertia, 100, 4000);
            NaturalInertiaSlider.Value = natInertia;
            SetValueText(NaturalInertiaValue, $"{natInertia:F0}");

            double stiff = (_data.SoftLimitStiffness / (400.0 / 9.0)) - 2.25 + 1.0;
            stiff = Math.Round(Clamp(stiff, 1, 10));
            SoftLimitStiffnessSlider.Value = stiff;
            SetValueText(SoftLimitStiffnessValue, $"{stiff:F0}");
            SoftLimitRetainCheck.IsChecked = _data.SoftLimitRetain > 0;

            StandbyCheck.IsChecked = _data.WorkMode > 0;
            SyncAutoStandbyCombo();
            LedStatusCheck.IsChecked = _data.LedStatus != 0;
            BluetoothCheck.IsChecked = _data.BleMode == 0;

            // FFB Equalizer (100% = flat). Ranges depend on the band mode:
            // legacy 0-400 on all six; 10-band fw is 0-500 except the 100 Hz
            // band (Eq6), which keeps its 0-100 cap. SetSliderRaw clamps, so
            // the ranges are load-bearing.
            int eqHi = eq10 ? 500 : 400;
            SetSliderRaw(Eq1Slider, Eq1Value, _data.Equalizer1, 0, eqHi, "%");
            SetSliderRaw(Eq2Slider, Eq2Value, _data.Equalizer2, 0, eqHi, "%");
            SetSliderRaw(Eq3Slider, Eq3Value, _data.Equalizer3, 0, eqHi, "%");
            SetSliderRaw(Eq4Slider, Eq4Value, _data.Equalizer4, 0, eqHi, "%");
            SetSliderRaw(Eq5Slider, Eq5Value, _data.Equalizer5, 0, eqHi, "%");
            SetSliderRaw(Eq6Slider, Eq6Value, _data.Equalizer6, 0, eq10 ? 100 : 400, "%");
            if (eq10)
            {
                SetSliderRaw(Eq7Slider, Eq7Value, _data.Equalizer7, 0, 500, "%");
                SetSliderRaw(Eq8Slider, Eq8Value, _data.Equalizer8, 0, 500, "%");
                SetSliderRaw(Eq9Slider, Eq9Value, _data.Equalizer9, 0, 500, "%");
                SetSliderRaw(Eq10Slider, Eq10Value, _data.Equalizer10, 0, 500, "%");
            }

            // FFB Curve — X1..X4 are the draggable input positions of points 1-4
            // (point 5 fixed at input=100%); Y1..Y5 the output values.
            SetSliderRaw(FfbCurveX1Slider, FfbCurveX1Value, _data.FfbCurveX1, 0, 100, "");
            SetSliderRaw(FfbCurveX2Slider, FfbCurveX2Value, _data.FfbCurveX2, 0, 100, "");
            SetSliderRaw(FfbCurveX3Slider, FfbCurveX3Value, _data.FfbCurveX3, 0, 100, "");
            SetSliderRaw(FfbCurveX4Slider, FfbCurveX4Value, _data.FfbCurveX4, 0, 100, "");
            SetSliderRaw(FfbCurveY1Slider, FfbCurveY1Value, _data.FfbCurveY1, 0, 100, "");
            SetSliderRaw(FfbCurveY2Slider, FfbCurveY2Value, _data.FfbCurveY2, 0, 100, "");
            SetSliderRaw(FfbCurveY3Slider, FfbCurveY3Value, _data.FfbCurveY3, 0, 100, "");
            SetSliderRaw(FfbCurveY4Slider, FfbCurveY4Value, _data.FfbCurveY4, 0, 100, "");
            SetSliderRaw(FfbCurveY5Slider, FfbCurveY5Value, _data.FfbCurveY5, 0, 100, "");
        }

    }
}
