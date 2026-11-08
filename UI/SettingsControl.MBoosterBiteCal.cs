using System;
using System.Windows;
using System.Windows.Controls;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;
using static MozaPlugin.UI.UiHelpers;
using ClutchSource = MozaPlugin.Devices.MBooster.MBoosterBitePointSource;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Clutch bite point calibration section of the mBooster Clutch Bite Point card
    /// (per game; see MozaPlugin.MBoosterBitePoint.cs). Trigger Input Level shows
    /// the calibrated value while the calibration is in use, else the fallback.
    /// </summary>
    public partial class SettingsControl
    {

        /// <summary>Repaint from plugin state. Runs under the refresh tick's suppressor.</summary>
        private void RefreshMBoosterBiteCal()
        {
            var source = _plugin.BitePointSource;
            MBoosterBiteSource.SelectedIndex = (int)source;
            bool formula = source == ClutchSource.Formula;
            MBoosterBiteSourceFx.Visibility = formula ? Visibility.Visible : Visibility.Collapsed;
            string formulaText = _plugin.BitePointSourceFormula;
            MBoosterBiteSourceFormulaText.Text = formulaText;
            MBoosterBiteSourceFormulaText.Visibility = formula && formulaText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool hid = source == ClutchSource.HidAxis;
            if (hid && MBoosterBiteHidHost.Content == null) MBoosterBiteHidHost.Content = new HidAxisPicker();
            var hidVis = hid ? Visibility.Visible : Visibility.Collapsed;
            if (MBoosterBiteHidHost.Visibility != hidVis) MBoosterBiteHidHost.Visibility = hidVis;
            MBoosterBiteSourceValue.Text = _plugin.TryGetBitePointClutchPosition(out double clutch01)
                ? Math.Round(clutch01 * 100) + "%" : "—";

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

        private void MBoosterBiteSource_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || MBoosterBiteSource.SelectedIndex < 0) return;
            _plugin.BitePointSource = (ClutchSource)MBoosterBiteSource.SelectedIndex;
            using (_suppressor.Begin()) RefreshMBoosterBiteCal();
            if (_plugin.BitePointSource == ClutchSource.Formula && _plugin.BitePointSourceFormula.Length == 0)
                MBoosterBiteSourceFx_Click(sender, e);
        }

        private async void MBoosterBiteSourceFx_Click(object sender, RoutedEventArgs e)
        {
            string? formula = await FormulaEditing.EditAsync(this, _plugin.ChannelFormulaEngine,
                _plugin.BitePointSourceFormula, Strings.Label_ClutchSource);
            if (formula == null) return;
            _plugin.BitePointSourceFormula = formula;
            using (_suppressor.Begin()) RefreshMBoosterBiteCal();
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
