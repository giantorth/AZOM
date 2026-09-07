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
        // recorded frame; 0 means "not started". Never reset by EnsureRunning.
        private long _startTicks;

        // Always-on JSONL sink (bridge-format). Independent of in-memory ring.
        private readonly object _fileLock = new object();
        private StreamWriter? _fileSink;
        private string? _fileSinkPath;
        // Lock-free fast path for the common case where the file sink is
        // closed. RecordTx/RecordRx fires on every serial frame (~250-1000Hz);
        // taking _fileLock just to read a null pointer is wasteful contention.
        // The flag is written under _fileLock so it stays consistent with
        // _fileSink, but read without it on the hot path.
        private volatile bool _fileSinkEnabled;
        // Off-thread writer. RecordTx/RecordRx run on the serial write and read
        // threads; a WriteLine plus a periodic synchronous Flush under _fileLock on
        // both of them was the hot-path stall the buffered sink was meant to
        // avoid. Lines go into a bounded queue drained by one background thread
        // that owns the StreamWriter for its lifetime (flushes ~10×/s).
        private readonly ConcurrentQueue<string> _fileSinkQueue = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _fileSinkSignal = new AutoResetEvent(false);
        private Thread? _fileSinkThread;
        private volatile bool _fileSinkStop;
        private int _fileSinkQueued;
        private long _fileSinkDropped;
        private const int FileSinkQueueCap = 20000;
        private const int FileSinkFlushIntervalMs = 100;
        /// <summary>Lines dropped because the writer fell behind the queue cap.</summary>
        public long FileSinkDroppedLines => Volatile.Read(ref _fileSinkDropped);

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
        public string? FileSinkPath => _fileSinkPath;

        // Per-segment stats for the Diagnostics-tab status line.
        public int StartupFrameCount => Volatile.Read(ref _startupCount);
        public long StartupByteSize => Volatile.Read(ref _startupBytes);
        public int RollingFrameCount => Volatile.Read(ref _rollingCount);
        public long RollingByteSize => Volatile.Read(ref _rollingBytes);
        public bool StartupFrozen
        {
            get
            {
                long start = Interlocked.Read(ref _startTicks);
                if (start == 0) return false;
                long elapsedMs = (DateTime.UtcNow.Ticks - start) / TimeSpan.TicksPerMillisecond;
                return elapsedMs >= StartupWindowMs || Volatile.Read(ref _startupBytes) >= StartupByteCap;
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

        /// <summary>Clear both segments and restart the capture clock.</summary>
        public void Start()
        {
            Clear();
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
                var sink = new StreamWriter(new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.Read,
                    bufferSize: 16384))
                {
                    AutoFlush = false,
                };
                _fileSink = sink;
                _fileSinkPath = path;
                _fileSinkStop = false;
                _fileSinkThread = new Thread(() => FileSinkLoop(sink))
                {
                    IsBackground = true,
                    Name = "moza-wire-sink",
                };
                _fileSinkThread.Start();
                _fileSinkEnabled = true;
            }
        }

        public void StopFileSink()
        {
            lock (_fileLock) CloseFileSinkLocked();
        }

        private void CloseFileSinkLocked()
        {
            _fileSinkEnabled = false;
            var t = _fileSinkThread;
            _fileSinkThread = null;
            _fileSinkStop = true;
            _fileSinkSignal.Set();
            // The writer drains what is queued, then flushes and disposes the
            // stream itself; it never takes _fileLock, so joining here is safe.
            if (t != null) { try { t.Join(2000); } catch { } }
            while (_fileSinkQueue.TryDequeue(out _)) { }
            Volatile.Write(ref _fileSinkQueued, 0);
            _fileSink = null;
            _fileSinkPath = null;
        }

        private void FileSinkLoop(StreamWriter sink)
        {
            try
            {
                int lastFlush = Environment.TickCount;
                bool dirty = false;
                while (true)
                {
                    _fileSinkSignal.WaitOne(FileSinkFlushIntervalMs);
                    while (_fileSinkQueue.TryDequeue(out var line))
                    {
                        Interlocked.Decrement(ref _fileSinkQueued);
                        sink.WriteLine(line);
                        dirty = true;
                    }
                    bool stopping = _fileSinkStop && _fileSinkQueue.IsEmpty;
                    if (dirty && (stopping || unchecked(Environment.TickCount - lastFlush) >= FileSinkFlushIntervalMs))
                    {
                        sink.Flush();
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
                try { sink.Flush(); } catch { }
                try { sink.Dispose(); } catch { }
            }
        }

        /// <summary>Disable capture and return an ordered snapshot of all entries.</summary>
        public IReadOnlyList<Entry> Stop()
        {
            _enabled = false;
            var list = new List<Entry>(Count);
            foreach (var e in _startupEntries) list.Add(e);
            foreach (var e in _rollingEntries) list.Add(e);
            return list;
        }

        public void Clear()
        {
            while (_startupEntries.TryDequeue(out _)) { }
            while (_rollingEntries.TryDequeue(out _)) { }
            Volatile.Write(ref _startupBytes, 0);
            Volatile.Write(ref _rollingBytes, 0);
            Volatile.Write(ref _startupCount, 0);
            Volatile.Write(ref _rollingCount, 0);
            Interlocked.Exchange(ref _startTicks, 0);
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

        public void RecordTx(string source, byte[] frame) => Record(Direction.Tx, source, frame);
        public void RecordRx(string source, byte[] frame) => Record(Direction.Rx, source, frame);

        private void Record(Direction dir, string source, byte[] frame)
        {
            if (frame == null || frame.Length == 0) return;
            // Cumulative byte counters — always-on, drives the Diagnostics-tab
            // bandwidth sparklines independent of the ring buffer enable state.
            if (dir == Direction.Rx) Interlocked.Add(ref _totalRxBytes, frame.Length);
            else                     Interlocked.Add(ref _totalTxBytes, frame.Length);

            // File sink — always writes when open, even if ring is off.
            // Lock-free gate: skip the entire StringBuilder + JSON serialise
            // when no sink is open (the common case).
            if (_fileSinkEnabled)
                WriteFileSinkLine(dir, frame);

            if (!_enabled) return;

            var now = DateTime.UtcNow;
            // Set the capture clock exactly once. CompareExchange returns the
            // prior value; if it was 0 we won the race and now.Ticks is the start.
            long prior = Interlocked.CompareExchange(ref _startTicks, now.Ticks, 0);
            long start = prior == 0 ? now.Ticks : prior;
            long elapsedMs = (now.Ticks - start) / TimeSpan.TicksPerMillisecond;

            // Copy — caller buffers (e.g. read-loop tmp buffer) get reused.
            var copy = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, copy, 0, frame.Length);
            var entry = new Entry
            {
                TimestampUtc = now,
                Dir = dir,
                Source = source ?? string.Empty,
                Bytes = copy,
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

        private void WriteFileSinkLine(Direction dir, byte[] frame)
        {
            // Bridge-compatible JSONL: {"t":..., "dir":"h2b"|"b2h", "len":N, "ok":true,
            //                            "hex":"...", "grp":..., "dev":..., "payload":"..."}
            // Tx (host→device) = h2b; Rx (device→host) = b2h.
            // Frame layout: 7E [N] grp dev payload[N] cs. Skip when frame is too short.
            // Caller is expected to have checked _fileSinkEnabled before calling
            // (saves the work on the closed-sink hot path). The line is formatted
            // here (cheap, allocation only) and handed to the writer thread.

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
            string line = sb.ToString();
            if (Volatile.Read(ref _fileSinkQueued) >= FileSinkQueueCap)
            {
                // Writer has fallen behind (disk stall): drop rather than grow.
                Interlocked.Increment(ref _fileSinkDropped);
                return;
            }
            int queued = Interlocked.Increment(ref _fileSinkQueued);
            _fileSinkQueue.Enqueue(line);
            // Wake the writer on the empty→non-empty edge; it drains everything
            // per wake and has a timed backstop.
            if (queued == 1)
            {
                try { _fileSinkSignal.Set(); } catch (ObjectDisposedException) { }
            }
        }

        private static readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Render entries as one-line-per-frame text. Timestamps are local time
        /// to ms; bytes are space-separated uppercase hex with no prefix.
        /// </summary>
        public static string Format(IReadOnlyList<Entry> entries)
        {
            var sb = new StringBuilder(entries.Count * 64);
            sb.Append("# timestamp (local)        dir source     bytes\n");
            foreach (var e in entries)
            {
                AppendEntryPrefix(sb, e);
                AppendHex(sb, e.Bytes);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Shared per-entry line prefix (timestamp, direction, source) used by
        /// both <see cref="Format"/> and the redacting formatter.
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

        private static void AppendHex(StringBuilder sb, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(HexChar(data[i] >> 4));
                sb.Append(HexChar(data[i] & 0xF));
            }
        }

        internal static char HexChar(int n) => (char)(n < 10 ? '0' + n : 'a' + (n - 10));
    }
}
