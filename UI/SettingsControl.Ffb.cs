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
using MozaPlugin.Settings;

namespace MozaPlugin.UI
{
    public partial class SettingsControl : UserControl
    {

        // ===== FFB Equalizer handlers =====

        // EQ write commands in register order. Shared with the AZOM step
        // actions so the button macros and the bindings drive identical values.
        private static readonly string[] EqCommands = BaseSettingCatalog.EqRegisterCommands;

        private void Eq1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq1Value, "%", v => { _data.Equalizer1 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[0], v); });
        private void Eq2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq2Value, "%", v => { _data.Equalizer2 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[1], v); });
        private void Eq3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq3Value, "%", v => { _data.Equalizer3 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[2], v); });
        private void Eq4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq4Value, "%", v => { _data.Equalizer4 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[3], v); });
        private void Eq5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq5Value, "%", v => { _data.Equalizer5 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[4], v); });
        private void Eq6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq6Value, "%", v => { _data.Equalizer6 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[5], v); });
        private void Eq7Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq7Value, "%", v => { _data.Equalizer7 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[6], v); });
        private void Eq8Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq8Value, "%", v => { _data.Equalizer8 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[7], v); });
        private void Eq9Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq9Value, "%", v => { _data.Equalizer9 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[8], v); });
        private void Eq10Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq10Value, "%", v => { _data.Equalizer10 = v; _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[9], v); });

        // 10-band mappings in FREQUENCY order (5/10/15/25/30/40/50/60/80/100 Hz)
        // — the new registers interleave. Keep in sync with the FfbEqualizer10
        // slider binding in SettingsControl.Redesign.cs.
        private static readonly string[] Eq10Commands = BaseSettingCatalog.Eq10FreqOrderCommands;
        private Slider[] Eq10Sliders() => new[] {
            Eq1Slider, Eq7Slider, Eq2Slider, Eq3Slider, Eq8Slider,
            Eq4Slider, Eq9Slider, Eq5Slider, Eq10Slider, Eq6Slider };
        private TextBox[] Eq10Labels() => new[] {
            Eq1Value, Eq7Value, Eq2Value, Eq3Value, Eq8Value,
            Eq4Value, Eq9Value, Eq5Value, Eq10Value, Eq6Value };
        private Action<int>[] Eq10DataSetters() => new Action<int>[] {
            v => _data.Equalizer1 = v, v => _data.Equalizer7 = v,
            v => _data.Equalizer2 = v, v => _data.Equalizer3 = v,
            v => _data.Equalizer8 = v, v => _data.Equalizer4 = v,
            v => _data.Equalizer9 = v, v => _data.Equalizer5 = v,
            v => _data.Equalizer10 = v, v => _data.Equalizer6 = v };

        // Swap the EQ card between the 6-band and 10-band presentations. Runs
        // inside the refresh tick's suppressor, so the slider Maximum coercion
        // on a mode flip never reaches the device-write path.
        private bool? _eq10ModeApplied;
        private void ApplyEqBandMode(bool eq10)
        {
            if (_eq10ModeApplied == eq10) return;
            _eq10ModeApplied = eq10;
            FfbEqualizer.Visibility = eq10 ? Visibility.Collapsed : Visibility.Visible;
            FfbEqualizer10.Visibility = eq10 ? Visibility.Visible : Visibility.Collapsed;
            Eq1Slider.Maximum = eq10 ? 500 : 400;
            Eq2Slider.Maximum = eq10 ? 500 : 400;
            Eq3Slider.Maximum = eq10 ? 500 : 400;
            Eq4Slider.Maximum = eq10 ? 500 : 400;
            Eq5Slider.Maximum = eq10 ? 500 : 400;
            Eq6Slider.Maximum = eq10 ? 100 : 400;
            FfbEqCard.Subtitle = eq10 ? Strings.Subtitle_FfbEqualizer10 : Strings.Subtitle_FfbEqualizer;
        }

        // Apply a 6-band value set (10/15/25/40/60/100 Hz register order).
        private void ApplyFfbEqPreset(int[] p)
        {
            using (_suppressor.Begin())
            {
                Eq1Slider.Value = p[0]; Eq1Value.Text = $"{p[0]}%"; _data.Equalizer1 = p[0];
                Eq2Slider.Value = p[1]; Eq2Value.Text = $"{p[1]}%"; _data.Equalizer2 = p[1];
                Eq3Slider.Value = p[2]; Eq3Value.Text = $"{p[2]}%"; _data.Equalizer3 = p[2];
                Eq4Slider.Value = p[3]; Eq4Value.Text = $"{p[3]}%"; _data.Equalizer4 = p[3];
                Eq5Slider.Value = p[4]; Eq5Value.Text = $"{p[4]}%"; _data.Equalizer5 = p[4];
                Eq6Slider.Value = p[5]; Eq6Value.Text = $"{p[5]}%"; _data.Equalizer6 = p[5];
            }
            for (int i = 0; i < 6; i++)
                _plugin.HardwareApplier.WriteIfBaseConnected(EqCommands[i], p[i]);
            _plugin.SaveSettings();
        }

        private void ApplyFfbEqPreset10(int[] p)
        {
            var sliders = Eq10Sliders();
            var labels = Eq10Labels();
            var setters = Eq10DataSetters();
            using (_suppressor.Begin())
            {
                for (int i = 0; i < 10; i++)
                {
                    sliders[i].Value = p[i];
                    labels[i].Text = $"{p[i]}%";
                    setters[i](p[i]);
                }
            }
            for (int i = 0; i < 10; i++)
                _plugin.HardwareApplier.WriteIfBaseConnected(Eq10Commands[i], p[i]);
            _plugin.SaveSettings();
        }

        // PitHouse "sensitivity" presets 0..10 — one-shot macros writing
        // road-sensitivity (0x0C = 10 + 4*N) plus a canned EQ curve; no
        // dedicated sensitivity register exists, so the buttons are momentary.
        // Values in frequency order 5/10/15/25/30/40/50/60/80/100 Hz. On
        // legacy firmware only the six old registers are written (columns
        // via Eq6FreqColumns) — the four new bands are skipped.
        private static readonly int[][] EqSensitivityPresets = BaseSettingCatalog.EqSensitivityPresets;

        // Frequency-order columns carried by the legacy registers Eq1..Eq6
        // (5/15/25/40/60/100 Hz).
        private static readonly int[] Eq6FreqColumns = BaseSettingCatalog.Eq6FreqColumns;

        private void EqSensitivity_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !int.TryParse(b.Tag as string, out int n)
                || n < 0 || n > 10)
                return;

            int sensitivity = 10 + 4 * n;
            _data.RoadSensitivity = sensitivity;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-road-sensitivity", sensitivity);

            int[] p = EqSensitivityPresets[n];
            if (_data.BaseSupportsEq10)
            {
                ApplyFfbEqPreset10(p);
            }
            else
            {
                var six = new int[6];
                for (int i = 0; i < 6; i++) six[i] = p[Eq6FreqColumns[i]];
                ApplyFfbEqPreset(six);
            }
        }

        // ===== FFB Curve handlers =====

        // Presets: [Y1, Y2, Y3, Y4, Y5] — X breakpoints are fixed at [20, 40, 60, 80]
        private static readonly int[][] FfbCurvePresets =
        {
            new[] { 20, 40, 60, 80, 100 }, // Linear
            new[] {  8, 24, 76, 92, 100 }, // S-Curve
            new[] {  6, 14, 28, 54, 100 }, // Exponential
            new[] { 46, 72, 86, 94, 100 }, // Parabolic
        };

        private void ApplyFfbCurvePreset(int[] p)
        {
            using (_suppressor.Begin())
            {
                // Presets are Y-shapes defined at the standard breakpoints, so
                // snap any dragged X positions back to 20/40/60/80.
                FfbCurveX1Slider.Value = 20; FfbCurveX1Value.Text = "20"; _data.FfbCurveX1 = 20;
                FfbCurveX2Slider.Value = 40; FfbCurveX2Value.Text = "40"; _data.FfbCurveX2 = 40;
                FfbCurveX3Slider.Value = 60; FfbCurveX3Value.Text = "60"; _data.FfbCurveX3 = 60;
                FfbCurveX4Slider.Value = 80; FfbCurveX4Value.Text = "80"; _data.FfbCurveX4 = 80;
                FfbCurveY1Slider.Value = p[0]; FfbCurveY1Value.Text = $"{p[0]}"; _data.FfbCurveY1 = p[0];
                FfbCurveY2Slider.Value = p[1]; FfbCurveY2Value.Text = $"{p[1]}"; _data.FfbCurveY2 = p[1];
                FfbCurveY3Slider.Value = p[2]; FfbCurveY3Value.Text = $"{p[2]}"; _data.FfbCurveY3 = p[2];
                FfbCurveY4Slider.Value = p[3]; FfbCurveY4Value.Text = $"{p[3]}"; _data.FfbCurveY4 = p[3];
                FfbCurveY5Slider.Value = p[4]; FfbCurveY5Value.Text = $"{p[4]}"; _data.FfbCurveY5 = p[4];
            }
            // Always write fixed X breakpoints first
            _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x1", 20); _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x2", 40);
            _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x3", 60); _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x4", 80);
            _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y1", p[0]); _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y2", p[1]);
            _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y3", p[2]); _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y4", p[3]);
            _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y5", p[4]);
            _plugin.SaveSettings();
        }

        private void FfbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[0]);
        private void FfbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[1]);
        private void FfbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[2]);
        private void FfbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[3]);

        private void FfbCurveX1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX1Value, "", v => { _data.FfbCurveX1 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x1", v); });
        private void FfbCurveX2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX2Value, "", v => { _data.FfbCurveX2 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x2", v); });
        private void FfbCurveX3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX3Value, "", v => { _data.FfbCurveX3 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x3", v); });
        private void FfbCurveX4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX4Value, "", v => { _data.FfbCurveX4 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-x4", v); });
        private void FfbCurveY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY1Value, "", v => { _data.FfbCurveY1 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y1", v); });
        private void FfbCurveY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY2Value, "", v => { _data.FfbCurveY2 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y2", v); });
        private void FfbCurveY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY3Value, "", v => { _data.FfbCurveY3 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y3", v); });
        private void FfbCurveY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY4Value, "", v => { _data.FfbCurveY4 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y4", v); });
        private void FfbCurveY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY5Value, "", v => { _data.FfbCurveY5 = v; _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-curve-y5", v); });

        // ===== Bluetooth + Base Calibration =====

        private void BluetoothCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = BluetoothCheck.IsChecked == true ? 0 : 85;
            _data.BleMode = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-ble-mode", val);
            _plugin.SaveSettings();
        }

        private void BaseCalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.HardwareApplier.WriteIfBaseConnected("base-calibration", 1);
            BaseCalibrateStatus.Text = Strings.Status_CalibrationSent;
            _baseCalStatusTimer?.Stop();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, _) =>
            {
                BaseCalibrateStatus.Text = "";
                ((DispatcherTimer)s!).Stop();
                if (ReferenceEquals(_baseCalStatusTimer, s)) _baseCalStatusTimer = null;
            };
            _baseCalStatusTimer = timer;   // stopped by OnUnloadedStopTimers
            timer.Start();
        }

        private DispatcherTimer? _baseCalStatusTimer;

    }
}
