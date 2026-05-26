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

        // Cap covers a few minutes of dense init chatter (observed ~4/s during
        // active dashboard switches, lower at idle). Older entries drop
        // silently. Each entry is small (timestamp + ~60 char string), so the
        // total memory footprint at full capacity is < 32 kB.
        private const int MaxEntries = 256;

        private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
        private readonly object _gate = new object();
        private long _totalReceived;

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
