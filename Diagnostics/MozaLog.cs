using System;
using System.Collections.Generic;
using System.Text;

namespace MozaPlugin
{
    /// <summary>
    /// Thin wrapper over <c>SimHub.Logging.Current</c> that mirrors every
    /// emitted line into an in-process ring buffer for the Diagnostics tab's
    /// export bundle. The wrapper is the single source of truth for [AZOM]
    /// log lines, so the export never has to read SimHub's rolling files
    /// (which buffer to disk and use varying paths/extensions per build).
    /// All call sites in the plugin should use this class instead of
    /// <c>SimHub.Logging.Current</c> directly.
    /// </summary>
    public static class MozaLog
    {
        // Cap covers many sessions of dense [AZOM] traffic. Older lines drop
        // silently; the export pulls a chronological snapshot on demand.
        private const int MaxLines = 5000;

        private static readonly LinkedList<string> _lines = new LinkedList<string>();
        private static readonly object _gate = new object();

        public static void Info(string message)
        {
            try { SimHub.Logging.Current.Info(message); } catch { }
            Record("INFO", message);
        }

        public static void Debug(string message)
        {
            try { SimHub.Logging.Current.Debug(message); } catch { }
            Record("DEBUG", message);
        }

        public static void Warn(string message)
        {
            try { SimHub.Logging.Current.Warn(message); } catch { }
            Record("WARN", message);
        }

        public static void Error(string message)
        {
            try { SimHub.Logging.Current.Error(message); } catch { }
            Record("ERROR", message);
        }

        private static void Record(string level, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";
            lock (_gate)
            {
                _lines.AddLast(line);
                while (_lines.Count > MaxLines)
                    _lines.RemoveFirst();
            }
        }

        public static int Count
        {
            get { lock (_gate) return _lines.Count; }
        }

        /// <summary>Snapshot the buffered lines as a single newline-joined string.</summary>
        public static string Snapshot()
        {
            lock (_gate)
            {
                if (_lines.Count == 0) return string.Empty;
                var sb = new StringBuilder(_lines.Count * 80);
                foreach (var l in _lines)
                    sb.Append(l).Append('\n');
                return sb.ToString();
            }
        }

        // Number of trailing characters left visible when redacting an
        // identifier (serial number, MCU UID hex). Short enough to avoid
        // leaking the full ID, long enough to disambiguate when comparing
        // logs to a physical sticker.
        private const int RedactTailChars = 4;

        /// <summary>
        /// Redact a string identifier, leaving only the last
        /// <see cref="RedactTailChars"/> characters visible. Returns "—" for
        /// null/empty, all-asterisks if the value is shorter than the tail.
        /// </summary>
        public static string RedactId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "—";
            if (s.Length <= RedactTailChars) return new string('*', s.Length);
            return new string('*', s.Length - RedactTailChars) + s.Substring(s.Length - RedactTailChars);
        }

        /// <summary>
        /// Hex-encode a byte array and redact all but the trailing
        /// <see cref="RedactTailChars"/> hex characters. Returns "—" for
        /// null/empty.
        /// </summary>
        public static string RedactBytesHex(byte[] b)
        {
            if (b == null || b.Length == 0) return "—";
            var hex = BitConverter.ToString(b).Replace("-", "");
            return RedactId(hex);
        }
    }
}
