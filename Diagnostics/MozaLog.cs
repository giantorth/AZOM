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
        // Gates the per-frame Debug lines (the WIRE session-chunk diag and the
        // firmware-debug echo on the serial read thread, the FSR1 gap resolve on
        // the display tick) — each pays caller string interpolation + a ring
        // insert under the global lock at frame rate. Everything else logs
        // unconditionally. Default OFF so steady-state frame traffic can't evict
        // the connect/handshake history from the ring; the wire trace carries the
        // same bytes into the same bundle. MozaPluginSettings.VerboseWireDebugLog
        // turns it back on. Applies before settings load too, so a slow cold start
        // doesn't log frames the user never asked for.
        public static volatile bool WireDebugEnabled = false;

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

        /// <summary>
        /// Debug line that goes to SimHub's log but NOT the ring buffer. For
        /// high-rate output that is already captured elsewhere in the bundle
        /// (the firmware-debug echo has its own ring in <c>FirmwareDebugLog</c>
        /// plus the wire trace) — mirroring it here evicted everything else.
        /// </summary>
        public static void DebugNoRing(string message)
        {
            try { SimHub.Logging.Current.Debug(message); } catch { }
        }

        // Repeat-suppression state for DebugIfChanged, keyed per call site.
        private static readonly Dictionary<string, KeyValuePair<string, DateTime>> _lastByKey =
            new Dictionary<string, KeyValuePair<string, DateTime>>();
        private static readonly object _repeatGate = new object();
        // A suppressed line is re-emitted this often even when unchanged, so a
        // bundle pulled hours in still shows the current state.
        private const double RepeatRefreshMinutes = 5.0;

        /// <summary>
        /// Debug line emitted only when <paramref name="message"/> differs from the
        /// last one logged under <paramref name="key"/> (or once every
        /// <see cref="RepeatRefreshMinutes"/> minutes regardless). For steady-state
        /// poll output: the 5 s reconnect tick emitted five identical lines per tick,
        /// which filled the entire 5 000-line ring in ~40 minutes and pushed the
        /// connect/handshake history out of every bug report. Callers that run on
        /// more than one pipe must fold the pipe label into the key.
        /// </summary>
        public static void DebugIfChanged(string key, string message)
        {
            if (string.IsNullOrEmpty(key)) { Debug(message); return; }
            var now = DateTime.UtcNow;
            lock (_repeatGate)
            {
                if (_lastByKey.TryGetValue(key, out var prev)
                    && prev.Key == message
                    && (now - prev.Value).TotalMinutes < RepeatRefreshMinutes)
                    return;
                _lastByKey[key] = new KeyValuePair<string, DateTime>(message, now);
            }
            Debug(message);
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
            // Invariant: the ':' separator and the calendar follow the OS culture
            // otherwise, and the bundle tooling parses this prefix.
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                + " " + level.PadRight(5) + " " + message;
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
