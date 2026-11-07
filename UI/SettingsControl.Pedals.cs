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
using MozaPlugin.Devices.MBooster;
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

        // ===== Pedals Tab =====

        // Presets: [Y1, Y2, Y3, Y4, Y5]
        private static readonly int[][] PedalCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        // Passive pedals an mBooster hosts (a CRP2 throttle/clutch behind an
        // active brake), indexed by role — see
        // MozaMBoosterRegistry.IsPedalsTabPassive. A group with an entry here
        // edits that pedal's mBooster config and writes mbooster-{role}-*
        // through its controller; a group without one drives CRP/SRP pedals.
        private (MBoosterDeviceController Controller, int Axis)?[] _passivePedals =
            new (MBoosterDeviceController, int)?[3];

        private static int PedalRoleIndex(string pedal) =>
            pedal == "throttle" ? 0 : pedal == "brake" ? 1 : pedal == "clutch" ? 2 : -1;

        private (MBoosterDeviceController Controller, int Axis)? PassivePedalFor(string pedal)
        {
            int role = PedalRoleIndex(pedal);
            return role >= 0 ? _passivePedals[role] : null;
        }

        private void RefreshPedalsTab()
        {
            // An mBooster on a base/hub pedal port answers as device 0x19 and so
            // latches PedalsDetected, but every pedals-* write lands on the
            // mBooster's own registers (same group/cmd bytes as mbooster-*) —
            // including the calibration routine, which would run against a
            // motorized pedal. So CRP/SRP mode only without a routed lane; the
            // passive pedals an mBooster hosts are shown instead.
            bool plainPedals = _plugin.IsPedalsDetected
                               && !(_plugin.MBoosterRegistry?.AnyRoutedPedalLane ?? false);
            _passivePedals = _plugin.MBoosterRegistry?.PedalsTabPassivePedals()
                             ?? new (MBoosterDeviceController, int)?[3];
            bool passive = _passivePedals[0] != null || _passivePedals[1] != null || _passivePedals[2] != null;
            bool detected = plainPedals || passive;
            PedalsTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected || !PedalsTab.IsSelected) return;

            ApplyPedalGroupAvailability(passive);
            if (passive)
            {
                SeedPassivePedal("throttle", ThrottleDirCheck, ThrottleRange, ThrottleRangeText, _throttleCurveSliders!);
                SeedPassivePedal("brake", BrakeDirCheck, BrakeRange, BrakeRangeText, _brakeCurveSliders!);
                SeedPassivePedal("clutch", ClutchDirCheck, ClutchRange, ClutchRangeText, _clutchCurveSliders!);
                return;
            }

            ThrottleDirCheck.IsChecked = _data.PedalsThrottleDir != 0;
            SeedPedalRange("throttle", ThrottleRange, ThrottleRangeText, _data.PedalsThrottleMin, _data.PedalsThrottleMax);
            SetDataSlider(ThrottleY1Slider, _data.PedalsThrottleCurve[0], 0, 100);
            SetDataSlider(ThrottleY2Slider, _data.PedalsThrottleCurve[1], 0, 100);
            SetDataSlider(ThrottleY3Slider, _data.PedalsThrottleCurve[2], 0, 100);
            SetDataSlider(ThrottleY4Slider, _data.PedalsThrottleCurve[3], 0, 100);
            SetDataSlider(ThrottleY5Slider, _data.PedalsThrottleCurve[4], 0, 100);

            BrakeDirCheck.IsChecked = _data.PedalsBrakeDir != 0;
            SeedPedalRange("brake", BrakeRange, BrakeRangeText, _data.PedalsBrakeMin, _data.PedalsBrakeMax);
            BrakeAngleRatioSlider.Value = Clamp(_data.PedalsBrakeAngleRatio, 0, 100);
            SetValueText(BrakeAngleRatioValue, $"{_data.PedalsBrakeAngleRatio}%");
            SetDataSlider(BrakeY1Slider, _data.PedalsBrakeCurve[0], 0, 100);
            SetDataSlider(BrakeY2Slider, _data.PedalsBrakeCurve[1], 0, 100);
            SetDataSlider(BrakeY3Slider, _data.PedalsBrakeCurve[2], 0, 100);
            SetDataSlider(BrakeY4Slider, _data.PedalsBrakeCurve[3], 0, 100);
            SetDataSlider(BrakeY5Slider, _data.PedalsBrakeCurve[4], 0, 100);

            ClutchDirCheck.IsChecked = _data.PedalsClutchDir != 0;
            SeedPedalRange("clutch", ClutchRange, ClutchRangeText, _data.PedalsClutchMin, _data.PedalsClutchMax);
            SetDataSlider(ClutchY1Slider, _data.PedalsClutchCurve[0], 0, 100);
            SetDataSlider(ClutchY2Slider, _data.PedalsClutchCurve[1], 0, 100);
            SetDataSlider(ClutchY3Slider, _data.PedalsClutchCurve[2], 0, 100);
            SetDataSlider(ClutchY4Slider, _data.PedalsClutchCurve[3], 0, 100);
            SetDataSlider(ClutchY5Slider, _data.PedalsClutchCurve[4], 0, 100);
        }

        /// <summary>
        /// Passive mode shows only the columns a passive pedal holds (the active
        /// pedal stays on the mBooster tab), and drops Sensor Ratio — a
        /// brake-named singleton register that would land on the active pedal.
        /// </summary>
        private void ApplyPedalGroupAvailability(bool passive)
        {
            ThrottlePedalGroup.Visibility = !passive || _passivePedals[0] != null ? Visibility.Visible : Visibility.Collapsed;
            BrakePedalGroup.Visibility = !passive || _passivePedals[1] != null ? Visibility.Visible : Visibility.Collapsed;
            ClutchPedalGroup.Visibility = !passive || _passivePedals[2] != null ? Visibility.Visible : Visibility.Collapsed;
            BrakeAngleRatioRow.Visibility = passive ? Visibility.Collapsed : Visibility.Visible;
            // A passive pedal's column edits its mBooster's settings entry —
            // read-only until that entry is final, same as the mBooster tab.
            ThrottlePedalGroup.IsEnabled = PassiveGroupEditable(0, passive);
            BrakePedalGroup.IsEnabled = PassiveGroupEditable(1, passive);
            ClutchPedalGroup.IsEnabled = PassiveGroupEditable(2, passive);
        }

        private bool PassiveGroupEditable(int role, bool passive)
        {
            if (!passive) return true;
            var t = _passivePedals[role];
            return t != null && _plugin.IsMBoosterSettingsResolved(t.Value.Controller);
        }

        /// <summary>
        /// Seed one passive pedal's group: the profile's override where set,
        /// else what the device last reported, else the CRP defaults.
        /// </summary>
        private void SeedPassivePedal(string pedal, System.Windows.Controls.Primitives.ToggleButton dir,
            MozaControls.MozaRangeSlider range, TextBlock rangeText, Slider[] curveSliders)
        {
            var target = PassivePedalFor(pedal);
            if (target == null) return;
            var c = target.Value.Controller;
            int axis = target.Value.Axis;
            var cfg = MozaMBoosterRegistry.PeekPedalConfig(
                _plugin.GetOrCreateMBoosterSettings(c.Identity), axis, c.SoleConnectedAxis());
            bool haveDev = c.TryCalibDeviceForAxis(axis, out byte dev);
            int Device(string field) => haveDev ? c.OutputRegisterValue(dev, $"mbooster-{pedal}-{field}") : -1;
            int Pick(int stored, string field, int fallback)
            {
                if (stored >= 0) return stored;
                int reported = Device(field);
                return reported >= 0 ? reported : fallback;
            }

            dir.IsChecked = Pick(cfg?.Direction ?? -1, "dir", 0) != 0;
            MozaMBoosterRegistry.ResolveOutputMapping(cfg, Device, out int lo, out int hi, out float[] curve);
            SeedPedalRange(pedal, range, rangeText, lo, hi);
            for (int i = 0; i < 5; i++)
                SetDataSlider(curveSliders[i], (int)Math.Round(curve[i]), 0, 100);
        }

        /// <summary>
        /// Persist one passive-pedal edit to its mBooster config and park the
        /// write on its unit — flash-backed, so a drag coalesces to one write
        /// (MBoosterDeviceController.QueueCalibWrite). The config is also what
        /// the connect-time apply replays.
        /// </summary>
        private void WritePassivePedal(string pedal, (MBoosterDeviceController Controller, int Axis) target,
            Action<IMBoosterPedalConfig> store, string field, Action<MBoosterDeviceController, byte> push)
        {
            var c = target.Controller;
            var cfg = MozaMBoosterRegistry.GetOrCreatePedalConfig(
                _plugin.GetOrCreateMBoosterSettings(c.Identity), target.Axis, c.SoleConnectedAxis());
            if (cfg == null) return;
            bool haveDev = c.TryCalibDeviceForAxis(target.Axis, out byte dev);
            // Curve and range are one setting; pin the rest as shown.
            if (field != "dir")
                MozaMBoosterRegistry.PinOutputMapping(cfg,
                    f => haveDev ? c.OutputRegisterValue(dev, $"mbooster-{pedal}-{f}") : -1);
            store(cfg);
            if (!haveDev) return;
            c.QueueCalibWrite($"{dev:x2}:mbooster-{pedal}-{field}", () => push(c, dev));
        }

        private void PedalDirClick(string pedal, System.Windows.Controls.Primitives.ToggleButton check, Action<int> plain)
        {
            if (_suppressEvents) return;
            int v = check.IsChecked == true ? 1 : 0;
            var target = PassivePedalFor(pedal);
            if (target == null) plain(v);
            else WritePassivePedal(pedal, target.Value, cfg => cfg.Direction = v, "dir",
                (c, dev) => c.SendIntWrite($"mbooster-{pedal}-dir", v, dev));
            _plugin.SaveSettings();
        }

        // Last seeded/committed bounds per role, so a range change writes only
        // the end that moved.
        private readonly int[] _pedalRangeLo = { 0, 0, 0 };
        private readonly int[] _pedalRangeHi = { 100, 100, 100 };

        private static string PedalRangeText(int lo, int hi) => $"{lo} – {hi}%";

        private void SeedPedalRange(string pedal, MozaControls.MozaRangeSlider range, TextBlock text, int lo, int hi)
        {
            int role = PedalRoleIndex(pedal);
            lo = (int)Clamp(lo, 0, 100);
            hi = (int)Clamp(hi, 0, 100);
            _pedalRangeLo[role] = lo;
            _pedalRangeHi[role] = hi;
            range.LowValue = lo;
            range.HighValue = hi;
            text.Text = PedalRangeText(lo, hi);
        }

        private void PedalRangeChanged(string pedal, MozaControls.MozaRangeSlider range, TextBlock text,
            Action<int> plainMin, Action<int> plainMax)
        {
            if (_suppressEvents) return;
            int role = PedalRoleIndex(pedal);
            int lo = (int)Math.Round(range.LowValue), hi = (int)Math.Round(range.HighValue);
            text.Text = PedalRangeText(lo, hi);
            bool minMoved = lo != _pedalRangeLo[role], maxMoved = hi != _pedalRangeHi[role];
            if (!minMoved && !maxMoved) return;
            _pedalRangeLo[role] = lo;
            _pedalRangeHi[role] = hi;

            var target = PassivePedalFor(pedal);
            if (minMoved) Commit("min", lo, plainMin);
            if (maxMoved) Commit("max", hi, plainMax);
            _plugin.SaveSettings();

            void Commit(string field, int v, Action<int> plain)
            {
                if (target == null) { plain(v); return; }
                bool isMin = field == "min";
                WritePassivePedal(pedal, target.Value, cfg => { if (isMin) cfg.Min = v; else cfg.Max = v; }, field,
                    (c, dev) => c.SendIntWrite($"mbooster-{pedal}-{field}", v, dev));
            }
        }

        private void PedalCurveChanged(string pedal, double newValue, Slider[] sliders, Action<int> plain)
        {
            var target = PassivePedalFor(pedal);
            OnIntSliderChanged(newValue, v =>
            {
                if (target == null) plain(v);
                else WritePassiveCurve(pedal, target.Value, sliders);
            });
        }

        /// <summary>All five nodes as one write set — the curve has no
        /// partial form worth keeping apart, and one key keeps a drag
        /// across nodes latest-wins.</summary>
        private void WritePassiveCurve(string pedal, (MBoosterDeviceController Controller, int Axis) target, Slider[] sliders)
        {
            var curve = new float[5];
            for (int i = 0; i < 5; i++) curve[i] = (float)Math.Round(sliders[i].Value);
            WritePassivePedal(pedal, target, cfg => cfg.HardwareCurveY = curve, "curve", (c, dev) =>
            {
                for (int i = 0; i < 5; i++)
                    c.SendFloatWrite($"mbooster-{pedal}-y{i + 1}", curve[i], dev);
            });
        }

        /// <summary>
        /// The pot calibration: CRP/SRP pedals over pedals-*, a passive pedal
        /// over its mBooster's mbooster-{role}-cal-start/-stop, both with the
        /// CRP pedals-bus param 1.
        /// </summary>
        private void StartPedalCalibration(string pedal, Button button, TextBlock status)
        {
            var target = PassivePedalFor(pedal);
            if (target == null)
            {
                RunCalibrationCountdown(button, status, Strings.Hint_CalibratePedal,
                    () => _plugin.HardwareApplier.WriteIfPedalsDetected($"pedals-{pedal}-cal-start", 1),
                    () => _plugin.HardwareApplier.WriteIfPedalsDetected($"pedals-{pedal}-cal-stop", 1));
                return;
            }

            var c = target.Value.Controller;
            int axis = target.Value.Axis;
            string? error = !c.IsConnected ? "device not connected"
                : !c.TryCalibDeviceForAxis(axis, out _) ? "pedal unit not resolved yet"
                // A motor routine reboots the whole unit mid-countdown.
                : (_plugin.MBoosterRegistry?.CalibrationRunnerOrNull?.Snapshot().IsRunning ?? false) ? "a calibration is already running"
                : null;
            if (error != null)
            {
                status.Text = string.Format(Strings.Status_CalibrationFailed, error);
                status.Visibility = Visibility.Visible;
                return;
            }
            RunCalibrationCountdown(button, status, Strings.Hint_CalibratePedal,
                () => { if (c.TryCalibDeviceForAxis(axis, out byte dev)) c.SendIntWrite($"mbooster-{pedal}-cal-start", 1, dev); },
                () => { if (c.TryCalibDeviceForAxis(axis, out byte dev)) c.SendIntWrite($"mbooster-{pedal}-cal-stop", 1, dev); });
        }

    }
}
