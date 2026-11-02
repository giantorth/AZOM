using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Process-wide, always-on ring of timestamped serial frames, split into two
    /// segments so a bug report keeps the connect/handshake even after hours of
    /// running:
    ///   • startup — every frame in the first <see cref="StartupWindowMs"/> ms
    ///     (capped at <see cref="StartupByteCap"/>). Frozen once either bound is
    ///     hit; never trimmed, so the cold-start handshake is always retained.
    ///   • rolling — begins only once the startup segment has frozen, then holds
    ///     the last <see cref="RollingWindowMs"/> ms of (post-startup) traffic,
    ///     hard-bounded by <see cref="RollingByteCap"/> (byte cap is the RAM
    ///     ceiling; age-trim gives the wall-clock window at low frame rates). The
    ///     first minute lives only in the startup segment, never duplicated here.
    /// Both segments are lock-free (ConcurrentQueue + Interlocked) — the serial
    /// read thread's Record path must never take a lock. RAM only: nothing is
    /// written to disk unless the developer JSONL sink is explicitly opened.
    /// The <see cref="_startTicks"/> clock is set once and survives the
    /// game-switch plugin reload (this is a singleton on the persistent wire),
    /// so the startup segment reflects the true first launch.
    /// Also keeps an optional always-on JSONL sink that mirrors the layout
    /// produced by <c>sim/bridge.py</c> for capture-comparison tooling.
    /// </summary>
    public sealed class SerialTrafficCapture
    {
        public static SerialTrafficCapture Instance { get; } = new SerialTrafficCapture();

        // Startup segment: first minute, hard-capped ~4 MB. Rolling segment:
        // last 5 minutes, hard-capped ~8 MB. Total in-memory ceiling ≈ 12 MB
        // regardless of frame rate — the byte caps are the load-bearing bound;
        // the time windows just shape coverage.
        private const long StartupWindowMs = 60_000;
        private const long StartupByteCap = 4L * 1024 * 1024;
        private const long RollingWindowMs = 300_000;
        private const long RollingByteCap = 8L * 1024 * 1024;

        // MOZA frames are tiny (often 5-50 bytes), so per-entry object overhead
        // (Entry + byte[] header + queue slot, x86) dwarfs the payload. The caps
        // account for this fixed overhead per frame so they bound real RAM, not
        // just summed payload — otherwise 8 MB of payload could be 30+ MB live.
        private const int EntryOverheadBytes = 48;

        public enum Direction : byte { Tx = (byte)'T', Rx = (byte)'R' }

        public sealed class Entry
        {
            public DateTime TimestampUtc;
            public Direction Dir;
            public string Source = string.Empty;
            public byte[] Bytes = Array.Empty<byte>();
        }

        // Startup segment (frozen after the window/cap) and rolling ring.
        private readonly ConcurrentQueue<Entry> _startupEntries = new ConcurrentQueue<Entry>();
        private readonly ConcurrentQueue<Entry> _rollingEntries = new ConcurrentQueue<Entry>();
        private long _startupBytes;
        private long _rollingBytes;
        private int _startupCount;
        private int _rollingCount;

        private volatile bool _enabled;
        // Capture clock, in UTC ticks. Set once via CompareExchange on the first
        // recorded frame; 0 means "not started". Never reset.
        private long _startTicks;

        // Always-on JSONL sink (bridge-format). Independent of in-memory ring.
        // _fileLock serializes open/close; the hot path reads _fileSink once
        // without it (null = closed, the common case).
        private readonly object _fileLock = new object();
        private volatile FileSink? _fileSink;
        private const int FileSinkQueueCap = 20000;
        private const int FileSinkFlushIntervalMs = 100;

        // One open sink. RecordTx/RecordRx run on the serial write and read
        // threads, so lines go into a bounded queue drained by a background
        // thread that owns the StreamWriter (flushes ~10×/s). All writer state
        // is per instance: a writer that outlives its Close join (disk stall)
        // keeps its own stop flag and queue and can't steal the next sink's lines.
        private sealed class FileSink
        {
            private readonly StreamWriter _writer;
            private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private readonly Thread _thread;
            private volatile bool _stop;
            private int _queued;

            internal FileSink(StreamWriter writer)
            {
                _writer = writer;
                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "moza-wire-sink",
                };
            }

            internal void Start() => _thread.Start();

            internal void Enqueue(string line)
            {
                // Writer has fallen behind (disk stall): drop rather than grow.
                if (Volatile.Read(ref _queued) >= FileSinkQueueCap) return;
                int queued = Interlocked.Increment(ref _queued);
                _queue.Enqueue(line);
                // Wake the writer on the empty→non-empty edge; it drains everything
                // per wake and has a timed backstop.
                if (queued == 1)
                {
                    try { _signal.Set(); } catch (ObjectDisposedException) { }
                }
            }

            /// <summary>Signal the writer to drain, flush and dispose, waiting at
            /// most <paramref name="joinMs"/>. The writer never takes _fileLock.</summary>
            internal void Close(int joinMs)
            {
                _stop = true;
                try { _signal.Set(); } catch (ObjectDisposedException) { }
                try { _thread.Join(joinMs); } catch { }
            }

            private void Loop()
            {
                try
                {
                    int lastFlush = Environment.TickCount;
                    bool dirty = false;
                    while (true)
                    {
                        _signal.WaitOne(FileSinkFlushIntervalMs);
                        while (_queue.TryDequeue(out var line))
                        {
                            Interlocked.Decrement(ref _queued);
                            _writer.WriteLine(line);
                            dirty = true;
                        }
                        bool stopping = _stop && _queue.IsEmpty;
                        if (dirty && (stopping || unchecked(Environment.TickCount - lastFlush) >= FileSinkFlushIntervalMs))
                        {
                            _writer.Flush();
                            lastFlush = Environment.TickCount;
                            dirty = false;
                        }
                        if (stopping) break;
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Wire-trace file sink writer stopped: {ex.Message}");
                }
                finally
                {
                    try { _writer.Flush(); } catch { }
                    try { _writer.Dispose(); } catch { }
                    try { _signal.Dispose(); } catch { }
                }
            }
        }

        public bool Enabled => _enabled;
        public int Count => Volatile.Read(ref _startupCount) + Volatile.Read(ref _rollingCount);
        public DateTime StartedAtUtc
        {
            get
            {
                long t = Interlocked.Read(ref _startTicks);
                return t == 0 ? default : new DateTime(t, DateTimeKind.Utc);
            }
        }

        // Always-on cumulative byte counters — incremented on every RecordTx /
        // RecordRx regardless of whether the in-memory ring is enabled. Powers
        // the Diagnostics-tab bandwidth sparklines.
        private long _totalRxBytes;
        private long _totalTxBytes;
        public long TotalRxBytes => Volatile.Read(ref _totalRxBytes);
        public long TotalTxBytes => Volatile.Read(ref _totalTxBytes);

        private SerialTrafficCapture() { }

        /// <summary>
        /// Idempotent enable: turns capture on without clearing the buffer or
        /// resetting the capture clock. No-op when already enabled. This is the
        /// primary entry point — MozaPlugin.Init calls it before any device
        /// traffic so the startup segment covers the full connect/handshake,
        /// and it survives the game-switch plugin reload.
        /// </summary>
        public void EnsureRunning()
        {
            _enabled = true;
        }

        /// <summary>
        /// Open a JSONL sink at <paramref name="path"/>. Each subsequent Tx/Rx is
        /// written as a single bridge-compatible JSON line. Independent of
        /// <see cref="Enabled"/> — file sink writes whether the in-memory ring
        /// is on or off.
        /// </summary>
        public void StartFileSink(string path)
        {
            lock (_fileLock)
            {
                CloseFileSinkLocked();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                // AutoFlush=true forced sync flush per frame which under Wine
                // blocks the read/write hot path for milliseconds → telemetry
                // tick stall → visible test-mode lag. Use OS buffering; flush
                // periodically (every ~64 lines) to keep crashes from losing
                // more than a fraction of a second.
                var writer = new StreamWriter(new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.Read,
                    bufferSize: 16384))
                {
                    AutoFlush = false,
                };
                var sink = new FileSink(writer);
                sink.Start();
                _fileSink = sink;
            }
        }

        public void StopFileSink()
        {
            lock (_fileLock) CloseFileSinkLocked();
        }

        private void CloseFileSinkLocked()
        {
            var sink = _fileSink;
            _fileSink = null;
            // A Record that read the old instance just before the swap enqueues
            // into it, never into the next sink.
            sink?.Close(2000);
        }

        /// <summary>Ordered snapshot of the frozen first-minute startup segment.</summary>
        public IReadOnlyList<Entry> SnapshotStartup()
        {
            var list = new List<Entry>(Volatile.Read(ref _startupCount));
            foreach (var e in _startupEntries) list.Add(e);
            return list;
        }

        /// <summary>Ordered snapshot of the rolling last-N-minutes segment.</summary>
        public IReadOnlyList<Entry> SnapshotRolling()
        {
            var list = new List<Entry>(Volatile.Read(ref _rollingCount));
            foreach (var e in _rollingEntries) list.Add(e);
            return list;
        }

        /// <summary>Record a sent frame. The bytes are copied: senders may reuse
        /// or patch their frame buffers.</summary>
        public void RecordTx(string source, byte[] frame) => Record(Direction.Tx, source, frame, copy: true);

        /// <summary>Record a received frame without copying. The caller hands over
        /// a buffer that is never written again (the read loop allocates one per
        /// frame and inbound handlers only read it).</summary>
        public void RecordRx(string source, byte[] frame) => Record(Direction.Rx, source, frame, copy: false);

        private void Record(Direction dir, string source, byte[] frame, bool copy)
        {
            if (frame == null || frame.Length == 0) return;
            // Cumulative byte counters — always-on, drives the Diagnostics-tab
            // bandwidth sparklines independent of the ring buffer enable state.
            if (dir == Direction.Rx) Interlocked.Add(ref _totalRxBytes, frame.Length);
            else                     Interlocked.Add(ref _totalTxBytes, frame.Length);

            // File sink — always writes when open, even if ring is off.
            // Lock-free gate: skip the entire StringBuilder + JSON serialise
            // when no sink is open (the common case).
            var sink = _fileSink;
            if (sink != null)
                WriteFileSinkLine(sink, dir, frame);

            if (!_enabled) return;

            var now = DateTime.UtcNow;
            // Set the capture clock exactly once. CompareExchange returns the
            // prior value; if it was 0 we won the race and now.Ticks is the start.
            long prior = Interlocked.CompareExchange(ref _startTicks, now.Ticks, 0);
            long start = prior == 0 ? now.Ticks : prior;
            long elapsedMs = (now.Ticks - start) / TimeSpan.TicksPerMillisecond;

            byte[] bytes = frame;
            if (copy)
            {
                bytes = new byte[frame.Length];
                Buffer.BlockCopy(frame, 0, bytes, 0, frame.Length);
            }
            var entry = new Entry
            {
                TimestampUtc = now,
                Dir = dir,
                Source = source ?? string.Empty,
                Bytes = bytes,
            };

            int cost = frame.Length + EntryOverheadBytes;

            // During the startup window frames go ONLY to the startup segment;
            // the rolling segment doesn't begin until startup freezes (window
            // elapsed or byte cap hit). This keeps the first minute out of the
            // rolling ring and avoids storing it twice. A couple of frames may
            // slip past the bound under concurrency; harmless.
            if (elapsedMs < StartupWindowMs && Volatile.Read(ref _startupBytes) < StartupByteCap)
            {
                _startupEntries.Enqueue(entry);
                Interlocked.Add(ref _startupBytes, cost);
                Interlocked.Increment(ref _startupCount);
                return;
            }

            // Startup is frozen — feed the rolling segment, then trim by byte cap and age.
            _rollingEntries.Enqueue(entry);
            Interlocked.Add(ref _rollingBytes, cost);
            Interlocked.Increment(ref _rollingCount);

            // Byte-cap trim: drop oldest until back inside cap.
            while (Volatile.Read(ref _rollingBytes) > RollingByteCap
                   && _rollingEntries.TryDequeue(out var dropped))
            {
                Interlocked.Add(ref _rollingBytes, -(dropped.Bytes.Length + EntryOverheadBytes));
                Interlocked.Decrement(ref _rollingCount);
            }
            // Age trim: drop the head while it is older than the window. Peek to
            // test age; the dequeued entry may differ from the peeked one under
            // concurrency, but we subtract its actual cost so counters stay
            // consistent (cap is approximate, matching the byte-trim above).
            long cutoff = now.Ticks - RollingWindowMs * TimeSpan.TicksPerMillisecond;
            while (_rollingEntries.TryPeek(out var head) && head.TimestampUtc.Ticks < cutoff)
            {
                if (!_rollingEntries.TryDequeue(out var aged)) break;
                Interlocked.Add(ref _rollingBytes, -(aged.Bytes.Length + EntryOverheadBytes));
                Interlocked.Decrement(ref _rollingCount);
            }
        }

        private static void WriteFileSinkLine(FileSink sink, Direction dir, byte[] frame)
        {
            // Bridge-compatible JSONL: {"t":..., "dir":"h2b"|"b2h", "len":N, "ok":true,
            //                            "hex":"...", "grp":..., "dev":..., "payload":"..."}
            // Tx (host→device) = h2b; Rx (device→host) = b2h.
            // Frame layout: 7E [N] grp dev payload[N] cs. Skip when frame is too short.
            // The line is formatted here (cheap, allocation only) and handed to
            // the writer thread.

            var sb = new StringBuilder(frame.Length * 2 + 96);
            double t = (DateTime.UtcNow - _epoch).TotalSeconds;
            sb.Append("{\"t\":");
            sb.Append(t.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"dir\":\"");
            sb.Append(dir == Direction.Tx ? "h2b" : "b2h");
            sb.Append("\",\"len\":");
            sb.Append(frame.Length);
            sb.Append(",\"ok\":true,\"hex\":\"");
            for (int i = 0; i < frame.Length; i++)
            {
                sb.Append(HexChar(frame[i] >> 4));
                sb.Append(HexChar(frame[i] & 0xF));
            }
            sb.Append('"');
            // grp/dev/payload extraction. Two shapes:
            //   * Tx: full wire frame `7E [N] grp dev <body> cs` — body length = frame.Length - 5
            //     Two N conventions exist:
            //       legacy (VGS/F1):  N = body.Length excluding grp+dev (= cmd+prefix+...+data)
            //                         frame.Length = N + 5
            //       Type02 (CSP/W17): N = body.Length INCLUDING grp+dev
            //                         frame.Length = N + 3
            //     We don't trust N for slicing — instead, payload spans from offset 4 to
            //     frame.Length-1 (= just before checksum). This works for both conventions.
            //   * Rx: parsed message (FrameSplitter already stripped framing) —
            //     starts directly with `grp dev payload...`, no checksum.
            int grp = -1, dev = -1, payStart = -1, payEnd = -1;
            if (frame.Length >= 6 && frame[0] == 0x7E)
            {
                grp = frame[2];
                dev = frame[3];
                payStart = 4;
                payEnd = frame.Length - 1; // last byte is checksum
            }
            else if (frame.Length >= 2)
            {
                grp = frame[0];
                dev = frame[1];
                payStart = 2;
                payEnd = frame.Length;
            }
            if (grp >= 0 && dev >= 0 && payStart >= 0)
            {
                sb.Append(",\"grp\":");
                sb.Append(grp);
                sb.Append(",\"dev\":");
                sb.Append(dev);
                sb.Append(",\"payload\":\"");
                for (int i = payStart; i < payEnd; i++)
                {
                    sb.Append(HexChar(frame[i] >> 4));
                    sb.Append(HexChar(frame[i] & 0xF));
                }
                sb.Append('"');
            }
            sb.Append('}');
            sink.Enqueue(sb.ToString());
        }

        private static readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Per-entry line prefix (timestamp, direction, source) for the
        /// redacting formatter.
        /// </summary>
        internal static void AppendEntryPrefix(StringBuilder sb, Entry e)
        {
            var local = e.TimestampUtc.ToLocalTime();
            // Invariant: ':' is the culture's time separator (fi/sv render '.'),
            // and tools/ parse this column.
            sb.Append(local.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append((char)e.Dir);
            sb.Append("  ");
            sb.Append(e.Source.PadRight(10));
            sb.Append(' ');
        }

        internal static char HexChar(int n) => (char)(n < 10 ? '0' + n : 'a' + (n - 10));
    }
}
