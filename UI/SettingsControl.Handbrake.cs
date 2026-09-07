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

        // ===== RPM range slider handlers =====

        // ===== Handbrake tab =====

        // ===== Handbrake Range + Curve + Calibration =====

        private void RefreshHandbrakeTab()
        {
            bool detected = _plugin.IsHandbrakeDetected;
            HandbrakeTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;

            if (!detected) return;

            SetComboSafe(HandbrakeModeCombo, _data.HandbrakeMode);

            bool buttonMode = _data.HandbrakeMode == 1;
            HandbrakeThresholdPanel.Visibility = buttonMode ? Visibility.Visible : Visibility.Collapsed;
            HandbrakeAxisPanel.Visibility = buttonMode ? Visibility.Collapsed : Visibility.Visible;
            HandbrakeButtonStatus.Visibility = buttonMode ? Visibility.Visible : Visibility.Collapsed;

            HandbrakeThresholdSlider.Value = Clamp(_data.HandbrakeButtonThreshold, 0, 100);
            SetValueText(HandbrakeThresholdValue, $"{_data.HandbrakeButtonThreshold}%");

            HandbrakeDirectionCheck.IsChecked = _data.HandbrakeDirection != 0;

            HandbrakeMinSlider.Value = Clamp(_data.HandbrakeMin, 0, 100);
            SetValueText(HandbrakeMinValue, $"{_data.HandbrakeMin}%");
            HandbrakeMaxSlider.Value = Clamp(_data.HandbrakeMax, 0, 100);
            SetValueText(HandbrakeMaxValue, $"{_data.HandbrakeMax}%");

            SetSliderRaw(HbY1Slider, HbY1Value, _data.HandbrakeCurve[0], 0, 100, "");
            SetSliderRaw(HbY2Slider, HbY2Value, _data.HandbrakeCurve[1], 0, 100, "");
            SetSliderRaw(HbY3Slider, HbY3Value, _data.HandbrakeCurve[2], 0, 100, "");
            SetSliderRaw(HbY4Slider, HbY4Value, _data.HandbrakeCurve[3], 0, 100, "");
            SetSliderRaw(HbY5Slider, HbY5Value, _data.HandbrakeCurve[4], 0, 100, "");
        }

        private void HandbrakeModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeModeCombo.SelectedIndex;
            _data.HandbrakeMode = val;
            _plugin.HardwareApplier.WriteIfHandbrakeDetected("handbrake-mode", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            HandbrakeThresholdValue.Text = $"{val}%";
            _data.HandbrakeButtonThreshold = val;
            _plugin.HardwareApplier.WriteIfHandbrakeDetected("handbrake-button-threshold", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeDirectionCheck.IsChecked == true ? 1 : 0;
            _data.HandbrakeDirection = val;
            _plugin.HardwareApplier.WriteIfHandbrakeDetected("handbrake-direction", val);
            _plugin.SaveSettings();
        }

        // ===== Handbrake Range + Curve + Calibration =====

        private static readonly int[][] HbCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void ApplyHbCurvePreset(int[] p)
        {
            using (_suppressor.Begin())
            {
                HbY1Slider.Value = p[0]; HbY1Value.Text = $"{p[0]}"; _data.HandbrakeCurve[0] = p[0];
                HbY2Slider.Value = p[1]; HbY2Value.Text = $"{p[1]}"; _data.HandbrakeCurve[1] = p[1];
                HbY3Slider.Value = p[2]; HbY3Value.Text = $"{p[2]}"; _data.HandbrakeCurve[2] = p[2];
                HbY4Slider.Value = p[3]; HbY4Value.Text = $"{p[3]}"; _data.HandbrakeCurve[3] = p[3];
                HbY5Slider.Value = p[4]; HbY5Value.Text = $"{p[4]}"; _data.HandbrakeCurve[4] = p[4];
            }
            for (int i = 0; i < 5; i++)
                _plugin.HardwareApplier.WriteFloatIfHandbrakeDetected($"handbrake-y{i + 1}", p[i]);
            _plugin.SaveSettings();
        }

        private void HbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[0]);
        private void HbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[1]);
        private void HbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[2]);
        private void HbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[3]);

        private void HandbrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, HandbrakeMinSlider, _data.HandbrakeMax, isMin: true, HandbrakeMinValue, v => { _data.HandbrakeMin = v; _plugin.HardwareApplier.WriteIfHandbrakeDetected("handbrake-min", v); });
        private void HandbrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, HandbrakeMaxSlider, _data.HandbrakeMin, isMin: false, HandbrakeMaxValue, v => { _data.HandbrakeMax = v; _plugin.HardwareApplier.WriteIfHandbrakeDetected("handbrake-max", v); });
        private void HbY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY1Value, "", v => { _data.HandbrakeCurve[0] = v; _plugin.HardwareApplier.WriteFloatIfHandbrakeDetected("handbrake-y1", v); });
        private void HbY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY2Value, "", v => { _data.HandbrakeCurve[1] = v; _plugin.HardwareApplier.WriteFloatIfHandbrakeDetected("handbrake-y2", v); });
        private void HbY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY3Value, "", v => { _data.HandbrakeCurve[2] = v; _plugin.HardwareApplier.WriteFloatIfHandbrakeDetected("handbrake-y3", v); });
        private void HbY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY4Value, "", v => { _data.HandbrakeCurve[3] = v; _plugin.HardwareApplier.WriteFloatIfHandbrakeDetected("handbrake-y4", v); });
        private void HbY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY5Value, "", v => { _data.HandbrakeCurve[4] = v; _plugin.HardwareApplier.WriteFloatIfHandbrakeDetected("handbrake-y5", v); });

        private const int CalibrationSeconds = 5;

        // Shared single-button calibration flow: send start, show a live
        // countdown instruction, then auto-send stop after CalibrationSeconds.
        private void RunCalibrationCountdown(Button startButton, TextBlock status,
            string instructionFormat, Action sendStart, Action sendStop)
        {
            sendStart();
            startButton.IsEnabled = false;
            int remaining = CalibrationSeconds;
            status.Text = string.Format(instructionFormat, remaining);
            status.Visibility = Visibility.Visible;

            // Held in a field so OnUnloadedStopTimers can stop it: a running
            // DispatcherTimer roots this control (and _plugin) until it fires.
            _calCountdownTimer?.Stop();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, _) =>
            {
                remaining--;
                if (remaining > 0)
                {
                    status.Text = string.Format(instructionFormat, remaining);
                    return;
                }
                ((DispatcherTimer)s!).Stop();
                if (ReferenceEquals(_calCountdownTimer, s)) _calCountdownTimer = null;
                sendStop();
                status.Text = Strings.Status_Done;
                startButton.IsEnabled = true;
            };
            _calCountdownTimer = timer;
            timer.Start();
        }

        private DispatcherTimer? _calCountdownTimer;

        private void HbCalStartButton_Click(object sender, RoutedEventArgs e) =>
            RunCalibrationCountdown(HbCalStartButton, HbCalStatus, Strings.Hint_CalibrateHandbrake,
                () => _plugin.HardwareApplier.WriteIfHandbrakeDetected("handbrake-cal-start", 1),
                () => _plugin.HardwareApplier.WriteIfHandbrakeDetected("handbrake-cal-stop", 1));

    }
}
