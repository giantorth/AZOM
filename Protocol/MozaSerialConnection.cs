using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using HidSharp;
using MozaPlugin.Diagnostics;


namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Latest-wins coalescing slot identifier; one slot per kind. Use Send() for
    /// one-shot/session traffic instead.
    /// </summary>
    public enum StreamKind
    {
        TierDash0 = 0,
        TierDash1 = 1,
        TierDash2 = 2,
        TierDash3 = 3,
        TierDash4 = 4,
        TierDash5 = 5,
        TierDash6 = 6,
        TierDash7 = 7,
        Enable = 8,
        Sequence = 9,
        Mode = 10,
        // AB9 host-rendered engine vibration (0x20/0x0A 05) at ~91 Hz.
        Ab9EngineVibration = 11,
        // AB9 secondary FFB sub-streams (latest-wins per lane).
        Ab9EnginePulse = 12,
        Ab9TriggerA = 13,        // 0x0D 0x02 + 0x0D 0x03 (flat ~9 Hz keepalive)
        Ab9TriggerRpm = 14,      // 0x0D 0x05 (RPM-tracking trigger)
        Ab9TriggerExtra = 15,    // 0x0D 0x01 (newly-observed sub-cmd)
        Ab9LowRate = 16,         // 0x08 0x04 + 0x08 0x06 (signed-pair low-rate)
        // mBooster motor-write lane (single slot per connection; the worker
        // emits one frame per ~20 ms tick across all four effects, so a
        // shared lane is sufficient — latest-wins on the writer-lag edge).
        MBoosterEffect = 17,
    }

    /// <summary>Device family targeted by the serial probe fallback (registry-empty case).</summary>
    public enum MozaProbeTarget
    {
        BaseAndHub,
        Ab9,
        // mBooster has no handshake (protocol note § 1) and the multi-device
        // discovery is registry-driven; the serial-probe fallback would have
        // to write at every COM port to find a unit, which we deliberately
        // skip to keep the per-port probe surface minimal. See FindMozaPort.
        MBooster,
        // Universal Hub on its OWN dedicated connection (PID 0x0020), used when
        // a wheelbase is also present so the base stays the telemetry-driving
        // primary and the hub is enumerated in parallel. Probe fallback issues
        // only the hub probe (0x64/0x12/0x03), a single pass. The hub-ONLY case
        // (no base) is still handled by the BaseAndHub primary, which falls back
        // to the hub when no wheelbase port exists — so this target never claims
        // a hub the primary already holds (the _activePorts guard enforces it).
        HubOnly,
        // Standalone-USB pedals / handbrake on their OWN dedicated connection
        // (pedals PID 0x0001/0x0003/0x0011, handbrake 0x001F), one per
        // MozaStandalonePeripheralRegistry controller. Like MBooster, discovery
        // is registry-only by design (the PIDs are registered, so the registry
        // always classifies them) and the serial-probe fallback is force-
        // disabled — these targets never write scan bytes to unclassified COM
        // ports. Kept as distinct values so the probe shape stays honest if the
        // fallback is ever enabled; no probe-emission branch is wired today.
        PedalsOnly,
        HandbrakeOnly,
    }

    public class MozaSerialConnection : IDisposable
    {
        // Stream-slot lanes (latest-wins coalescing). Layout:
        //   0..10  — tier-def pipeline at slot-base 0 (TierDash0-7, Enable, Sequence,
        //            Mode) — the WHEEL screen pipeline, or an FSR1 driver's records.
        //   11..17 — AB9 / mBooster (absolute, per StreamKind).
        //   18..28 — a SECOND tier-def pipeline at slot-base 18 (a bus-attached CM2
        //            dash sharing this connection). See TelemetrySender.StreamSlotBase.
        // A CM2 on its own USB connection runs at base 0 on THAT connection, so the
        // second block is only used when two pipelines share one connection.
        private const int StreamSlotCount = 32;

        // Ports held by a live connection — probe path skips these (Wine pty
        // doesn't enforce O_EXCL, so a second Open would steal the device).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activePorts =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when a live sibling connection in this process currently
        /// holds <paramref name="portName"/>. Lets the plugin's primary→wheelbase
        /// migration skip a hub port already claimed by another connection.</summary>
        public static bool IsPortHeld(string? portName) =>
            !string.IsNullOrEmpty(portName) && _activePorts.ContainsKey(portName!);

        // PID filter for port discovery; null PID = probe-based (unknown).
        private readonly Func<string?, bool>? _pidFilter;
        private readonly MozaProbeTarget _probeTarget;
        // Hard-disable serial probe fallback (user-toggle via MozaPluginSettings).
        private readonly Func<bool>? _disableProbeFallback;

        private volatile IMozaPort? _port;
        private Thread? _readThread;
        private Thread? _writeThread;
        // Ports with a probe thread that timed out and was ABANDONED (its
        // SerialPort.Open() is still blocked in a Wine syscall). We must NOT
        // close/dispose that SerialPort from another thread — doing so mid-Open
        // is a native-crash vector under Wine (the original "crashes SimHub on
        // a freshly-powered base": a not-ready CDC-ACM port makes Open() hang,
        // the old timeout path force-closed it cross-thread, Wine segfaulted).
        // Instead we abandon the background thread (it self-cleans when the
        // syscall finally returns) and skip re-probing the port until it does,
        // so we never spawn a second hung thread on the same stuck port and
        // never enter the cross-thread-dispose crash window. Keyed by port name.
        private static readonly ConcurrentDictionary<string, byte> _probeInFlight =
            new ConcurrentDictionary<string, byte>();

        // Priority lane: unpaced FIFO for tiny, time-critical frames (fc:00 session
        // acks). Drained ahead of one-shot every WriteLoop iteration so an ack
        // can't get buried behind a 1000-chunk tier-def burst — the wheel times
        // out sessions whose acks lag more than ~1 s and silently drops them.
        // Verified: PH sess=0x02 ack-lag stays ≤ 870 ms even during heavy bursts
        // across 9 PitHouse bridge captures (median 35–95 ms idle, 50–354 ms busy);
        // the plugin's single-FIFO setup let user-bundle sess=0x07 acks lag 4.5 s+
        // during dashboard switch tier-def floods, matching the "telemetry dies
        // after switch" symptom on issue #43.
        private readonly ConcurrentQueue<byte[]> _priorityQueue = new ConcurrentQueue<byte[]>();
        // One-shot lane: FIFO + 4 ms burst pacing (bases drop unpaced rapid writes).
        private readonly ConcurrentQueue<byte[]> _oneShotQueue = new ConcurrentQueue<byte[]>();
        // Stream lane: per-kind latest-wins slots, unpaced. SendStream overwrites pending values.
        private readonly byte[]?[] _streamSlots = new byte[StreamSlotCount][];
        private readonly WriteBudget _budget = new WriteBudget();
        private int _framesDropped;
        private int _checksumFailures;
        private int _frameStartScanResyncs;
        // Resync histogram by skip-byte count. Buckets [0]=1B, [1]=2B,
        // [2]=3-4B, [3]=5-8B, [4]=9-16B, [5]=17-32B, [6]=33-64B, [7]=>64B.
        // Lets diagnostics show "are resyncs single stray bytes (USB padding)
        // or multi-byte gaps (real wire corruption)?" without surfacing every
        // individual resync as a log line.
        private readonly int[] _resyncSkipBucket = new int[8];
        // Last-N skipped-byte samples (hex) for diagnostics. Newest-first
        // ring under a tiny lock — appended at every resync. Cap at 16 so
        // the tab shows enough variety to spot patterns (e.g. always 0x00,
        // always firmware-debug header bytes) without bloating the buffer.
        private const int ResyncSampleCapacity = 16;
        private readonly object _resyncSampleLock = new object();
        private readonly System.Collections.Generic.LinkedList<string> _resyncSamples
            = new System.Collections.Generic.LinkedList<string>();
        private volatile bool _running;
        private readonly object _lock = new object();
        private string? _lastPortName;

        /// <summary>
        /// Last COM port that connected successfully. Persisted across sessions
        /// by the plugin so <see cref="Connect"/> can try it first on next launch.
        /// </summary>
        public string? LastPortName
        {
            get => _lastPortName;
            set => _lastPortName = value;
        }
        private volatile bool _shutdownRequested;

        // Consecutive I/O error tracking. After sleep/resume the SerialPort handle
        // stays .IsOpen==true but every read/write throws IOException("Not ready"),
        // so nothing triggers reconnect. Count failures and force-close at threshold.
        private int _consecutiveIoErrors;
        // 0 = healthy, 1 = port-dead branch already taken. int+CompareExchange
        // so two failure paths can race the threshold check without both
        // closing the port and double-logging.
        private int _portFailureLogged;
        private const int PortDeadThreshold = 10;
        // Half-open-tty liveness. The count-only PortDeadThreshold above never
        // fires for a "half-open" port that delivers BytesToRead==0 forever
        // WITHOUT throwing (a real failure mode: sleep/resume, USB stall) — the
        // ReadLoop just spins at Thread.Sleep(2) and nothing triggers reconnect.
        // We stamp the last successful read and, once the wheel HAS talked,
        // force a reconnect if inbound goes silent past ReadIdleDeadMs. The
        // plugin's ~1 Hz parity polls keep a healthy wheel answering well inside
        // this window, so a breach means the port is dead, not merely idle.
        private long _lastRxUtcTicks;
        private const int ReadIdleDeadMs = 30_000;

        // Classified open-failure surface. UI hint-builder reads this every
        // 500 ms to distinguish port-in-use from generic disconnect. Counter
        // is incremented atomically; the snapshot struct is copied under
        // _failureLock so the UI sees a consistent view across fields.
        private readonly object _failureLock = new object();
        private ConnectionFailureInfo _lastFailure;
        private int _consecutiveOpenFailures;
        private DateTime _lastSuccessfulOpenUtc;

        public event Action<byte[]>? MessageReceived;
        // Raised on the I/O thread after HandleIoFailure force-closes the port.
        // Subscribers must be background-safe and non-blocking.
        public event Action? Disconnected;
        public bool IsConnected => _port?.IsOpen == true;

        /// <summary>
        /// Label used when traffic on this connection is recorded by
        /// <see cref="SerialTrafficCapture"/>. Set by the owner (e.g. "wheelbase",
        /// "ab9") so the export can disambiguate frames from each pipe.
        /// </summary>
        public string CaptureLabel { get; set; } = "wheelbase";

        /// <summary>
        /// The HID Product ID discovered from WMI during device enumeration.
        /// Null if PID could not be determined (e.g. probe-based discovery under Wine).
        /// </summary>
        public string? DiscoveredPid { get; private set; }

        /// <summary>True if the hub probe (0x64/0x12/0x03) succeeded — gates the post-session 5-slot burst.</summary>
        public bool HubProbeSucceeded { get; private set; }

        /// <summary>Sliding-window snapshot of write-budget utilization. Read by
        /// the diagnostics tab so the user can see when the link approaches
        /// saturation. Each call resets the rolling peak.</summary>
        public WriteBudget.Snapshot CurrentBudget => _budget.GetSnapshot();

        /// <summary>Wire-error counters surfaced together — drops on write,
        /// checksum mismatches on read, and frame-start resyncs (junk bytes
        /// skipped between frames).</summary>
        public WireErrorCounters WireErrors
        {
            get
            {
                int[] histo = new int[_resyncSkipBucket.Length];
                for (int i = 0; i < histo.Length; i++)
                    histo[i] = Interlocked.CompareExchange(ref _resyncSkipBucket[i], 0, 0);
                string[] samples;
                lock (_resyncSampleLock)
                {
                    samples = new string[_resyncSamples.Count];
                    int j = 0;
                    foreach (var s in _resyncSamples) samples[j++] = s;
                }
                return new WireErrorCounters(
                    Interlocked.CompareExchange(ref _framesDropped, 0, 0),
                    Interlocked.CompareExchange(ref _checksumFailures, 0, 0),
                    Interlocked.CompareExchange(ref _frameStartScanResyncs, 0, 0),
                    histo,
                    samples);
            }
        }

        public readonly struct WireErrorCounters
        {
            public readonly int FramesDropped;
            public readonly int ChecksumFailures;
            public readonly int FrameStartScanResyncs;
            /// <summary>Distribution of bytes-skipped at each resync. Buckets:
            /// [0]=1B, [1]=2B, [2]=3-4B, [3]=5-8B, [4]=9-16B, [5]=17-32B,
            /// [6]=33-64B, [7]=>64B. Total across buckets ==
            /// <see cref="FrameStartScanResyncs"/>.</summary>
            public readonly int[] ResyncSkipHistogram;
            /// <summary>Most recent skipped-byte samples, hex-formatted
            /// ("3B: 00 41 0B"). Oldest first, newest last. Capped to 16.</summary>
            public readonly string[] RecentResyncSamples;

            public WireErrorCounters(int dropped, int cksum, int resync,
                int[] histo, string[] samples)
            {
                FramesDropped = dropped;
                ChecksumFailures = cksum;
                FrameStartScanResyncs = resync;
                ResyncSkipHistogram = histo;
                RecentResyncSamples = samples;
            }
        }

        /// <summary>
        /// Snapshot of the most recent open-failure classification. Returns
        /// <see cref="ConnectionFailureInfo.None"/> when no failure is current
        /// (either we've never tried to connect or the last connect succeeded
        /// and <see cref="ResetFailureState"/> ran).
        /// </summary>
        public ConnectionFailureInfo LastFailure
        {
            get { lock (_failureLock) return _lastFailure; }
        }

        /// <summary>
        /// Number of consecutive <see cref="TryOpen"/> calls that have failed
        /// since the last successful open. Reset to 0 on each successful Open
        /// and on <see cref="ResetFailureState"/>. UI hint-builder requires
        /// >= 2 before showing the port-in-use banner so a single transient
        /// failure during plug-in doesn't flash a banner.
        /// </summary>
        public int ConsecutiveOpenFailures =>
            Interlocked.CompareExchange(ref _consecutiveOpenFailures, 0, 0);

        /// <summary>UTC of the most recent successful <see cref="TryOpen"/>;
        /// <see cref="DateTime.MinValue"/> if never connected this session.</summary>
        public DateTime LastSuccessfulOpenUtc
        {
            get { lock (_failureLock) return _lastSuccessfulOpenUtc; }
        }

        /// <summary>
        /// Clear the classified-failure surface. Called from <see cref="TryOpen"/>
        /// on success and from <see cref="MozaPlugin"/> when the user toggles
        /// the connection off (so a stale "port in use" banner doesn't linger
        /// after a deliberate disable).
        /// </summary>
        public void ResetFailureState()
        {
            Interlocked.Exchange(ref _consecutiveOpenFailures, 0);
            lock (_failureLock)
            {
                _lastFailure = ConnectionFailureInfo.None;
            }
        }

        private void RecordOpenFailure(string portName, ConnectionFailureKind kind, Exception ex)
        {
            Interlocked.Increment(ref _consecutiveOpenFailures);
            lock (_failureLock)
            {
                _lastFailure = new ConnectionFailureInfo(kind, portName, ex.Message, DateTime.UtcNow);
            }
        }

        // Record a POST-open runtime failure (port wedge / half-open tty / live IO
        // error) into the failure tracker WITHOUT touching _consecutiveOpenFailures
        // — that counter is open-retry-specific and drives the port-in-use banner.
        // This is what makes Diagnostics show a real failure instead of
        // "LastFailure: kind=None" while a connected-but-dead link is being reset.
        private void RecordRuntimeFailure(ConnectionFailureKind kind, string message)
        {
            lock (_failureLock)
            {
                _lastFailure = new ConnectionFailureInfo(kind, _lastPortName, message, DateTime.UtcNow);
            }
        }

        // SerialPort.Open / CreateFile under Wine and native Windows both
        // produce ERROR_ACCESS_DENIED (HResult 0x80070005) when another
        // process holds the port (PitHouse is the canonical case). The
        // exception type can be UnauthorizedAccessException OR IOException
        // depending on driver path; the substring check covers both, and
        // the HResult check is a belt-and-braces alternative when the
        // message has been localized.
        private static bool LooksLikeAccessDenied(Exception ex)
        {
            const int E_ACCESSDENIED = unchecked((int)0x80070005);
            if (ex.HResult == E_ACCESSDENIED) return true;
            var msg = ex.Message;
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.IndexOf("access is denied", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("already in use", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("sharing violation", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("resource busy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // "Port vanished" — registry enumeration showed the COM but Open
        // can't see it. Distinct from access-denied because the remediation
        // is different (replug, not close-other-app).
        private static bool LooksLikePortVanished(Exception ex)
        {
            var msg = ex.Message;
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("no such", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Connection scoped to a subset of MOZA PIDs. <paramref name="pidFilter"/>
        /// accepts/rejects ports by PID; <paramref name="probeTarget"/> selects which
        /// probe frames the fallback issues; <paramref name="disableProbeFallback"/>
        /// hard-disables the fallback (default keeps it armed for empty-registry case).
        /// </summary>
        public MozaSerialConnection(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget = MozaProbeTarget.BaseAndHub,
            Func<bool>? disableProbeFallback = null)
        {
            _pidFilter = pidFilter;
            _probeTarget = probeTarget;
            _disableProbeFallback = disableProbeFallback;
        }

        /// <summary>
        /// Format a 16-bit PID as the canonical "0x" + 4-hex-uppercase string
        /// the rest of the plugin (PID filters, DiscoveredPid, device.json
        /// templates) is built around.
        /// </summary>
        private static string FormatPid(ushort pid) =>
            "0x" + pid.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Latch hub-observed (idempotent); used to fire the post-session 5-slot burst.</summary>
        public void MarkHubDetected()
        {
            HubProbeSucceeded = true;
        }

        public bool Connect()
        {
            if (_shutdownRequested)
                return false;

            // Tear down any stale threads/port from a previous dead session
            // (e.g. after sleep/resume killed the tty but handle stayed open).
            if (_running || _port != null)
                Disconnect();

            // Try the cached port first, gated on registry confirming it still
            // belongs to a MOZA device of the right family. _activePorts guards
            // against same-process sibling-connection double-open on Wine ptys.
            if (_lastPortName != null
                && !_activePorts.ContainsKey(_lastPortName))
            {
                if (MozaPortDiscovery.Instance.TryGetByPort(_lastPortName, out var info)
                    && (_pidFilter == null || _pidFilter(FormatPid(info.Pid))))
                {
                    DiscoveredPid = FormatPid(info.Pid);
                    if (TryOpen(_lastPortName))
                        return true;
                    MozaLog.Debug(
                        $"[Moza] Cached port {_lastPortName} validated but failed to open — clearing");
                    _lastPortName = null;
                }
                else
                {
                    MozaLog.Debug(
                        $"[Moza] Cached port {_lastPortName} no longer matches a MOZA device in the registry — clearing");
                    _lastPortName = null;
                }
            }

            var (portName, pid, viaHubProbe) = FindMozaPort(
                _pidFilter, _probeTarget, _lastPortName, _disableProbeFallback,
                () => _shutdownRequested);
            if (portName == null)
                return false;

            if (pid != null)
                DiscoveredPid = pid;
            HubProbeSucceeded = viaHubProbe;

            return TryOpen(portName);
        }

        private bool TryOpen(string portName)
        {
            try
            {
                var sp = new SerialPortMozaPort(portName, MozaProtocol.BaudRate);
                return FinishOpen(sp, portName);
            }
            catch (UnauthorizedAccessException ex)
            {
                RecordOpenFailure(portName, ConnectionFailureKind.AccessDenied, ex);
                MozaLog.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (IOException ex) when (LooksLikeAccessDenied(ex))
            {
                RecordOpenFailure(portName, ConnectionFailureKind.AccessDenied, ex);
                MozaLog.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (FileNotFoundException ex)
            {
                RecordOpenFailure(portName, ConnectionFailureKind.PortVanished, ex);
                MozaLog.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (IOException ex) when (LooksLikePortVanished(ex))
            {
                RecordOpenFailure(portName, ConnectionFailureKind.PortVanished, ex);
                MozaLog.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                RecordOpenFailure(portName, ConnectionFailureKind.OpenFailedOther, ex);
                MozaLog.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
        }

        // Shared commit path: wire an already-open port into the read/write loops.
        // Throws on thread-start failure (caller classifies); tears down on the way out.
        private bool FinishOpen(IMozaPort port, string portName)
        {
            // Drain any stale messages from a previous connection.
            while (_priorityQueue.TryDequeue(out _)) { }
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);

            _port = port;
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "MozaSerialRead" };
            _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "MozaSerialWrite" };

            try
            {
                _readThread.Start();
                _writeThread.Start();
            }
            catch
            {
                // If either start failed, tear down: signal stop, join whichever started,
                // close port, then rethrow so the caller logs it.
                _running = false;
                try { _readThread?.Join(500); } catch { }
                try { _writeThread?.Join(500); } catch { }
                try { _port?.Close(); } catch { }
                _port = null;
                throw;
            }

            _lastPortName = portName;
            _activePorts[portName] = 1;
            Interlocked.Exchange(ref _consecutiveIoErrors, 0);
            Interlocked.Exchange(ref _portFailureLogged, 0);
            // Fresh connection: clear the half-open idle stamp so a stale
            // value from a prior connection can't immediately force-close.
            Interlocked.Exchange(ref _lastRxUtcTicks, 0);
            lock (_failureLock)
            {
                _lastFailure = ConnectionFailureInfo.None;
                _lastSuccessfulOpenUtc = DateTime.UtcNow;
            }
            Interlocked.Exchange(ref _consecutiveOpenFailures, 0);
            MozaLog.Info($"[Moza] Connected to {portName}");
            return true;
        }

        public void Disconnect()
        {
            _running = false;

            // Close before joining so a syscall-wedged R/W returns to its loop.
            IMozaPort? p;
            lock (_lock)
            {
                p = _port;
                _port = null;
            }
            if (p != null)
            {
                try { p.Close(); }
                catch (Exception ex) { MozaLog.Debug($"[Moza] Port close: {ex.Message}"); }
            }

            if (_lastPortName != null)
                _activePorts.TryRemove(_lastPortName, out _);

            _readThread?.Join(1000);
            _writeThread?.Join(1000);
        }

        public void Send(byte[] message)
        {
            if (message != null)
                _oneShotQueue.Enqueue(message);
        }

        /// <summary>
        /// Enqueue a tiny, time-critical frame (fc:00 session ack) on the priority
        /// lane. WriteLoop drains this lane ahead of the one-shot FIFO and applies
        /// no pacing — acks are 10 bytes, negligible against the write budget, and
        /// the wheel times out sessions whose acks lag past ~1 s. Use only for
        /// frames the wheel will treat as overdue if delayed (acks); regular
        /// commands should still use <see cref="Send"/> so they share the paced
        /// queue with tier-def chunks and respect the bandwidth budget.
        /// </summary>
        public void SendPriority(byte[] message)
        {
            if (message != null)
                _priorityQueue.Enqueue(message);
        }

        /// <summary>Enqueue a periodic-stream frame with latest-wins coalescing per <see cref="StreamKind"/>.</summary>
        public void SendStream(StreamKind kind, byte[] message)
        {
            if (message == null) return;
            int idx = (int)kind;
            if ((uint)idx >= (uint)_streamSlots.Length) return;
            Interlocked.Exchange(ref _streamSlots[idx], message);
        }

        // Set by FlushPendingWrites (any thread), serviced by the write thread.
        // DiscardOutBuffer must run on the write thread so it never races the
        // unlocked _port.Write there (SerialPort is not thread-safe).
        private volatile bool _flushRequested;

        /// <summary>Drop priority + one-shot FIFOs + all stream slots + the OS write buffer (Stop button halts the wheel instantly).</summary>
        public void FlushPendingWrites()
        {
            while (_priorityQueue.TryDequeue(out _)) { }
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
            // Defer the OS-buffer discard to the write thread (see _flushRequested).
            _flushRequested = true;
        }

        /// <summary>
        /// Clear only one pipeline's stream-slot lane (slots [from, from+count)),
        /// leaving the shared queues, OS buffer, and the OTHER pipeline's slots
        /// intact. Used when one of two senders sharing this connection stops/
        /// restarts so it doesn't blank the co-resident pipeline's frames.
        /// </summary>
        public void ClearStreamSlots(int from, int count)
        {
            int end = Math.Min(from + count, _streamSlots.Length);
            for (int k = Math.Max(0, from); k < end; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
        }

        // Record an I/O failure. Throttles log spam and force-closes the port
        // once the failure count crosses the threshold so the reconnect timer
        // can reopen it (handles sleep/resume where .IsOpen stays true on dead tty).
        private void HandleIoFailure(string label, Exception ex)
        {
            if (!_running) return;

            int count = Interlocked.Increment(ref _consecutiveIoErrors);

            if (Volatile.Read(ref _portFailureLogged) == 0)
            {
                MozaLog.Error($"[Moza] {label} error: {ex.GetType().Name}: {ex.Message}");
            }

            // Single-winner gate: only one thread crosses threshold AND wins
            // the CAS. Loser path skips the close+log entirely instead of
            // racing on a second close.
            if (count >= PortDeadThreshold &&
                Interlocked.CompareExchange(ref _portFailureLogged, 1, 0) == 0)
            {
                MozaLog.Warn(
                    $"[Moza] Port wedged after {count} consecutive I/O errors — closing for reconnect");
                RecordRuntimeFailure(ConnectionFailureKind.IoFailureAfterOpen,
                    $"{label}: {ex.Message} (after {count} consecutive I/O errors — port wedged)");
                lock (_lock)
                {
                    try { _port?.Close(); } catch { }
                    _port = null;
                }
                while (_priorityQueue.TryDequeue(out _)) { }
                while (_oneShotQueue.TryDequeue(out _)) { }
                for (int k = 0; k < _streamSlots.Length; k++)
                    Interlocked.Exchange(ref _streamSlots[k], null);
                try { Disconnected?.Invoke(); } catch (Exception dex)
                {
                    MozaLog.Debug($"[Moza] Disconnected handler: {dex.Message}");
                }
            }
        }

        private void ReadLoop()
        {
            MozaLog.Debug("[Moza] Read thread started");
            int messageCount = 0;
            // Bulk read buffer — drains all available bytes from the OS read
            // buffer in one SerialPort.Read() call, then parses frames from
            // this byte array in memory. Per-byte ReadByte() under Wine/tty0tty
            // had ~100μs per-call overhead which made multi-chunk burst pacing
            // marginal even for valid frames.
            var rx = new List<byte>(capacity: 8192);
            var tmp = new byte[4096];
            // Reusable de-stuff scratch (grown on demand) so each received frame
            // doesn't allocate a fresh byte[] on the high-frequency RX path. Only
            // the per-frame `data` copy handed to subscribers is freshly allocated.
            byte[] destuff = new byte[256];

            while (_running)
            {
                try
                {
                    var port = _port;
                    if (port == null || !port.IsOpen)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    // Wine SerialPort.Read blocks for full count; poll BytesToRead instead.
                    int avail = port.BytesToRead;
                    if (avail == 0)
                    {
                        // Half-open-tty detector: if the wheel has talked before
                        // but has now been silent past ReadIdleDeadMs, the port
                        // is dead-but-IsOpen (BytesToRead==0 forever, no throw),
                        // which the count-only PortDeadThreshold can't catch.
                        // Force a reconnect. (Skipped until first inbound so a
                        // never-engaged port doesn't self-trip.)
                        long lastRx = Interlocked.Read(ref _lastRxUtcTicks);
                        if (lastRx != 0
                            && (DateTime.UtcNow.Ticks - lastRx) > ReadIdleDeadMs * TimeSpan.TicksPerMillisecond)
                        {
                            Interlocked.Exchange(ref _lastRxUtcTicks, 0);
                            HandleIoFailure("ReadIdle",
                                new IOException($"No inbound for >{ReadIdleDeadMs}ms while port open — half-open tty"));
                            continue;
                        }
                        Thread.Sleep(2);
                        continue;
                    }
                    if (avail > tmp.Length) avail = tmp.Length;
                    int n = port.Read(tmp, 0, avail);
                    if (n <= 0) continue;
                    for (int i = 0; i < n; i++)
                        rx.Add(tmp[i]);
                    Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                    Interlocked.Exchange(ref _lastRxUtcTicks, DateTime.UtcNow.Ticks);

                    // Parse as many complete frames from `rx` as possible, then
                    // keep any trailing partial frame for the next bulk read.
                    int cursor = 0;
                    while (cursor < rx.Count)
                    {
                        int frameStart = cursor;
                        // Scan for frame start 0x7E
                        while (frameStart < rx.Count && rx[frameStart] != MozaProtocol.MessageStart)
                            frameStart++;
                        if (frameStart > cursor)
                        {
                            int skipped = frameStart - cursor;
                            Interlocked.Increment(ref _frameStartScanResyncs);
                            // Histogram bucket (cheap — no allocation):
                            int b;
                            if (skipped <= 1) b = 0;
                            else if (skipped == 2) b = 1;
                            else if (skipped <= 4) b = 2;
                            else if (skipped <= 8) b = 3;
                            else if (skipped <= 16) b = 4;
                            else if (skipped <= 32) b = 5;
                            else if (skipped <= 64) b = 6;
                            else b = 7;
                            Interlocked.Increment(ref _resyncSkipBucket[b]);
                            // Sample the actual skipped bytes (capped to
                            // 24 hex chars) so the diagnostics tab can
                            // show what's between frames. Newest-first
                            // ring buffer with a tiny lock — hits at most
                            // a few thousand times per session.
                            int sampleLen = Math.Min(skipped, 12);
                            var sb = new System.Text.StringBuilder(2 + sampleLen * 3);
                            sb.Append(skipped);
                            sb.Append("B:");
                            for (int k = 0; k < sampleLen; k++)
                            {
                                sb.Append(' ');
                                sb.Append(rx[cursor + k].ToString("X2"));
                            }
                            if (sampleLen < skipped) sb.Append(" …");
                            string sample = sb.ToString();
                            lock (_resyncSampleLock)
                            {
                                _resyncSamples.AddLast(sample);
                                while (_resyncSamples.Count > ResyncSampleCapacity)
                                    _resyncSamples.RemoveFirst();
                            }
                        }
                        if (frameStart >= rx.Count)
                        {
                            // No start byte found at all — discard junk.
                            cursor = rx.Count;
                            break;
                        }
                        // Need at least start + length byte to proceed.
                        if (frameStart + 1 >= rx.Count)
                        {
                            cursor = frameStart;
                            break;
                        }
                        int payloadLength = rx[frameStart + 1];
                        // LEN field counts CMD bytes only (group + dev + chk
                        // are framing). The lower bound was historically `< 2`
                        // as defensive noise rejection, but that silently
                        // dropped legitimate short wheel responses:
                        //   LEN=0  → `7E 00 [group] [dev] [chk]` — presence-
                        //            probe ACKs (e.g. `7E 00 80 dev_swap chk`),
                        //            simple polled-status responses (e.g.
                        //            `7E 00 C0 31 7C` channel-cfg response
                        //            from base, `7E 00 A2 21 4E` from main).
                        //   LEN=1  → `7E 01 [group] [dev] [cmd] [chk]` — minimal
                        //            session-mgmt responses (e.g. `7E 01 C3 71
                        //            80 40` SerialStream wheel response).
                        // Rejecting these produced silent resyncs (no DROP log,
                        // since neither frameError nor cksumFail fired) at
                        // ~3/s steady-state, and the rest of the plugin never
                        // got to see these frames — including `MozaPlugin
                        // .OnMessageReceived`'s `data.Length == 2 && data[0]
                        // == 0x80` presence-probe ACK handler, which was
                        // unreachable in practice. Accept any non-negative LEN;
                        // the checksum still has to validate, so a stray byte
                        // run that happens to look like `7E N` only survives if
                        // its checksum byte also coincidentally matches (1/256).
                        // Upper bound raised to 200 — larger than any observed
                        // wheel frame, matches the catalog parser's record-size
                        // ceiling for symmetric framing assumptions.
                        if (payloadLength > 200)
                        {
                            // Invalid length — skip this start byte and resync on
                            // the next 0x7E. Common at connect when junk precedes
                            // real frames.
                            cursor = frameStart + 1;
                            continue;
                        }
                        int needed = payloadLength + 3; // group + device + payload + checksum
                        // Walk wire bytes starting after [start, len], collapsing
                        // 0x7E 0x7E wire pairs back to a single 0x7E body byte.
                        if (needed > destuff.Length) destuff = new byte[needed];
                        byte[] raw = destuff;
                        int decoded = 0;
                        int wirePos = frameStart + 2;
                        bool frameError = false;
                        bool needMoreData = false;
                        while (decoded < needed)
                        {
                            if (wirePos >= rx.Count) { needMoreData = true; break; }
                            byte wb = rx[wirePos++];
                            if (wb == MozaProtocol.MessageStart)
                            {
                                if (wirePos >= rx.Count) { needMoreData = true; break; }
                                byte esc = rx[wirePos++];
                                if (esc != MozaProtocol.MessageStart)
                                {
                                    frameError = true;
                                    break;
                                }
                                raw[decoded++] = MozaProtocol.MessageStart;
                            }
                            else
                            {
                                raw[decoded++] = wb;
                            }
                        }
                        if (needMoreData)
                        {
                            // Frame straddles buffer end; wait for more bytes.
                            cursor = frameStart;
                            break;
                        }
                        if (frameError || decoded != needed)
                        {
                            int nn = Math.Min(8, Math.Max(0, decoded));
                            string first8a = nn > 0 ? BitConverter.ToString(raw, 0, nn) : "(empty)";
                            MozaLog.Debug(
                                $"[Moza] DROP frame-error: decoded={decoded}/{needed} len={payloadLength} first8={first8a}");
                            // Skip past the bad start byte and try to resync.
                            cursor = frameStart + 1;
                            continue;
                        }

                        // Validate wire-level checksum (includes 0x7E escape
                        // accounting per doc § 54). Allocation-free overload —
                        // ReadLoop used to allocate a wireFrame byte[] here every
                        // received frame just to feed the array-based checksum;
                        // CalculateWireChecksumFromParts derives the same value
                        // directly from raw + payloadLength.
                        byte expected = MozaProtocol.CalculateWireChecksumFromParts(
                            (byte)payloadLength, raw, payloadLength + 2);
                        byte actual = raw[needed - 1];
                        if (expected != actual)
                        {
                            Interlocked.Increment(ref _checksumFailures);
                            int nn = Math.Min(8, needed);
                            string first8a = nn > 0 ? BitConverter.ToString(raw, 0, nn) : "(empty)";
                            MozaLog.Debug(
                                $"[Moza] DROP checksum mismatch: expected=0x{expected:X2} actual=0x{actual:X2} " +
                                $"len={payloadLength} group=0x{raw[0]:X2} dev=0x{raw[1]:X2} first8={first8a}");
                            cursor = frameStart + 1;
                            continue;
                        }

                        // Strip the checksum byte before passing to the parser.
                        var data = new byte[needed - 1];
                        Array.Copy(raw, 0, data, 0, data.Length);

                        messageCount++;
                        if (messageCount <= 5)
                        {
                            MozaLog.Debug(
                                $"[Moza] Received msg #{messageCount}: len={payloadLength} " +
                                $"group=0x{data[0]:X2} dev=0x{data[1]:X2} ({data.Length} bytes)");
                        }
                        // Diagnostic: per-chunk log for SerialStream session-data
                        // frames (0xC3 / wheel / 7C / 00) — session 0x09 chunk reception.
                        if (data.Length >= 8 && data[0] == MozaProtocol.SerialStreamRespGroup
                            && data[1] == MozaProtocol.WheelDeviceIdSwapped
                            && data[2] == MozaProtocol.SerialStreamOpcodeData
                            && data[3] == 0x00)
                        {
                            byte sess = data[4];
                            byte type = data[5];
                            int seqWire = data[6] | (data[7] << 8);
                            int bodyLen = data.Length - 8;
                            string first8 = bodyLen > 0
                                ? BitConverter.ToString(data, 8, Math.Min(8, bodyLen))
                                : "(empty)";
                            MozaLog.Debug(
                                $"[Moza] WIRE sess=0x{sess:X2} type=0x{type:X2} seq={seqWire} " +
                                $"totalLen={data.Length} payload={bodyLen}B first8={first8}");
                        }
                        SerialTrafficCapture.Instance.RecordRx(CaptureLabel, data);
                        MessageReceived?.Invoke(data);
                        // Move cursor past the consumed wire bytes.
                        cursor = wirePos;
                    }
                    // Drop consumed bytes so `rx` doesn't grow unbounded.
                    if (cursor > 0)
                    {
                        if (cursor >= rx.Count)
                            rx.Clear();
                        else
                            rx.RemoveRange(0, cursor);
                    }
                }
                catch (TimeoutException)
                {
                    // Normal timeout under Wine, continue
                }
                catch (Exception ex)
                {
                    HandleIoFailure("Read", ex);
                    Thread.Sleep(100);
                }
            }
        }

        private void WriteLoop()
        {
            MozaLog.Debug("[Moza] Write thread started");
            int writeCount = 0;
            // Pooled stuffing buffer. Worst-case stuffed size is 2 * decoded size;
            // grows on demand if a larger frame arrives.
            byte[] stuffBuf = new byte[512];
            // Monotonic 64-bit clock for write pacing. Replaces Environment.TickCount
            // (signed Int32, wraps every ~24.8 days) so the 4ms gate stays correct
            // across long uptime — Stopwatch.GetTimestamp ticks at high resolution
            // and never wraps for any plausible session length.
            long stopwatchFreq = System.Diagnostics.Stopwatch.Frequency;
            long fourMsTicks = stopwatchFreq * 4 / 1000;
            long oneSecTicks = stopwatchFreq;
            long lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp() - stopwatchFreq;
            long lastBudgetWarnTs = 0;
            bool lastWasOneShot = false;

            while (_running)
            {
                bool didWork = false;

                // Serviced here (write thread) so DiscardOutBuffer never races
                // _port.Write below. Set by FlushPendingWrites on the Stop path.
                if (_flushRequested)
                {
                    _flushRequested = false;
                    try { _port?.DiscardOutBuffer(); }
                    catch (Exception ex) { MozaLog.Debug($"[Moza] DiscardOutBuffer: {ex.Message}"); }
                }

                // 0) Priority lane: drain all queued fc:00 acks first, unpaced.
                //    Acks are 10 bytes each and the wheel times out sessions whose
                //    acks lag — they must NOT sit behind a tier-def burst in the
                //    one-shot FIFO. Loop drains all queued acks each iteration so
                //    a flurry of inbound chunks (every wheel tick during a switch)
                //    doesn't leave the back of the line stuck for another cycle.
                while (_priorityQueue.TryDequeue(out var ackMsg))
                {
                    int writtenAck = WriteFrame(ackMsg, ref stuffBuf, MozaProtocol.StuffedFrameSize(ackMsg));
                    if (writtenAck > 0)
                    {
                        writeCount++;
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        // Don't claim "lastWasOneShot" — priority writes shouldn't
                        // count toward the 4ms one-shot inter-frame gate (acks
                        // back-to-back are fine; they're tiny and the wheel handles
                        // bursts of acks without issue per PH wire traces).
                        lastWasOneShot = false;
                        didWork = true;
                    }
                }

                // 1) One-shot FIFO with 4 ms inter-write pacing (bases drop unpaced bursts).
                //    WriteBudget extends the gate under bandwidth pressure.
                if (_oneShotQueue.TryDequeue(out var msg))
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    int stuffedSize = MozaProtocol.StuffedFrameSize(msg);
                    int budgetExtraMs = _budget.RecommendOneShotDelayMs(stuffedSize);
                    int baseGapMs = 0;
                    if (lastWasOneShot)
                    {
                        long sinceTicks = now - lastWriteTs;
                        if (sinceTicks < fourMsTicks)
                            baseGapMs = (int)((fourMsTicks - sinceTicks) * 1000 / stopwatchFreq);
                    }
                    int sleepMs = Math.Max(baseGapMs, budgetExtraMs);
                    if (sleepMs > 0) Thread.Sleep(sleepMs);

                    int written = WriteFrame(msg, ref stuffBuf, stuffedSize);
                    if (written > 0)
                    {
                        writeCount++;
                        if (writeCount <= 5)
                            MozaLog.Debug($"[Moza] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastWasOneShot = true;
                    }
                    didWork = true;
                }

                // 2) Stream lane drained after every FIFO item (retransmit bursts
                //    can keep FIFO non-empty for seconds). No software gating —
                //    latest-wins + OS write-buffer block provides backpressure.
                for (int k = 0; k < _streamSlots.Length; k++)
                {
                    var slot = Interlocked.Exchange(ref _streamSlots[k], null);
                    if (slot == null) continue;
                    int written = WriteFrame(slot, ref stuffBuf, MozaProtocol.StuffedFrameSize(slot));
                    if (written > 0)
                    {
                        writeCount++;
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastWasOneShot = false;
                        didWork = true;
                    }
                }

                // Periodic budget warning — 1/s when over 90% of target.
                long warnNow = System.Diagnostics.Stopwatch.GetTimestamp();
                if (warnNow - lastBudgetWarnTs >= oneSecTicks)
                {
                    // PeekSnapshot preserves the rolling peak for the diagnostics tab.
                    var snap = _budget.PeekSnapshot();
                    if (snap.PercentBudget >= 90)
                    {
                        MozaLog.Warn(
                            $"[Moza] Write budget {snap.PercentBudget}% ({snap.BytesLastSec} B/s, peak {snap.PeakBurstBytes})");
                    }
                    lastBudgetWarnTs = warnNow;
                }

                if (!didWork)
                    Thread.Sleep(2);
            }
        }

        /// <summary>Byte-stuff and write a frame. Returns wire-byte count or -1 on write failure.</summary>
        private int WriteFrame(byte[] msg, ref byte[] stuffBuf, int needed)
        {
            try
            {
                if (stuffBuf.Length < needed)
                    stuffBuf = new byte[Math.Max(needed, stuffBuf.Length * 2)];
                int len = MozaProtocol.StuffFrame(msg, stuffBuf);
                // No lock on Write: only this thread calls it; Close races
                // resolve via IOException/ObjectDisposedException below.
                _port?.Write(stuffBuf, 0, len);
                Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                SerialTrafficCapture.Instance.RecordTx(CaptureLabel, msg);
                _budget.Record(len);
                return len;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _framesDropped);
                HandleIoFailure("Write", ex);
                return -1;
            }
        }

        /// <summary>
        /// Locate a MOZA device: (1) registry walk (no serial bytes), (2) probe fallback
        /// against unclassified ports only. Registry-classified mismatches are skipped.
        /// <paramref name="preferredPort"/> tilts toward the saved port on multi-rig setups.
        /// </summary>
        private static (string? PortName, string? Pid, bool ViaHubProbe) FindMozaPort(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget,
            string? preferredPort,
            Func<bool>? disableProbeFallback,
            Func<bool>? cancel = null)
        {
            // Stage 1: registry walk. Take the full MOZA enumeration first
            // so we can distinguish "the registry is working but our PID
            // isn't there" from "the registry sees nothing at all".
            var allRegistryPorts = MozaPortDiscovery.Instance.Enumerate();

            // Per-port lookup we'll reuse in the probe loops below so the
            // probe never writes bytes at a port whose PID is already
            // known to belong to a different device category.
            var registryByPort = new Dictionary<string, MozaPortDiscovery.PortInfo>(
                allRegistryPorts.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < allRegistryPorts.Count; i++)
                registryByPort[allRegistryPorts[i].PortName] = allRegistryPorts[i];

            // Filter through the existing string-based pidFilter contract.
            // Also drop ports already held by a sibling connection in this
            // process: the cached-port path in Connect() honours _activePorts,
            // but this registry walk did not — so the dedicated hub connection
            // could otherwise try to re-open the port the primary already claimed
            // (the hub-only case, where the BaseAndHub primary took the hub).
            var matchingPorts = (pidFilter == null
                    ? (IEnumerable<MozaPortDiscovery.PortInfo>)allRegistryPorts
                    : allRegistryPorts.Where(p => pidFilter(FormatPid(p.Pid))))
                .Where(p => !_activePorts.ContainsKey(p.PortName))
                .ToList();

            if (matchingPorts.Count > 0)
            {
                MozaPortDiscovery.PortInfo chosen = matchingPorts[0];
                bool matchedPreferred = false;
                if (!string.IsNullOrEmpty(preferredPort))
                {
                    for (int i = 0; i < matchingPorts.Count; i++)
                    {
                        if (string.Equals(matchingPorts[i].PortName, preferredPort,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = matchingPorts[i];
                            matchedPreferred = true;
                            break;
                        }
                    }
                }
                // Wheel-location rule: when a wheelbase is present it must be the
                // telemetry-driving primary, so the BaseAndHub connection prefers a
                // Wheelbase-category port over a Hub/unknown one. Falls back to
                // matchingPorts[0] when no wheelbase exists — that's the hub-only
                // case, where the primary correctly binds to the hub and runs the
                // full wheel/session/telemetry pipeline. Only applied when the
                // saved preferred port didn't already pin the choice.
                if (!matchedPreferred && probeTarget == MozaProbeTarget.BaseAndHub)
                {
                    for (int i = 0; i < matchingPorts.Count; i++)
                    {
                        if (matchingPorts[i].Category == MozaDeviceCategory.Wheelbase)
                        {
                            chosen = matchingPorts[i];
                            break;
                        }
                    }
                }
                MozaLog.Debug(
                    $"[Moza] Found MOZA device on {chosen.PortName} PID={FormatPid(chosen.Pid)} (registry)");
                return (chosen.PortName, FormatPid(chosen.Pid), false);
            }

            if (disableProbeFallback?.Invoke() == true)
            {
                MozaLog.Debug(
                    "[Moza] No matching MOZA device in registry; DisableSerialProbeFallback is on so probe is skipped");
                return (null, null, false);
            }

            // Probe-based discovery: try opening each COM port and sending a Moza read command.
            // This works under Proton/Wine where COM ports are symlinked to /dev/ttyACM*.
            // We probe high-numbered ports first since Wine maps ttyACM devices to COM33+.
            var ports = SerialPort.GetPortNames();
            Array.Sort(ports, (a, b) =>
            {
                int na = ExtractPortNumber(a);
                int nb = ExtractPortNumber(b);
                return nb.CompareTo(na); // Descending - check high ports first
            });

            // Skip the probe entirely when every COM port is registry-classified
            // with a non-matching PID (kept the AB9 probe storm off wheelbase-only users).
            int probeEligible = 0;
            for (int i = 0; i < ports.Length; i++)
            {
                if (!registryByPort.TryGetValue(ports[i], out var info))
                {
                    probeEligible++;
                    continue;
                }
                if (pidFilter == null || pidFilter(FormatPid(info.Pid)))
                    probeEligible++;
            }
            if (probeEligible == 0 && allRegistryPorts.Count > 0)
            {
                MozaLog.Debug(
                    $"[Moza] Registry classifies all {ports.Length} COM port(s); none match this connection's PID filter — skipping probe (trust registry)");
                return (null, null, false);
            }

            if (allRegistryPorts.Count == 0)
                MozaLog.Debug("[Moza] No MOZA device in registry, falling back to serial probe");
            else
                MozaLog.Debug(
                    $"[Moza] Registry classifies {registryByPort.Count} of {ports.Length} COM port(s); probing the remainder");

            // 600ms budget per port — SerialPort.Open can hang indefinitely under Wine
            // if another process holds the tty. Background-thread the probe so one bad
            // port can't block all detection.
            var unreachable = new HashSet<string>();

            // Skip ports held by a sibling connection (Wine pty has no O_EXCL).
            bool IsHeldByPeer(string port) => _activePorts.ContainsKey(port);

            // Per-port registry guard: unclassified → probe, matching → claim,
            // mismatching → caller skips. Shared by AB9 and BaseAndHub branches.
            bool RegistrySaysSkip(string port, out (string?, string?, bool) decided)
            {
                decided = (null, null, false);
                if (!registryByPort.TryGetValue(port, out var info)) return false;
                string pidStr = FormatPid(info.Pid);
                if (pidFilter == null || pidFilter(pidStr))
                {
                    MozaLog.Debug(
                        $"[Moza] Port {port} already classified by registry as PID={pidStr} ({MozaUsbIds.Describe(info.Pid)}) — claiming without probe");
                    decided = (port, pidStr, false);
                    return true;
                }
                MozaLog.Debug(
                    $"[Moza] Port {port} classified by registry as PID={pidStr} ({MozaUsbIds.Describe(info.Pid)}) — not for this connection, skipping probe");
                // Sentinel: mismatching classification — caller treats as
                // "skip and continue" by checking decided.Item1 == null
                // && we returned true.
                return true;
            }

            if (probeTarget == MozaProbeTarget.MBooster)
            {
                // mBooster has no application-level handshake (protocol note § 1)
                // and dev id 0x12 collides with wheelbase Main + AB9 main, so
                // writing a discovery probe at every COM port is high-risk.
                // The multi-device registry path is registry-only by design —
                // if the registry doesn't see the device we don't probe.
                MozaLog.Debug("[Moza] mBooster probe fallback is disabled by design (registry-only discovery)");
                return (null, null, false);
            }

            if (probeTarget == MozaProbeTarget.Ab9)
            {
                // AB9 dev id 0x12 collides with wheelbase Main — disambiguate
                // with a base probe (0x2B/0x13) first; only ports that don't
                // reply to it get the AB9 probe. Registry-classified wheelbase
                // ports are skipped entirely (no base probe written).
                foreach (var port in ports)
                {
                    if (cancel?.Invoke() == true) return (null, null, false);
                    if (IsHeldByPeer(port)) continue;
                    if (RegistrySaysSkip(port, out var decided))
                    {
                        if (decided.Item1 != null) return decided;
                        continue;
                    }

                    var (baseResp, baseReach) = ProbeWithTimeout(port, 600, ProbeKind.Base);
                    if (!baseReach) { unreachable.Add(port); continue; }
                    if (baseResp)
                    {
                        MozaLog.Debug($"[Moza] Probe {port} Ab9: base probe matched — wheelbase territory, skipping");
                        continue;
                    }

                    var (ab9Resp, _) = ProbeWithTimeout(port, 600, ProbeKind.Ab9);
                    if (ab9Resp)
                    {
                        MozaLog.Info($"[Moza] Found Moza AB9 shifter on {port} (probe)");
                        return (port, null, false);
                    }
                }

                MozaLog.Debug("[Moza] No AB9 device found on any COM port");
                return (null, null, false);
            }

            if (probeTarget == MozaProbeTarget.HubOnly)
            {
                // Dedicated hub connection (base also present). Single hub-probe
                // pass; never sends the base probe, so it can't claim a wheelbase
                // port. Held ports (the primary's) are skipped via IsHeldByPeer,
                // and registry-classified non-hub ports via RegistrySaysSkip.
                foreach (var port in ports)
                {
                    if (cancel?.Invoke() == true) return (null, null, false);
                    if (IsHeldByPeer(port)) continue;
                    if (RegistrySaysSkip(port, out var decided))
                    {
                        if (decided.Item1 != null) return decided;
                        continue;
                    }

                    var (responded, _) = ProbeWithTimeout(port, 600, ProbeKind.Hub);
                    if (responded)
                    {
                        MozaLog.Info($"[Moza] Found Moza hub on {port} (probe, dedicated hub connection)");
                        return (port, null, true);
                    }
                }

                MozaLog.Debug("[Moza] No Moza hub found on any COM port (dedicated hub connection)");
                return (null, null, false);
            }

            // BaseAndHub: two-pass probe — bases first, then hubs. v0.7.0 sent both
            // probes per port and returned the first port with any 0x7E reply, which
            // mis-selected the hub when both base + hub were present, or when probe-
            // cycle timing left the base unresponsive after the wrong message hit it.
            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null, false);
                if (IsHeldByPeer(port)) continue;
                if (RegistrySaysSkip(port, out var decided))
                {
                    if (decided.Item1 != null) return decided;
                    continue;
                }

                var (responded, reachable) = ProbeWithTimeout(port, 600, ProbeKind.Base);
                if (responded)
                {
                    MozaLog.Info($"[Moza] Found Moza base on {port} (probe)");
                    return (port, null, false);
                }
                if (!reachable) unreachable.Add(port);
            }

            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null, false);
                if (unreachable.Contains(port)) continue;
                if (IsHeldByPeer(port)) continue;
                // Pass 1 already short-circuited on registry-matching ports
                // via RegistrySaysSkip, so a registry-classified port reaching
                // here is guaranteed mismatching — skip without re-logging.
                if (registryByPort.ContainsKey(port)) continue;

                var (responded, _) = ProbeWithTimeout(port, 600, ProbeKind.Hub);
                if (responded)
                {
                    MozaLog.Info($"[Moza] Found Moza hub on {port} (probe)");
                    return (port, null, true);
                }
            }

            // Drop to Debug — reconnect timer fires every 5s, so Info-level
            // would flood the log when no device is plugged in.
            MozaLog.Debug("[Moza] No MOZA device found on any COM port");
            return (null, null, false);
        }

        // ProbeKind, the probe frames, and the open+probe core live in the
        // shared SerialProbeCore (Protocol/SerialProbeCore.cs).

        private static (bool responded, bool reachable) ProbeWithTimeout(string portName, int timeoutMs, ProbeKind kind)
        {
            // The open+probe runs on a throwaway background thread so a hung
            // SerialPort.Open() on a not-ready CDC-ACM port (Wine's freshly-
            // powered-base wedge) doesn't block detection: the thread is
            // abandoned at the deadline (see _probeInFlight docs) and self-cleans
            // when the syscall finally returns.

            // A prior probe on this port hung in Open() and was abandoned (see
            // _probeInFlight docs). Its SerialPort still owns the OS handle, so
            // a new Open() here would fail anyway — and, more importantly,
            // re-probing keeps re-entering the Wine open path that can crash on
            // a not-ready CDC-ACM port. Skip until that thread self-cleans.
            if (_probeInFlight.ContainsKey(portName))
            {
                MozaLog.Debug(
                    $"[Moza] Probe {portName}: skipped — a prior probe is still " +
                    "blocked in Open() (port not ready); will retry once it clears.");
                return (false, false);
            }

            bool responded = false;
            bool reachable = false;
            _probeInFlight[portName] = 1;

            var t = new Thread(() =>
            {
                try
                {
                    // SerialProbeCore opens, probes, and closes the port entirely
                    // on THIS thread (no cross-thread dispose — that segfaults
                    // Wine mid-Open). On the abandoned-timeout path the thread is
                    // simply left running; it self-cleans here when Open finally
                    // returns.
                    (responded, reachable) = SerialProbeCore.ProbeOnePort(
                        portName, kind, m => MozaLog.Debug($"[Moza] {m}"));
                }
                catch { responded = false; reachable = false; }
                finally
                {
                    _probeInFlight.TryRemove(portName, out _);
                }
            })
            { IsBackground = true, Name = $"MozaProbe-{portName}" };
            try
            {
                t.Start();
            }
            catch
            {
                // Thread couldn't start (e.g. resource exhaustion). The probe
                // thread's finally — which removes the in-flight marker — will
                // never run, so clear it here; otherwise this port is skipped
                // forever (the marker would be a permanent in-flight tombstone).
                _probeInFlight.TryRemove(portName, out _);
                return (false, false);
            }

            if (!t.Join(timeoutMs))
            {
                // Timed out: the port is not ready and Open() is still blocked.
                // ABANDON the background thread instead of force-closing the
                // SerialPort from here — cross-thread Close/Dispose during a
                // native Open() is the freshly-powered-base crash. The thread
                // stays in _probeInFlight and self-cleans (Close on its own
                // thread, removes the in-flight marker) when the syscall finally
                // returns; until then this port is skipped above. The thread is
                // IsBackground, so it never blocks process exit.
                MozaLog.Debug(
                    $"[Moza] Probe {portName}: timed out after {timeoutMs}ms — abandoning " +
                    "blocked probe thread (not force-closing cross-thread; port marked in-flight).");
                return (false, false);
            }
            return (responded, reachable);
        }

        private static int ExtractPortNumber(string portName)
        {
            int num = 0;
            for (int i = 0; i < portName.Length; i++)
            {
                if (portName[i] >= '0' && portName[i] <= '9')
                    num = num * 10 + (portName[i] - '0');
            }
            return num;
        }

        public void Dispose()
        {
            _shutdownRequested = true;
            Disconnect();
        }
    }
}
