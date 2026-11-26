using System;
using System.Windows;
using System.Windows.Controls;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;
using static MozaPlugin.UI.UiHelpers;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Clutch bite point calibration section of the mBooster Clutch Bite Point card
    /// (per game; see MozaPlugin.MBoosterBitePoint.cs). No clutch source picker:
    /// the card's effect reads the mBooster clutch (MozaPlugin.BitePointSource).
    /// Trigger Input Level shows the calibrated value while the calibration is in
    /// use, else the fallback.
    /// </summary>
    public partial class SettingsControl
    {

        /// <summary>Repaint from plugin state. Runs under the refresh tick's suppressor.</summary>
        private void RefreshMBoosterBiteCal()
        {
            var info = _plugin.ClutchBitePointInfo();
            bool running = _plugin.IsBitePointCalibrationRunning;
            MBoosterBiteCalState.Text = _plugin.BitePointStatusText(info);
            MBoosterBiteCalButton.Content = running ? Strings.Button_Cancel : Strings.Button_Calibrate;
            MBoosterBiteCalResetButton.IsEnabled = (info.IsCalibrated && !info.Inherited) || running;
            MBoosterBiteCalAuto.IsChecked = _plugin.BitePointAutoCalibrate;

            var vis = info.IsCalibrated ? Visibility.Visible : Visibility.Collapsed;
            if (MBoosterBiteCalValuePanel.Visibility != vis) MBoosterBiteCalValuePanel.Visibility = vis;

            var fx = PeekMBoosterEffectTarget();
            bool showsCalibration = BiteTriggerShowsCalibration(fx, out _);
            int shown = showsCalibration ? info.Pct : fx?.BitePoint?.TriggerLevelPct ?? 50;
            // The calibrated value is read-only here; Manual makes it editable.
            MBoosterBitePointTriggerLevel.IsEnabled = !showsCalibration;
            MBoosterBitePointTriggerLevelValue.IsEnabled = !showsCalibration;
            // Don't fight a drag.
            if ((int)Math.Round(MBoosterBitePointTriggerLevel.Value) != shown && !MBoosterBitePointTriggerLevel.IsMouseCaptureWithin)
            {
                MBoosterBitePointTriggerLevel.Value = shown;
                SetValueText(MBoosterBitePointTriggerLevelValue, shown.ToString());
            }
        }

        /// <summary>True when the effect triggers at the current game's calibration,
        /// so Trigger Input Level shows and edits that instead of the fallback.</summary>
        private bool BiteTriggerShowsCalibration(IMBoosterEffects? fx, out string game)
        {
            game = _plugin.CurrentGameName ?? "";
            return (fx?.BitePoint?.UseCalibratedBitePoint ?? true)
                   && game.Length > 0
                   && _plugin.ClutchBitePointInfo().IsCalibrated;
        }

        private void MBoosterBiteCal_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ToggleBitePointCalibration();
        }

        private void MBoosterBiteCalReset_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ResetClutchBitePoint();
            using (_suppressor.Begin()) RefreshMBoosterBiteCal();
        }

        private void MBoosterBiteCalAuto_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.BitePointAutoCalibrate = MBoosterBiteCalAuto.IsChecked == true;
        }

        private void MBoosterBitePointSource_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || MBoosterBitePointSource.SelectedIndex < 0) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.BitePoint ??= new MBoosterEffectSettings()).UseCalibratedBitePoint = MBoosterBitePointSource.SelectedIndex == 0;
            _plugin.SaveSettings();
            using (_suppressor.Begin()) RefreshMBoosterBiteCal();
        }
    }
}
