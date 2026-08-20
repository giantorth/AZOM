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

        // ===== Base tab slider handlers =====
        // Each handler writes to device AND updates _data so the refresh timer doesn't revert.

        private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int deg = (int)Math.Round(e.NewValue);
            // Expression: /2 (display degrees → raw)
            int raw = deg / 2;
            RotationValue.Text = $"{deg}°";
            _data.Limit = raw;
            _data.MaxAngle = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-limit", raw);
            _plugin.HardwareApplier.WriteIfBaseConnected("base-max-angle", raw);
            _plugin.SaveSettings();

            // Diagnostic — confirms the value reached the active profile.
            // CaptureFromCurrent inside SaveSettings should have set
            // profile.Limit from _data.Limit; the post-capture value lets us
            // verify the persist path on future bug reports. Debug-level so
            // it doesn't spam SimHub.txt during slider drags (~100 ticks per
            // full sweep with the 10° snap); the in-process MozaLog ring
            // buffer still records it for the Diagnostics export bundle.
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            MozaLog.Debug(
                $"[AZOM] Rotation slider → {deg}° (raw={raw}); " +
                $"active profile='{profile?.Name ?? "(none)"}', " +
                $"profile.Limit={profile?.Limit.ToString() ?? "n/a"}, " +
                $"baseConnected={_data.IsBaseConnected}");

            // Restart the readback debounce so it fires once, after the last
            // tick of a drag.
            _rotationLastWrittenDeg = deg;
            _rotationReadbackLogPhase = false;
            _rotationReadbackTimer.Stop();
            _rotationReadbackTimer.Interval = TimeSpan.FromMilliseconds(400);
            _rotationReadbackTimer.Start();
        }

        private void OnRotationReadbackTick(object? sender, EventArgs e)
        {
            if (!_rotationReadbackLogPhase)
            {
                // Phase 1: ask the base what it actually stored. The replies
                // land in _data.Limit/_data.MaxAngle and the refresh tick
                // snaps the slider to device truth.
                _plugin.HardwareApplier.ReadIfBaseConnected("base-limit");
                _plugin.HardwareApplier.ReadIfBaseConnected("base-max-angle");
                _rotationReadbackLogPhase = true;
                _rotationReadbackTimer.Interval = TimeSpan.FromMilliseconds(500);
                return;
            }

            _rotationReadbackTimer.Stop();
            int reportedDeg = _data.Limit * 2;
            if (reportedDeg == _rotationLastWrittenDeg)
                MozaLog.Debug($"[AZOM] Rotation readback: device kept {reportedDeg}°");
            else
                MozaLog.Info($"[AZOM] Rotation readback: wrote {_rotationLastWrittenDeg}°, device reports {reportedDeg}° (firmware clamp)");
        }

        private void FfbStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FfbStrengthValue.Text = $"{pct}%";
            _data.FfbStrength = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-strength", raw);
            _plugin.SaveSettings();
        }

        private void InterpolationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int display = (int)Math.Round(e.NewValue);   // 0-10
            int raw = display * 10;                       // wire value 0-100
            InterpolationValue.Text = $"{display}";
            _data.Interpolation = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-interpolation", raw);
            _plugin.SaveSettings();
        }

        private void TorqueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            TorqueValue.Text = $"{val}%";
            _data.Torque = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-torque", val);
            _plugin.SaveSettings();
        }

        private void PerformanceOutputCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = PerformanceOutputCombo.SelectedIndex;
            if (val < 0) return;
            _data.TempStrategy = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-temp-strategy", val);
            _plugin.SaveSettings();
        }

        private void GearshiftVibrationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            GearshiftVibrationValue.Text = val.ToString();
            _data.GearshiftVibration = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-gearshift-vibration", val);
            _plugin.SaveSettings();
        }

        private void GearshiftVibrateOnNeutralCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = GearshiftVibrateOnNeutralCheck.IsChecked == true;
            _plugin.UpdateActiveProfile(p => p.GearshiftVibrateOnNeutral = on ? 1 : 0);
            _plugin.SaveSettings();
        }

        private void GearshiftDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            // Snap to 50 ms grid (IsSnapToTickEnabled + TickFrequency=50 already
            // enforces this on user input, but be defensive against external
            // sources that bypass the tick grid).
            val = ((val + 25) / 50) * 50;
            if (val < 0) val = 0;
            if (val > 1000) val = 1000;
            GearshiftDebounceValue.Text = $"{val} ms";
            _plugin.UpdateActiveProfile(p => p.GearshiftDebounceMs = val);
            _plugin.SaveSettings();
        }

        // ===== Checkbox handlers =====

        private void FfbReverseCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = FfbReverseCheck.IsChecked == true ? 1 : 0;
            _data.FfbReverse = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-ffb-reverse", val);
            _plugin.SaveSettings();
        }

        private void ProtectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = ProtectionCheck.IsChecked == true ? 1 : 0;
            _data.Protection = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-protection", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitRetainCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = SoftLimitRetainCheck.IsChecked == true ? 1 : 0;
            _data.SoftLimitRetain = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-soft-limit-retain", val);
            _plugin.SaveSettings();
        }

        private void StandbyCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StandbyCheck.IsChecked == true ? 1 : 0;
            _data.WorkMode = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-work-mode", val);
            _plugin.SaveSettings();
        }

        private void AutoStandbyTimeoutCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (!(AutoStandbyTimeoutCombo.SelectedItem is ComboBoxItem item
                  && item.Tag is string tag && int.TryParse(tag, out int minutes)))
                return;

            if (minutes <= 0)
            {
                // "Disabled" — turn auto-standby off and wake the base if we put
                // it to sleep. The timeout value is left as-is for next time.
                _plugin.Settings.AutoStandbyWhenNoGame = false;
                _plugin.SaveSettings();
                _plugin.Standby?.Cancel();
            }
            else
            {
                _plugin.Settings.AutoStandbyWhenNoGame = true;
                _plugin.Settings.AutoStandbyTimeoutMinutes = minutes;
                _plugin.SaveSettings();
                // Selecting a timeout counts as activity so we never standby
                // immediately; the idle timer starts fresh from here.
                _plugin.Standby?.NotifyUserActivity();
                _plugin.Standby?.Apply();
            }
        }

        // Selects "Disabled" when auto-standby is off, else the saved timeout.
        private void SyncAutoStandbyCombo()
        {
            int target = _plugin.Settings.AutoStandbyWhenNoGame
                ? _plugin.Settings.AutoStandbyTimeoutMinutes
                : 0;
            foreach (var obj in AutoStandbyTimeoutCombo.Items)
            {
                if (obj is ComboBoxItem it && it.Tag is string t
                    && int.TryParse(t, out int m) && m == target)
                {
                    AutoStandbyTimeoutCombo.SelectedItem = it;
                    return;
                }
            }
        }

        private void LedStatusCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = LedStatusCheck.IsChecked == true ? 1 : 0;
            _data.LedStatus = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-led-status", val);
            _plugin.SaveSettings();
        }

    }
}
