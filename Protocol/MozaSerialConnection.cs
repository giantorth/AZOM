using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Threading;
using MozaPlugin.Diagnostics;


namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Stream identifier for coalescing latest-wins telemetry frames. Each kind has
    /// a single slot in <see cref="MozaSerialConnection"/>; enqueueing a new frame
    /// for the same kind overwrites any not-yet-sent predecessor, so stale frames
    /// can never pile up behind newer ones. One-shot/session traffic should still
    /// use <see cref="MozaSerialConnection.Send(byte[])"/>.
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
    }

    /// <summary>
    /// Which device family the probe path should target when WMI enumeration
    /// fails. Each target uses a frame the device responds to with its own
    /// distinct response group, so a single probe pass can confirm identity
    /// without grabbing the wrong device's port.
    /// </summary>
    public enum MozaProbeTarget
    {
        BaseAndHub,
        Ab9,
    }

    public class MozaSerialConnection : IDisposable
    {
        private const int StreamSlotCount = 11;

        // Ports currently held by a live MozaSerialConnection. Probe path skips
        // these so the AB9 manager (or any future second connection) can't open
        // the wheelbase's tty under Wine — Linux pty serial drivers don't enforce
        // O_EXCL, so a second SerialPort.Open succeeds and any probe written
        // there reaches the device that's already conversing with the first
        // connection. The unsolicited 0x89 reply then lands on the wrong read
        // thread and the parser logs it as a spurious wheel-presence response.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activePorts =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Filter applied during port discovery. Receives the WMI-reported PID
        // ("0x1000") or null for probe-based discovery where PID is unknown.
        // Returns true to accept, false to skip. Default = accept all.
        private readonly Func<string?, bool>? _pidFilter;
        private readonly MozaProbeTarget _probeTarget;

        private volatile SerialPort? _port;
        private Thread? _readThread;
        private Thread? _writeThread;
        // One-shot lane: session traffic, settings writes, probes — must preserve
        // FIFO order and receives 4 ms pacing when bursted (Moza bases drop rapid
        // settings writes otherwise; see WriteLoop).
        private readonly ConcurrentQueue<byte[]> _oneShotQueue = new ConcurrentQueue<byte[]>();
        // Stream lane: per-kind latest-wins slots for periodic telemetry. Drained
        // after the one-shot lane, unpaced. SendStream overwrites any pending
        // slot so lagging frames never reach the wire stale.
        private readonly byte[]?[] _streamSlots = new byte[StreamSlotCount][];
        private readonly WriteBudget _budget = new WriteBudget();
        private int _framesDropped;
        private int _checksumFailures;
        private int _frameStartScanResyncs;
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

        // Set by ReadLoop to the group byte of the first valid frame
        // received after TryOpen. Used by Connect() to verify a cached
        // port has the expected MOZA device behind it — not a ghost port,
        // not a different device that happens to send bytes. 0 = nothing
        // received yet (0x00 is never a valid response group).
        private int _firstRxGroup;

        // Bitmap of every response group byte ReadLoop has seen since the
        // last Connect() reset. Cached-port validation checks this — looking
        // for the EXPECTED probe response group anywhere in the validation
        // window instead of just the literal first frame. Required because
        // some wheels emit boot-time debug-log frames (group 0x0E) before
        // responding to the base/AB9/hub probe; the probe response then
        // arrives a few ms later mixed in with the noise, and pre-2026-05-10
        // logic at `_firstRxGroup` saw the noise byte first and rejected
        // the (valid) port. 32 bytes × 8 bits covers the full byte range.
        private readonly byte[] _rxGroupsSeen = new byte[32];
        private void NoteRxGroup(byte g) { _rxGroupsSeen[g >> 3] |= (byte)(1 << (g & 7)); }
        private bool HaveSeenRxGroup(byte g) => (_rxGroupsSeen[g >> 3] & (1 << (g & 7))) != 0;
        private void ResetRxGroupsSeen() { for (int i = 0; i < _rxGroupsSeen.Length; i++) _rxGroupsSeen[i] = 0; }

        public event Action<byte[]>? MessageReceived;
        // Raised on the thread that hit the I/O failure (reader or writer)
        // when the port was force-closed by HandleIoFailure. Subscribers
        // must be safe to invoke from a background thread and should not
        // block — used to pause the telemetry sender and reset wheel
        // detection state immediately rather than waiting for the next
        // reconnect-timer tick.
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

        /// <summary>
        /// True when the last successful port-detect probe succeeded via the
        /// hub probe (group 0x64, dev 0x12, cmd 0x03). Indicates a Universal Hub
        /// is on the bus. Used by TelemetrySender to gate the post-session-open
        /// 5-slot enumeration burst (`0x64/0x12 cmd 01 NN 00`) that PitHouse
        /// fires only when a hub is attached.
        /// </summary>
        public bool HubProbeSucceeded { get; private set; }

        /// <summary>Sliding-window snapshot of write-budget utilization. Read by
        /// the diagnostics tab so the user can see when the link approaches
        /// saturation. Each call resets the rolling peak.</summary>
        public WriteBudget.Snapshot CurrentBudget => _budget.GetSnapshot();

        /// <summary>Wire-error counters surfaced together — drops on write,
        /// checksum mismatches on read, and frame-start resyncs (junk bytes
        /// skipped between frames).</summary>
        public WireErrorCounters WireErrors => new WireErrorCounters(
            Interlocked.CompareExchange(ref _framesDropped, 0, 0),
            Interlocked.CompareExchange(ref _checksumFailures, 0, 0),
            Interlocked.CompareExchange(ref _frameStartScanResyncs, 0, 0));

        public readonly struct WireErrorCounters
        {
            public readonly int FramesDropped;
            public readonly int ChecksumFailures;
            public readonly int FrameStartScanResyncs;

            public WireErrorCounters(int dropped, int cksum, int resync)
            {
                FramesDropped = dropped;
                ChecksumFailures = cksum;
                FrameStartScanResyncs = resync;
            }
        }

        public MozaSerialConnection() : this(null, MozaProbeTarget.BaseAndHub) { }

        /// <summary>
        /// Construct a serial connection scoped to a subset of MOZA composite
        /// devices. <paramref name="pidFilter"/> receives each candidate port's
        /// PID (null under Wine probe discovery) and returns true to accept.
        /// <paramref name="probeTarget"/> selects which probe frames are issued
        /// when WMI is unavailable; AB9 callers must pass <see cref="MozaProbeTarget.Ab9"/>
        /// so the AB9-specific identity probe is used (and so the wheelbase
        /// hub probe never claims an AB9 port).
        /// </summary>
        public MozaSerialConnection(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget = MozaProbeTarget.BaseAndHub)
        {
            _pidFilter = pidFilter;
            _probeTarget = probeTarget;
        }

        public bool Connect()
        {
            if (_shutdownRequested)
                return false;

            // Tear down any stale threads/port from a previous dead session
            // (e.g. after sleep/resume killed the tty but handle stayed open).
            if (_running || _port != null)
                Disconnect();

            // Try the last known port first to avoid re-probing (which
            // opens/closes the port and can reset the device under Wine).
            // Check `_activePorts` first so a peer connection (wheelbase vs
            // AB9 with the same saved port) doesn't grab a port already
            // claimed — Wine allows the dual-open and silently splits the
            // byte stream between the two readers, which then loses ~half of
            // each wheel response (visible as missing session 0x09 chunks
            // and false "AB9 shifter detected" on a base-only setup).
            //
            // On native Windows, validate via WMI that the cached port
            // still has a live MOZA device behind it. Moving USB ports causes
            // Windows to assign a new COM number; the old COM entry may stay
            // openable as a ghost device in the registry while the real device
            // is on a different port — writes go into a black hole and the
            // plugin never discovers the real port.
            //
            // When WMI is unavailable (Wine/Proton), open the cached port and
            // send a quick liveness probe. If no frame arrives within 400ms,
            // the port is dead — disconnect and let FindMozaPort re-discover.
            // The brief open+close of the wrong port won't reset the real
            // device (they're different ttys).
            if (_lastPortName != null
                && !_activePorts.ContainsKey(_lastPortName))
            {
                var wmiPorts = FindAllMozaPorts();
                bool wmiAvailable = wmiPorts.Count > 0;
                bool cachedPortValid = false;
                if (wmiAvailable)
                {
                    foreach (var (wmiPort, wmiPid) in wmiPorts)
                    {
                        if (string.Equals(wmiPort, _lastPortName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (_pidFilter == null || _pidFilter(wmiPid))
                            {
                                cachedPortValid = true;
                                if (wmiPid != null) DiscoveredPid = wmiPid;
                            }
                            else
                            {
                                MozaLog.Debug(
                                    $"[Moza] Cached port {_lastPortName} is now PID={wmiPid ?? "?"} (filtered out)");
                            }
                            break;
                        }
                    }
                }
                else if (TryOpen(_lastPortName))
                {
                    // No WMI — liveness-probe the cached port. The read
                    // thread is running; write the appropriate probe and
                    // look for the expected response group **anywhere** in
                    // the receive stream during the validation window, not
                    // just at byte offset 0. The wheel commonly emits
                    // boot-time debug-log frames (group 0x0E, ASCII text
                    // like "MCU temp: 37.000 (°C)") immediately on open —
                    // the legacy `_firstRxGroup` check saw that noise first
                    // and rejected the (valid) port even though the probe
                    // response arrived a few ms later in the same window.
                    //
                    // We also re-emit the probe a couple of times so a
                    // probe lost in the debug-log flood gets a fresh chance.
                    byte[] probe;
                    byte expectedGroup;
                    if (_probeTarget == MozaProbeTarget.Ab9)
                    {
                        probe = Ab9ProbeFrame;
                        expectedGroup = Ab9RespGroup;
                    }
                    else
                    {
                        probe = BaseProbeFrame;
                        expectedGroup = BaseRespGroup;
                    }

                    const int ValidationWindowMs = 1500;
                    const int ProbeRepeatMs = 250;
                    const int PollSliceMs = 25;

                    int waited = 0;
                    int nextProbeAt = 0;
                    while (waited < ValidationWindowMs)
                    {
                        if (waited >= nextProbeAt)
                        {
                            try { _port?.Write(probe, 0, probe.Length); } catch { }
                            nextProbeAt = waited + ProbeRepeatMs;
                        }
                        Thread.Sleep(PollSliceMs);
                        waited += PollSliceMs;
                        bool seen;
                        lock (_rxGroupsSeen) seen = HaveSeenRxGroup(expectedGroup);
                        if (seen) break;
                    }

                    bool seenExpected;
                    lock (_rxGroupsSeen) seenExpected = HaveSeenRxGroup(expectedGroup);
                    if (seenExpected)
                    {
                        cachedPortValid = true;
                        MozaLog.Debug(
                            $"[Moza] Cached port {_lastPortName} responded with 0x{expectedGroup:X2} " +
                            $"within {waited}ms — confirmed");
                    }
                    else
                    {
                        int rxGroup = Volatile.Read(ref _firstRxGroup);
                        string detail = rxGroup == 0
                            ? "no response"
                            : $"no 0x{expectedGroup:X2} seen (first was 0x{rxGroup:X2})";
                        MozaLog.Debug(
                            $"[Moza] Cached port {_lastPortName}: {detail} after {ValidationWindowMs}ms — stale, will re-discover");
                        Disconnect();
                    }
                }

                if (!cachedPortValid)
                {
                    MozaLog.Debug(
                        $"[Moza] Clearing saved port {_lastPortName}");
                    _lastPortName = null;
                }
                else
                {
                    // WMI path: port was validated but not yet opened.
                    // Non-WMI path: already opened + probed, _running is true.
                    if (!_running && !TryOpen(_lastPortName))
                    {
                        MozaLog.Debug(
                            $"[Moza] Cached port {_lastPortName} validated but failed to open — clearing");
                        _lastPortName = null;
                    }
                    else
                    {
                        return true;
                    }
                }
            }

            var (portName, pid, viaHubProbe) = FindMozaPort(_pidFilter, _probeTarget, () => _shutdownRequested);
            if (portName == null)
                return false;

            if (pid != null)
                DiscoveredPid = pid;
            HubProbeSucceeded = viaHubProbe;

            return TryOpen(portName);
        }

        private bool TryOpen(string portName)
        {
            // Drain any stale messages from a previous connection
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
            Interlocked.Exchange(ref _firstRxGroup, 0);
            lock (_rxGroupsSeen) ResetRxGroupsSeen();

            try
            {
                _port = new SerialPort(portName, MozaProtocol.BaudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    // Larger buffers cushion bursts under Wine/tty0tty where
                    // the kernel-side pty queue can hold multiple concurrent
                    // device-session chunks (session 0x09 configJson state
                    // burst is up to 7 × 68B = ~500B in under 40ms).
                    ReadBufferSize = 65536,
                    WriteBufferSize = 16384,
                    // CDC ACM uses DTR as "host connected" signal. Must be true
                    // so Close() drops DTR and Open() raises it — giving the
                    // wheel a real disconnect/reconnect on the control lines.
                    DtrEnable = true,
                };
                _port.Open();
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

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
                    // close port, then rethrow so the outer catch logs it.
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
                MozaLog.Info($"[Moza] Connected to {portName}");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;

            // Close the port BEFORE joining threads so any read/write blocked
            // in a kernel syscall on a wedged tty (sleep/resume, unplug, or
            // device-side stall) returns with an error and the thread can
            // exit. If we joined first, a wedged Write would sit holding no
            // lock but still pinned in the syscall; Join would hit its 1000ms
            // timeout and we'd proceed to Close, but SimHub's End() timeout
            // on the surrounding call would already have expired.
            SerialPort? p;
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
        /// Enqueue a periodic-stream frame with latest-wins coalescing. Any frame
        /// already pending in the same <see cref="StreamKind"/> slot is discarded.
        /// Use for telemetry/enable/sequence/mode — frames that only matter at their
        /// latest value. For ordered one-shot traffic use <see cref="Send(byte[])"/>.
        /// </summary>
        public void SendStream(StreamKind kind, byte[] message)
        {
            if (message == null) return;
            int idx = (int)kind;
            if ((uint)idx >= (uint)_streamSlots.Length) return;
            Interlocked.Exchange(ref _streamSlots[idx], message);
        }

        /// <summary>
        /// Drop everything pending: one-shot FIFO, all stream slots, and the OS
        /// write buffer. Called from TelemetrySender.Stop so clicking the test
        /// button's Stop halts the wheel immediately instead of bleeding through
        /// the ~16 KB WriteBufferSize (~1.4 s at 115200 baud).
        /// </summary>
        public void FlushPendingWrites()
        {
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
            lock (_lock)
            {
                try { _port?.DiscardOutBuffer(); }
                catch (Exception ex) { MozaLog.Debug($"[Moza] DiscardOutBuffer: {ex.Message}"); }
            }
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
                lock (_lock)
                {
                    try { _port?.Close(); } catch { }
                    _port = null;
                }
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

                    // Wine's SerialPort.Read blocks for full count (doesn't
                    // return early when fewer bytes available), so we can't use
                    // a plain blocking Read. Instead poll BytesToRead and pull
                    // whatever is available. 2 ms sleep when idle keeps CPU
                    // usage negligible while still draining the pty buffer
                    // promptly — observed 1-15 bytes per pull at 115200 baud.
                    int avail = port.BytesToRead;
                    if (avail == 0)
                    {
                        Thread.Sleep(2);
                        continue;
                    }
                    if (avail > tmp.Length) avail = tmp.Length;
                    int n = port.Read(tmp, 0, avail);
                    if (n <= 0) continue;
                    for (int i = 0; i < n; i++)
                        rx.Add(tmp[i]);
                    Interlocked.Exchange(ref _consecutiveIoErrors, 0);

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
                            Interlocked.Increment(ref _frameStartScanResyncs);
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
                        if (payloadLength < 2 || payloadLength > 64)
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
                        var raw = new byte[needed];
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
                        // accounting per doc § 54).
                        var wireFrame = new byte[2 + payloadLength + 2];
                        wireFrame[0] = MozaProtocol.MessageStart;
                        wireFrame[1] = (byte)payloadLength;
                        Array.Copy(raw, 0, wireFrame, 2, payloadLength + 2);
                        byte expected = MozaProtocol.CalculateWireChecksum(wireFrame);
                        byte actual = raw[raw.Length - 1];
                        if (expected != actual)
                        {
                            Interlocked.Increment(ref _checksumFailures);
                            int nn = Math.Min(8, raw.Length);
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
                        // Diagnostic: per-chunk log for 0xC3/71/7C/00 SerialStream
                        // frames so we can verify session 0x09 chunk reception.
                        if (data.Length >= 8 && data[0] == 0xC3 && data[1] == 0x71 &&
                            data[2] == 0x7C && data[3] == 0x00)
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
                        Interlocked.CompareExchange(ref _firstRxGroup, data[0], 0);
                        // Also record every group seen so cached-port
                        // validation can find the expected response group
                        // even when boot-time debug spam beats it to the port.
                        lock (_rxGroupsSeen) NoteRxGroup(data[0]);
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

                // 1) One-shot FIFO: session traffic, settings writes, probes.
                //    Paced at 4 ms between consecutive one-shots — Moza bases drop
                //    settings writes when flooded (ApplyProfile sends 30+ in a burst).
                //    Pacing is skipped when the previous write was a stream frame,
                //    since telemetry-group writes never trigger the drop.
                //
                //    Under bandwidth pressure, the budget extends the gate further
                //    (still order-preserving FIFO, never drops) so the wheel can
                //    drain its receive buffer before we pile on more.
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

                // 2) Stream lane: latest-wins per kind, drained after every FIFO
                //    item (not only when FIFO is empty). Retransmit + blind
                //    retransmit can keep the FIFO perpetually non-empty for seconds,
                //    starving value frames if streams are gated on FIFO-empty.
                //
                //    The stream lane is NOT bandwidth-gated. Latest-wins
                //    coalescing already provides natural backpressure — when
                //    writes lag, fresh SendStream calls overwrite the slot
                //    before the previous frame is dequeued, so the wheel sees
                //    the freshest value automatically. SerialPort.Write blocks
                //    when the OS buffer fills (16 kB), giving us a hard
                //    physical-layer gate without artificial software gating.
                //    Software gating here was the 2026-05-08 regression: it
                //    held value frames for ~700ms-1s during routine bursts and
                //    starved the wheel into dropping configJson chunks during
                //    cold-start and post-switch.
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

                // Periodic budget warning — single line per second, only when over
                // 90 % of target. Lets the user / log analyzer correlate stress
                // periods with downstream symptoms (frame drops, retransmits).
                long warnNow = System.Diagnostics.Stopwatch.GetTimestamp();
                if (warnNow - lastBudgetWarnTs >= oneSecTicks)
                {
                    // PeekSnapshot doesn't reset the rolling peak — leaves it
                    // intact for the diagnostics tab's GetSnapshot to consume.
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

        /// <summary>
        /// Byte-stuff <paramref name="msg"/> into <paramref name="stuffBuf"/> (growing
        /// it if needed) and write the stuffed bytes to the port in one call.
        /// Returns the wire-byte count on success or -1 if the port write raised.
        /// Callers pass <paramref name="needed"/> (precomputed via
        /// <see cref="MozaProtocol.StuffedFrameSize"/>) so this method doesn't
        /// re-scan the input twice on the one-shot hot path.
        /// </summary>
        private int WriteFrame(byte[] msg, ref byte[] stuffBuf, int needed)
        {
            try
            {
                if (stuffBuf.Length < needed)
                    stuffBuf = new byte[Math.Max(needed, stuffBuf.Length * 2)];
                int len = MozaProtocol.StuffFrame(msg, stuffBuf);
                // No lock around the kernel Write syscall. Only this thread
                // calls WriteFrame, and Disconnect/HandleIoFailure close the
                // port without contending. A concurrent Close turns this into
                // an IOException/ObjectDisposedException, which is handled
                // below. Previously we locked around Write; a wedged port
                // (dead tty after sleep/resume) could leave the syscall
                // blocked past WriteTimeout, pinning the lock forever and
                // hanging SimHub shutdown.
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
        /// Enumerate every MOZA composite serial port currently visible via WMI,
        /// returning <c>(portName, pid)</c> pairs in WMI iteration order. Empty
        /// list when WMI is unavailable (probe-based discovery handles that path
        /// inside <see cref="FindMozaPort"/>). Public so callers that need to
        /// open more than one MOZA device — e.g. wheelbase + AB9 shifter — can
        /// inspect the full topology before deciding which port belongs to whom.
        /// </summary>
        public static IReadOnlyList<(string PortName, string? Pid)> FindAllMozaPorts()
        {
            var results = new List<(string, string?)>();
            try
            {
                var asm = Assembly.Load("System.Management");
                var searcherType = asm.GetType("System.Management.ManagementObjectSearcher")!;
                using var searcher = (IDisposable)Activator.CreateInstance(
                    searcherType, "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'")!;
                var collection = (IDisposable)searcherType.GetMethod("Get", Type.EmptyTypes)!.Invoke(searcher, null)!;
                using (collection)
                {
                    foreach (var obj in (IEnumerable)collection)
                    {
                        var objType = obj.GetType();
                        var prop = objType.GetProperty("Item", new[] { typeof(string) })!;
                        var deviceId = prop.GetValue(obj, new object[] { "DeviceID" })?.ToString() ?? "";
                        var caption = prop.GetValue(obj, new object[] { "Caption" })?.ToString() ?? "";

                        if (deviceId.IndexOf("VID_346E", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        int comStart = caption.IndexOf("(COM");
                        if (comStart < 0) continue;
                        int comEnd = caption.IndexOf(")", comStart);
                        if (comEnd <= comStart) continue;

                        var portName = caption.Substring(comStart + 1, comEnd - comStart - 1);
                        results.Add((portName, ExtractPid(deviceId)));
                    }
                }
            }
            catch (Exception ex)
            {
                // WMI unavailable (Wine/Proton) or query failed. Caller falls back
                // to probe path. Log at Debug — reconnect timer fires every 5 s,
                // and Info-level here would flood the log on systems where
                // System.Management can't load (e.g. SimHub plugin AppDomain
                // without the assembly resolvable).
                MozaLog.Debug(
                    $"[Moza] WMI enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }
            return results;
        }

        private static (string? PortName, string? Pid, bool ViaHubProbe) FindMozaPort(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget,
            Func<bool>? cancel = null)
        {
            // Try WMI-based discovery first (native Windows). Iterate every Moza
            // port and pick the first one that satisfies the optional PID filter
            // — keeps wheelbase code from accidentally opening an attached AB9
            // shifter when both enumerate as VID_346E composite devices.
            var wmiPorts = FindAllMozaPorts();
            if (wmiPorts.Count > 0)
            {
                foreach (var (portName, pid) in wmiPorts)
                {
                    if (pidFilter != null && !pidFilter(pid))
                    {
                        MozaLog.Debug(
                            $"[Moza] Skipping {portName} PID={pid ?? "unknown"} (filtered)");
                        continue;
                    }
                    MozaLog.Debug(
                        $"[Moza] Found Moza device on {portName} PID={pid ?? "unknown"} (WMI)");
                    return (portName, pid, false);
                }
                // No match for this filter — silent at Debug level. Reconnect
                // timer ticks every 5s, so Info would spam the log.
                MozaLog.Debug("[Moza] No matching MOZA device found via WMI");
                return (null, null, false);
            }

            // WMI unavailable — drop to probe. Probe-target specific frames
            // positively confirm device identity by response group, so AB9
            // callers can use the probe path safely (no null-PID rejection
            // needed any more — the AB9 probe only matches AB9 responses).
            MozaLog.Debug("[Moza] WMI discovery returned no devices, trying probe");

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

            // 600ms budget per port — SerialPort.Open can hang indefinitely under Wine
            // if another process holds the tty. Background-thread the probe so one bad
            // port can't block all detection.
            var unreachable = new HashSet<string>();

            // Skip ports already held by a sibling MozaSerialConnection. Linux
            // pty drivers don't enforce O_EXCL so a probe Open() on the live
            // wheelbase tty would succeed and the probe write would reach the
            // device that connection is already conversing with — the resulting
            // 0x89 reply lands on the original read thread and the parser logs
            // a spurious wheel-presence response every probe cycle.
            bool IsHeldByPeer(string port) => _activePorts.ContainsKey(port);

            if (probeTarget == MozaProbeTarget.Ab9)
            {
                // AB9 main device id (0x12) is shared with the wheelbase Main
                // controller, and the wheelbase happily answers AB9 identity
                // probes — so claiming a port purely on a 0x89 reply mis-claims
                // a wheelbase tty whenever the wheelbase manager hasn't locked
                // its port yet (e.g. first reconnect tick after plugin start).
                // Disambiguate by running a base probe (group 0x2B dev 0x13)
                // first: AB9 has no dev 0x13 and never replies to it, while
                // the wheelbase always does. A 0xAB reply means "this is a
                // wheelbase" → skip. Only ports that don't respond to the base
                // probe get the AB9 probe.
                foreach (var port in ports)
                {
                    if (cancel?.Invoke() == true) return (null, null, false);
                    if (IsHeldByPeer(port)) continue;

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

            // BaseAndHub: two-pass probe — bases first, then hubs. v0.7.0 sent both
            // probes per port and returned the first port with any 0x7E reply, which
            // mis-selected the hub when both base + hub were present, or when probe-
            // cycle timing left the base unresponsive after the wrong message hit it.
            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null, false);
                if (IsHeldByPeer(port)) continue;

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

        /// <summary>
        /// Extract the PID from a WMI DeviceID string.
        /// Format: USB\VID_346E&amp;PID_XXXX\...
        /// Returns the PID as a hex string like "0x0006", or null if not found.
        /// </summary>
        private static string? ExtractPid(string deviceId)
        {
            int pidIndex = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (pidIndex < 0 || pidIndex + 8 > deviceId.Length)
                return null;

            var pidHex = deviceId.Substring(pidIndex + 4, 4);

            // Validate it's actually hex
            foreach (char c in pidHex)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                    return null;
            }

            return "0x" + pidHex.ToUpperInvariant();
        }

        private enum ProbeKind { Base, Hub, Ab9 }

        // Pre-built probe frames. Base targets group 43, device 19, cmd id 2 (state-change probe).
        // Hub targets group 100, device 18, cmd id 3 (port1 power).
        // AB9 targets group 9, device 18 — PitHouse identity probe; the AB9 main
        // device shares dev id 0x12 with Hub but does not respond to the hub-read
        // group 0x64, so its response group (0x89) cleanly disambiguates the two.
        // Base probe arg matches PitHouse wire pattern (documented § Group 0x2B) so device
        // responses stay consistent across clients.
        private static readonly byte[] BaseProbeFrame = BuildProbe(new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x02, 0x00, 0x00, 0x00 });
        private static readonly byte[] HubProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x03, 0x64, 0x12, 0x03, 0x00, 0x00, 0x00 });
        private static readonly byte[] Ab9ProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x00, 0x09, 0x12, 0x00 });

        // Expected response groups (request group with bit 7 toggled, per MozaResponseParser).
        private const byte BaseRespGroup = 0xAB;
        private const byte HubRespGroup  = 0xE4;
        private const byte Ab9RespGroup  = 0x89;

        private static byte[] BuildProbe(byte[] frame)
        {
            // Wire-level checksum so the probe stays valid if the payload ever
            // contains a 0x7E byte (unstuffed wire writes a 0x7E payload byte
            // raw, but the receiver decodes by collapsing 0x7E 0x7E → 0x7E and
            // sums the wire-doubled byte twice). For the current static probes
            // there are no 0x7E bytes from index 2 onward so the result equals
            // CalculateChecksum, but using the wire variant prevents a silent
            // failure if a future probe template is added with a 0x7E in it.
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        /// <summary>
        /// Run the probe on a background thread with a hard time budget (ms).
        /// Returns (responded, reachable). reachable=false means open/timeout failed
        /// — caller can skip this port in subsequent passes.
        ///
        /// On timeout, the outer thread closes the SerialPort to unblock any inner
        /// syscall (Open/Write/Read) — without this, a stuck Write would leave the
        /// probe thread alive holding the port handle until the OS eventually
        /// returned, leaking threads under repeat probe pressure.
        /// </summary>
        private static (bool responded, bool reachable) ProbeWithTimeout(string portName, int timeoutMs, ProbeKind kind)
        {
            bool responded = false;
            bool reachable = false;
            SerialPort? portRef = null;

            var t = new Thread(() =>
            {
                SerialPort? probe = null;
                try
                {
                    probe = new SerialPort(portName, MozaProtocol.BaudRate)
                    {
                        ReadTimeout = 300,
                        WriteTimeout = 300
                    };
                    // Publish BEFORE Open() so a timed-out caller can close the
                    // port even if Open itself blocks on a syscall.
                    System.Threading.Volatile.Write(ref portRef, probe);
                    probe.Open();
                    (responded, reachable) = ProbeMozaDeviceCore(probe, kind, portName);
                }
                catch { responded = false; reachable = false; }
                finally
                {
                    try { probe?.Close(); } catch { }
                    try { probe?.Dispose(); } catch { }
                }
            })
            { IsBackground = true, Name = $"MozaProbe-{portName}" };
            t.Start();

            if (!t.Join(timeoutMs))
            {
                // Force the inner syscall to throw by closing the port from
                // this side. .NET SerialPort.Close from another thread while
                // a Write is in progress is documented to throw IOException
                // on the blocked call — which the inner catch handles.
                var p = System.Threading.Volatile.Read(ref portRef);
                try { p?.Close(); } catch { }
                try { p?.Dispose(); } catch { }
                try { t.Join(500); } catch { }
                MozaLog.Debug($"[Moza] Probe {portName}: timed out after {timeoutMs}ms (port force-closed)");
                return (false, false);
            }
            return (responded, reachable);
        }

        /// <summary>
        /// Send the requested probe on an already-open port and confirm the reply
        /// by checking the response group at wire offset 2 against the expected
        /// toggled-bit-7 group for the kind being probed. Disambiguating by response
        /// group stops AB9 (dev id 0x12, same as Hub) from being misidentified as a
        /// hub when both share the bus.
        /// Single-message probe avoids the v0.7.0 issue where back-to-back base+hub
        /// writes left the device in a state where it stopped answering after reopen.
        ///
        /// Caller (ProbeWithTimeout) owns the port lifecycle so a hung syscall can
        /// be unblocked by closing the port from outside.
        /// </summary>
        private static (bool responded, bool reachable) ProbeMozaDeviceCore(SerialPort probe, ProbeKind kind, string portName)
        {
            byte[] msg;
            byte expectedRespGroup;
            switch (kind)
            {
                case ProbeKind.Base: msg = BaseProbeFrame; expectedRespGroup = BaseRespGroup; break;
                case ProbeKind.Hub:  msg = HubProbeFrame;  expectedRespGroup = HubRespGroup;  break;
                case ProbeKind.Ab9:  msg = Ab9ProbeFrame;  expectedRespGroup = Ab9RespGroup;  break;
                default: return (false, false);
            }

            try
            {
                probe.DiscardInBuffer();

                // Generous validation window with periodic re-probes. The
                // wheel commonly emits a burst of boot-time debug-log
                // frames (group 0x0E, ASCII text) for several hundred ms
                // on first open, drowning out a single 100ms probe-and-
                // peek. We poll in short slices, re-emit the probe on a
                // ~200ms cadence, and accept as soon as we see ANY frame
                // whose group at wire offset +2 matches the expected
                // response — surviving leading debug spam without a
                // false positive on 0x0E frames.
                const int TotalBudgetMs = 500;
                const int ProbeRepeatMs = 200;
                const int PollSliceMs = 25;
                const int MaxAccumBytes = 4096;

                var accum = new System.Collections.Generic.List<byte>(512);
                byte firstSeenGroup = 0xFF;
                bool responded = false;

                int waited = 0;
                int nextProbeAt = 0;
                while (waited < TotalBudgetMs)
                {
                    if (waited >= nextProbeAt)
                    {
                        try { probe.Write(msg, 0, msg.Length); } catch { return (false, false); }
                        nextProbeAt = waited + ProbeRepeatMs;
                    }

                    System.Threading.Thread.Sleep(PollSliceMs);
                    waited += PollSliceMs;

                    int avail = probe.BytesToRead;
                    if (avail > 0)
                    {
                        int want = Math.Min(avail, MaxAccumBytes - accum.Count);
                        if (want > 0)
                        {
                            var tmp = new byte[want];
                            int n = probe.Read(tmp, 0, want);
                            for (int i = 0; i < n; i++) accum.Add(tmp[i]);
                        }
                        for (int i = 0; i + 2 < accum.Count; i++)
                        {
                            if (accum[i] != MozaProtocol.MessageStart) continue;
                            byte respGroup = accum[i + 2];
                            if (firstSeenGroup == 0xFF) firstSeenGroup = respGroup;
                            if (respGroup == expectedRespGroup)
                            {
                                responded = true;
                                break;
                            }
                        }
                        if (responded) break;
                    }
                }

                if (!responded && firstSeenGroup != 0xFF)
                {
                    MozaLog.Debug(
                        $"[Moza] Probe {portName} {kind}: {accum.Count}B in {waited}ms, " +
                        $"no 0x{expectedRespGroup:X2} (first seen 0x{firstSeenGroup:X2})");
                }
                return (responded, true);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] Probe {portName}: {ex.GetType().Name}");
                return (false, false);
            }
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
