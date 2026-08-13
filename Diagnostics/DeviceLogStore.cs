using System;
using System.Collections.Generic;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Ring buffer of log lines pulled from the wheel display's own logger via
    /// the session FF <c>kind=14</c> request / <c>kind=15</c> receipt pair
    /// (<see cref="DeviceLogParser"/>). The display runs a Linux
    /// <c>MOZADash</c> application; these lines carry its backtraces, memory
    /// stats, dashboard-load state and render errors.
    ///
    /// Deliberately SEPARATE from <see cref="FirmwareDebugLog"/>: that one
    /// holds the unsolicited group-0x0E ASCII chatter from the base / wheel /
    /// pedal MCUs, in a ring sized (256) for short one-line messages. A single
    /// log pull delivers up to 100 lines of several hundred bytes each and
    /// would flush that ring completely. Different source, different format,
    /// different retention.
    ///
    /// Thread safety: a single shared lock guards all mutations. The writer is
    /// the serial read thread (one <see cref="Record"/> per completed kind=14
    /// payload); the UI thread reads via <see cref="Snapshot"/>.
    /// </summary>
    public sealed class DeviceLogStore
    {
        /// <summary>Per-entry record exposed to readers.</summary>
        public readonly struct Entry
        {
            /// <summary>When the host received the line, not when the device
            /// logged it — device lines carry their own timestamp in the text.</summary>
            public readonly DateTime ReceivedUtc;
            /// <summary>Which display produced the line ("wheel", "dash", …).
            /// A rig can run a wheel screen and a CM2 dash concurrently, each
            /// with its own logger, and both land in this one ring.</summary>
            public readonly string Source;
            public readonly string Text;

            public Entry(DateTime receivedUtc, string source, string text)
            {
                ReceivedUtc = receivedUtc;
                Source = source;
                Text = text;
            }
        }

        // ~1000 lines ≈ 10 pulls of 100. At an observed 150–400 B per line
        // that is well under 1 MB, and covers enough history that a fault is
        // still present when the user exports a bundle.
        private const int MaxEntries = 1000;

        private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
        private readonly object _gate = new object();
        private long _totalRecorded;
        // Dedup state is per-source: a wheel screen and a CM2 dash pull
        // independently, and device B's block must not be suppressed by
        // device A's.
        private readonly Dictionary<string, DedupState> _dedup =
            new Dictionary<string, DedupState>(StringComparer.Ordinal);

        private sealed class DedupState
        {
            public string? LastText;
            public int LastBlockHash;
            public int LastBlockLength;
        }

        /// <summary>Order-sensitive hash of a whole payload, for the
        /// re-delivered-block guard. Not cryptographic — a collision only costs
        /// one dropped payload, and the length is checked alongside it.</summary>
        private static int HashBlock(string[] lines)
        {
            unchecked
            {
                int h = 17;
                foreach (var line in lines)
                    h = h * 31 + (line?.GetHashCode() ?? 0);
                return h;
            }
        }

        /// <summary>
        /// Record one pull's worth of lines (oldest-first, as the device sends
        /// them). Two dedup guards, both for the case where our kind=15 receipt
        /// is lost and the device re-delivers the same head-of-buffer block on
        /// the next pull:
        /// <list type="bullet">
        /// <item>a payload identical to the previous one is dropped whole;</item>
        /// <item>a line identical to the immediately preceding one is skipped.</item>
        /// </list>
        /// Note the caller must still send the receipt for a dropped block —
        /// that is what makes the device finally discard it.
        /// </summary>
        public void Record(string source, string[] lines)
        {
            if (lines == null || lines.Length == 0) return;
            source = string.IsNullOrEmpty(source) ? "?" : source;
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                if (!_dedup.TryGetValue(source, out var st))
                {
                    st = new DedupState();
                    _dedup[source] = st;
                }

                int blockHash = HashBlock(lines);
                if (blockHash == st.LastBlockHash && st.LastBlockLength == lines.Length)
                    return;
                st.LastBlockHash = blockHash;
                st.LastBlockLength = lines.Length;

                foreach (var line in lines)
                {
                    string text = line ?? string.Empty;
                    if (text.Length == 0) continue;
                    if (string.Equals(text, st.LastText, StringComparison.Ordinal)) continue;
                    st.LastText = text;
                    _entries.AddLast(new Entry(now, source, text));
                    while (_entries.Count > MaxEntries)
                        _entries.RemoveFirst();
                    _totalRecorded++;
                }
            }
        }

        /// <summary>Number of lines currently held in the ring buffer.</summary>
        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>Lines recorded across the connection lifetime, including
        /// ones that have fallen out of the ring. Post-dedup — re-delivered
        /// blocks are not counted twice.</summary>
        public long TotalRecorded
        {
            get { lock (_gate) return _totalRecorded; }
        }

        /// <summary>Snapshot the ring as an immutable array, oldest-first.
        /// Safe to call from the UI thread.</summary>
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

        /// <summary>Clear all recorded lines. Called on connection open/close
        /// so a prior device's log doesn't linger.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _totalRecorded = 0;
                _dedup.Clear();
            }
        }
    }
}
