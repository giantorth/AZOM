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
using MozaPlugin.UI;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;
using static MozaPlugin.UI.UiHelpers;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;
using CaptureRedactor = MozaPlugin.Diagnostics.CaptureRedactor;
using MozaPlugin.Settings;
using MozaPlugin.Devices.Extensions;

namespace MozaPlugin.UI
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

        private void KeepaliveTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int sec = (int)Math.Round(e.NewValue);
            KeepaliveTimeoutValue.Text = $"{sec} s";
            _plugin.Settings.WheelKeepaliveTimeoutSec = sec;
            _plugin.SaveSettings();
        }

        // Forza Horizon compatibility — a persistent wheelbase mode, not a plugin
        // setting: the write goes to the device and the toggle is re-synced from
        // main-get-compat-mode, so a value PitHouse set is reflected here.
        // Polarity is plain 1/0 — do NOT copy BluetoothCheck's inverted 0/85.
        private void ForzaCompatCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = ForzaCompatCheck.IsChecked == true ? 1 : 0;
            _data.CompatMode = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-compat-mode", val);
        }

        // Periodic re-sync for Options-tab controls backed by DEVICE state rather
        // than plugin settings. Settings-driven toggles here are one-shot (nothing
        // else can change them), but compat-mode lives on the base and PitHouse or
        // another host can flip it behind our back, so it is re-read every tick.
        // Called from RefreshDisplay inside the event suppressor.
        private void RefreshOptionsTab()
        {
            if (ForzaCompatCheck != null)
                ForzaCompatCheck.IsChecked = _data.CompatMode == 1;
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
                wheelbasePid, DeviceDefinitionDeployer.ResolveDashboardPid(wheelbasePid),
                _plugin.WheelbaseWantsShakeItHaptics);

            if (deployed.Written > 0)
                _plugin.DeviceDefinitionDeployed = true;

            RedeployDefinitionsStatusText.Text = string.Format(
                Strings.Status_RedeployedFmt, deployed.Written, deployed.Total, wheelbasePid);
        }

        /// <summary>
        /// Seed the LFE-source selector and disable the ShakeIt option on a SimHub
        /// that has no device haptics feature (pre-9.12) — the definition would
        /// declare a block that build can't read.
        /// </summary>
        private void SyncWheelbaseLfeSourceCombo()
        {
            bool supported = Devices.Haptics.MozaBaseHapticsBridge.IsSupported;
            WheelbaseLfeSourceCombo.IsEnabled = supported;

            var source = _plugin.Settings.WheelbaseLfeSource;
            if (!supported) source = WheelbaseLfeSource.PluginTab;
            WheelbaseLfeSourceCombo.SelectedIndex = source == WheelbaseLfeSource.ShakeIt ? 1 : 0;

            WheelbaseLfeSourceStatusText.Text = supported ? "" : Strings.Status_WheelbaseLfeShakeItUnavailable;
        }

        private void WheelbaseLfeSource_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            var chosen = WheelbaseLfeSourceCombo.SelectedIndex == 1
                ? WheelbaseLfeSource.ShakeIt
                : WheelbaseLfeSource.PluginTab;
            if (chosen == _plugin.Settings.WheelbaseLfeSource) return;

            _plugin.Settings.WheelbaseLfeSource = chosen;
            _plugin.SaveSettings();

            // The choice IS the HapticsFeature block, so the definition has to be
            // rewritten and SimHub restarted before it takes effect.
            if (DeviceDefinitionDeployer.DeployForBaseModel(
                    _plugin.Data?.BaseModelName,
                    _plugin.Connection?.DiscoveredPid,
                    _plugin.DetectionState.BaseAmbientLedSupported,
                    _plugin.WheelbaseWantsShakeItHaptics))
                _plugin.DeviceDefinitionDeployed = true;

            WheelbaseLfeSourceStatusText.Text = _plugin.DeviceDefinitionDeployed
                ? Strings.Status_WheelbaseLfeSourceRestartRequired : "";
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
                SyncWheelbaseLfeSourceCombo();
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
            // anyway).
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

    }
}
