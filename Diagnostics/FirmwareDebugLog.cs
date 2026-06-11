using System;
using System.Collections.Generic;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Ring buffer of recent firmware-debug frames (raw wire group 0x0E,
    /// subtype 0x05) received from any of the wheel-bus devices (main bridge,
    /// wheel, display). The wheel firmware emits unsolicited ASCII status /
    /// log lines on this group during operation — e.g.
    /// <c>[INFO]param_manage.c:344 Table 10, Param 11 Written: 1114636288</c>.
    /// They are not part of the request/response protocol and the rest of the
    /// plugin ignores them, but they carry useful firmware-side visibility
    /// (parameter writes, init traces, occasional warnings) so we capture the
    /// most recent into a small ring buffer for the Diagnostics tab and bundle
    /// export.
    ///
    /// Thread safety: a single shared lock guards all mutations. Writers run
    /// on the serial-read thread (one append per inbound 0x0E frame); the UI
    /// thread reads via <see cref="Snapshot"/>.
    /// </summary>
    public sealed class FirmwareDebugLog
    {
        /// <summary>Per-entry record exposed to readers.</summary>
        public readonly struct Entry
        {
            public readonly DateTime TimestampUtc;
            public readonly byte RawDeviceId;
            public readonly string SourceName;
            public readonly string Text;

            public Entry(DateTime timestampUtc, byte rawDeviceId, string sourceName, string text)
            {
                TimestampUtc = timestampUtc;
                RawDeviceId = rawDeviceId;
                SourceName = sourceName;
                Text = text;
            }
        }

        /// <summary>
        /// Summary of the wheel's recent param-read/write failure rate, used to
        /// detect the legacy-"CS" Table 8 storm (firmware spews
        /// <c>param_manage.c:424 Table 8: Failed to Read Parameter N</c> when the
        /// plugin reads parameters that wheel doesn't implement). PitHouse on the
        /// same hardware produces zero such lines, so any sustained rate is a
        /// plugin-caused fault. Consumed by the header warning banner and the
        /// runtime read backoff.
        /// </summary>
        public readonly struct FirmwareErrorState
        {
            /// <summary>True once the failure rate crosses the storm threshold.</summary>
            public readonly bool StormActive;
            /// <summary>Failures seen within the rolling window.</summary>
            public readonly int RecentFailCount;
            /// <summary>UTC of the first failure in the current burst (default if none).</summary>
            public readonly DateTime FirstSeenUtc;
            /// <summary>A representative failure line for diagnostics/UI.</summary>
            public readonly string SampleLine;

            public FirmwareErrorState(bool stormActive, int recentFailCount,
                DateTime firstSeenUtc, string sampleLine)
            {
                StormActive = stormActive;
                RecentFailCount = recentFailCount;
                FirstSeenUtc = firstSeenUtc;
                SampleLine = sampleLine;
            }
        }

        // Cap covers a few minutes of dense init chatter (observed ~4/s during
        // active dashboard switches, lower at idle). Older entries drop
        // silently. Each entry is small (timestamp + ~60 char string), so the
        // total memory footprint at full capacity is < 32 kB.
        private const int MaxEntries = 256;

        private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
        private readonly object _gate = new object();
        private long _totalReceived;

        // Param-failure storm detection. The wheel firmware logs one
        // "Failed to Read/Write Parameter N" line per parameter it can't service
        // (the legacy bare-"CS" wheel sweeps 0..127). We track the timestamps of
        // recent failures in a small ring and call it a storm once the count in
        // the trailing window crosses the threshold. PitHouse never emits these,
        // so the threshold only needs to clear stray noise — a sustained burst is
        // always a plugin-caused fault. Guarded by the same _gate as the ring
        // buffer (a private leaf lock taken only on the serial-read thread's
        // capture path and the UI reader — never on the ack path, so it cannot
        // deadlock the Tick→ack flow).
        private const int StormWindowSeconds = 10;
        // 3 is safe against false positives: the detector only matches
        // "Failed to Read/Write Parameter" lines, which PitHouse never emits (the
        // benign startup error_code 40/41/42 lines do NOT match this pattern).
        private const int StormThreshold = 3;
        private const int MaxFailTimestamps = 256;
        private readonly LinkedList<DateTime> _failTimestamps = new LinkedList<DateTime>();
        private DateTime _firstFailUtc;
        private string _lastFailSample = string.Empty;
        // Latch so the storm is logged once per connection (a record for users
        // who never open the plugin pane), not once per failing parameter.
        private bool _stormLogged;

        private static bool IsParamFailure(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("Failed to Read Parameter", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Failed to Write Parameter", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PruneFailTimestamps(DateTime nowUtc)
        {
            // Caller holds _gate.
            var cutoff = nowUtc - TimeSpan.FromSeconds(StormWindowSeconds);
            while (_failTimestamps.Count > 0 && _failTimestamps.First.Value < cutoff)
                _failTimestamps.RemoveFirst();
            if (_failTimestamps.Count == 0)
                _firstFailUtc = default;
        }

        /// <summary>
        /// Record a firmware-debug frame. <paramref name="rawDeviceId"/> is the
        /// nibble-swapped device byte from the wire (0x21 main bridge,
        /// 0x71 wheel, 0xB1 display) — passed in raw form so the source label
        /// can be derived locally and the caller doesn't need to know the swap.
        /// Empty text is recorded too (continuation frames split mid-string)
        /// so the ordering between adjacent fragments is preserved.
        /// </summary>
        public void Record(byte rawDeviceId, string text)
        {
            var entry = new Entry(
                DateTime.UtcNow,
                rawDeviceId,
                SourceNameFor(rawDeviceId),
                text ?? string.Empty);
            lock (_gate)
            {
                _entries.AddLast(entry);
                while (_entries.Count > MaxEntries)
                    _entries.RemoveFirst();
                _totalReceived++;

                if (IsParamFailure(entry.Text))
                {
                    if (_failTimestamps.Count == 0)
                        _firstFailUtc = entry.TimestampUtc;
                    _failTimestamps.AddLast(entry.TimestampUtc);
                    while (_failTimestamps.Count > MaxFailTimestamps)
                        _failTimestamps.RemoveFirst();
                    _lastFailSample = entry.Text;
                    PruneFailTimestamps(entry.TimestampUtc);

                    if (!_stormLogged && _failTimestamps.Count >= StormThreshold)
                    {
                        _stormLogged = true;
                        MozaLog.Warn(
                            "[AZOM] Wheel firmware param-read storm detected " +
                            $"({_failTimestamps.Count}+ failures in {StormWindowSeconds}s): \"{entry.Text}\". " +
                            "Enable serial capture (AZOM About tab) to diagnose immediately.");
                    }
                }
            }
        }

        /// <summary>
        /// Current param-failure storm verdict, evaluated against the trailing
        /// window. Safe to call from the UI thread. Returns a default (no storm)
        /// state when no recent failures are held.
        /// </summary>
        public FirmwareErrorState GetFirmwareErrorState()
        {
            lock (_gate)
            {
                PruneFailTimestamps(DateTime.UtcNow);
                int count = _failTimestamps.Count;
                bool storm = count >= StormThreshold;
                return new FirmwareErrorState(
                    storm, count,
                    count > 0 ? _firstFailUtc : default,
                    count > 0 ? _lastFailSample : string.Empty);
            }
        }

        /// <summary>Number of entries currently held in the ring buffer.</summary>
        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>Total number of frames recorded across the connection
        /// lifetime (including ones that fell out of the ring). Diagnostics
        /// displays this so the user can see firmware-chatter rate.</summary>
        public long TotalReceived
        {
            get { lock (_gate) return _totalReceived; }
        }

        /// <summary>Snapshot the ring buffer as an immutable array. Returns
        /// entries oldest-first. Safe to call from the UI thread; the array
        /// is independent of the internal list.</summary>
        public Entry[] Snapshot()
        {
            lock (_gate)
            {
                var arr = new Entry[_entries.Count];
                int i = 0;
                foreach (var e in _entries) arr[i++] = e;
                return arr;
            }
        }

        /// <summary>Clear all recorded entries. Called on connection
        /// open/close to drop chatter from a prior session.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _totalReceived = 0;
                _failTimestamps.Clear();
                _firstFailUtc = default;
                _lastFailSample = string.Empty;
                _stormLogged = false;
            }
        }

        /// <summary>Map the raw (nibble-swapped) wire device byte to a
        /// human-readable source label for diagnostics. Unknown devices fall
        /// back to a hex display so unmapped firmware sources are still
        /// distinguishable in the log.</summary>
        private static string SourceNameFor(byte rawDeviceId)
        {
            // Wire device byte = swap(actual device id).
            // 0x21 ↔ 0x12 = main bridge
            // 0x71 ↔ 0x17 = wheel
            // 0xB1 ↔ 0x1B = display sub-device
            switch (rawDeviceId)
            {
                case 0x21: return "main";
                case 0x71: return "wheel";
                case 0xB1: return "display";
                default:   return $"dev=0x{rawDeviceId:X2}";
            }
        }
    }
}
