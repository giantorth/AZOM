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
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.UI
{
    public partial class SettingsControl : UserControl
    {

        // ===== Calibration (experimental) ===================================

        private void MBoosterDirCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.Direction = MBoosterDirCheck.IsChecked == true ? 1 : 0;
            _plugin.SaveSettings();
        }
        private void MBoosterMinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            MBoosterMinValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.Min = v;
            _plugin.SaveSettings();
        }
        private void MBoosterMaxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            MBoosterMaxValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.Max = v;
            _plugin.SaveSettings();
        }

        // Sim Input Mapping output curve presets (6 nodes) — derived by
        // sampling the existing 5-point PedalCurvePresets shapes at this
        // curve's own fixed breakpoints (100/6 * k for k=1..6, matching
        // MozaMBoosterRegistry.DefaultCurveX), not new hand-picked values.
        // Linear is the identity (Y[k] == breakpoint[k]), so it also serves
        // as the default X breakpoints below. (Previously 100/7 * k, which
        // left the last breakpoint ~85.7% instead of 100% — see
        // MozaMBoosterRegistry.DefaultCurveX's history — so Linear capped
        // at ~86% instead of reaching 100%; MozaPlugin.FixMBoosterCurveArraysSeventhsBug
        // migrates any profile that saved one of the old values below.)
        private static readonly int[][] MBoosterCurvePresets =
        {
            new[] { 17, 33, 50, 67, 83, 100 }, // Linear
            new[] { 6, 16, 50, 84, 94, 100 },  // S Curve
            new[] { 5, 11, 20, 35, 61, 100 },  // Exponential
            new[] { 39, 65, 80, 89, 95, 100 }, // Parabolic
        };
        private static readonly float[] MBoosterOutputCurveDefault =
            Array.ConvertAll(MBoosterCurvePresets[0], x => (float)x);

        // Sim Input Mapping output curve (6-point) — PURELY host-side, no
        // wire command (see MozaMBoosterRegistry.EvaluateCurveArbitraryX and
        // docs/protocol/devices/mbooster.md "Sim Input Mapping"): remaps the
        // pedal's already-hardware-shaped raw HID position into what AZOM
        // reports as game telemetry. Nodes are also draggable horizontally
        // (AllowHorizontalDrag on the editor) so "100% output before 100%
        // input" works — see MozaMBoosterRegistry.OnHidAxisUpdate, which
        // evaluates (CurveX, CurveY) directly at the live position rather
        // than resampling to any fixed set of breakpoints.
        private void SetMBoosterCurveY(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.CurveY == null || s.CurveY.Length != MBoosterUiConstants.SimInputMappingNodeCount)
                s.CurveY = (float[])MBoosterOutputCurveDefault.Clone();
            s.CurveY[index] = v;
            _plugin.SaveSettings();
        }

        private void SetMBoosterCurveX(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.CurveX == null || s.CurveX.Length != MBoosterUiConstants.SimInputMappingNodeCount)
                s.CurveX = (float[])MBoosterOutputCurveDefault.Clone();
            if (s.CurveY == null || s.CurveY.Length != MBoosterUiConstants.SimInputMappingNodeCount)
                s.CurveY = (float[])MBoosterOutputCurveDefault.Clone();
            s.CurveX[index] = v;
            _plugin.SaveSettings();
        }

        /// <summary>The wire-command role prefix (throttle/brake/clutch) for the
        /// currently-selected config pedal, or null if it has no game role.</summary>
        private string? MBoosterSelectedPedalRolePrefix()
        {
            var s = CurrentMBoosterSettings();
            var c = CurrentMBoosterController();
            if (s == null || c == null) return null;
            // Resolve against the CONNECTED axis count, not the raw HID axis
            // count (bug: a chain-capable hub exposes all 3 GenericDesktop
            // axes even with only one pedal plugged in, so using the raw
            // count here fell into ResolveAxisRole's axis-order fallback
            // instead of reading the single pedal's own Role — silently
            // showing "Throttle" for axis 0 regardless of what the Role
            // dropdown said, and hiding the brake-only Sensor Output Ratio /
            // Max Threshold sliders even for a pedal explicitly set to
            // Brake). Same fix as RefreshMBoosterTab already applies when
            // building the device row list.
            int connectedAxisCount = c.ConnectedAxisIndices().Count;
            if (connectedAxisCount <= 0) connectedAxisCount = 1;
            var role = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.ResolveAxisRole(s, _mboosterEffectPedalIndex, connectedAxisCount);
            return role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Throttle ? "throttle"
                 : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Brake ? "brake"
                 : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Clutch ? "clutch" : null;
        }

        private void MBoosterY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY1Value, "", v => SetMBoosterCurveY(0, v));
        private void MBoosterY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY2Value, "", v => SetMBoosterCurveY(1, v));
        private void MBoosterY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY3Value, "", v => SetMBoosterCurveY(2, v));
        private void MBoosterY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY4Value, "", v => SetMBoosterCurveY(3, v));
        private void MBoosterY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY5Value, "", v => SetMBoosterCurveY(4, v));
        private void MBoosterY6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY6Value, "", v => SetMBoosterCurveY(5, v));

        private void MBoosterX1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX1Value, "", v => SetMBoosterCurveX(0, v));
        private void MBoosterX2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX2Value, "", v => SetMBoosterCurveX(1, v));
        private void MBoosterX3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX3Value, "", v => SetMBoosterCurveX(2, v));
        private void MBoosterX4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX4Value, "", v => SetMBoosterCurveX(3, v));
        private void MBoosterX5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX5Value, "", v => SetMBoosterCurveX(4, v));
        private void MBoosterX6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX6Value, "", v => SetMBoosterCurveX(5, v));

        private void ApplyMBoosterCurvePreset(int[] curve)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = MBoosterUiConstants.SimInputMappingNodeCount;
            if (s.CurveY == null || s.CurveY.Length != n) s.CurveY = new float[n];
            // Presets are a clean, standard shape — reset any dragged X
            // positions back to the fixed breakpoints too.
            s.CurveX = null;
            using (_suppressor.Begin())
            {
                MBoosterY1Slider.Value = curve[0]; SetValueText(MBoosterY1Value, curve[0].ToString());
                MBoosterY2Slider.Value = curve[1]; SetValueText(MBoosterY2Value, curve[1].ToString());
                MBoosterY3Slider.Value = curve[2]; SetValueText(MBoosterY3Value, curve[2].ToString());
                MBoosterY4Slider.Value = curve[3]; SetValueText(MBoosterY4Value, curve[3].ToString());
                MBoosterY5Slider.Value = curve[4]; SetValueText(MBoosterY5Value, curve[4].ToString());
                MBoosterY6Slider.Value = curve[5]; SetValueText(MBoosterY6Value, curve[5].ToString());
                MBoosterX1Slider.Value = MBoosterOutputCurveDefault[0]; SetValueText(MBoosterX1Value, MBoosterOutputCurveDefault[0].ToString("F0"));
                MBoosterX2Slider.Value = MBoosterOutputCurveDefault[1]; SetValueText(MBoosterX2Value, MBoosterOutputCurveDefault[1].ToString("F0"));
                MBoosterX3Slider.Value = MBoosterOutputCurveDefault[2]; SetValueText(MBoosterX3Value, MBoosterOutputCurveDefault[2].ToString("F0"));
                MBoosterX4Slider.Value = MBoosterOutputCurveDefault[3]; SetValueText(MBoosterX4Value, MBoosterOutputCurveDefault[3].ToString("F0"));
                MBoosterX5Slider.Value = MBoosterOutputCurveDefault[4]; SetValueText(MBoosterX5Value, MBoosterOutputCurveDefault[4].ToString("F0"));
                MBoosterX6Slider.Value = MBoosterOutputCurveDefault[5]; SetValueText(MBoosterX6Value, MBoosterOutputCurveDefault[5].ToString("F0"));
            }
            for (int i = 0; i < n; i++)
                s.CurveY[i] = curve[i];
            _plugin.SaveSettings();
        }

        private void MBoosterCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyMBoosterCurvePreset(MBoosterCurvePresets[0]);
        private void MBoosterCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyMBoosterCurvePreset(MBoosterCurvePresets[1]);
        private void MBoosterCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyMBoosterCurvePreset(MBoosterCurvePresets[2]);
        private void MBoosterCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyMBoosterCurvePreset(MBoosterCurvePresets[3]);

        // Pedal Feel curve presets (6 nodes) — derived by sampling the
        // existing 5-point PedalCurvePresets shapes (Linear/S-Curve/
        // Exponential/Parabolic) at this curve's own fixed breakpoints
        // (k/7, see MozaMBoosterRegistry.FeelCurveFractions), not new
        // hand-picked values, so the presets keep the same visual identity
        // users already know from the 5-point curve.
        //
        // These were originally sampled at 8.05/19.5/44.2/72.4/90.0/97.9%,
        // which is what FeelCurveFractions held before the Max-Force fix
        // replaced it with the hardware-verified k/7 spacing; the presets
        // were never resampled, so Linear's nodes came out bunched instead
        // of evenly spaced (deltas 8/11/25/28/18/8 rather than ~14 each).
        private static readonly int[][] MBoosterInputCurvePresets =
        {
            new[] { 14, 29, 43, 57, 71, 86 }, // Linear
            new[] { 5, 12, 30, 70, 88, 95 },  // S Curve
            new[] { 4, 9, 16, 25, 41, 66 },   // Exponential
            new[] { 34, 59, 75, 84, 91, 96 }, // Parabolic
        };

        // The curve's fixed X breakpoints, as a percentage. Derived straight
        // from FeelCurveFractions so the two can never drift apart again —
        // Linear above is the identity over these, and a preset click resets
        // dragged X positions back to them.
        private static readonly float[] MBoosterInputCurveDefault =
            Array.ConvertAll(
                global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.FeelCurveFractions,
                f => (float)Math.Round(f * 100.0));

        // Pedal Feel input curve — CONFIRMED real hardware calibration (see
        // MozaMBoosterRegistry.ComputeFeelCurveY and
        // MBoosterDeviceController.PushFeelCurveResync): its 6 nodes (0-100%
        // of the Deadzone-Max Force span) populate mbooster-brake-
        // feelcurve-1..6 directly. Unlike SetMBoosterCurveY (host-side,
        // never pushes), every edit here calls PushMBoosterFeelCurve.
        private void SetMBoosterInputCurveY(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.PedalFeelNodeCount;
            if (s.InputCurveY == null || s.InputCurveY.Length != n)
                s.InputCurveY = Array.ConvertAll(MBoosterInputCurvePresets[0], x => (float)x);
            s.InputCurveY[index] = v;
            PushMBoosterFeelCurve(s);
        }

        private void MBoosterInputY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY1Value, "", v => SetMBoosterInputCurveY(0, v));
        private void MBoosterInputY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY2Value, "", v => SetMBoosterInputCurveY(1, v));
        private void MBoosterInputY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY3Value, "", v => SetMBoosterInputCurveY(2, v));
        private void MBoosterInputY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY4Value, "", v => SetMBoosterInputCurveY(3, v));
        private void MBoosterInputY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY5Value, "", v => SetMBoosterInputCurveY(4, v));
        private void MBoosterInputY6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY6Value, "", v => SetMBoosterInputCurveY(5, v));

        // Pedal Feel node X position (0-100% of the Deadzone-Max Force span)
        // — CONFIRMED real hardware calibration alongside InputCurveY (see
        // MBoosterDeviceSettings.InputCurveX and
        // MBoosterDeviceController.PushFeelCurveResync): every edit here
        // pushes the same atomic (X, Y) resync SetMBoosterInputCurveY does.
        private void SetMBoosterInputCurveX(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.PedalFeelNodeCount;
            if (s.InputCurveX == null || s.InputCurveX.Length != n)
                s.InputCurveX = Array.ConvertAll(MBoosterInputCurvePresets[0], x => (float)x);
            s.InputCurveX[index] = v;
            PushMBoosterFeelCurve(s);
        }

        private void MBoosterInputX1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX1Value, "", v => SetMBoosterInputCurveX(0, v));
        private void MBoosterInputX2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX2Value, "", v => SetMBoosterInputCurveX(1, v));
        private void MBoosterInputX3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX3Value, "", v => SetMBoosterInputCurveX(2, v));
        private void MBoosterInputX4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX4Value, "", v => SetMBoosterInputCurveX(3, v));
        private void MBoosterInputX5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX5Value, "", v => SetMBoosterInputCurveX(4, v));
        private void MBoosterInputX6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX6Value, "", v => SetMBoosterInputCurveX(5, v));

        private void ApplyMBoosterInputCurvePreset(int[] curve)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.PedalFeelNodeCount;
            if (s.InputCurveY == null || s.InputCurveY.Length != n) s.InputCurveY = new float[n];
            // Presets are a clean, standard shape — reset any dragged X
            // positions back to the fixed breakpoints too (same convention
            // as ApplyMBoosterCurvePreset for Sim Input Mapping).
            s.InputCurveX = null;
            using (_suppressor.Begin())
            {
                MBoosterInputY1Slider.Value = curve[0]; SetValueText(MBoosterInputY1Value, curve[0].ToString());
                MBoosterInputY2Slider.Value = curve[1]; SetValueText(MBoosterInputY2Value, curve[1].ToString());
                MBoosterInputY3Slider.Value = curve[2]; SetValueText(MBoosterInputY3Value, curve[2].ToString());
                MBoosterInputY4Slider.Value = curve[3]; SetValueText(MBoosterInputY4Value, curve[3].ToString());
                MBoosterInputY5Slider.Value = curve[4]; SetValueText(MBoosterInputY5Value, curve[4].ToString());
                MBoosterInputY6Slider.Value = curve[5]; SetValueText(MBoosterInputY6Value, curve[5].ToString());
                MBoosterInputX1Slider.Value = MBoosterInputCurveDefault[0]; SetValueText(MBoosterInputX1Value, MBoosterInputCurveDefault[0].ToString("F0"));
                MBoosterInputX2Slider.Value = MBoosterInputCurveDefault[1]; SetValueText(MBoosterInputX2Value, MBoosterInputCurveDefault[1].ToString("F0"));
                MBoosterInputX3Slider.Value = MBoosterInputCurveDefault[2]; SetValueText(MBoosterInputX3Value, MBoosterInputCurveDefault[2].ToString("F0"));
                MBoosterInputX4Slider.Value = MBoosterInputCurveDefault[3]; SetValueText(MBoosterInputX4Value, MBoosterInputCurveDefault[3].ToString("F0"));
                MBoosterInputX5Slider.Value = MBoosterInputCurveDefault[4]; SetValueText(MBoosterInputX5Value, MBoosterInputCurveDefault[4].ToString("F0"));
                MBoosterInputX6Slider.Value = MBoosterInputCurveDefault[5]; SetValueText(MBoosterInputX6Value, MBoosterInputCurveDefault[5].ToString("F0"));
            }
            for (int i = 0; i < n; i++)
                s.InputCurveY[i] = curve[i];
            PushMBoosterFeelCurve(s);
            _plugin.SaveSettings();
        }

        private void MBoosterInputCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[0]);
        private void MBoosterInputCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[1]);
        private void MBoosterInputCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[2]);
        private void MBoosterInputCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[3]);

        // Start/End of pedal travel (mm) — a real hardware calibration
        // write, reverse-engineered from two real Pit House USB captures:
        // wire commands mbooster-brake-travel-start/-end (cmdIds 0x84/0x85),
        // 2-byte ints, same shape as Min/Max. See
        // MozaMBoosterProtocol.EncodeTravelMm and
        // docs/protocol/devices/mbooster.md "Pedal Feel". MozaRangeSlider
        // has no built-in "changed" CLR event (its Low/HighValue are plain
        // DPs), so it raises RangeChanged instead of the ValueChanged the
        // other mBooster sliders use.
        /// <summary>Motor/config device id for the currently-selected mBooster
        /// pedal's PHYSICAL (per-unit) calibration writes — travel, endstop,
        /// max threshold, sensor ratio, curve7 — routed by ROLE through the
        /// calibration-derived chain map (same as the effect worker; see
        /// MBoosterDeviceController.MotorDeviceForRole), NOT the raw HID axis.
        /// The motor/config device id follows the chain plug position, which
        /// doesn't match the HID axis order, so an axis-index device sends
        /// these to the wrong physical pedal. Falls back to the axis device
        /// until the map resolves. (Direction/Min/Max/output-curve stay on the
        /// host 0x12, which aggregates the output mapping.)</summary>
        private static byte MBoosterCalibDevice(global::MozaPlugin.Devices.MBooster.MBoosterDeviceController? controller, int axisIndex)
        {
            if (controller == null) return global::MozaPlugin.Protocol.MozaProtocol.DeviceMain;
            // Resolve against the CONNECTED axis count, not the raw HID axis
            // count — same fix as MBoosterSelectedPedalRolePrefix. Otherwise
            // a chain-capable hub with fewer pedals wired than raw axis slots
            // falls into ResolveAxisRole's axis-order fallback here too,
            // routing calibration writes (Travel/Endstop/Max Threshold/
            // Sensor Ratio) to the wrong physical MotorDeviceForRole.
            int axisCount = controller.ConnectedAxisIndices().Count;
            if (axisCount <= 0) axisCount = 1;
            var role = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.ResolveAxisRole(controller.CurrentSettings, axisIndex, axisCount);
            int roleIdx = role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Throttle ? 0
                        : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Brake ? 1
                        : role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Clutch ? 2 : -1;
            return controller.MotorDeviceForRole(roleIdx, axisIndex);
        }

        /// <summary>
        /// Park a slider-driven mBooster calibration write on the selected
        /// pedal's own unit, coalesced so a drag emits one write set instead
        /// of one per tick (see MBoosterDeviceController.QueueCalibWrite).
        /// </summary>
        private void QueueMBoosterCalibPush(string key, Action<MBoosterDeviceController, byte> push)
        {
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            byte dev = MBoosterCalibDevice(controller, _mboosterEffectPedalIndex);
            controller.QueueCalibWrite($"{dev:x2}:{key}", () => push(controller, dev));
        }

        /// <summary>
        /// Park a PEDAL FEEL write — Travel / End Stop / Natural Friction /
        /// Segmented Damping / Deadzone-Max Force-feel curve. These live on
        /// brake-named SINGLETON cmdIds with no per-pedal selector, so they
        /// can only configure the pedal that owns that hardware. Pushing them
        /// from a PASSIVE pedal's config doesn't configure that pedal — it
        /// overwrites the ACTIVE pedal's registers (bundle J5PSSQG8: selecting
        /// a passive pedal's page pushed its stored values over the brake's
        /// real ones ~400ms later, so nothing the user set ever stuck). Same
        /// gate ApplyMBoosterToHardware applies on connect; this closes the
        /// live-UI path it never covered.
        /// </summary>
        private void QueueMBoosterPedalFeelPush(string key, Action<MBoosterDeviceController, byte> push)
        {
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            if (!controller.IsAxisMotorized(_mboosterEffectPedalIndex)) return;
            byte dev = MBoosterCalibDevice(controller, _mboosterEffectPedalIndex);
            controller.QueueCalibWrite($"{dev:x2}:{key}", () => push(controller, dev));
        }

        /// <summary>
        /// Park a Deadzone/Max Force write (cmdId 0xAB selectors 0x07-0x0E) —
        /// separate from <see cref="QueueMBoosterCalibPush"/> because neither
        /// capture that confirmed this family (max-force-24-75-128-166-200
        /// .pcapng, deadzone-0-5-11-14.pcapng) included the curve7-1..6
        /// resync that helper tacks on for every other calibration write, so
        /// reusing it here would send frames Pit House itself never sends
        /// for this field. Uses the CURRENT value of whichever of the two
        /// fields didn't just change, since the device has no partial-update
        /// form for this 8-value family — see
        /// MBoosterDeviceController.PushFeelCurveResync.
        /// </summary>
        private void PushMBoosterFeelCurve(IMBoosterPedalConfig s)
        {
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            PushMBoosterFeelCurve(s, controller, _mboosterEffectPedalIndex);
        }

        /// <summary>
        /// Axis-explicit overload — used when the write must target a
        /// SPECIFIC pedal that isn't necessarily the one currently selected
        /// in the UI (e.g. clamping Max Force/Deadzone into range right after
        /// a role change, before the user has selected that row). The
        /// zero-arg overload above delegates here using the currently
        /// selected controller/axis.
        /// </summary>
        private void PushMBoosterFeelCurve(IMBoosterPedalConfig s, global::MozaPlugin.Devices.MBooster.MBoosterDeviceController controller, int axisIndex)
        {
            // Singleton Pedal Feel hardware — see QueueMBoosterPedalFeelPush.
            if (!controller.IsAxisMotorized(axisIndex)) return;
            byte dev = MBoosterCalibDevice(controller, axisIndex);
            double dz = s.DeadzoneKg >= 0 ? s.DeadzoneKg : 0;
            double mf = s.MaxForceKg >= 0 ? s.MaxForceKg : 200;
            float[]? curveY = s.InputCurveY;
            float[]? curveX = s.InputCurveX;
            controller.QueueCalibWrite($"{dev:x2}:feel-curve", () =>
                controller.PushFeelCurveResync(dz, mf, curveY, curveX, dev));
        }

        private void MBoosterTravelRangeSlider_RangeChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.TravelStartMm = (float)MBoosterTravelRangeSlider.LowValue;
            s.TravelEndMm = (float)MBoosterTravelRangeSlider.HighValue;
            // Travel is a physical setting on every pedal mode — push to THIS
            // pedal's own mBooster unit (device 0x12 host / 0x1d / 0x1e chain).
            float startMm = s.TravelStartMm, endMm = s.TravelEndMm;
            QueueMBoosterPedalFeelPush("travel", (c, dev) =>
            {
                c.SendIntWrite("mbooster-brake-travel-start",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(startMm), dev);
                c.SendIntWrite("mbooster-brake-travel-end",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(endMm), dev);
            });
            _plugin.SaveSettings();
        }

        // Deadzone at the start of pedal travel (0..40kg) — CONFIRMED real
        // hardware calibration (mbooster-brake-deadzone, cmdId 0xAB selector
        // 0x07), reverse-engineered from deadzone-0-5-11-14.pcapng (bug
        // bundle 5VR5AQ8Y). See MBoosterDeviceController.PushFeelCurveResync
        // and PushMBoosterFeelCurve. Decimal precision (0.1kg ticks), so this
        // doesn't reuse OnIntSliderChanged (which rounds to whole numbers
        // like the other mBooster sliders).
        private void MBoosterDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            double v = Math.Round(e.NewValue, 1);
            SetValueText(MBoosterDeadzoneValue, v.ToString("F1"));
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.DeadzoneKg = (float)v;
            PushMBoosterFeelCurve(s);
            _plugin.SaveSettings();
        }

        // Max Force (0..200kg) — the force at which the pedal's raw HID axis
        // reaches 100% travel. CONFIRMED real hardware calibration
        // (mbooster-brake-maxforce, cmdId 0xAB selector 0x0E), reverse-
        // engineered from max-force-24-75-128-166-200.pcapng (bug bundle
        // 5VR5AQ8Y) — not clamped to Max Threshold on the wire. See
        // MBoosterDeviceController.PushFeelCurveResync and
        // PushMBoosterFeelCurve.
        private void MBoosterMaxForceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterMaxForceValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.MaxForceKg = v;
                PushMBoosterFeelCurve(s);
            });

        // Sensor Output Ratio — blend between the mBooster's angle sensor
        // (0%) and its load cell (100%). Live-pushes on every drag, same as
        // the wheelbase Brake tab's BrakeAngleRatioSlider (pedals-brake-angle-ratio) —
        // this is the mBooster-side twin of that control (mbooster-brake-angle-ratio).
        private void MBoosterRatioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            SetValueText(MBoosterRatioValue, $"{v}%");
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.SensorOutputRatioPct = v;
            QueueMBoosterCalibPush("angle-ratio",
                (c, dev) => c.SendFloatWrite("mbooster-brake-angle-ratio", v, dev));
            _plugin.SaveSettings();
        }

        // Max Threshold (kg) — Pit House's load-cell-force-for-100%-output
        // setting. Reverse-engineered from a real capture: wire command
        // mbooster-brake-threshold (cmdId 0xB3), a 4-byte big-endian raw
        // uint (NOT a float) on a fixed 0-200kg scale — see
        // MozaMBoosterProtocol.EncodeThresholdKg and
        // docs/protocol/devices/mbooster.md "Sim Input Mapping".
        private void MBoosterMaxThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterMaxThresholdValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.MaxThresholdKg = v;
                QueueMBoosterCalibPush("brake-threshold", (c, dev) =>
                    c.SendIntWrite("mbooster-brake-threshold",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeThresholdKg(v), dev));
            });

        // End Stop Stiffness (Front Limit / End Limit), 1-10 — Pit House's
        // own hardware calibration. Reverse-engineered from two real
        // captures: both share wire command cmdId 0xB2 with a selector byte
        // (mbooster-brake-endstop-front/-end), 2-byte int on a fixed 1-10
        // scale — see MozaMBoosterProtocol.EncodeEndstopStiffness and
        // docs/protocol/devices/mbooster.md "Pedal Feel".
        private void MBoosterEndstopFrontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterEndstopFrontValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.EndstopFrontStiffness = v;
                QueueMBoosterPedalFeelPush("endstop-front", (c, dev) =>
                    c.SendIntWrite("mbooster-brake-endstop-front",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(v), dev));
            });

        private void MBoosterEndstopEndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterEndstopEndValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.EndstopEndStiffness = v;
                QueueMBoosterPedalFeelPush("endstop-end", (c, dev) =>
                    c.SendIntWrite("mbooster-brake-endstop-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(v), dev));
            });

        // Natural Friction (0-100%) — simulates a frictional force
        // independent of game output. Reverse-engineered from two real Pit
        // House USB captures (a toggle on/off, and a 0/25/50/75/100% slider
        // sweep — see docs/protocol/devices/mbooster.md "Pedal Feel"): wire
        // cmdId 0xAE, sharing the same "prefix bytes + selector" shape as
        // End Stop Stiffness (0xB2). Every capture write sent BOTH
        // selectors with the IDENTICAL value in the same burst, so this
        // control always writes mbooster-brake-friction-0 and -1 together
        // rather than exposing them as separate sliders. There is no
        // separate wire enable bit — the capture's toggle-off write simply
        // sent raw 0 (confirmed via the firmware's own debug log echoing
        // it as fixed-point 0.0).
        private void MBoosterNaturalFrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterNaturalFrictionValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.NaturalFrictionPct = v;
                int raw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(v);
                QueueMBoosterPedalFeelPush("friction", (c, dev) =>
                {
                    c.SendIntWrite("mbooster-brake-friction-0", raw, dev);
                    c.SendIntWrite("mbooster-brake-friction-1", raw, dev);
                });
            });

        // Master on/off for Natural Friction — see
        // MBoosterDeviceSettings.NaturalFrictionEnabled. Off pushes raw 0
        // immediately (same effect as dragging the slider to 0, per Pit
        // House's own toggle-off capture) without touching the stored
        // NaturalFrictionPct, so switching back on restores it. The slider
        // is disabled while off to avoid a drag implicitly re-enabling it.
        private void MBoosterNaturalFrictionEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.NaturalFrictionEnabled = MBoosterNaturalFrictionEnable.IsChecked == true;
            MBoosterNaturalFrictionSlider.IsEnabled = s.NaturalFrictionEnabled;
            _plugin.SaveSettings();
            float pct = s.NaturalFrictionEnabled ? (float)MBoosterNaturalFrictionSlider.Value : 0f;
            int raw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(pct);
            QueueMBoosterPedalFeelPush("friction", (c, dev) =>
            {
                c.SendIntWrite("mbooster-brake-friction-0", raw, dev);
                c.SendIntWrite("mbooster-brake-friction-1", raw, dev);
            });
        }

        // Segmented Damping — "When Pressed". Reverse-engineered from real
        // Pit House USB captures (see docs/protocol/devices/mbooster.md
        // "Segmented Damping"): a SINGLE wire command (cmdId 0xB7) carries
        // the entire feature's state — both "When Pressed" and "When
        // Released" — as one 10-field snapshot, so every edit here must
        // resend all 10 fields, not just the ones this plot owns. The
        // "*Released" fields have no UI yet; they're sent using Pit
        // House's own factory defaults (or whatever was last saved) until
        // "When Released" gets its own plot.
        private void MBoosterSegDampPressedPlot_ValuesChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            var sd = s.SegmentedDamping ??= new MBoosterSegmentedDampingSettings();
            sd.Divider1Pressed = (float)MBoosterSegDampPressedPlot.Divider1;
            sd.Divider2Pressed = (float)MBoosterSegDampPressedPlot.Divider2;
            sd.Seg1Pressed = (float)MBoosterSegDampPressedPlot.Seg1Value;
            sd.Seg2Pressed = (float)MBoosterSegDampPressedPlot.Seg2Value;
            sd.Seg3Pressed = (float)MBoosterSegDampPressedPlot.Seg3Value;
            PushSegmentedDamping(sd);
        }

        // Segmented Damping — "When Released". Same shared wire command as
        // "When Pressed" (see that handler and docs/protocol/devices/
        // mbooster.md "Segmented Damping") — every edit here ALSO resends
        // the current Pressed fields alongside the updated Released ones,
        // since the frame is always a whole-feature snapshot.
        private void MBoosterSegDampReleasedPlot_ValuesChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            var sd = s.SegmentedDamping ??= new MBoosterSegmentedDampingSettings();
            sd.Divider1Released = (float)MBoosterSegDampReleasedPlot.Divider1;
            sd.Divider2Released = (float)MBoosterSegDampReleasedPlot.Divider2;
            sd.Seg1Released = (float)MBoosterSegDampReleasedPlot.Seg1Value;
            sd.Seg2Released = (float)MBoosterSegDampReleasedPlot.Seg2Value;
            sd.Seg3Released = (float)MBoosterSegDampReleasedPlot.Seg3Value;
            PushSegmentedDamping(sd);
        }

        /// <summary>
        /// Save + send the ONE Segmented Damping wire frame (cmdId 0xB7)
        /// covering both "When Pressed" and "When Released" — shared by
        /// both plots' change handlers since either one touching its own
        /// half still has to resend the other half's current values (the
        /// wire command has no partial-update form). Not-yet-set fields
        /// (-1 sentinel) fall back to Pit House's own factory defaults, same
        /// as <see cref="MozaPlugin.ApplyMBoosterToHardware"/> does on connect.
        /// When <see cref="MBoosterSegmentedDampingSettings.DampingEnabled"/>
        /// is off, every segment field is forced to 0% regardless of what's
        /// stored/displayed — same wire effect as the user zeroing all six
        /// sliders themselves.
        /// </summary>
        private void PushSegmentedDamping(MBoosterSegmentedDampingSettings sd)
        {
            _plugin.SaveSettings();

            bool enabled = sd.DampingEnabled;

            // Built inside the parked action so the flush sends whatever the
            // plots hold when the drag settles, not a mid-drag snapshot.
            QueueMBoosterPedalFeelPush("segdamp", (c, dev) =>
                c.SendOneShot(global::MozaPlugin.Protocol.MozaMBoosterProtocol.BuildSegmentedDampingFrame(
                    sd.Divider1Pressed >= 0 ? sd.Divider1Pressed : MBoosterUiConstants.SegDampDivider1PressedDefaultPct,
                    sd.Divider2Pressed >= 0 ? sd.Divider2Pressed : MBoosterUiConstants.SegDampDivider2PressedDefaultPct,
                    sd.Divider1Released >= 0 ? sd.Divider1Released : MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct,
                    sd.Divider2Released >= 0 ? sd.Divider2Released : MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct,
                    !enabled ? 0 : sd.Seg1Pressed >= 0 ? sd.Seg1Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg1Released >= 0 ? sd.Seg1Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg2Pressed >= 0 ? sd.Seg2Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg2Released >= 0 ? sd.Seg2Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg3Pressed >= 0 ? sd.Seg3Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg3Released >= 0 ? sd.Seg3Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    dev)));
        }

        // Master on/off for the whole Segmented Damping feature — see
        // MBoosterSegmentedDampingSettings.DampingEnabled and
        // PushSegmentedDamping's zero-forcing above.
        private void MBoosterSegDampEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            var sd = s.SegmentedDamping ??= new MBoosterSegmentedDampingSettings();
            sd.DampingEnabled = MBoosterSegDampEnable.IsChecked == true;
            MBoosterSegDampPressedPlot.IsEnabled = sd.DampingEnabled;
            MBoosterSegDampReleasedPlot.IsEnabled = sd.DampingEnabled;
            PushSegmentedDamping(sd);
        }

        private void MBoosterReadCalButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentMBoosterController()?.RequestCalibrationReads();
        }
        private void MBoosterApplyCalButton_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentMBoosterController();
            var s = CurrentMBoosterSettings();
            if (c == null || s == null) return;
            _plugin.HardwareApplier.ApplyMBoosterToHardware(c, s);
        }

        // ------- Language picker (Options tab) -------
        // null Culture = "Auto" row; otherwise a BCP-47 tag the user picked
        // explicitly. Display is the language's own name so a user who can't
        // read the current UI can still find theirs.
        private sealed class LanguageOption
        {
            public string? Culture { get; set; }
            public string Display { get; set; } = "";
            public override string ToString() => Display;
        }

        private void InitLanguageCombo()
        {
            using (_suppressor.Begin())
            {
                var items = new List<LanguageOption>
                {
                    new LanguageOption { Culture = null, Display = "Auto" },
                };
                foreach (var code in LanguageResolver.SupportedCultures)
                {
                    var display = LanguageResolver.DisplayNames.TryGetValue(code, out var name) ? name : code;
                    items.Add(new LanguageOption { Culture = code, Display = display });
                }
                LanguageCombo.ItemsSource = items;

                var current = _plugin.Settings.PreferredLanguage;
                LanguageCombo.SelectedItem = items.Find(i =>
                    string.Equals(i.Culture ?? "", current ?? "", StringComparison.OrdinalIgnoreCase))
                    ?? items[0];
            }
        }

        private void LanguageCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (LanguageCombo.SelectedItem is not LanguageOption opt) return;
            _plugin.Settings.PreferredLanguage = opt.Culture; // null = Auto
            _plugin.SaveSettings();
        }

    }
}
