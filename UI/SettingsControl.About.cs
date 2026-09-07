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

        // ── Diagnostics tab ─────────────────────────────────────────────
        // ===== About-tab link handlers =====

        private const string AboutGitHubUrl  = "https://github.com/giantorth/moza-simhub-plugin";
        private const string AboutDiscordUrl = "https://discord.gg/J4enw43e62";
        private const string AboutSponsorUrl = "https://github.com/sponsors/giantorth";
        private const string AboutKofiUrl    = "https://ko-fi.com/giantorth";

        private void AboutGitHubButton_Click(object sender, System.Windows.RoutedEventArgs e)  => OpenExternalUrl(AboutGitHubUrl);
        private void AboutDiscordButton_Click(object sender, System.Windows.RoutedEventArgs e) => OpenExternalUrl(AboutDiscordUrl);
        private void AboutSponsorButton_Click(object sender, System.Windows.RoutedEventArgs e) => OpenExternalUrl(AboutSponsorUrl);
        private void AboutKofiButton_Click(object sender, System.Windows.RoutedEventArgs e)    => OpenExternalUrl(AboutKofiUrl);

        // Open a URL via the OS shell. On Windows this hits the default
        // browser; under Wine/Proton it routes through winebrowser which
        // forwards to the host's xdg-open.
        private static void OpenExternalUrl(string url)
        {
            try
            {
                // The returned Process (when any) is an unmanaged handle — dispose it.
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                })) { }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[About] failed to open {url}: {ex.Message}");
            }
        }

        // Live state TextBlocks were removed (the FULL DIAGNOSTIC REPORT expander
        // shows the same content); BuildDiagnosticsDump now sources every line
        // straight from DiagnosticsTextBuilder.

        private void DiagCopyAll_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try { System.Windows.Clipboard.SetText(BuildDiagnosticsDump()); }
            catch { /* clipboard may be contested under Wine */ }
        }

        private string BuildDiagnosticsDump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Plugin ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildPluginInfo());
            sb.AppendLine();
            sb.AppendLine("=== USB detection ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildUsbDetection(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Standalone peripherals (own USB port) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildStandalonePeripherals(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== mBooster pedals ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildMBoosterDevices(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== Stalks ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildStalks(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== Wheel identity ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildWheelIdentity(_data, _plugin.DetectionState));
            sb.AppendLine();
            sb.AppendLine("=== Wheel LED zones ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildWheelLedZones(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== Base identity ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildBaseIdentity(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== Display sub-device identity ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildDisplayIdentity(_data));
            sb.AppendLine();
            sb.AppendLine("=== Standalone dashboard ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildStandaloneDashboardState(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Dashboard state ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildDashboardState(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Tile-server state ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildTileServer(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Session state ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSessionState(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Wheel channel catalog ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildWheelCatalog(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Last subscription sent ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSubscription(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Wheel response on 0x02 (post-subscription window) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSubscriptionResponse(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Firmware debug (wire group 0x0E) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildFirmwareDebug(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Device display log (session FF kind=14) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildDeviceLog(_plugin));
            return sb.ToString();
        }

        // ── Diagnostics bundle export ────────────────────────────────────
        // Capture is always-on (dual-segment ring in SerialTrafficCapture). The
        // "Export bundle" button (in the Report-a-problem card) saves the same
        // bundle locally; BuildBundleContent lives in SettingsControl.BugReport.cs.

        private async void SerialCaptureExport_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var modelSlug = DiagnosticsBundleWriter.BuildWheelModelFilenameSlug(_data?.WheelModelName);
            var prefix = string.IsNullOrEmpty(modelSlug) ? "" : modelSlug + "-";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"{prefix}moza-diagnostics-bundle-{stamp}.zip",
                Filter = Strings.Diag_ZipFilter,
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;

            var button = sender as System.Windows.Controls.Button;
            try
            {
                if (button != null) button.IsEnabled = false;
                // Diagnostics dump, two capture redaction passes, settings JSON and
                // the zip — seconds on a big capture, so off the dispatcher.
                string path = dlg.FileName;
                await System.Threading.Tasks.Task.Run(() =>
                    DiagnosticsBundleWriter.Write(path, BuildBundleContent(reportText: null)));
                SetBugReportStatus(string.Format(Strings.Status_ExportedTo, path));
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Diagnostics export failed: {ex}");
                System.Windows.MessageBox.Show(
                    System.Windows.Window.GetWindow(this),
                    string.Format(Strings.Dialog_ExportFailed, ex.Message),
                    "AZOM",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

    }
}
