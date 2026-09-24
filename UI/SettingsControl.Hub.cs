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

        // ===== Hub Tab =====

        private void RefreshHubTab()
        {
            bool detected = _plugin.IsHubDetected;
            HubTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            // Pedals port: high byte >= 1 means connected (foxblat convention)
            UpdateHubPortIndicator(HubPedals1Dot, HubPedals1Label, _data.HubPedals1Power, isPedals: true);

            // Accessory ports: value <= 1 means connected
            UpdateHubPortIndicator(HubPort1Dot, HubPort1Label, _data.HubPort1Power, isPedals: false);
            UpdateHubPortIndicator(HubPort2Dot, HubPort2Label, _data.HubPort2Power, isPedals: false);
            UpdateHubPortIndicator(HubPort3Dot, HubPort3Label, _data.HubPort3Power, isPedals: false);
        }

        private static void UpdateHubPortIndicator(Ellipse dot, TextBlock label, int value, bool isPedals)
        {
            if (value < 0)
            {
                dot.Fill = Brushes.Gray;
                label.Text = "--";
                return;
            }

            bool connected = isPedals ? (value >> 8) >= 1 : value <= 1;
            dot.Fill = connected ? Brushes.LimeGreen : Brushes.Gray;
            label.Text = connected ? "Connected" : "Disconnected";
        }

        private void ApplyPedalCurvePreset(string pedal, int[] curve, int[] dataArray,
            Slider[] sliders, TextBox[] labels)
        {
            var passive = PassivePedalFor(pedal);
            using (_suppressor.Begin())
            {
                for (int i = 0; i < 5; i++)
                {
                    sliders[i].Value = curve[i];
                    labels[i].Text = $"{curve[i]}";
                    if (passive == null) dataArray[i] = curve[i];
                }
            }
            if (passive != null)
                WritePassiveCurve(pedal, passive.Value, sliders);
            else
                for (int i = 0; i < 5; i++)
                    _plugin.HardwareApplier.WriteFloatIfPedalsDetected($"pedals-{pedal}-y{i + 1}", curve[i]);
            _plugin.SaveSettings();
        }

        // Throttle presets
        private void ThrottleCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("throttle", PedalCurvePresets[0], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("throttle", PedalCurvePresets[1], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[2], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("throttle", PedalCurvePresets[3], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);

        // Brake presets
        private void BrakeCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("brake", PedalCurvePresets[0], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("brake", PedalCurvePresets[1], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[2], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("brake", PedalCurvePresets[3], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);

        // Clutch presets
        private void ClutchCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("clutch", PedalCurvePresets[0], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("clutch", PedalCurvePresets[1], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[2], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("clutch", PedalCurvePresets[3], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);

        // Throttle direction + range + curve sliders
        private void ThrottleDirCheck_Click(object sender, RoutedEventArgs e) => PedalDirClick("throttle", ThrottleDirCheck, v => { _data.PedalsThrottleDir = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-throttle-dir", v); });
        private void ThrottleMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalRangeChanged("throttle", isMin: true, e.NewValue, ThrottleMinSlider, ThrottleMaxSlider, _data.PedalsThrottleMax, ThrottleMinValue, v => { _data.PedalsThrottleMin = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-throttle-min", v); });
        private void ThrottleMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalRangeChanged("throttle", isMin: false, e.NewValue, ThrottleMaxSlider, ThrottleMinSlider, _data.PedalsThrottleMin, ThrottleMaxValue, v => { _data.PedalsThrottleMax = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-throttle-max", v); });
        private void ThrottleY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("throttle", e.NewValue, ThrottleY1Value, _throttleCurveSliders!, v => { _data.PedalsThrottleCurve[0] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-throttle-y1", v); });
        private void ThrottleY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("throttle", e.NewValue, ThrottleY2Value, _throttleCurveSliders!, v => { _data.PedalsThrottleCurve[1] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-throttle-y2", v); });
        private void ThrottleY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("throttle", e.NewValue, ThrottleY3Value, _throttleCurveSliders!, v => { _data.PedalsThrottleCurve[2] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-throttle-y3", v); });
        private void ThrottleY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("throttle", e.NewValue, ThrottleY4Value, _throttleCurveSliders!, v => { _data.PedalsThrottleCurve[3] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-throttle-y4", v); });
        private void ThrottleY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("throttle", e.NewValue, ThrottleY5Value, _throttleCurveSliders!, v => { _data.PedalsThrottleCurve[4] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-throttle-y5", v); });

        // Throttle calibration
        private void ThrottleCalStartButton_Click(object sender, RoutedEventArgs e) =>
            StartPedalCalibration("throttle", ThrottleCalStartButton, ThrottleCalStatus);

        // Brake direction + range + curve sliders
        private void BrakeDirCheck_Click(object sender, RoutedEventArgs e) => PedalDirClick("brake", BrakeDirCheck, v => { _data.PedalsBrakeDir = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-brake-dir", v); });
        private void BrakeAngleRatioSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeAngleRatioValue.Text = $"{v}%"; _data.PedalsBrakeAngleRatio = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-brake-angle-ratio", v); _plugin.SaveSettings(); }
        private void BrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalRangeChanged("brake", isMin: true, e.NewValue, BrakeMinSlider, BrakeMaxSlider, _data.PedalsBrakeMax, BrakeMinValue, v => { _data.PedalsBrakeMin = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-brake-min", v); });
        private void BrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalRangeChanged("brake", isMin: false, e.NewValue, BrakeMaxSlider, BrakeMinSlider, _data.PedalsBrakeMin, BrakeMaxValue, v => { _data.PedalsBrakeMax = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-brake-max", v); });
        private void BrakeY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("brake", e.NewValue, BrakeY1Value, _brakeCurveSliders!, v => { _data.PedalsBrakeCurve[0] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-brake-y1", v); });
        private void BrakeY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("brake", e.NewValue, BrakeY2Value, _brakeCurveSliders!, v => { _data.PedalsBrakeCurve[1] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-brake-y2", v); });
        private void BrakeY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("brake", e.NewValue, BrakeY3Value, _brakeCurveSliders!, v => { _data.PedalsBrakeCurve[2] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-brake-y3", v); });
        private void BrakeY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("brake", e.NewValue, BrakeY4Value, _brakeCurveSliders!, v => { _data.PedalsBrakeCurve[3] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-brake-y4", v); });
        private void BrakeY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("brake", e.NewValue, BrakeY5Value, _brakeCurveSliders!, v => { _data.PedalsBrakeCurve[4] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-brake-y5", v); });

        // Brake calibration
        private void BrakeCalStartButton_Click(object sender, RoutedEventArgs e) =>
            StartPedalCalibration("brake", BrakeCalStartButton, BrakeCalStatus);

        // Clutch direction + range + curve sliders
        private void ClutchDirCheck_Click(object sender, RoutedEventArgs e) => PedalDirClick("clutch", ClutchDirCheck, v => { _data.PedalsClutchDir = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-clutch-dir", v); });
        private void ClutchMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalRangeChanged("clutch", isMin: true, e.NewValue, ClutchMinSlider, ClutchMaxSlider, _data.PedalsClutchMax, ClutchMinValue, v => { _data.PedalsClutchMin = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-clutch-min", v); });
        private void ClutchMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalRangeChanged("clutch", isMin: false, e.NewValue, ClutchMaxSlider, ClutchMinSlider, _data.PedalsClutchMin, ClutchMaxValue, v => { _data.PedalsClutchMax = v; _plugin.HardwareApplier.WriteIfPedalsDetected("pedals-clutch-max", v); });
        private void ClutchY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("clutch", e.NewValue, ClutchY1Value, _clutchCurveSliders!, v => { _data.PedalsClutchCurve[0] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-clutch-y1", v); });
        private void ClutchY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("clutch", e.NewValue, ClutchY2Value, _clutchCurveSliders!, v => { _data.PedalsClutchCurve[1] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-clutch-y2", v); });
        private void ClutchY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("clutch", e.NewValue, ClutchY3Value, _clutchCurveSliders!, v => { _data.PedalsClutchCurve[2] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-clutch-y3", v); });
        private void ClutchY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("clutch", e.NewValue, ClutchY4Value, _clutchCurveSliders!, v => { _data.PedalsClutchCurve[3] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-clutch-y4", v); });
        private void ClutchY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => PedalCurveChanged("clutch", e.NewValue, ClutchY5Value, _clutchCurveSliders!, v => { _data.PedalsClutchCurve[4] = v; _plugin.HardwareApplier.WriteFloatIfPedalsDetected("pedals-clutch-y5", v); });

        // Clutch calibration
        private void ClutchCalStartButton_Click(object sender, RoutedEventArgs e) =>
            StartPedalCalibration("clutch", ClutchCalStartButton, ClutchCalStatus);

    }
}
