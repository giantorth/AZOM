using System;
using System.Collections.Generic;
using System.Text;

namespace MozaPlugin.UI.BugReport
{
    /// <summary>
    /// Ring of this session's upload attempts with their full failure detail.
    /// The diagnostics bundle embeds it as <c>upload-log.txt</c>, so a user whose
    /// submits keep getting refused can export the bundle by hand and send it
    /// another way with the reason already inside it.
    /// </summary>
    internal static class BugReportUploadLog
    {
        private const int MaxAttempts = 10;

        private static readonly object s_lock = new object();
        private static readonly Queue<string> s_attempts = new Queue<string>();
        private static int s_total;

        public static void Record(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (s_lock)
            {
                s_total++;
                s_attempts.Enqueue(text);
                while (s_attempts.Count > MaxAttempts) s_attempts.Dequeue();
            }
        }

        /// <summary>Bundle-entry text; empty string when nothing was attempted this session.</summary>
        public static string Snapshot()
        {
            lock (s_lock)
            {
                if (s_attempts.Count == 0) return string.Empty;

                var sb = new StringBuilder();
                sb.AppendLine("AZOM bug-report upload attempts");
                sb.Append("Attempts this session: ").Append(s_total);
                if (s_total > s_attempts.Count)
                    sb.Append(" (oldest ").Append(s_total - s_attempts.Count).Append(" dropped)");
                sb.AppendLine();
                sb.AppendLine();
                foreach (var attempt in s_attempts)
                {
                    sb.Append(attempt);
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }
    }
}
