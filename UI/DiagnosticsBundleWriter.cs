using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using MozaPlugin.Devices;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.UI
{
    /// <summary>Writes a diagnostics bundle ZIP via tmp-rename (atomic — no partial files).</summary>
    internal static class DiagnosticsBundleWriter
    {
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
        /// .tmp file + rename. Caller supplies the already-rendered text bodies.
        /// </summary>
        public static void Write(
            string zipPath,
            string diagnosticsDumpText,
            string serialCaptureText,
            IReadOnlyList<SerialTrafficCapture.Entry>? captureSnapshot)
        {
            // [AZOM] log lines come from MozaLog's in-process ring buffer — every
            // plugin call site goes through that wrapper, so the snapshot is
            // current without depending on SimHub's rolling-file flush cadence.
            string logText = MozaLog.Snapshot();
            int logEntryCount = MozaLog.Count;

            // Header — quick idea of what's in the bundle and when it was made.
            var manifest = new StringBuilder();
            manifest.AppendLine("AZOM diagnostics bundle");
            manifest.AppendLine($"Created (local):     {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            manifest.AppendLine($"Plugin version:      {DiagnosticsTextBuilder.GetPluginVersion()}");
            manifest.AppendLine($"OS:                  {Environment.OSVersion}");
            manifest.AppendLine($"CLR:                 {Environment.Version}");
            manifest.AppendLine();
            manifest.AppendLine("Files:");
            manifest.AppendLine("  serial-capture.txt   – TX/RX frames captured between Start/Stop (timestamps in local time)");
            manifest.AppendLine("  diagnostics.txt      – snapshot of the Diagnostics tab text");
            manifest.AppendLine($"  moza-log.txt         – [AZOM] log lines from MozaLog ring buffer ({logEntryCount} entries)");
            manifest.AppendLine();
            manifest.AppendLine("Capture summary:");
            if (captureSnapshot != null)
            {
                manifest.AppendLine($"  Started (UTC):     {SerialTrafficCapture.Instance.StartedAtUtc:yyyy-MM-dd HH:mm:ss}");
                manifest.AppendLine($"  Frames:            {captureSnapshot.Count}");
            }
            else
            {
                manifest.AppendLine("  (no capture buffer was active when this bundle was produced)");
            }

            // Tmp-rename so a mid-write failure doesn't leave a partial zip
            // at the user-visible path.
            string tmpPath = zipPath + ".tmp";
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, "manifest.txt", manifest.ToString());
                    WriteEntry(zip, "serial-capture.txt", serialCaptureText);
                    WriteEntry(zip, "diagnostics.txt", diagnosticsDumpText);
                    WriteEntry(zip, "moza-log.txt", logText);
                }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                File.Move(tmpPath, zipPath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
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
