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

namespace MozaPlugin
{
    public partial class SettingsControl : UserControl
    {

        // ===== Connection toggle =====

        private void ConnectionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.SetConnectionEnabled(ConnectionToggle.IsChecked == true);
        }

        private void SoftRebootButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Strings.Dialog_RestartWheelbase_Body,
                Strings.Dialog_RestartWheelbase_Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-soft-reboot", 1);
        }

        // ===== Options tab =====

        private void AutoApplyProfileCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AutoApplyProfileOnLaunch = AutoApplyProfileCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void LimitWheelUpdatesCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.LimitWheelUpdates = LimitWheelUpdatesCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void AlwaysResendBitmaskCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AlwaysResendBitmask = AlwaysResendBitmaskCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void KeepaliveTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int sec = (int)Math.Round(e.NewValue);
            KeepaliveTimeoutValue.Text = $"{sec} s";
            _plugin.Settings.WheelKeepaliveTimeoutSec = sec;
            _plugin.SaveSettings();
        }

        private void DisableSerialProbeFallbackCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.DisableSerialProbeFallback = DisableSerialProbeFallbackCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void DisableAb9DetectionCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.DisableAb9Detection = DisableAb9DetectionCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void RedeployDefinitionsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Strings.Dialog_RedeployDefinitions_Body,
                Strings.Dialog_RedeployDefinitions_Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            var wheelbasePid = DeviceDefinitionDeployer.ResolveWheelbasePid(_plugin.Connection);
            var deployed = DeviceDefinitionDeployer.DeployAllKnown(
                wheelbasePid, DeviceDefinitionDeployer.ResolveDashboardPid(wheelbasePid));

            if (deployed.Written > 0)
                _plugin.DeviceDefinitionDeployed = true;

            RedeployDefinitionsStatusText.Text = string.Format(
                Strings.Status_RedeployedFmt, deployed.Written, deployed.Total, wheelbasePid);
        }

        private void ClearAllSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Strings.Dialog_ClearAllSettings_Body,
                Strings.Dialog_ClearAllSettings_Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            _plugin.ClearSettings();

            using (_suppressor.Begin())
            {
                AutoApplyProfileCheck.IsChecked = _plugin.Settings.AutoApplyProfileOnLaunch;
                ShowAllTabsCheck.IsChecked = _plugin.Settings.ShowAllTabs;
                LimitWheelUpdatesCheck.IsChecked = _plugin.Settings.LimitWheelUpdates;
                ConnectionToggle.IsChecked = _plugin.Settings.ConnectionEnabled;
                ProfileListControl.DataContext = null;
                ProfileListControl.DataContext = _plugin.ProfileStore;
            }
        }

        // ===== Profile system (SimHub native) =====

        private MozaProfileStore ProfileStore => _plugin.ProfileStore;

        private void InitProfilesTab()
        {
            ProfileListControl.DataContext = ProfileStore;
        }

        // ===== Telemetry (Options tab) =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryTab()
        {
            // One-shot init for controls whose state is purely settings-driven
            // and doesn't change after load (upload/download toggles — hidden
            // anyway). The firmware-era combo isn't covered here because it
            // needs to re-sync once a wheel identifies: per-page-GUID lookup
            // returns Auto before the wheel's model name is known, and the
            // settings-tab is built before that point. See RefreshTelemetryTab.
            if (!_telemetryUIInitialized)
            {
                _telemetryUIInitialized = true;
                using (_suppressor.Begin())
                {
                    var s = _plugin.Settings;
                    UploadDashboardCheck.IsChecked = s.TelemetryUploadDashboard;
                    DownloadDashboardCheck.IsChecked = s.TelemetryDownloadDashboard;
                }
            }

            RefreshTelemetryTab();
        }

        // Re-syncs UI controls that depend on per-wheel state which only
        // resolves after the wheel identifies. Safe to call repeatedly; uses
        // the suppressor to keep SelectionChanged handlers from firing on
        // programmatic writes.
        // ComboBox item order ↔ MozaWheelEra. The enum is non-contiguous
        // (value 2 is the retired Era2025 hole), so map by position rather than
        // casting the index. Keep in lockstep with the FirmwareEraCombo items
        // in SettingsControl.xaml.
        private static readonly MozaWheelEra[] EraComboOrder =
        {
            MozaWheelEra.Auto,
            MozaWheelEra.Era2024,
            MozaWheelEra.Era2026,
        };

        private void RefreshTelemetryTab()
        {
            int desired = System.Array.IndexOf(EraComboOrder, _plugin.ActiveTelemetryWheelEra);
            if (desired < 0) desired = 0; // fall back to Auto
            if (FirmwareEraCombo.SelectedIndex != desired)
            {
                using (_suppressor.Begin())
                {
                    FirmwareEraCombo.SelectedIndex = desired;
                }
            }
        }

        private void UploadDashboard_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryUploadDashboard = UploadDashboardCheck.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void DownloadDashboard_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryDownloadDashboard = DownloadDashboardCheck.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void FirmwareEra_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            // Map combo index → enum via EraComboOrder (the enum is
            // non-contiguous). -1 (no selection) falls back to Auto so the
            // plugin stays in a valid state even if the combo is deselected.
            int idx = FirmwareEraCombo.SelectedIndex;
            _plugin.ActiveTelemetryWheelEra = (idx >= 0 && idx < EraComboOrder.Length)
                ? EraComboOrder[idx]
                : MozaWheelEra.Auto;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

    }
}
