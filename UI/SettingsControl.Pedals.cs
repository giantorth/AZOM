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

        // ===== Pedals Tab =====

        // Presets: [Y1, Y2, Y3, Y4, Y5]
        private static readonly int[][] PedalCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void RefreshPedalsTab()
        {
            // An mBooster on a base/hub pedal port answers as device 0x19 and so
            // latches PedalsDetected, but every control on this tab writes the
            // same group/cmd bytes as the mbooster-* set — including the
            // calibration routine, which would run against a motorized pedal.
            // Its own card is the correct surface, so hide this one entirely.
            bool detected = _plugin.IsPedalsDetected
                            && !(_plugin.MBoosterRegistry?.AnyRoutedPedalLane ?? false);
            PedalsTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            ThrottleDirCheck.IsChecked = _data.PedalsThrottleDir != 0;
            ThrottleMinSlider.Value = Clamp(_data.PedalsThrottleMin, 0, 100);
            SetValueText(ThrottleMinValue, $"{_data.PedalsThrottleMin}%");
            ThrottleMaxSlider.Value = Clamp(_data.PedalsThrottleMax, 0, 100);
            SetValueText(ThrottleMaxValue, $"{_data.PedalsThrottleMax}%");
            SetSliderRaw(ThrottleY1Slider, ThrottleY1Value, _data.PedalsThrottleCurve[0], 0, 100, "");
            SetSliderRaw(ThrottleY2Slider, ThrottleY2Value, _data.PedalsThrottleCurve[1], 0, 100, "");
            SetSliderRaw(ThrottleY3Slider, ThrottleY3Value, _data.PedalsThrottleCurve[2], 0, 100, "");
            SetSliderRaw(ThrottleY4Slider, ThrottleY4Value, _data.PedalsThrottleCurve[3], 0, 100, "");
            SetSliderRaw(ThrottleY5Slider, ThrottleY5Value, _data.PedalsThrottleCurve[4], 0, 100, "");

            BrakeDirCheck.IsChecked = _data.PedalsBrakeDir != 0;
            BrakeMinSlider.Value = Clamp(_data.PedalsBrakeMin, 0, 100);
            SetValueText(BrakeMinValue, $"{_data.PedalsBrakeMin}%");
            BrakeMaxSlider.Value = Clamp(_data.PedalsBrakeMax, 0, 100);
            SetValueText(BrakeMaxValue, $"{_data.PedalsBrakeMax}%");
            BrakeAngleRatioSlider.Value = Clamp(_data.PedalsBrakeAngleRatio, 0, 100);
            SetValueText(BrakeAngleRatioValue, $"{_data.PedalsBrakeAngleRatio}%");
            SetSliderRaw(BrakeY1Slider, BrakeY1Value, _data.PedalsBrakeCurve[0], 0, 100, "");
            SetSliderRaw(BrakeY2Slider, BrakeY2Value, _data.PedalsBrakeCurve[1], 0, 100, "");
            SetSliderRaw(BrakeY3Slider, BrakeY3Value, _data.PedalsBrakeCurve[2], 0, 100, "");
            SetSliderRaw(BrakeY4Slider, BrakeY4Value, _data.PedalsBrakeCurve[3], 0, 100, "");
            SetSliderRaw(BrakeY5Slider, BrakeY5Value, _data.PedalsBrakeCurve[4], 0, 100, "");

            ClutchDirCheck.IsChecked = _data.PedalsClutchDir != 0;
            ClutchMinSlider.Value = Clamp(_data.PedalsClutchMin, 0, 100);
            SetValueText(ClutchMinValue, $"{_data.PedalsClutchMin}%");
            ClutchMaxSlider.Value = Clamp(_data.PedalsClutchMax, 0, 100);
            SetValueText(ClutchMaxValue, $"{_data.PedalsClutchMax}%");
            SetSliderRaw(ClutchY1Slider, ClutchY1Value, _data.PedalsClutchCurve[0], 0, 100, "");
            SetSliderRaw(ClutchY2Slider, ClutchY2Value, _data.PedalsClutchCurve[1], 0, 100, "");
            SetSliderRaw(ClutchY3Slider, ClutchY3Value, _data.PedalsClutchCurve[2], 0, 100, "");
            SetSliderRaw(ClutchY4Slider, ClutchY4Value, _data.PedalsClutchCurve[3], 0, 100, "");
            SetSliderRaw(ClutchY5Slider, ClutchY5Value, _data.PedalsClutchCurve[4], 0, 100, "");
        }

    }
}
