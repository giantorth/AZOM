using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Cbor;
using MozaPlugin.Sdk.PitHouseUdp.Handlers;

namespace MozaPlugin.Sdk.PitHouseUdp
{
    /// <summary>
    /// UDP listener for the second PitHouse-equivalent control protocol —
    /// plain CBOR datagrams (no CoAP wrapper) on a separate port from the
    /// SDK CoAP server. Used today by the RallySimFans launcher to set or
    /// read MOZA wheelbase steer lock; other wheel-config tools are
    /// expected to use the same surface for additional PacketIds.
    ///
    /// <para>
    /// Lifecycle mirrors <see cref="MozaSdkCoapServer"/>:
    /// <list type="number">
    ///   <item><see cref="Start"/> binds a <see cref="UdpClient"/> to
    ///   <c>127.0.0.1:</c><see cref="ControlPort"/> and spawns a single
    ///   receive thread.</item>
    ///   <item>Each datagram is decoded as CBOR, validated as a
    ///   <c>{Head, Payload}</c> envelope, and dispatched to the handler
    ///   registered for the packet's <c>Head.PacketId</c>.</item>
    ///   <item><see cref="Stop"/> closes the socket and joins the
    ///   receive thread with a 1 s timeout.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Started and stopped alongside <see cref="MozaSdkCoapServer"/> from
    /// <c>MozaPlugin.Init</c>; the two together form the PitHouse-stub
    /// surface third-party tools talk to.
    /// </para>
    /// </summary>
    public sealed class MozaControlUdpServer : IDisposable
    {
        /// <summary>
        /// PitHouse UDP control-port — protocol-fixed at 40288. PitHouse
        /// persists the value to <c>%USERPROFILE%\Documents\Moza Pit
        /// House\settings.ini</c> <c>[Application] udpPort</c> and
        /// third-party clients (RallySimFans confirmed) read that file
        /// to discover it. Allowing a SimHub user to pick a different
        /// port just guarantees those clients can't reach us, so it is
        /// not exposed as a setting.
        /// </summary>
        public const int ControlPort = 40288;

        /// <summary>Maximum number of rows retained in <see cref="RecentRequests"/>.</summary>
        public const int RecentRequestCapacity = 20;

        /// <summary>One row of the rolling recent-requests buffer.</summary>
        public readonly struct RecentRequest
        {
            public DateTime Time { get; }
            /// <summary>PacketId from the request header, or -1 when CBOR parse failed.</summary>
            public int PacketId { get; }
            /// <summary>Handler name (e.g. "SteerLock write"), or "unknown" / "parse-fail" / "error".</summary>
            public string Operation { get; }
            /// <summary>Free-text detail (set by the handler) or short error description.</summary>
            public string Detail { get; }
            public int DurationMs { get; }

            public RecentRequest(DateTime time, int packetId, string operation, string detail, int durationMs)
            {
                Time = time;
                PacketId = packetId;
                Operation = operation ?? string.Empty;
                Detail = detail ?? string.Empty;
                DurationMs = durationMs;
            }
        }

        private readonly Dictionary<int, IPitHousePacketHandler> _handlers
            = new Dictionary<int, IPitHousePacketHandler>();

        private readonly object _recentGate = new object();
        private readonly LinkedList<RecentRequest> _recent = new LinkedList<RecentRequest>();

        private readonly object _stateGate = new object();
        private UdpClient? _udp;
        private Thread? _thread;
        private volatile bool _stopRequested;
        private volatile bool _running;
        private string _status = "Disabled";
        private int _boundPort;

        // Throttle "unknown PacketId" warnings so a misbehaving client
        // can't flood the log. Sample 1-in-N per distinct PacketId.
        private const int UnknownLogEvery = 60;
        private readonly Dictionary<int, int> _unknownCounters = new Dictionary<int, int>();

        /// <summary>
        /// Build a server with the standard PitHouse handler set
        /// (PacketId 3 = SteerLock write, PacketId 4 = SteerLock read).
        /// Additional handlers can be registered via
        /// <see cref="RegisterHandler"/> before <see cref="Start"/>.
        /// </summary>
        /// <param name="data">Live device-state model. Read handlers query
        ///   this for current values.</param>
        /// <param name="hardware">Hardware applier — write handlers route
        ///   through it to reach the wheelbase.</param>
        internal MozaControlUdpServer(MozaData data, HardwareApplier hardware)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (hardware == null) throw new ArgumentNullException(nameof(hardware));

            RegisterHandler(new SteerLockWriteHandler(data, hardware));
            RegisterHandler(new SteerLockReadHandler(data));
        }

        /// <summary>
        /// Register a handler for a PacketId before <see cref="Start"/>.
        /// Replaces any existing handler for the same PacketId; the last
        /// registration wins so tests / experiments can override the
        /// default handlers. Internal because
        /// <see cref="IPitHousePacketHandler"/> is internal — the
        /// constructor takes the standard set; external callers don't
        /// need to add their own.
        /// </summary>
        internal void RegisterHandler(IPitHousePacketHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers[handler.PacketId] = handler;
        }

        public bool IsRunning => _running;

        public string Status
        {
            get { lock (_stateGate) return _status; }
        }

        public int BoundPort
        {
            get { lock (_stateGate) return _boundPort; }
        }

        /// <summary>
        /// Rolling buffer of the most recent <see cref="RecentRequestCapacity"/>
        /// requests, newest last. Returns a snapshot; subsequent mutation does
        /// not affect the returned list.
        /// </summary>
        public IReadOnlyList<RecentRequest> RecentRequests
        {
            get
            {
                lock (_recentGate)
                {
                    var snapshot = new RecentRequest[_recent.Count];
                    int i = 0;
                    foreach (var r in _recent) snapshot[i++] = r;
                    return snapshot;
                }
            }
        }

        /// <summary>
        /// Raised on the receive thread immediately after a row is appended to
        /// <see cref="RecentRequests"/>. UI subscribers MUST marshal to the
        /// dispatcher before touching WPF controls — the listener fires this
        /// on its own thread.
        /// </summary>
        public event Action? RecentRequestAppended;

        /// <summary>
        /// Bind the UDP socket and spawn the receive thread. Idempotent.
        /// Errors set <see cref="Status"/> and are logged; the call
        /// swallows exceptions so a bad port doesn't prevent plugin
        /// startup.
        /// </summary>
        public void Start()
        {
            lock (_stateGate)
            {
                if (_running)
                {
                    MozaLog.Warn("[PitHouseUdp] server already running; Start() ignored.");
                    return;
                }

                int desiredPort = ControlPort;

                try
                {
                    var endpoint = new IPEndPoint(IPAddress.Loopback, desiredPort);
                    var udp = new UdpClient(endpoint);

                    // No ReceiveTimeout: ReceiveLoop uses Socket.Poll for
                    // its 200 ms wake-up budget. The prior approach set
                    // ReceiveTimeout=200 and caught the resulting
                    // SocketException(TimedOut) silently, but SimHub's
                    // AppDomain.FirstChanceException listener logs every
                    // thrown exception regardless of catch — at ~5 Hz it
                    // spammed SimHub.txt with thousands of "First chance
                    // exception" stack traces and made the log unusable
                    // for real errors. Poll returns a bool on timeout
                    // (no exception) so the steady-state idle loop stays
                    // exception-free.

                    _udp = udp;
                    _boundPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                    _stopRequested = false;

                    _thread = new Thread(ReceiveLoop)
                    {
                        IsBackground = true,
                        Name = "MozaControlUdpServer",
                    };
                    _running = true;
                    _status = $"Listening on 127.0.0.1:{_boundPort}";
                    _thread.Start();

                    MozaLog.Info($"[PitHouseUdp] control server listening on 127.0.0.1:{_boundPort} ({_handlers.Count} handler(s))");
                }
                catch (SocketException sx)
                {
                    bool inUse = sx.SocketErrorCode == SocketError.AddressAlreadyInUse;
                    _status = inUse
                        ? $"Port {desiredPort} in use by another process"
                        : $"Error: {sx.SocketErrorCode} ({sx.Message})";
                    MozaLog.Error($"[PitHouseUdp] bind failed on port {desiredPort}: {sx.SocketErrorCode}: {sx.Message}");
                    CleanupSocketLocked();
                }
                catch (Exception ex)
                {
                    _status = $"Error: {ex.Message}";
                    MozaLog.Error($"[PitHouseUdp] start failed: {ex}");
                    CleanupSocketLocked();
                }
            }
        }

        /// <summary>
        /// Stop the receive thread and close the socket. Idempotent.
        /// Waits at most 1 s for the receive thread to exit.
        /// </summary>
        public void Stop()
        {
            Thread? t;
            UdpClient? udp;
            lock (_stateGate)
            {
                if (!_running && _udp == null)
                {
                    _status = "Disabled";
                    return;
                }

                _stopRequested = true;
                t = _thread;
                udp = _udp;
                try { udp?.Close(); } catch { /* swallow */ }
            }

            if (t != null)
            {
                try
                {
                    if (!t.Join(1000))
                        MozaLog.Warn("[PitHouseUdp] receive thread did not exit within 1s; leaving as background daemon.");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[PitHouseUdp] thread join: {ex.Message}");
                }
            }

            lock (_stateGate)
            {
                CleanupSocketLocked();
                _running = false;
                _thread = null;
                _status = "Disabled";
                _boundPort = 0;
            }

            MozaLog.Info("[PitHouseUdp] control server stopped");
        }

        public void Dispose() => Stop();

        // ----- receive loop -----

        private void ReceiveLoop()
        {
            UdpClient? udp;
            lock (_stateGate) udp = _udp;

            while (!_stopRequested && udp != null)
            {
                // Wait up to 200 ms for data or stop-check budget. Poll
                // returns false on timeout (no exception), true when data
                // is ready / the socket is closed / an error is pending.
                // Receive is only called when Poll says data is ready,
                // so the idle loop never throws SocketException(TimedOut).
                bool ready;
                try
                {
                    ready = udp.Client.Poll(200_000, SelectMode.SelectRead);
                }
                catch (SocketException sx)
                {
                    if (sx.SocketErrorCode == SocketError.Interrupted) break;
                    if (sx.SocketErrorCode == SocketError.OperationAborted) break;
                    if (_stopRequested) break;
                    MozaLog.Debug($"[PitHouseUdp] poll socket error: {sx.SocketErrorCode}: {sx.Message}");
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                if (!ready) continue;

                IPEndPoint? remote = null;
                byte[] datagram;
                try
                {
                    var any = new IPEndPoint(IPAddress.Any, 0);
                    datagram = udp.Receive(ref any);
                    remote = any;
                }
                catch (SocketException sx)
                {
                    if (sx.SocketErrorCode == SocketError.Interrupted) break;
                    if (sx.SocketErrorCode == SocketError.OperationAborted) break;
                    if (_stopRequested) break;
                    MozaLog.Debug($"[PitHouseUdp] receive socket error: {sx.SocketErrorCode}: {sx.Message}");
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_stopRequested) break;
                    MozaLog.Warn($"[PitHouseUdp] receive failed: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (remote == null || datagram == null || datagram.Length == 0) continue;

                try
                {
                    HandleDatagram(udp, remote, datagram);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[PitHouseUdp] handler threw: {ex.GetType().Name}: {ex.Message}");
                    // No reply on error — RSF treats absence-of-reply as
                    // "read failed", which is the closest available
                    // signal to the client side.
                }
            }
        }

        private void HandleDatagram(UdpClient udp, IPEndPoint remote, byte[] datagram)
        {
            var sw = Stopwatch.StartNew();

            PitHousePacket packet;
            try
            {
                packet = DecodePacket(datagram);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[PitHouseUdp] CBOR parse failed from {remote}: {ex.Message}");
                AppendRecent(new RecentRequest(DateTime.Now, -1, "parse-fail", ex.Message, (int)sw.ElapsedMilliseconds));
                return;
            }

            if (!_handlers.TryGetValue(packet.PacketId, out var handler))
            {
                LogUnknownPacketId(packet.PacketId, remote);
                AppendRecent(new RecentRequest(DateTime.Now, packet.PacketId, "unknown", $"no handler from {remote}", (int)sw.ElapsedMilliseconds));
                return;
            }

            var ctx = new PitHouseReplyContext(remote, packet.ReplyPort, udp);
            try
            {
                handler.Handle(packet, ctx);
                AppendRecent(new RecentRequest(DateTime.Now, packet.PacketId, handler.Name, ctx.Summary ?? string.Empty, (int)sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[PitHouseUdp] handler {handler.Name} threw: {ex.GetType().Name}: {ex.Message}");
                AppendRecent(new RecentRequest(DateTime.Now, packet.PacketId, handler.Name, $"error: {ex.GetType().Name}: {ex.Message}", (int)sw.ElapsedMilliseconds));
            }
        }

        private void AppendRecent(RecentRequest row)
        {
            lock (_recentGate)
            {
                _recent.AddLast(row);
                while (_recent.Count > RecentRequestCapacity)
                    _recent.RemoveFirst();
            }

            var handler = RecentRequestAppended;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[PitHouseUdp] RecentRequestAppended subscriber threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Decode a CBOR datagram into a <see cref="PitHousePacket"/>.
        /// Validates the envelope shape (top-level map with a "Head"
        /// sub-map containing at least <c>PacketId</c>) but leaves the
        /// payload as the raw object the reader produced so handlers
        /// validate their own expected shape.
        /// </summary>
        internal static PitHousePacket DecodePacket(byte[] datagram)
        {
            object root = CborReader.ReadItem(datagram);
            if (root is not Dictionary<string, object> rootMap)
                throw new InvalidOperationException($"top level is {root?.GetType().Name ?? "null"}, expected map");
            if (!rootMap.TryGetValue("Head", out var headObj) || headObj is not Dictionary<string, object> headMap)
                throw new InvalidOperationException("missing or malformed 'Head' map");
            if (!headMap.TryGetValue("PacketId", out var pidObj))
                throw new InvalidOperationException("missing 'Head.PacketId'");

            int packetId = pidObj switch
            {
                int i => i,
                uint u when u <= int.MaxValue => (int)u,
                ulong ul when ul <= int.MaxValue => (int)ul,
                _ => throw new InvalidOperationException($"'Head.PacketId' has unsupported type {pidObj?.GetType().Name ?? "null"}"),
            };

            string version = headMap.TryGetValue("Version", out var verObj) && verObj is string s ? s : string.Empty;

            int? replyPort = null;
            if (headMap.TryGetValue("ReplyPort", out var portObj))
            {
                replyPort = portObj switch
                {
                    int i when i >= 0 && i <= 65535 => i,
                    uint u when u <= 65535 => (int)u,
                    ulong ul when ul <= 65535 => (int)ul,
                    _ => null,
                };
            }

            rootMap.TryGetValue("Payload", out var payload);
            return new PitHousePacket(packetId, version, replyPort, payload);
        }

        private void LogUnknownPacketId(int packetId, IPEndPoint remote)
        {
            int n;
            lock (_unknownCounters)
            {
                if (!_unknownCounters.TryGetValue(packetId, out n)) n = 0;
                n++;
                _unknownCounters[packetId] = n;
            }
            if ((n % UnknownLogEvery) != 1) return;
            MozaLog.Debug($"[PitHouseUdp] unknown PacketId {packetId} from {remote} (sample 1/{UnknownLogEvery})");
        }

        // must hold _stateGate
        private void CleanupSocketLocked()
        {
            if (_udp != null)
            {
                try { _udp.Close(); } catch { /* swallow */ }
                try { ((IDisposable)_udp).Dispose(); } catch { /* swallow */ }
                _udp = null;
            }
        }
    }
}
