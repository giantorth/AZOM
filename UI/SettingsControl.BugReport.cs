using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MozaPlugin.Diagnostics;
using MozaPlugin.Resources;
using MozaPlugin.UI.BugReport;

namespace MozaPlugin
{
    // Partial-class continuation of SettingsControl: the About-tab "Report a
    // problem" flow and the shared diagnostics-bundle assembly used by both the
    // submit path and the Options-tab local Export.
    public partial class SettingsControl
    {
        /// <summary>
        /// Assemble a diagnostics bundle from the live capture. Capture text is
        /// redacted here (identifiers masked) so both the uploaded and the
        /// locally-exported bundle are consistent. <paramref name="reportText"/>
        /// is null for a plain export; set for a bug-report submit.
        /// </summary>
        private UI.DiagnosticsBundleWriter.BundleContent BuildBundleContent(string? reportText, bool includeRolling = true)
        {
            var cap = SerialTrafficCapture.Instance;
            var startup = cap.SnapshotStartup();
            IReadOnlyList<SerialTrafficCapture.Entry> rolling = includeRolling
                ? cap.SnapshotRolling()
                : Array.Empty<SerialTrafficCapture.Entry>();

            return new UI.DiagnosticsBundleWriter.BundleContent
            {
                DiagnosticsDumpText = BuildDiagnosticsDump(),
                StartupSnapshot = startup,
                RollingSnapshot = rolling,
                StartupCaptureText = CaptureRedactor.FormatRedacted(startup, _data),
                RollingCaptureText = includeRolling
                    ? CaptureRedactor.FormatRedacted(rolling, _data)
                    : "(rolling segment omitted to fit the upload size limit)\n",
                ReportText = reportText,
            };
        }

        private string BuildReportText(string description, string contact, string version, string os, bool rollingOmitted)
        {
            var sb = new StringBuilder();
            sb.AppendLine("AZOM bug report");
            sb.AppendLine($"Plugin version: {version}");
            sb.AppendLine($"OS:             {os}");
            sb.AppendLine($"CLR:            {Environment.Version}");
            sb.AppendLine($"Wheel model:    {(string.IsNullOrEmpty(_data?.WheelModelName) ? "—" : _data!.WheelModelName)}");
            sb.AppendLine($"Contact:        {(string.IsNullOrEmpty(contact) ? "—" : contact)}");
            if (!SerialTrafficCapture.Instance.Enabled)
                sb.AppendLine("Note:           diagnostic capture was OFF — serial-capture files are empty.");
            if (rollingOmitted)
                sb.AppendLine("Note:           rolling capture segment omitted (bundle size limit).");
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.AppendLine(description);
            return sb.ToString();
        }

        private async void SubmitBugReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string description = BugReportService.SanitizeUserText(
                    BugReportDescriptionBox.Text, BugReportService.MaxDescriptionChars);
                if (string.IsNullOrEmpty(description))
                {
                    BugReportStatusText.Text = Strings.Status_BugReportNeedDescription;
                    return;
                }

                // Local double-submit guard (distinct from the server's per-IP
                // rate limit); the Worker enforces the real limits.
                var since = DateTime.UtcNow - _plugin.Settings.LastBugReportUtc;
                if (since < BugReportService.SubmitCooldown)
                {
                    MozaLog.Info("[AZOM] Bug report skipped: local submit cooldown active");
                    BugReportStatusText.Text = Strings.Status_BugReportCooldown;
                    return;
                }

                string contact = BugReportService.SanitizeUserText(
                    BugReportContactBox.Text, BugReportService.MaxContactChars, singleLine: true);
                string version = UI.DiagnosticsTextBuilder.GetPluginVersion();
                string os = Environment.OSVersion.ToString();
                string model = UI.DiagnosticsBundleWriter.BuildWheelModelFilenameSlug(_data?.WheelModelName);

                SubmitBugReportButton.IsEnabled = false;
                BugReportStatusText.Text = Strings.Status_BugReportUploading;

                // Assemble on the UI thread (light: snapshots + text), then
                // compress off-thread (heavier) so the pane stays responsive.
                bool rollingOmitted = false;
                var content = BuildBundleContent(
                    BuildReportText(description, contact, version, os, rollingOmitted), includeRolling: true);
                byte[] bundle = await Task.Run(() => UI.DiagnosticsBundleWriter.BuildBundleBytes(content));

                if (bundle.Length > BugReportService.MaxUploadBytes)
                {
                    rollingOmitted = true;
                    content = BuildBundleContent(
                        BuildReportText(description, contact, version, os, rollingOmitted), includeRolling: false);
                    bundle = await Task.Run(() => UI.DiagnosticsBundleWriter.BuildBundleBytes(content));
                    if (bundle.Length > BugReportService.MaxUploadBytes)
                    {
                        BugReportStatusText.Text = Strings.Status_BugReportTooLarge;
                        return;
                    }
                }

                var result = await BugReportService.SubmitAsync(
                    bundle, description, contact, version, os, model, CancellationToken.None);

                switch (result.Outcome)
                {
                    case BugReportService.Outcome.Success:
                        _plugin.Settings.LastBugReportUtc = DateTime.UtcNow;
                        _plugin.SaveSettings();
                        MozaLog.Info($"[AZOM] Bug report submitted ({bundle.Length} bytes), ref {result.TicketId ?? "?"}");
                        BugReportStatusText.Text = string.Format(
                            Strings.Status_BugReportSubmitted,
                            string.IsNullOrEmpty(result.TicketId) ? "—" : result.TicketId);
                        BugReportDescriptionBox.Text = "";
                        break;
                    case BugReportService.Outcome.RateLimited:
                        MozaLog.Warn($"[AZOM] Bug report rate-limited by server: {result.Detail}");
                        BugReportStatusText.Text = Strings.Status_BugReportRateLimited;
                        break;
                    case BugReportService.Outcome.TooLarge:
                        MozaLog.Warn($"[AZOM] Bug report rejected (too large): {result.Detail}");
                        BugReportStatusText.Text = Strings.Status_BugReportTooLarge;
                        break;
                    default:
                        MozaLog.Warn($"[AZOM] Bug report upload failed: {result.Outcome} {result.Detail}");
                        BugReportStatusText.Text = Strings.Status_BugReportFailed;
                        break;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Bug report submit error: {ex}");
                BugReportStatusText.Text = Strings.Status_BugReportFailed;
            }
            finally
            {
                SubmitBugReportButton.IsEnabled = true;
            }
        }
    }
}
