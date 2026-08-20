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

        private static readonly float[] MBoosterDefaultCurve = { 20, 40, 60, 80, 100 };

        // Output curve (5-point, mirrors the wheelbase pedal Y curves). The
        // mBooster's single physical axis always writes through the
        // "throttle" command slot regardless of assigned role — same
        // convention as Direction/Min/Max above (see ApplyMBoosterToHardware).
        //
        // Nodes are also draggable horizontally (AllowHorizontalDrag on the
        // editor) so "100% output before 100% input" works without a
        // (nonexistent) hardware X-breakpoint command: every Y or X change
        // resamples the whole (CurveX, CurveY) shape at the fixed
        // 20/40/60/80/100 breakpoints the wire protocol actually supports
        // and pushes all 5 through the existing y1-y5 commands, instead of
        // pushing just the one changed value. When CurveX is still the
        // default, resampling is the identity, so this is a no-op change in
        // behavior for anyone who never drags a node sideways.
        private void SetMBoosterCurveY(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.CurveY == null || s.CurveY.Length != 5) s.CurveY = (float[])MBoosterDefaultCurve.Clone();
            s.CurveY[index] = v;
            PushResampledMBoosterCurve(s);
        }

        private void SetMBoosterCurveX(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.CurveX == null || s.CurveX.Length != 5) s.CurveX = (float[])MBoosterDefaultCurve.Clone();
            if (s.CurveY == null || s.CurveY.Length != 5) s.CurveY = (float[])MBoosterDefaultCurve.Clone();
            s.CurveX[index] = v;
            PushResampledMBoosterCurve(s);
        }

        private void PushResampledMBoosterCurve(IMBoosterPedalConfig s)
        {
            if (s.CurveY == null || s.CurveY.Length != 5) return;
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            string? prefix = MBoosterSelectedPedalRolePrefix();
            if (prefix == null) return;
            // Pushed to the SELECTED pedal's own role command (not always
            // throttle, and not always the host 0x12) so the curve lands on the
            // right pedal. Coalesced rather than live-per-node now: these are
            // flash-backed registers and a node drag fires per pixel — the
            // device sees the settled shape ~400ms after the drag stops.
            var resampled = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResampleCurveAtFixedBreakpoints(s.CurveX, s.CurveY);
            QueueMBoosterCalibPush($"curve-{prefix}", (c, dev) =>
            {
                for (int i = 0; i < 5; i++)
                    c.SendFloatWrite($"mbooster-{prefix}-y{i + 1}", resampled[i], dev);
            });
        }

        /// <summary>The wire-command role prefix (throttle/brake/clutch) for the
        /// currently-selected config pedal, or null if it has no game role.</summary>
        private string? MBoosterSelectedPedalRolePrefix()
        {
            var s = CurrentMBoosterSettings();
            var c = CurrentMBoosterController();
            if (s == null || c == null) return null;
            int axisCount = c.AxisCount > 0 ? c.AxisCount : 1;
            var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(s, _mboosterEffectPedalIndex, axisCount);
            return role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? "throttle"
                 : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? "brake"
                 : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? "clutch" : null;
        }

        private void MBoosterY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY1Value, "", v => SetMBoosterCurveY(0, v));
        private void MBoosterY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY2Value, "", v => SetMBoosterCurveY(1, v));
        private void MBoosterY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY3Value, "", v => SetMBoosterCurveY(2, v));
        private void MBoosterY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY4Value, "", v => SetMBoosterCurveY(3, v));
        private void MBoosterY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY5Value, "", v => SetMBoosterCurveY(4, v));

        private void MBoosterX1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX1Value, "", v => SetMBoosterCurveX(0, v));
        private void MBoosterX2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX2Value, "", v => SetMBoosterCurveX(1, v));
        private void MBoosterX3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX3Value, "", v => SetMBoosterCurveX(2, v));
        private void MBoosterX4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX4Value, "", v => SetMBoosterCurveX(3, v));
        private void MBoosterX5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX5Value, "", v => SetMBoosterCurveX(4, v));

        private void ApplyMBoosterCurvePreset(int[] curve)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.CurveY == null || s.CurveY.Length != 5) s.CurveY = new float[5];
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
                MBoosterX1Slider.Value = MBoosterDefaultCurve[0]; SetValueText(MBoosterX1Value, MBoosterDefaultCurve[0].ToString("F0"));
                MBoosterX2Slider.Value = MBoosterDefaultCurve[1]; SetValueText(MBoosterX2Value, MBoosterDefaultCurve[1].ToString("F0"));
                MBoosterX3Slider.Value = MBoosterDefaultCurve[2]; SetValueText(MBoosterX3Value, MBoosterDefaultCurve[2].ToString("F0"));
                MBoosterX4Slider.Value = MBoosterDefaultCurve[3]; SetValueText(MBoosterX4Value, MBoosterDefaultCurve[3].ToString("F0"));
                MBoosterX5Slider.Value = MBoosterDefaultCurve[4]; SetValueText(MBoosterX5Value, MBoosterDefaultCurve[4].ToString("F0"));
            }
            for (int i = 0; i < 5; i++)
                s.CurveY[i] = curve[i];
            PushResampledMBoosterCurve(s);
            _plugin.SaveSettings();
        }

        private void MBoosterCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyMBoosterCurvePreset(PedalCurvePresets[0]);
        private void MBoosterCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyMBoosterCurvePreset(PedalCurvePresets[1]);
        private void MBoosterCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyMBoosterCurvePreset(PedalCurvePresets[2]);
        private void MBoosterCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyMBoosterCurvePreset(PedalCurvePresets[3]);

        // Pedal Feel input curve (host-side only — see MozaMBoosterRegistry.
        // EvaluateInputCurve). Reshapes the reported HID position before it
        // reaches the game or the Sim Input Mapping output curve above;
        // never writes to the device, unlike SetMBoosterCurveY.
        private void SetMBoosterInputCurveY(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.InputCurveY == null || s.InputCurveY.Length != 5) s.InputCurveY = (float[])MBoosterDefaultCurve.Clone();
            s.InputCurveY[index] = v;
        }

        private void MBoosterInputY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY1Value, "", v => SetMBoosterInputCurveY(0, v));
        private void MBoosterInputY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY2Value, "", v => SetMBoosterInputCurveY(1, v));
        private void MBoosterInputY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY3Value, "", v => SetMBoosterInputCurveY(2, v));
        private void MBoosterInputY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY4Value, "", v => SetMBoosterInputCurveY(3, v));
        private void MBoosterInputY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY5Value, "", v => SetMBoosterInputCurveY(4, v));

        private void ApplyMBoosterInputCurvePreset(int[] curve)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.InputCurveY == null || s.InputCurveY.Length != 5) s.InputCurveY = new float[5];
            using (_suppressor.Begin())
            {
                MBoosterInputY1Slider.Value = curve[0]; SetValueText(MBoosterInputY1Value, curve[0].ToString());
                MBoosterInputY2Slider.Value = curve[1]; SetValueText(MBoosterInputY2Value, curve[1].ToString());
                MBoosterInputY3Slider.Value = curve[2]; SetValueText(MBoosterInputY3Value, curve[2].ToString());
                MBoosterInputY4Slider.Value = curve[3]; SetValueText(MBoosterInputY4Value, curve[3].ToString());
                MBoosterInputY5Slider.Value = curve[4]; SetValueText(MBoosterInputY5Value, curve[4].ToString());
            }
            for (int i = 0; i < 5; i++)
                s.InputCurveY[i] = curve[i];
            _plugin.SaveSettings();
        }

        private void MBoosterInputCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyMBoosterInputCurvePreset(PedalCurvePresets[0]);
        private void MBoosterInputCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyMBoosterInputCurvePreset(PedalCurvePresets[1]);
        private void MBoosterInputCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyMBoosterInputCurvePreset(PedalCurvePresets[2]);
        private void MBoosterInputCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyMBoosterInputCurvePreset(PedalCurvePresets[3]);

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
        private static byte MBoosterCalibDevice(global::MozaPlugin.Devices.MBoosterDeviceController? controller, int axisIndex)
        {
            if (controller == null) return global::MozaPlugin.Protocol.MozaProtocol.DeviceMain;
            int axisCount = controller.AxisCount > 0 ? controller.AxisCount : 1;
            var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(controller.CurrentSettings, axisIndex, axisCount);
            int roleIdx = role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? 0
                        : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? 1
                        : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? 2 : -1;
            return controller.MotorDeviceForRole(roleIdx, axisIndex);
        }

        /// <summary>
        /// Park a slider-driven mBooster calibration write on the selected
        /// pedal's own unit, coalesced so a drag emits one write set instead of
        /// one per tick (see MBoosterDeviceController.QueueCalibWrite). The
        /// EXPERIMENTAL curve7 resync every one of these writes needs to
        /// actually commit rides inside the same parked action, so it can never
        /// be reordered ahead of the write it is committing.
        /// </summary>
        private void QueueMBoosterCalibPush(string key, Action<MBoosterDeviceController, byte> push)
        {
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            byte dev = MBoosterCalibDevice(controller, _mboosterEffectPedalIndex);
            var s = CurrentMBoosterEffectTarget();
            float[]? curveX = s?.CurveX, curveY = s?.CurveY;
            controller.QueueCalibWrite($"{dev:x2}:{key}", () =>
            {
                push(controller, dev);
                controller.PushCurve7Resync(curveX, curveY, dev);
            });
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
            QueueMBoosterCalibPush("travel", (c, dev) =>
            {
                c.SendIntWrite("mbooster-brake-travel-start",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(startMm), dev);
                c.SendIntWrite("mbooster-brake-travel-end",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(endMm), dev);
            });
            _plugin.SaveSettings();
        }

        // Deadzone at the start of pedal travel (0..40kg, host-side only —
        // see MozaMBoosterRegistry.ApplyDeadzoneAndMaxForce). Decimal
        // precision (0.1kg ticks), so this doesn't reuse OnIntSliderChanged
        // (which rounds to whole numbers like the other mBooster sliders).
        private void MBoosterDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            double v = Math.Round(e.NewValue, 1);
            SetValueText(MBoosterDeadzoneValue, v.ToString("F1"));
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.DeadzoneKg = (float)v;
            _plugin.SaveSettings();
        }

        // Max Force (0..200kg, host-side only, default 200 = off) — the
        // force at which the Pedal Feel input curve's X-axis reaches 100%.
        // See MozaMBoosterRegistry.ApplyDeadzoneAndMaxForce.
        private void MBoosterMaxForceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterMaxForceValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.MaxForceKg = v;
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
                // This IS the raw axis's full scale, so it is also the ceiling
                // Max Force is expressed against — re-scale that slider now
                // rather than leaving its top span silently inert.
                ApplyMBoosterMaxForceCeiling(s);
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
                QueueMBoosterCalibPush("endstop-front", (c, dev) =>
                    c.SendIntWrite("mbooster-brake-endstop-front",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(v), dev));
            });

        private void MBoosterEndstopEndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterEndstopEndValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.EndstopEndStiffness = v;
                QueueMBoosterCalibPush("endstop-end", (c, dev) =>
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
                QueueMBoosterCalibPush("friction", (c, dev) =>
                {
                    c.SendIntWrite("mbooster-brake-friction-0", raw, dev);
                    c.SendIntWrite("mbooster-brake-friction-1", raw, dev);
                });
            });

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
        /// </summary>
        private void PushSegmentedDamping(MBoosterSegmentedDampingSettings sd)
        {
            _plugin.SaveSettings();

            // Built inside the parked action so the flush sends whatever the
            // plots hold when the drag settles, not a mid-drag snapshot.
            QueueMBoosterCalibPush("segdamp", (c, dev) =>
                c.SendOneShot(global::MozaPlugin.Protocol.MozaMBoosterProtocol.BuildSegmentedDampingFrame(
                    sd.Divider1Pressed >= 0 ? sd.Divider1Pressed : MBoosterUiConstants.SegDampDivider1PressedDefaultPct,
                    sd.Divider2Pressed >= 0 ? sd.Divider2Pressed : MBoosterUiConstants.SegDampDivider2PressedDefaultPct,
                    sd.Divider1Released >= 0 ? sd.Divider1Released : MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct,
                    sd.Divider2Released >= 0 ? sd.Divider2Released : MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct,
                    sd.Seg1Pressed >= 0 ? sd.Seg1Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    sd.Seg1Released >= 0 ? sd.Seg1Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    sd.Seg2Pressed >= 0 ? sd.Seg2Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    sd.Seg2Released >= 0 ? sd.Seg2Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    sd.Seg3Pressed >= 0 ? sd.Seg3Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    sd.Seg3Released >= 0 ? sd.Seg3Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    dev)));
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
