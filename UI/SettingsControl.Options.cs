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

        // ===== Default tab =====

        // One entry per MainTabs tab, labelled with its header.
        private void InitDefaultTabCombo()
        {
            DefaultTabCombo.Items.Clear();
            foreach (var item in MainTabs.Items)
            {
                if (item is TabItem tab)
                    DefaultTabCombo.Items.Add(new ComboBoxItem { Content = tab.Header, Tag = tab });
            }
            SyncDefaultTabCombo();
        }

        private void SyncDefaultTabCombo()
        {
            var tab = ResolveDefaultTab();
            foreach (var item in DefaultTabCombo.Items)
            {
                if (item is ComboBoxItem ci && ReferenceEquals(ci.Tag, tab))
                {
                    DefaultTabCombo.SelectedItem = ci;
                    return;
                }
            }
        }

        private TabItem ResolveDefaultTab()
        {
            string? name = _plugin.Settings.DefaultTab;
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var item in MainTabs.Items)
                {
                    if (item is TabItem t && t.Name == name) return t;
                }
            }
            return BaseTab;
        }

        // Takes effect the next time the pane is built; doesn't switch tabs now.
        private void DefaultTabCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (!(DefaultTabCombo.SelectedItem is ComboBoxItem ci && ci.Tag is TabItem tab)) return;
            _plugin.Settings.DefaultTab = tab.Name;
            _plugin.SaveSettings();
        }

        private void Ab9DetectionCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.SetAb9DetectionEnabled(Ab9DetectionCheck.IsChecked == true);
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
            if (!OptionsTab.IsSelected) return;
            if (ForzaCompatCheck != null)
                ForzaCompatCheck.IsChecked = _data.CompatMode == 1;
            UpdateWheelbaseLfeSourceStatus();
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

            UpdateWheelbaseLfeSourceStatus();
        }

        // Set once the LFE-source switch has redeployed the base definition.
        private bool _lfeSourceRestartPending;

        // Re-run every Options tick: the base firmware version lands after connect.
        // A pre-LFE base gets a firmware note instead of a restart prompt — neither
        // source does anything on that firmware.
        private void UpdateWheelbaseLfeSourceStatus()
        {
            string text;
            if (!Devices.Haptics.MozaBaseHapticsBridge.IsSupported)
                text = Strings.Status_WheelbaseLfeShakeItUnavailable;
            else if (_data.BaseFwVersion != 0 && !_data.BaseSupportsLfe)
                text = string.Format(Strings.Status_WheelbaseLfeFirmwareTooOldFmt, _data.BaseFwVersionText);
            else if (_lfeSourceRestartPending)
                text = Strings.Status_WheelbaseLfeSourceRestartRequired;
            else
                text = "";
            if (WheelbaseLfeSourceStatusText.Text != text)
            {
                WheelbaseLfeSourceStatusText.Text = text;
                WheelbaseLfeSourceStatusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
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

            _lfeSourceRestartPending = _plugin.DeviceDefinitionDeployed;
            UpdateWheelbaseLfeSourceStatus();
        }

        // ===== S12 source: plugin tab | ShakeIt per pedal | ShakeIt combined =====

        // Set once a source switch has deployed an S12 definition.
        private bool _pedalHapticsRestartPending;

        /// <summary>
        /// Seed the S12 source selector. On a SimHub without device haptics
        /// (pre-9.12) the ShakeIt items are disabled and the plugin tab is shown
        /// as the effective source; the stored preference is left alone.
        /// </summary>
        private void SyncPedalHapticsSourceCombo()
        {
            bool supported = Devices.Haptics.MozaPedalHapticsBridge.IsSupported;
            PedalHapticsSourceShakeItItem.IsEnabled = supported;
            PedalHapticsSourceCombinedItem.IsEnabled = supported;

            var s = _plugin.Settings;
            int idx = !_plugin.PedalHapticsRoutedToShakeIt ? 0 : (s.PedalHapticsCombinedDevice ? 2 : 1);
            PedalHapticsSourceCombo.SelectedIndex = idx;
            UpdatePedalHapticsSourceStatus();
        }

        private void UpdatePedalHapticsSourceStatus()
        {
            string text;
            if (!Devices.Haptics.MozaPedalHapticsBridge.IsSupported)
                text = Strings.Status_PedalHapticsShakeItUnavailable;
            else if (_pedalHapticsRestartPending)
                text = Strings.Status_WheelbaseLfeSourceRestartRequired;
            else
                text = "";
            if (PedalHapticsSourceStatusText.Text != text)
            {
                PedalHapticsSourceStatusText.Text = text;
                PedalHapticsSourceStatusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void PedalHapticsSource_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int idx = PedalHapticsSourceCombo.SelectedIndex;
            if (idx < 0) return;

            var source = idx == 0 ? PedalHapticsSource.PluginTab : PedalHapticsSource.ShakeIt;
            // Picking the plugin tab keeps the remembered ShakeIt layout.
            bool combined = idx == 0 ? _plugin.Settings.PedalHapticsCombinedDevice : idx == 2;
            if (_plugin.SetPedalHapticsMode(source, combined))
                _pedalHapticsRestartPending = true;
            UpdatePedalHapticsSourceStatus();
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

            bool wasCombined = _plugin.PedalHapticsCombined;
            bool wasShakeIt = _plugin.PedalHapticsRoutedToShakeIt;
            _plugin.ClearSettings();
            if ((_plugin.PedalHapticsCombined != wasCombined || _plugin.PedalHapticsRoutedToShakeIt != wasShakeIt)
                && _plugin.ApplyPedalHapticsLayout())
                _pedalHapticsRestartPending = true;

            using (_suppressor.Begin())
            {
                AutoApplyProfileCheck.IsChecked = _plugin.Settings.AutoApplyProfileOnLaunch;
                SyncDefaultTabCombo();
                ShowAllTabsCheck.IsChecked = _plugin.Settings.ShowAllTabs;
                Ab9DetectionCheck.IsChecked = _plugin.Settings.Ab9DetectionEnabled;
                SyncPedalHapticsSourceCombo();
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
