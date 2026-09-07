using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MozaPlugin.Diagnostics;
using MozaPlugin.Resources;
using MozaPlugin.UI.BugReport;

namespace MozaPlugin.UI
{
    // Partial-class continuation of SettingsControl: the About-tab "Report a
    // problem" flow and the shared diagnostics-bundle assembly used by both the
    // submit path and the Options-tab local Export.
    public partial class SettingsControl
    {
        // Reference from the last successful submit, for the copy button.
        private string? _lastBugReportTicketId;

        /// <summary>Set the bug-report status line (a read-only TextBox, so the user can select it).</summary>
        private void SetBugReportStatus(string text)
        {
            BugReportStatusText.Text = text ?? "";
            BugReportStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Arm (or clear) the copy-reference button. The reference outlives later
        /// status lines — an export done after a submit must not take it away —
        /// and is cleared only when a new submit starts.
        /// </summary>
        private void SetBugReportReference(string? ticketId)
        {
            _lastBugReportTicketId = string.IsNullOrEmpty(ticketId) ? null : ticketId;
            CopyBugReportRefButton.Visibility = _lastBugReportTicketId == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // "Upload failed…  [HTTP 403]" — the bracketed part stays untranslated on
        // purpose: it is the code the user quotes back to us.
        private static string AppendShortCode(string message, string? shortCode)
            => string.IsNullOrEmpty(shortCode) ? message : $"{message}  [{shortCode}]";

        private void CopyBugReportRef_Click(object sender, RoutedEventArgs e)
        {
            var ticketId = _lastBugReportTicketId;
            if (string.IsNullOrEmpty(ticketId)) return;
            try { Clipboard.SetText(ticketId); }
            catch { /* clipboard may be contested under Wine */ }
        }

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
                SettingsJson = SerializeSettings(),
                DeviceLogText = BuildDeviceLogFile(),
                ReportText = reportText,
            };
        }

        /// <summary>Full device-display-log ring, oldest-first, for the bundle's
        /// own entry. The diagnostics dump caps its render at the most recent
        /// 200 lines; this is the whole buffer, with hardware identifiers
        /// masked the same way the capture files are.</summary>
        private string BuildDeviceLogFile()
        {
            var entries = _plugin?.DeviceLogForDiagnostics?.Snapshot();
            if (entries == null || entries.Length == 0) return string.Empty;
            var sb = new StringBuilder(entries.Length * 96);
            sb.Append("# host receive time (local) | source | display application log (hardware identifiers masked as ..)\n");
            foreach (var e in entries)
            {
                sb.Append(e.ReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                sb.Append(" [").Append(e.Source).Append("] ");
                sb.Append(CaptureRedactor.RedactText(e.Text, _data));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private string SerializeSettings()
        {
            try
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(
                    _plugin.Settings, Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"(failed to serialize plugin settings: {ex.Message})";
            }
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
                    SetBugReportStatus(Strings.Status_BugReportNeedDescription);
                    return;
                }

                // Local double-submit guard (distinct from the server's per-IP
                // rate limit); the Worker enforces the real limits.
                var since = DateTime.UtcNow - _plugin.Settings.LastBugReportUtc;
                if (since < BugReportService.SubmitCooldown)
                {
                    MozaLog.Info("[AZOM] Bug report skipped: local submit cooldown active");
                    SetBugReportStatus(Strings.Status_BugReportCooldown);
                    return;
                }

                string contact = BugReportService.SanitizeUserText(
                    BugReportContactBox.Text, BugReportService.MaxContactChars, singleLine: true);
                string version = UI.DiagnosticsTextBuilder.GetPluginVersion();
                string os = Environment.OSVersion.ToString();
                string model = UI.DiagnosticsBundleWriter.BuildWheelModelFilenameSlug(_data?.WheelModelName);

                SubmitBugReportButton.IsEnabled = false;
                SetBugReportReference(null);
                SetBugReportStatus(Strings.Status_BugReportUploading);

                // Assemble AND compress off-thread: the two capture redaction passes
                // and the diagnostics dump are not light on a big rolling capture.
                bool rollingOmitted = false;
                string reportText = BuildReportText(description, contact, version, os, rollingOmitted);
                byte[] bundle = await Task.Run(() => UI.DiagnosticsBundleWriter.BuildBundleBytes(
                    BuildBundleContent(reportText, includeRolling: true)));

                if (bundle.Length > BugReportService.MaxUploadBytes)
                {
                    rollingOmitted = true;
                    string reportTextNoRolling = BuildReportText(description, contact, version, os, rollingOmitted);
                    bundle = await Task.Run(() => UI.DiagnosticsBundleWriter.BuildBundleBytes(
                        BuildBundleContent(reportTextNoRolling, includeRolling: false)));
                    if (bundle.Length > BugReportService.MaxUploadBytes)
                    {
                        SetBugReportStatus(Strings.Status_BugReportTooLarge);
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
                        SetBugReportStatus(string.Format(
                            Strings.Status_BugReportSubmitted,
                            string.IsNullOrEmpty(result.TicketId) ? "—" : result.TicketId));
                        SetBugReportReference(result.TicketId);
                        BugReportDescriptionBox.Text = "";
                        break;
                    case BugReportService.Outcome.RateLimited:
                        SetBugReportStatus(Strings.Status_BugReportRateLimited);
                        break;
                    case BugReportService.Outcome.TooLarge:
                        SetBugReportStatus(Strings.Status_BugReportTooLarge);
                        break;
                    default:
                        // Append the transport/HTTP code: it is what makes a
                        // "denied every time" report actionable when the user
                        // quotes the line, and it also sits in the exported
                        // bundle's upload-log.txt.
                        SetBugReportStatus(AppendShortCode(
                            result.EdgeBlocked ? Strings.Status_BugReportEdgeBlocked
                                               : Strings.Status_BugReportFailed,
                            result.ShortCode));
                        break;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Bug report submit error: {ex}");
                SetBugReportStatus(AppendShortCode(Strings.Status_BugReportFailed, ex.GetType().Name));
            }
            finally
            {
                SubmitBugReportButton.IsEnabled = true;
            }
        }
    }
}
