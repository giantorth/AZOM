using System;
using System.Windows;
using MozaPlugin.Sdk;
using MozaPlugin.Resources;

namespace MozaPlugin.UI
{
    // Partial-class continuation of SettingsControl holding the CoAP / UDP
    // control toggles, which live on the Options tab's SDK card. Per-request
    // rows are no longer mirrored into a UI list — both servers log every
    // request through MozaLog, so the traffic rides the ring buffer into the
    // diagnostics bundle instead.
    public partial class SettingsControl
    {
        /// <summary>
        /// Called from the SettingsControl constructor (after
        /// InitializeComponent) to seed the SDK toggles from persisted
        /// settings. Suppresses change events while seeding so the handlers
        /// don't re-save the values that just came out of the settings file.
        /// </summary>
        private void InitSdkCard()
        {
            using (_suppressor.Begin())
            {
                if (SdkEmulationEnabledCheck != null)
                    SdkEmulationEnabledCheck.IsChecked = _plugin.Settings.SdkEmulationEnabled;

                if (UdpControlEnabledCheck != null)
                    UdpControlEnabledCheck.IsChecked = _plugin.Settings.UdpControlEnabled;
            }

            RefreshSdkStatus();
        }

        /// <summary>
        /// Render one status line under each toggle: the CoAP listener (port
        /// 40266) plus its stub-manager process, and the PitHouse UDP control
        /// listener (port 40288). Each component has its own enable gate, so a
        /// user may run one without the other and the two lines report
        /// independently — a port collision or a stub that couldn't extract
        /// shows up against the piece that broke.
        /// </summary>
        private void RefreshSdkStatus()
        {
            // Defensive null checks: called from InitSdkCard before the controls
            // may have realized, and from the 500 ms refresh tick afterwards.
            if (SdkCoapStatusText != null)
            {
                SdkCoapStatusText.Text = string.Format(
                    "{0}: {1}  ·  {2}: {3}",
                    MozaSdkCoapServer.CoapPort,
                    DescribeServerStatus(_plugin.SdkServer?.Status, _plugin.Settings.SdkEmulationEnabled),
                    Strings.Label_StubManager,
                    DescribeStubStatus(_plugin.SdkStubManager, _plugin.Settings.SdkEmulationEnabled));
            }

            if (SdkUdpStatusText != null)
            {
                SdkUdpStatusText.Text = string.Format(
                    "{0}: {1}",
                    Sdk.PitHouseUdp.MozaControlUdpServer.ControlPort,
                    DescribeServerStatus(_plugin.ControlUdpServer?.Status, _plugin.Settings.UdpControlEnabled));
            }
        }

        private static string DescribeServerStatus(string? liveStatus, bool enabledIntent)
        {
            if (!string.IsNullOrEmpty(liveStatus)) return liveStatus!;
            // The toggles are live, so an "enabled" intent with no live status
            // yet is just the brief window before the background start finishes.
            return enabledIntent ? Strings.Sdk_Status_Starting : Strings.Sdk_Status_Disabled;
        }

        private static string DescribeStubStatus(Sdk.CoapStubManager? stub, bool enabledIntent)
        {
            if (stub == null)
                return enabledIntent ? Strings.Sdk_Status_Starting : Strings.Sdk_Status_Disabled;
            return stub.IsRunning
                ? string.Format(Strings.Sdk_Status_RunningPid, stub.ProcessId)
                : Strings.Sdk_Status_Stopped;
        }

        /// <summary>
        /// Tick hook used by the SettingsControl refresh DispatcherTimer to
        /// poll the servers' live state. Cheap — two formatted strings.
        /// </summary>
        private void RefreshSdkStatusTick()
        {
            RefreshSdkStatus();
        }

        // ===== Event handlers =====

        private void SdkEmulationEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = SdkEmulationEnabledCheck.IsChecked == true;
            _plugin.Settings.SdkEmulationEnabled = on;
            _plugin.SaveSettings();
            // Apply the change live — no plugin restart needed. Off-loaded to the
            // ThreadPool because the stub spawn/teardown (CreateProcess +
            // JobObject under Wine) can take a moment and must not stall the WPF
            // thread; the 500 ms RefreshSdkStatusTick renders the resulting status.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _plugin.SdkLifecycle?.SetEmulationEnabled(on); }
                catch { /* helper logs its own failures; status reflects them */ }
            });
            RefreshSdkStatus();
            // The SDK nudge (now in the shared PluginBanners control) self-hides
            // within its 500 ms tick once SdkEmulationEnabled flips.
        }

        private void UdpControlEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = UdpControlEnabledCheck.IsChecked == true;
            _plugin.Settings.UdpControlEnabled = on;
            _plugin.SaveSettings();
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _plugin.SdkLifecycle?.SetUdpControlEnabled(on); }
                catch { /* helper logs its own failures; status reflects them */ }
            });
            RefreshSdkStatus();
        }

        // ===== One-time "enable SDK support" nudge banner =====

        // The SDK-setup nudge banner + its Configure/Dismiss handlers live in the
        // shared PluginBanners control. The plugin pane wires that control's
        // ConfigureSdkInApp delegate here so Configure lands on the Options tab,
        // which is where the SDK toggles now live.
        internal void NavigateToSdkSettings()
        {
            try { if (MainTabs != null && OptionsTab != null) MainTabs.SelectedItem = OptionsTab; }
            catch (Exception ex) { MozaLog.Debug($"[SdkPrompt] navigate failed: {ex.Message}"); }
        }
    }
}
