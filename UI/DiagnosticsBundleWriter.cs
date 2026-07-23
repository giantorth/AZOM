using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using MozaPlugin.Devices;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.UI
{
    /// <summary>Writes a diagnostics bundle ZIP — to disk (atomic tmp-rename) or to a byte[] for upload.</summary>
    internal static class DiagnosticsBundleWriter
    {
        /// <summary>Everything that goes into a bundle. Capture text is already redacted by the caller.</summary>
        internal sealed class BundleContent
        {
            public string DiagnosticsDumpText = string.Empty;
            public string StartupCaptureText = string.Empty;
            public string RollingCaptureText = string.Empty;
            public IReadOnlyList<SerialTrafficCapture.Entry>? StartupSnapshot;
            public IReadOnlyList<SerialTrafficCapture.Entry>? RollingSnapshot;
            // Serialized MozaPluginSettings JSON (may be null if unavailable).
            public string? SettingsJson;
            // Populated only on the bug-report submit path; null for a local export.
            public string? ReportText;
        }

        /// <summary>
        /// Build a filesystem-safe slug from a wheel's firmware model name for
        /// use as a filename prefix on diagnostics bundles. Returns "" when no
        /// model is known so the caller can omit the prefix.
        /// </summary>
        public static string BuildWheelModelFilenameSlug(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return "";
            var friendly = WheelModelInfo.GetFriendlyName(WheelModelInfo.ExtractPrefix(modelName!));
            if (string.IsNullOrWhiteSpace(friendly)) return "";

            var sb = new StringBuilder(friendly.Length);
            foreach (var ch in friendly)
            {
                if (ch == ' ') sb.Append('-');
                else if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.') sb.Append(ch);
                // anything else (path separators, punctuation, control chars) is dropped
            }
            return sb.ToString().Trim('-');
        }

        /// <summary>
        /// Write the bundle to <paramref name="zipPath"/>. Atomic via sibling
        /// .tmp file + rename so a mid-write failure never leaves a partial zip
        /// at the user-visible path.
        /// </summary>
        public static void Write(string zipPath, BundleContent content)
        {
            string tmpPath = zipPath + ".tmp";
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                    WriteZip(fs, content);
                if (File.Exists(zipPath)) File.Delete(zipPath);
                File.Move(tmpPath, zipPath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        /// <summary>Build the bundle entirely in memory (for upload — no temp file, no disk churn).</summary>
        public static byte[] BuildBundleBytes(BundleContent content)
        {
            using (var ms = new MemoryStream())
            {
                WriteZip(ms, content);
                return ms.ToArray();
            }
        }

        private static void WriteZip(Stream dest, BundleContent content)
        {
            // [AZOM] log lines come from MozaLog's in-process ring buffer — every
            // plugin call site goes through that wrapper, so the snapshot is
            // current without depending on SimHub's rolling-file flush cadence.
            string logText = MozaLog.Snapshot();
            int logEntryCount = MozaLog.Count;

            var manifest = new StringBuilder();
            manifest.AppendLine("AZOM diagnostics bundle");
            manifest.AppendLine($"Created (local):     {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            manifest.AppendLine($"Plugin version:      {DiagnosticsTextBuilder.GetPluginVersion()}");
            manifest.AppendLine($"OS:                  {Environment.OSVersion}");
            manifest.AppendLine($"CLR:                 {Environment.Version}");
            manifest.AppendLine();
            manifest.AppendLine("Files:");
            if (content.ReportText != null)
                manifest.AppendLine("  report.txt               – user's problem description + contact");
            manifest.AppendLine("  serial-capture-startup.txt – first ~60s of traffic (connect/handshake), frozen");
            manifest.AppendLine("  serial-capture-rolling.txt – rolling last-N-minutes of traffic");
            manifest.AppendLine("  diagnostics.txt          – snapshot of the Diagnostics tab text");
            if (!string.IsNullOrEmpty(content.SettingsJson))
                manifest.AppendLine("  plugin-settings.json     – serialized MozaPluginSettings");
            manifest.AppendLine($"  moza-log.txt             – [AZOM] log lines from MozaLog ring buffer ({logEntryCount} entries)");
            manifest.AppendLine();
            manifest.AppendLine("Hardware identifiers (serial numbers, MCU UIDs) are masked as .. in the capture files.");
            manifest.AppendLine();
            manifest.AppendLine("Capture summary:");
            var startedUtc = SerialTrafficCapture.Instance.StartedAtUtc;
            manifest.AppendLine(startedUtc == default
                ? "  Started:           (capture clock not started)"
                : $"  Started (UTC):     {startedUtc:yyyy-MM-dd HH:mm:ss}");
            manifest.AppendLine($"  Startup frames:    {content.StartupSnapshot?.Count ?? 0}");
            manifest.AppendLine($"  Rolling frames:    {content.RollingSnapshot?.Count ?? 0}");

            using (var zip = new ZipArchive(dest, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "manifest.txt", manifest.ToString());
                if (content.ReportText != null)
                    WriteEntry(zip, "report.txt", content.ReportText);
                WriteEntry(zip, "serial-capture-startup.txt", content.StartupCaptureText);
                WriteEntry(zip, "serial-capture-rolling.txt", content.RollingCaptureText);
                WriteEntry(zip, "diagnostics.txt", content.DiagnosticsDumpText);
                if (!string.IsNullOrEmpty(content.SettingsJson))
                    WriteEntry(zip, "plugin-settings.json", content.SettingsJson);
                WriteEntry(zip, "moza-log.txt", logText);
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = entry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                w.Write(content);
        }
    }
}
