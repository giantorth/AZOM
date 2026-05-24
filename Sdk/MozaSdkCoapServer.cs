using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MozaPlugin.Hardware;
using MozaPlugin.Sdk.Coap;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// UDP listener that ties the foundation CoAP wire codec, resource
    /// registry, and observe-registry together into a runnable server. This
    /// is Stream 7 of the third-party SDK emulation feature; it owns no
    /// resource handlers of its own — every URI is dispatched into
    /// <see cref="CoapResourceRegistry"/> populated by
    /// <see cref="ResourceBindings.RegisterAll"/>.
    ///
    /// Lifecycle:
    /// <list type="number">
    ///   <item><see cref="Start"/> binds a <see cref="UdpClient"/> to
    ///   <c>127.0.0.1:</c><see cref="CoapPort"/> (loopback only — the
    ///   <see cref="MozaPluginSettings.SdkBindLoopbackOnly"/> flag is plumbed
    ///   for future use but ignored in v1, by design) and spawns a single
    ///   receive thread.</item>
    ///   <item>Receive loop parses each datagram, resolves the URI via the
    ///   registry, dispatches to a handler, encodes the response, and ships
    ///   it back to the source endpoint. CoAP CON pings (Code=0, Type=CON)
    ///   short-circuit to an RST reply.</item>
    ///   <item><see cref="Stop"/> closes the socket (which unblocks
    ///   <see cref="UdpClient.Receive"/>) and joins the thread with a 1 s
    ///   timeout.</item>
    /// </list>
    ///
    /// Per-request rows are recorded in <see cref="RecentRequests"/> (rolling
    /// 20-deep) for the UI tab; <see cref="RecentRequestAppended"/> fires
    /// after each append so the UI can refresh without polling.
    /// </summary>
    public sealed class MozaSdkCoapServer : IDisposable
    {
        /// <summary>
        /// CoAP listener port — hardcoded as `mov dx, 0x9D4A` in MOZA_SDK.dll
        /// (both the official 1.0.1.8 build and iRacing's customized variant).
        /// The SDK does not discover the port; binding anywhere else means
        /// every consumer that links the vendor DLL is silently unable to
        /// reach our handlers. Not exposed as a setting for that reason.
        /// </summary>
        public const int CoapPort = 40266;

        /// <summary>Maximum number of rows retained in <see cref="RecentRequests"/>.</summary>
        public const int RecentRequestCapacity = 20;

        /// <summary>One row of the recent-requests rolling buffer.</summary>
        public readonly struct RecentRequest
        {
            public DateTime Time { get; }
            public string Verb { get; }
            public string Uri { get; }
            public byte ResponseCode { get; }
            public int DurationMs { get; }

            public RecentRequest(DateTime time, string verb, string uri, byte responseCode, int durationMs)
            {
                Time = time;
                Verb = verb ?? string.Empty;
                Uri = uri ?? string.Empty;
                ResponseCode = responseCode;
                DurationMs = durationMs;
            }
        }

        private readonly DeviceCatalog _catalog;
        private readonly CoapResourceRegistry _registry;
        private readonly ObserveRegistry _observers = new ObserveRegistry();

        private readonly object _stateGate = new object();
        private UdpClient? _udp;
        private Thread? _thread;
        private volatile bool _stopRequested;
        private volatile bool _running;
        private string _status = "Disabled";
        private int _boundPort;

        private readonly object _recentGate = new object();
        private readonly LinkedList<RecentRequest> _recent = new LinkedList<RecentRequest>();

        // Per-URI sampling counters for noisy resources (Feedforward etc.).
        // Sampled at the same N=60 ratio as the Motor agent's emitter so
        // server-side trace cadence matches handler-side trace cadence.
        private const int NoisySampleEvery = 60;
        private static readonly HashSet<string> NoisyUriTrailingSegments = new HashSet<string>(StringComparer.Ordinal)
        {
            "Feedforward",
            "HighFrequencyTorque",
        };
        private readonly Dictionary<string, int> _noisyCounters = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Build a server bound to the fixed <see cref="CoapPort"/>.
        /// All handlers are registered up-front — no late binding.
        /// </summary>
        /// <param name="data">Live device-state model. Resource handlers read from
        ///   this for GET responses and write through <paramref name="hw"/> for POSTs.</param>
        /// <param name="hw">Hardware applier used by POST handlers to push values
        ///   onto the wheelbase / dash / pedals / etc.</param>
        /// <remarks>
        /// Constructor is internal because <see cref="HardwareApplier"/> is an
        /// internal type; the public read-only surface (Status, IsRunning,
        /// RecentRequests, etc.) remains accessible to external callers via
        /// <see cref="MozaPlugin.SdkServer"/>.
        /// </remarks>
        internal MozaSdkCoapServer(MozaData data, HardwareApplier hw)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (hw == null) throw new ArgumentNullException(nameof(hw));

            _catalog = new DeviceCatalog(data);
            _registry = new CoapResourceRegistry(_catalog);
            ResourceBindings.RegisterAll(_registry, _catalog, data, hw);
        }

        /// <summary>True while the receive thread is alive.</summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>Human-readable status string surfaced to the UI tab.</summary>
        public string Status
        {
            get { lock (_stateGate) return _status; }
        }

        /// <summary>The port the listener actually bound to. 0 when not running.</summary>
        public int BoundPort
        {
            get { lock (_stateGate) return _boundPort; }
        }

        /// <summary>Diagnostic: the URI suffixes the resource registry currently knows about.</summary>
        public IEnumerable<string> KnownUriSuffixes => _registry.KnownUriSuffixes;

        /// <summary>
        /// Rolling buffer of the most recent <see cref="RecentRequestCapacity"/>
        /// requests, newest last. Returns a snapshot; the underlying list may
        /// mutate concurrently so callers should not retain element references
        /// across UI tick boundaries.
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
        /// Bind the UDP socket and spawn the receive thread. Idempotent —
        /// calling <see cref="Start"/> on an already-running server is a no-op
        /// (a warning is logged). All errors set <see cref="Status"/> and are
        /// logged via <see cref="MozaLog"/>; the call swallows exceptions so a
        /// bad port doesn't prevent plugin startup.
        /// </summary>
        public void Start()
        {
            lock (_stateGate)
            {
                if (_running)
                {
                    MozaLog.Warn("[Sdk] CoAP server already running; Start() ignored.");
                    return;
                }

                int desiredPort = CoapPort;

                try
                {
                    // Always loopback in v1 — see class doc. The bind-loopback
                    // setting is honoured implicitly (true is the only path).
                    var endpoint = new IPEndPoint(IPAddress.Loopback, desiredPort);
                    var udp = new UdpClient(endpoint);

                    // No ReceiveTimeout: ReceiveLoop uses Socket.Poll for
                    // its 200 ms wake-up budget. ReceiveTimeout-based
                    // polling threw SocketException(TimedOut) every 200 ms
                    // and SimHub's AppDomain.FirstChanceException listener
                    // logged each throw regardless of catch, which would
                    // spam SimHub.txt when this server is enabled. Poll
                    // returns a bool on timeout so the steady-state idle
                    // loop stays exception-free.

                    _udp = udp;
                    _boundPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                    _stopRequested = false;

                    _thread = new Thread(ReceiveLoop)
                    {
                        IsBackground = true,
                        Name = "MozaSdkCoapServer",
                    };
                    _running = true;
                    _status = $"Listening on 127.0.0.1:{_boundPort}";
                    _thread.Start();

                    MozaLog.Info($"[Sdk] CoAP server listening on 127.0.0.1:{_boundPort}");
                }
                catch (SocketException sx)
                {
                    bool inUse = sx.SocketErrorCode == SocketError.AddressAlreadyInUse;
                    _status = inUse
                        ? $"Port {desiredPort} in use by another process"
                        : $"Error: {sx.SocketErrorCode} ({sx.Message})";
                    MozaLog.Error($"[Sdk] CoAP server bind failed on port {desiredPort}: {sx.SocketErrorCode}: {sx.Message}");
                    CleanupSocketLocked();
                }
                catch (Exception ex)
                {
                    _status = $"Error: {ex.Message}";
                    MozaLog.Error($"[Sdk] CoAP server start failed: {ex}");
                    CleanupSocketLocked();
                }
            }
        }

        /// <summary>
        /// Stop the receive thread and close the socket. Idempotent. Waits at
        /// most 1 s for the receive thread to exit; if it doesn't, the thread
        /// is left as a background daemon to be reclaimed at process exit.
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
                // Close inside the lock so the receive loop's next
                // Poll/Receive sees an ObjectDisposedException. Socket.Poll
                // also has a 200 ms wake-up budget so the loop re-checks
                // _stopRequested on every cycle even on platforms where
                // Close doesn't promptly interrupt a blocked Receive.
                try { udp?.Close(); } catch { }
            }

            if (t != null)
            {
                try
                {
                    if (!t.Join(1000))
                        MozaLog.Warn("[Sdk] CoAP server receive thread did not exit within 1s; leaving as background daemon.");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Sdk] CoAP server thread join: {ex.Message}");
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

            try { _observers.Clear(); } catch { }
            MozaLog.Info("[Sdk] CoAP server stopped");
        }

        /// <summary>Equivalent to <see cref="Stop"/>; <see cref="IDisposable"/> contract.</summary>
        public void Dispose()
        {
            Stop();
        }

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
                // so the idle loop never throws SocketException(TimedOut)
                // — that pattern surfaced through SimHub's
                // AppDomain.FirstChanceException listener and would spam
                // the log when this server is enabled.
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
                    MozaLog.Debug($"[Sdk] CoAP poll socket error: {sx.SocketErrorCode}: {sx.Message}");
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
                    MozaLog.Debug($"[Sdk] CoAP receive socket error: {sx.SocketErrorCode}: {sx.Message}");
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_stopRequested) break;
                    MozaLog.Warn($"[Sdk] CoAP receive failed: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (remote == null || datagram == null || datagram.Length == 0) continue;

                try
                {
                    HandleDatagram(udp, remote, datagram);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Sdk] CoAP handler threw: {ex.GetType().Name}: {ex.Message}");
                    // Best-effort 5.00 if the datagram parsed enough to give us
                    // a MID/Token. Swallow further errors — the client will
                    // retransmit if it needed the response.
                    TrySendInternalErrorFallback(udp, remote, datagram);
                }
            }
        }

        private void HandleDatagram(UdpClient udp, IPEndPoint remote, byte[] datagram)
        {
            var sw = Stopwatch.StartNew();

            CoapMessage request;
            try
            {
                request = CoapMessage.Decode(datagram);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Sdk] CoAP parse failed from {remote}: {ex.Message}");
                return;
            }

            // CoAP CON ping: Code=0 and Type=CON. Reply with empty RST echoing
            // the MID (no token, no payload). RFC 7252 §4.2.
            if (request.Code == CoapCode.Empty && request.Type == CoapCode.TypeCon)
            {
                var rst = new CoapMessage
                {
                    Type = CoapCode.TypeRst,
                    Code = CoapCode.Empty,
                    MessageId = request.MessageId,
                    Token = Array.Empty<byte>(),
                    Payload = Array.Empty<byte>(),
                };
                SendSafe(udp, rst, remote);
                AppendRecent(new RecentRequest(DateTime.Now, "Ping", string.Empty, CoapCode.Empty, (int)sw.ElapsedMilliseconds));
                return;
            }

            string uriPath = "/" + request.UriPath;
            string verb = MapVerb(request.Code);

            // Resolve and dispatch.
            CoapResourceResponse resourceResponse;
            CoapResourceHandler? handler = _registry.Resolve(uriPath, out string? deviceId, out string? propertyName);
            if (handler == null)
            {
                resourceResponse = CoapResourceResponse.NotFound("no handler bound");
            }
            else
            {
                int contentFormat = -1;
                var cfOpt = request.GetOption(CoapOptionNumber.ContentFormat);
                if (cfOpt != null) contentFormat = (int)cfOpt.Value.ValueAsUInt();

                var resourceReq = new CoapResourceRequest(
                    uriPath: uriPath,
                    deviceId: deviceId,
                    propertyName: propertyName,
                    payload: request.Payload,
                    contentFormat: contentFormat,
                    token: request.Token);

                try
                {
                    switch (request.Code)
                    {
                        case CoapCode.Get:
                            resourceResponse = handler.HandleGet(resourceReq);
                            break;
                        case CoapCode.Post:
                            resourceResponse = handler.HandlePost(resourceReq);
                            break;
                        default:
                            resourceResponse = CoapResourceResponse.MethodNotAllowed(
                                $"verb 0x{request.Code:X2} not implemented");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Sdk] handler {handler.GetType().Name} threw: {ex.GetType().Name}: {ex.Message}");
                    resourceResponse = CoapResourceResponse.InternalError(ex.GetType().Name);
                }
            }

            // Observe (RFC 7641). Register/Deregister is best-effort plumbing —
            // Phase 1 has no observe-supporting handlers, so the request still
            // produces a normal 2.05 below. Observe option only echoed when the
            // handler actually advertises support.
            bool emitObserveOption = false;
            uint observeSeq = 0;
            if (handler != null && handler.SupportsObserve && request.TryGetObserve(out uint obsValue))
            {
                if (obsValue == 0)
                {
                    try { _observers.Register(remote, request.Token, uriPath); }
                    catch (Exception ex) { MozaLog.Debug($"[Sdk] Observe register failed: {ex.Message}"); }
                    emitObserveOption = true;
                    observeSeq = 0; // initial response per RFC 7641 §3.4.
                }
                else if (obsValue == 1)
                {
                    try { _observers.Deregister(remote, request.Token); }
                    catch (Exception ex) { MozaLog.Debug($"[Sdk] Observe deregister failed: {ex.Message}"); }
                }
            }

            // Build response message.
            var response = new CoapMessage
            {
                Type = request.Type == CoapCode.TypeCon ? CoapCode.TypeAck : CoapCode.TypeNon,
                Code = resourceResponse.ResponseCode,
                MessageId = request.MessageId,
                Token = request.Token ?? Array.Empty<byte>(),
                Payload = resourceResponse.Payload ?? Array.Empty<byte>(),
            };
            if (resourceResponse.ContentFormat >= 0)
            {
                response.Options.Add(new CoapOption(
                    CoapOptionNumber.ContentFormat,
                    CoapOption.EncodeUInt((uint)resourceResponse.ContentFormat)));
            }
            if (emitObserveOption)
            {
                response.Options.Add(new CoapOption(
                    CoapOptionNumber.Observe,
                    CoapOption.EncodeUInt(observeSeq)));
            }

            SendSafe(udp, response, remote);

            int durationMs = (int)sw.ElapsedMilliseconds;
            AppendRecent(new RecentRequest(DateTime.Now, verb, uriPath, resourceResponse.ResponseCode, durationMs));
            LogRequest(verb, uriPath, propertyName, resourceResponse.ResponseCode, durationMs);
        }

        private void TrySendInternalErrorFallback(UdpClient udp, IPEndPoint remote, byte[] datagram)
        {
            try
            {
                if (datagram == null || datagram.Length < 4) return;
                CoapMessage req;
                try { req = CoapMessage.Decode(datagram); }
                catch { return; }

                var resp = new CoapMessage
                {
                    Type = req.Type == CoapCode.TypeCon ? CoapCode.TypeAck : CoapCode.TypeNon,
                    Code = CoapCode.InternalServerError,
                    MessageId = req.MessageId,
                    Token = req.Token ?? Array.Empty<byte>(),
                    Payload = Array.Empty<byte>(),
                };
                SendSafe(udp, resp, remote);
            }
            catch { /* swallow — last-ditch error path */ }
        }

        private static void SendSafe(UdpClient udp, CoapMessage msg, IPEndPoint remote)
        {
            byte[] encoded;
            try { encoded = msg.Encode(); }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Sdk] CoAP encode failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            try { udp.Send(encoded, encoded.Length, remote); }
            catch (ObjectDisposedException) { /* socket closed during shutdown */ }
            catch (SocketException sx) { MozaLog.Debug($"[Sdk] CoAP send: {sx.SocketErrorCode}: {sx.Message}"); }
            catch (Exception ex) { MozaLog.Warn($"[Sdk] CoAP send failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string MapVerb(byte code)
        {
            switch (code)
            {
                case CoapCode.Get: return "GET";
                case CoapCode.Post: return "POST";
                case CoapCode.Put: return "PUT";
                case CoapCode.Delete: return "DELETE";
                case CoapCode.Empty: return "Ping";
                default: return $"0x{code:X2}";
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
                    MozaLog.Debug($"[Sdk] RecentRequestAppended subscriber threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void LogRequest(string verb, string uriPath, string? propertyName, byte responseCode, int durationMs)
        {
            bool isNoisy = propertyName != null && NoisyUriTrailingSegments.Contains(propertyName);
            if (isNoisy)
            {
                int n;
                lock (_noisyCounters)
                {
                    if (!_noisyCounters.TryGetValue(propertyName!, out n)) n = 0;
                    n++;
                    _noisyCounters[propertyName!] = n;
                }
                if ((n % NoisySampleEvery) != 1) return;
                MozaLog.Debug(string.Format(
                    CultureInfo.InvariantCulture,
                    "[Sdk] {0} {1} -> {2} ({3}ms) (sample 1/{4})",
                    verb, uriPath, CoapCode.Format(responseCode), durationMs, NoisySampleEvery));
                return;
            }

            MozaLog.Debug(string.Format(
                CultureInfo.InvariantCulture,
                "[Sdk] {0} {1} -> {2} ({3}ms)",
                verb, uriPath, CoapCode.Format(responseCode), durationMs));
        }

        // must hold _stateGate
        private void CleanupSocketLocked()
        {
            if (_udp != null)
            {
                try { _udp.Close(); } catch { }
                try { ((IDisposable)_udp).Dispose(); } catch { }
                _udp = null;
            }
        }
    }
}
