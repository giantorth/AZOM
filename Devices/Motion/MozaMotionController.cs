using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Motion
{
    /// <summary>
    /// Dedicated lane for the MOZA Motion Controller (HMA150 platform, PID <c>0x2000</c>).
    /// Holds the controller's CDC port only while something needs it — a started SimHub
    /// Motion output or an open settings panel (<see cref="Acquire"/>) — so Motion Manager
    /// or another tool can open it the rest of the time. While held it keeps the host
    /// heartbeat alive (the controller raises error 269 and drops to mode 0 after ~500 ms
    /// of silence), enrols identity / port map / setup / effect values, and runs the engage
    /// sequence (travel self-check, mode-1 entry, 0x22/00 streaming). On release it puts
    /// the controller in standby and closes the port.
    ///
    /// Threading: inbound frames are parsed on the connection's dispatch thread into
    /// volatile/Interlocked fields (no locks — the 0x0E notification ack is sent from
    /// there). Everything that writes is driven from one worker thread: heartbeat,
    /// polls, queued UI requests and the active sequence (a C# iterator yielding wait
    /// times in ms).
    ///
    /// Protocol: docs/protocol/motion/.
    /// </summary>
    public sealed class MozaMotionController : IDisposable
    {
        private const int HeartbeatMs = 250;
        private const int PortPollMs = 500;
        private const int StaleFrameMs = 500;
        private const int SelfCheckTimeoutMs = 30_000;
        private const int GameEntryTimeoutMs = 15_000;
        private const int GameLossToleranceMs = 1_500;
        private const int PortSelfTestTimeoutMs = 40_000;
        private const int LogRingSize = 200;
        private const int EnrolReadSpacingMs = 15;
        private const int EnrolRetryMs = 500;
        private const int EnrolTimeoutMs = 5_000;
        private const int ConnectRetryMs = 1_000;
        private const int EngageConnectTimeoutMs = 15_000;
        private const int StandbyTimeoutMs = 5_000;
        private const int DesiredCheckMs = 1_000;
        private const int DesiredPushAttempts = 3;

        private readonly MozaSerialConnection _connection;
        private readonly Func<bool> _isShuttingDown;
        private readonly Thread _worker;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<Action> _requests = new ConcurrentQueue<Action>();
        private volatile bool _running = true;

        // ---- state written by the dispatch thread
        private volatile int _modeTarget = -1;
        private volatile int _modeCurrent = -1;
        private volatile int _flags;
        private volatile int _runState = -1;
        private volatile int _actuatorsPresent;
        private volatile int _actuatorsEnrolled;
        private volatile int _supported;
        private volatile int _portCount;
        private volatile bool _selfCheckPassed;
        private volatile bool _selfCheckSeen;
        private volatile bool _countsSeen;
        private int _portRolesSeen;           // bitmask of ports whose role read was answered
        private readonly int[] _portPresent = new int[MozaMotionProtocol.PortCount + 1];   // -1 unknown, 0, 1
        private readonly int[] _portSlave = new int[MozaMotionProtocol.PortCount + 1];
        private readonly int[] _portRole = new int[MozaMotionProtocol.PortCount + 1];      // -1 unknown
        private readonly long[] _portWord = new long[MozaMotionProtocol.PortCount + 1];
        private volatile string _model = string.Empty, _hwId = string.Empty, _hwRev = string.Empty;
        private volatile string _fw = string.Empty, _hw = string.Empty, _uid = string.Empty;
        private float _maxStroke = float.NaN, _hapticIntensity = float.NaN;
        private float _rigLength = float.NaN, _rigWidth = float.NaN;
        private readonly float[] _effectIntensity = new float[MozaMotionProtocol.EffectSlots.Length];
        private readonly float[] _effectSmoothness = new float[MozaMotionProtocol.EffectSlots.Length];
        private float _roadIntensity = float.NaN, _roadSmoothness = float.NaN;
        private MotionEffect?[]? _desiredEffects;
        private int _desiredPushes;
        private long _lastDesiredCheckMs;
        private readonly ConcurrentQueue<byte[]> _paced = new ConcurrentQueue<byte[]>();
        private long _nextPacedMs;
        private readonly ConcurrentDictionary<int, byte> _activeErrors = new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentQueue<string> _logRing = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<byte, string> _logPartial = new ConcurrentDictionary<byte, string>();
        private readonly ConcurrentDictionary<(byte Zone, byte Param), byte[]> _lighting
            = new ConcurrentDictionary<(byte, byte), byte[]>();

        // ---- state owned by the worker
        private volatile MotionLanePhase _phase = MotionLanePhase.Disconnected;
        private volatile string? _faultReason;
        private IEnumerator<int>? _sequence;
        private long _sequenceWakeMs;
        private long _lastHeartbeatMs = -HeartbeatMs;
        private long _lastPortPollMs = -PortPollMs;
        private long _nextFrameMs;
        private long _gameLostSinceMs = -1;
        private long _framesSent;
        private int _frameIntervalMs = 30;
        private bool _wasConnected;
        private bool _released;
        private long _lastConnectAttemptMs = -ConnectRetryMs;
        private IReadOnlyList<MotionConfigIssue> _lastIssues = Array.Empty<MotionConfigIssue>();

        // ---- port demand
        private int _holds;
        private MotionEngageConfig? _pendingEngage;
        private long _pendingEngageSinceMs;

        // ---- posted by the output
        private MotionTelemetryFrame? _postedFrame;
        private long _postedAtMs;
        private volatile bool _idle;

        private static readonly Regex ErrorOccurs = new Regex(@"error_code (\d+) occurs", RegexOptions.Compiled);
        private static readonly Regex ErrorClear = new Regex(@"error_code (\d+) clear", RegexOptions.Compiled);

        public MozaMotionController(Func<bool>? isShuttingDown)
        {
            _isShuttingDown = isShuttingDown ?? (() => false);
            for (int p = 0; p <= MozaMotionProtocol.PortCount; p++) { _portPresent[p] = -1; _portRole[p] = -1; }
            ResetEffects();

            // Registry-only like every other PID-classified lane: never scans ports.
            _connection = new MozaSerialConnection(
                pidFilter: MozaUsbIds.IsMotionPid,
                probeTarget: MozaProbeTarget.Motion,
                disableProbeFallback: () => true)
            {
                CaptureLabel = "motion",
            };
            _connection.MessageReceived += OnMessage;

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MozaMotion" };
            _worker.Start();
        }

        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;
        public MotionLanePhase Phase => _phase;
        public string? FaultReason => _faultReason;
        public bool IsStreaming => _phase == MotionLanePhase.Game;
        public int ModeCurrent => _modeCurrent;
        public string? LastPortName { get => _connection.LastPortName; set => _connection.LastPortName = value; }
        public string? LastDeviceId { get => _connection.LastDeviceId; set => _connection.LastDeviceId = value; }

        /// <summary>Whether the controller is on the USB bus (enumeration only; the port may be closed).</summary>
        public bool IsPresent => MozaPortDiscovery.Instance.EnumerateMatching(MozaUsbIds.IsMotionPid).Count > 0;

        /// <summary>True from <see cref="Engage"/> until the engage sequence starts or gives up.</summary>
        public bool EngagePending => Volatile.Read(ref _pendingEngage) != null;

        /// <summary>Keep the port open until the matching <see cref="Release"/>. The worker connects.</summary>
        public void Acquire() => Interlocked.Increment(ref _holds);

        /// <summary>Drop a hold; with none left the lane goes to standby and closes the port.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _holds) < 0) Interlocked.Exchange(ref _holds, 0);
        }

        private bool Wanted => Volatile.Read(ref _holds) > 0 || Volatile.Read(ref _pendingEngage) != null;

        // ------------------------------------------------------------ output API

        /// <summary>
        /// Self-check if needed, enter game mode and start streaming. Connects and enrols
        /// first when the port is closed; the caller should hold the lane (<see cref="Acquire"/>).
        /// </summary>
        public void Engage(MotionEngageConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            // Clear a previous fault now, so the output's first update after Start doesn't
            // see it before the worker picks the request up.
            _faultReason = null;
            var phase = _phase;
            if (phase == MotionLanePhase.Game || phase == MotionLanePhase.EnteringGame
                || phase == MotionLanePhase.SelfChecking) return;
            if (phase == MotionLanePhase.Faulted) _phase = MotionLanePhase.Ready;
            Interlocked.Exchange(ref _pendingEngageSinceMs, _clock.ElapsedMilliseconds);
            Volatile.Write(ref _pendingEngage, config);
        }

        /// <summary>Stop streaming: run state stop, then standby (motors off).</summary>
        public void Disengage()
        {
            Volatile.Write(ref _pendingEngage, null);
            _requests.Enqueue(() =>
            {
                if (_phase == MotionLanePhase.Disconnected || _phase == MotionLanePhase.Enrolling
                    || _phase == MotionLanePhase.PortSelfTest) return;
                StartSequence(DisengageSequence());
            });
        }

        /// <summary>Latest frame from the output. Streamed at the engage frame interval; stale after 500 ms.</summary>
        public void PostFrame(MotionTelemetryFrame frame)
        {
            Volatile.Write(ref _postedFrame, frame);
            Interlocked.Exchange(ref _postedAtMs, _clock.ElapsedMilliseconds);
        }

        /// <summary>SimHub idle: keep game mode, stream neutral frames with the in-game flag clear.</summary>
        public void SetIdle(bool idle) => _idle = idle;

        // ------------------------------------------------------------ setup API (settings panel)

        public void IdentifyPort(int port)
            => _requests.Enqueue(() => Send(MozaMotionProtocol.BuildIdentifyPort(port)));

        /// <summary>
        /// Motion Manager's "Configure to Self-Test": armed mode, a role per populated port,
        /// port self-test (mode 8, each actuator moves in turn), then idle and a re-read.
        /// </summary>
        public void RunPortSelfTest(IReadOnlyDictionary<int, MotionCorner> rolesByPort)
        {
            var roles = new Dictionary<int, MotionCorner>();
            foreach (var kv in rolesByPort) roles[kv.Key] = kv.Value;
            _requests.Enqueue(() =>
            {
                if (_phase != MotionLanePhase.Ready && _phase != MotionLanePhase.Faulted) return;
                StartSequence(PortSelfTestSequence(roles));
            });
        }

        /// <summary>Travel self-check outside an engage (mode 7, then idle).</summary>
        public void RunTravelSelfCheck()
        {
            _requests.Enqueue(() =>
            {
                if (_phase != MotionLanePhase.Ready && _phase != MotionLanePhase.Faulted) return;
                StartSequence(TravelSelfCheckSequence());
            });
        }

        /// <summary>Max Available Stroke (0..1). Write inferred from the 0x1F/04 read; read back.</summary>
        public void WriteMaxStroke(float value)
            => _requests.Enqueue(() =>
            {
                Send(MozaMotionProtocol.BuildGlobalWrite(MozaMotionProtocol.GlobalMaxStroke, value));
                Send(MozaMotionProtocol.BuildGlobalRead(MozaMotionProtocol.GlobalMaxStroke));
            });

        /// <summary>Actuator installation distances (mm). Write inferred from the 0x1F/08 read; read back.</summary>
        public void WriteRigDimensions(float lengthMm, float widthMm)
            => _requests.Enqueue(() =>
            {
                Send(MozaMotionProtocol.BuildRigDimensionsWrite(lengthMm, widthMm));
                Send(MozaMotionProtocol.BuildRigDimensionsRead());
            });

        /// <summary>
        /// The active profile's effect values, per <see cref="MozaMotionProtocol.EffectSlots"/>
        /// entry then road detail; null entries are left as the controller holds them. Written
        /// now if enrolled, otherwise once the next enrol has read the current values.
        /// </summary>
        public void SetDesiredEffects(MotionEffect?[] desired)
        {
            if (desired == null || desired.Length != MozaMotionProtocol.EffectSlots.Length + 1)
                throw new ArgumentException("one entry per effect slot plus road detail", nameof(desired));
            Volatile.Write(ref _desiredEffects, (MotionEffect?[])desired.Clone());
            _requests.Enqueue(() => { _desiredPushes = 0; PushDesiredEffects(); });
        }

        /// <summary>
        /// Queue a write + readback for every desired value the controller doesn't hold.
        /// Re-run each second from the tick (capped) so a write the controller dropped is retried.
        /// </summary>
        private void PushDesiredEffects()
        {
            var desired = Volatile.Read(ref _desiredEffects);
            if (desired == null || !_connection.IsConnected) return;
            if (_phase == MotionLanePhase.Enrolling || _phase == MotionLanePhase.Disconnected) return;
            if (_desiredPushes >= DesiredPushAttempts) return;
            int slots = MozaMotionProtocol.EffectSlots.Length;
            bool any = false;
            for (int i = 0; i <= slots; i++)
            {
                if (!desired[i].HasValue) continue;
                var want = desired[i]!.Value;
                float curI = i < slots ? Volatile.Read(ref _effectIntensity[i]) : Volatile.Read(ref _roadIntensity);
                float curS = i < slots ? Volatile.Read(ref _effectSmoothness[i]) : Volatile.Read(ref _roadSmoothness);
                if (Math.Abs(curI - want.Intensity) < 0.001f && Math.Abs(curS - want.Smoothness) < 0.001f) continue;
                any = true;
                if (i < slots)
                {
                    _paced.Enqueue(MozaMotionProtocol.BuildEffectSlot(MozaMotionProtocol.EffectSlots[i], want.Intensity, want.Smoothness));
                    _paced.Enqueue(MozaMotionProtocol.BuildEffectSlotRead(MozaMotionProtocol.EffectSlots[i]));
                }
                else
                {
                    _paced.Enqueue(MozaMotionProtocol.BuildRoadDetail(want.Intensity, want.Smoothness));
                    _paced.Enqueue(MozaMotionProtocol.BuildRoadDetailRead());
                }
            }
            if (any) _desiredPushes++;
        }

        /// <summary>Motion Effects slider pair (0..1 each); the controller keeps it across power cycles. Read back.</summary>
        public void WriteLighting(byte zone, byte param, byte[] value)
            => _requests.Enqueue(() =>
            {
                Send(MozaMotionProtocol.BuildLightingWrite(zone, param, value));
                Send(MozaMotionProtocol.BuildLightingRead(zone, param));
            });

        public void ReadLighting(byte zone, byte param)
            => _requests.Enqueue(() => Send(MozaMotionProtocol.BuildLightingRead(zone, param)));

        public byte[]? GetLighting(byte zone, byte param)
            => _lighting.TryGetValue((zone, param), out var v) ? v : null;

        // ------------------------------------------------------------ snapshot

        public MotionControllerSnapshot Snapshot()
        {
            var s = new MotionControllerSnapshot
            {
                Present = IsPresent,
                Connected = _connection.IsConnected,
                PortName = _connection.LastPortName,
                Phase = _phase,
                FaultReason = _faultReason,
                ModelName = _model,
                HardwareId = _hwId,
                HardwareRevision = _hwRev,
                FirmwareVersion = _fw,
                HardwareVersion = _hw,
                Uid = _uid,
                SupportedActuators = _supported,
                PortCount = _portCount,
                ModeTarget = _modeTarget,
                ModeCurrent = _modeCurrent,
                Flags = (byte)_flags,
                RunState = _runState,
                ActuatorsPresent = _actuatorsPresent,
                ActuatorsEnrolled = _actuatorsEnrolled,
                SelfCheckPassed = _selfCheckPassed,
                MaxStroke = Nullable(Volatile.Read(ref _maxStroke)),
                HapticIntensity = Nullable(Volatile.Read(ref _hapticIntensity)),
                RigLengthMm = Nullable(Volatile.Read(ref _rigLength)),
                RigWidthMm = Nullable(Volatile.Read(ref _rigWidth)),
                FramesSent = Interlocked.Read(ref _framesSent),
            };
            var errors = new List<int>(_activeErrors.Keys);
            errors.Sort();
            s.ActiveErrorCodes = errors;
            var ports = new MotionPortSnapshot[MozaMotionProtocol.PortCount];
            for (int p = 1; p <= MozaMotionProtocol.PortCount; p++)
            {
                int present = Volatile.Read(ref _portPresent[p]);
                int role = Volatile.Read(ref _portRole[p]);
                ports[p - 1] = new MotionPortSnapshot
                {
                    Port = p,
                    Present = present < 0 ? (bool?)null : present != 0,
                    Slave = (byte)Volatile.Read(ref _portSlave[p]),
                    Role = role < 0 ? (MotionCorner?)null : (MotionCorner)role,
                    StatusWord = (uint)Interlocked.Read(ref _portWord[p]),
                };
            }
            s.Ports = ports;
            var effects = new MotionEffect?[MozaMotionProtocol.EffectSlots.Length];
            for (int i = 0; i < effects.Length; i++)
                effects[i] = Effect(Volatile.Read(ref _effectIntensity[i]), Volatile.Read(ref _effectSmoothness[i]));
            s.Effects = effects;
            s.RoadDetail = Effect(Volatile.Read(ref _roadIntensity), Volatile.Read(ref _roadSmoothness));
            s.RecentLog = _logRing.ToArray();
            return s;
        }

        private static float? Nullable(float v) => float.IsNaN(v) ? (float?)null : v;

        private static MotionEffect? Effect(float intensity, float smoothness)
            => float.IsNaN(intensity) || float.IsNaN(smoothness) ? (MotionEffect?)null : new MotionEffect(intensity, smoothness);

        /// <summary>
        /// Configuration problems that block an engage: controller absent, actuators missing,
        /// port map incomplete or duplicated, port configuration pending. Empty = ready.
        /// With the port closed this is what the last session found; an engage re-checks
        /// after enrolling.
        /// </summary>
        public IReadOnlyList<MotionConfigIssue> GetConfigurationErrors()
        {
            if (!_connection.IsConnected)
            {
                if (!IsPresent) return new[] { new MotionConfigIssue(MotionConfigIssueKind.NotConnected) };
                return _lastIssues;
            }
            if (_phase == MotionLanePhase.Enrolling)
                return EngagePending ? Array.Empty<MotionConfigIssue>() : new[] { new MotionConfigIssue(MotionConfigIssueKind.Enrolling) };
            return CurrentIssues();
        }

        private List<MotionConfigIssue> CurrentIssues()
        {
            var issues = new List<MotionConfigIssue>();
            var seen = new HashSet<int>();
            int populated = 0;
            for (int p = 1; p <= MozaMotionProtocol.PortCount; p++)
            {
                if (Volatile.Read(ref _portPresent[p]) != 1) continue;
                populated++;
                int role = Volatile.Read(ref _portRole[p]);
                if (role <= 0) issues.Add(new MotionConfigIssue(MotionConfigIssueKind.PortUnassigned, p));
                else if (!seen.Add(role)) issues.Add(new MotionConfigIssue(MotionConfigIssueKind.PortDuplicate, p, (MotionCorner)role));
            }
            int expected = _supported > 0 ? _supported : MozaMotionProtocol.SupportedActuators;
            if (populated < expected)
                issues.Add(new MotionConfigIssue(MotionConfigIssueKind.ActuatorsMissing, populated, expected: expected));
            if ((_flags & MozaMotionProtocol.FlagPortConfigPending) != 0 && _phase != MotionLanePhase.PortSelfTest)
                issues.Add(new MotionConfigIssue(MotionConfigIssueKind.PortConfigPending));
            return issues;
        }

        // ------------------------------------------------------------ worker

        private void WorkerLoop()
        {
            while (_running)
            {
                try { Tick(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/Motion] Worker: {ex.Message}"); }
                Thread.Sleep(2);
            }
        }

        private void Tick()
        {
            long now = _clock.ElapsedMilliseconds;
            bool connected = _connection.IsConnected;
            if (!connected)
            {
                if (_wasConnected)
                {
                    _wasConnected = false;
                    bool streaming = _phase == MotionLanePhase.Game || _phase == MotionLanePhase.EnteringGame;
                    _sequence = null;
                    ResetDeviceState();
                    _phase = MotionLanePhase.Disconnected;
                    if (streaming) _faultReason = "Motion controller disconnected while running";
                    MozaLog.Info(_released ? "[AZOM/Motion] Port released" : "[AZOM/Motion] Controller disconnected");
                    _released = false;
                }
                while (_requests.TryDequeue(out _)) { }
                TryConnect(now);
                Thread.Sleep(50);
                return;
            }
            if (!_wasConnected)
            {
                _wasConnected = true;
                ResetDeviceState();
                _phase = MotionLanePhase.Enrolling;
                StartSequence(EnrolSequence());
            }

            if (now - _lastHeartbeatMs >= HeartbeatMs)
            {
                _lastHeartbeatMs = now;
                Send(MozaMotionProtocol.BuildModePoll());
                Send(MozaMotionProtocol.BuildControllerStatus());
            }
            if (now - _lastPortPollMs >= PortPollMs)
            {
                _lastPortPollMs = now;
                // MM polls every port id for presence and the populated ports for status.
                for (int p = 1; p <= MozaMotionProtocol.PortCount; p++)
                {
                    Send(MozaMotionProtocol.BuildPresence(MozaMotionProtocol.PortDevice(p)));
                    if (Volatile.Read(ref _portPresent[p]) != 0)
                        Send(MozaMotionProtocol.BuildPortStatus(p));
                }
            }

            while (_requests.TryDequeue(out var request))
            {
                try { request(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/Motion] Request: {ex.Message}"); }
            }

            // The controller drops requests that arrive faster than ~13 ms apart.
            if (now >= _nextPacedMs && _paced.TryDequeue(out var paced))
            {
                Send(paced);
                _nextPacedMs = now + EnrolReadSpacingMs;
            }
            if (_paced.IsEmpty && now - _lastDesiredCheckMs >= DesiredCheckMs)
            {
                _lastDesiredCheckMs = now;
                PushDesiredEffects();
            }

            if (_sequence != null && now >= _sequenceWakeMs)
            {
                bool more;
                try { more = _sequence.MoveNext(); }
                catch (Exception ex) { more = false; Fault("sequence failed: " + ex.Message); }
                if (more) _sequenceWakeMs = now + Math.Max(0, _sequence!.Current);
                else _sequence = null;
            }

            if (_sequence == null && (_phase == MotionLanePhase.Ready || _phase == MotionLanePhase.Faulted))
            {
                var engage = Interlocked.Exchange(ref _pendingEngage, null);
                if (engage != null)
                {
                    _frameIntervalMs = Math.Max(5, engage.FrameIntervalMs);
                    _faultReason = null;
                    StartSequence(EngageSequence());
                }
                else if (!Wanted)
                    StartSequence(ReleaseSequence());
            }

            if (_phase == MotionLanePhase.Game || _phase == MotionLanePhase.EnteringGame)
                StreamTick(now);
            if (_phase == MotionLanePhase.Game)
                WatchGameMode(now);
        }

        /// <summary>Open the port when wanted and present; an engage that can't connect gives up.</summary>
        private void TryConnect(long now)
        {
            if (!Wanted || _isShuttingDown()) return;
            if (EngagePending && now - Interlocked.Read(ref _pendingEngageSinceMs) > EngageConnectTimeoutMs)
            {
                Volatile.Write(ref _pendingEngage, null);
                _faultReason = "Motion controller not found";
                MozaLog.Warn($"[AZOM/Motion] {_faultReason}");
                return;
            }
            if (now - _lastConnectAttemptMs < ConnectRetryMs) return;
            _lastConnectAttemptMs = now;
            if (!IsPresent) return;
            if (_connection.Connect())
            {
                // Leave Disconnected now; the next tick resets state and starts the enrol.
                _phase = MotionLanePhase.Enrolling;
                MozaLog.Info($"[AZOM/Motion] Connected to MOZA Motion Controller on {_connection.LastPortName}");
            }
        }

        private void StartSequence(IEnumerator<int> sequence)
        {
            _sequence = sequence;
            _sequenceWakeMs = 0;
        }

        private void StreamTick(long now)
        {
            if (now < _nextFrameMs) return;
            _nextFrameMs = now + _frameIntervalMs;
            MotionTelemetryFrame frame;
            var posted = Volatile.Read(ref _postedFrame);
            long age = now - Interlocked.Read(ref _postedAtMs);
            // Entry and stale data: neutral with the in-game flag set, as the prototype does.
            // SimHub idle: neutral with the flag clear, as MM does while the car is off track.
            if (_phase == MotionLanePhase.Game && _idle)
                frame = MotionTelemetryFrame.Neutral(inGame: false);
            else if (_phase != MotionLanePhase.Game || posted == null || age > StaleFrameMs)
                frame = MotionTelemetryFrame.Neutral(inGame: true);
            else
                frame = posted;
            _connection.SendStream(StreamKind.MotionTelemetry, MozaMotionProtocol.BuildTelemetryFrame(frame));
            Interlocked.Increment(ref _framesSent);
        }

        private void WatchGameMode(long now)
        {
            if (_modeCurrent == (int)MotionMode.Game) { _gameLostSinceMs = -1; return; }
            if (_gameLostSinceMs < 0) { _gameLostSinceMs = now; return; }
            if (now - _gameLostSinceMs > GameLossToleranceMs)
                Fault($"controller left game mode (mode {_modeCurrent}, flags 0x{_flags:X2}{ErrorSuffix()})");
        }

        private string ErrorSuffix()
        {
            var keys = new List<int>(_activeErrors.Keys);
            return keys.Count == 0 ? "" : ", errors " + string.Join(",", keys);
        }

        private void Fault(string reason)
        {
            _faultReason = reason;
            _phase = MotionLanePhase.Faulted;
            _sequence = null;
            MozaLog.Warn($"[AZOM/Motion] {reason}");
        }

        private void Send(byte[] frame)
        {
            if (_connection.IsConnected) _connection.Send(frame);
        }

        private void SendRunState(MotionRunState sub)
        {
            Send(MozaMotionProtocol.BuildRunState(sub));
            Send(MozaMotionProtocol.BuildRunStateRead());
        }

        private void ResetDeviceState()
        {
            _modeTarget = _modeCurrent = -1;
            _flags = 0;
            _runState = -1;
            _selfCheckPassed = false;
            _countsSeen = false;
            _model = _hwId = _hwRev = _fw = _hw = _uid = string.Empty;
            Volatile.Write(ref _maxStroke, float.NaN);
            Volatile.Write(ref _hapticIntensity, float.NaN);
            Volatile.Write(ref _rigLength, float.NaN);
            Volatile.Write(ref _rigWidth, float.NaN);
            Interlocked.Exchange(ref _portRolesSeen, 0);
            for (int p = 0; p <= MozaMotionProtocol.PortCount; p++)
            {
                Volatile.Write(ref _portPresent[p], -1);
                Volatile.Write(ref _portRole[p], -1);
                Interlocked.Exchange(ref _portWord[p], 0);
            }
            ResetEffects();
            _desiredPushes = 0;
            while (_paced.TryDequeue(out _)) { }
            _activeErrors.Clear();
            _logPartial.Clear();
            _gameLostSinceMs = -1;
        }

        private void ResetEffects()
        {
            for (int i = 0; i < _effectIntensity.Length; i++)
            {
                Volatile.Write(ref _effectIntensity[i], float.NaN);
                Volatile.Write(ref _effectSmoothness[i], float.NaN);
            }
            Volatile.Write(ref _roadIntensity, float.NaN);
            Volatile.Write(ref _roadSmoothness, float.NaN);
        }

        // ------------------------------------------------------------ sequences (yield = wait ms)

        // The controller answers about one request per 13 ms and drops what overflows its
        // queue; Motion Manager re-sends unanswered reads every ~500 ms.
        private IEnumerator<int> EnrolSequence()
        {
            var reads = new List<(byte[] Frame, Func<bool>? Answered)>();
            foreach (var probe in MozaMotionProtocol.BuildIdentityProbes())
                reads.Add((probe, IdentityAnswered(probe)));
            reads.Add((MozaMotionProtocol.BuildCountsRead(), () => _countsSeen));
            reads.Add((MozaMotionProtocol.BuildModePoll(), () => _modeCurrent >= 0));
            reads.Add((MozaMotionProtocol.BuildRunStateRead(), () => _runState >= 0));
            reads.Add((MozaMotionProtocol.BuildGlobalRead(MozaMotionProtocol.GlobalMaxStroke),
                () => !float.IsNaN(Volatile.Read(ref _maxStroke))));
            reads.Add((MozaMotionProtocol.BuildGlobalRead(MozaMotionProtocol.GlobalHapticIntensity),
                () => !float.IsNaN(Volatile.Read(ref _hapticIntensity))));
            reads.Add((MozaMotionProtocol.BuildRigDimensionsRead(), () => !float.IsNaN(Volatile.Read(ref _rigLength))));
            for (int i = 0; i < MozaMotionProtocol.EffectSlots.Length; i++)
            {
                int idx = i;
                reads.Add((MozaMotionProtocol.BuildEffectSlotRead(MozaMotionProtocol.EffectSlots[idx]),
                    () => !float.IsNaN(Volatile.Read(ref _effectSmoothness[idx]))));
            }
            reads.Add((MozaMotionProtocol.BuildRoadDetailRead(), () => !float.IsNaN(Volatile.Read(ref _roadSmoothness))));
            for (int p = 1; p <= MozaMotionProtocol.PortCount; p++)
            {
                int port = p, bit = 1 << p;
                reads.Add((MozaMotionProtocol.BuildPortStatus(port), () => Volatile.Read(ref _portPresent[port]) != -1));
                reads.Add((MozaMotionProtocol.BuildPortRoleRead(port), () => (Volatile.Read(ref _portRolesSeen) & bit) != 0));
            }

            long deadline = _clock.ElapsedMilliseconds + EnrolTimeoutMs;
            for (bool first = true; ; first = false)
            {
                foreach (var (frame, answered) in reads)
                {
                    if (answered == null ? !first : answered()) continue;
                    Send(frame);
                    yield return EnrolReadSpacingMs;
                }
                long retryAt = _clock.ElapsedMilliseconds + EnrolRetryMs;
                while (!AllAnswered(reads) && _clock.ElapsedMilliseconds < retryAt)
                    yield return 20;
                if (AllAnswered(reads) || _clock.ElapsedMilliseconds >= deadline) break;
            }
            if (!AllAnswered(reads))
            {
                var missing = new List<string>();
                foreach (var (frame, answered) in reads)
                    if (answered != null && !answered()) missing.Add(BitConverter.ToString(frame).Replace("-", ""));
                MozaLog.Debug($"[AZOM/Motion] Enrol reads unanswered: {string.Join(" ", missing)}");
            }

            // MM ends its connect push with run-state stop and device type 1.
            SendRunState(MotionRunState.Stop);
            Send(MozaMotionProtocol.BuildDeviceType());
            _lastIssues = CurrentIssues();
            _phase = MotionLanePhase.Ready;
            MozaLog.Info($"[AZOM/Motion] Enrolled {_model} fw {_fw}, mode {_modeCurrent}, "
                + $"{CountPresent()} actuators");
            PushDesiredEffects();
        }

        private static bool AllAnswered(List<(byte[] Frame, Func<bool>? Answered)> reads)
        {
            foreach (var (_, answered) in reads)
                if (answered != null && !answered()) return false;
            return true;
        }

        /// <summary>Completion check for an identity probe; null = no tracked reply, sent once.</summary>
        private Func<bool>? IdentityAnswered(byte[] probe)
        {
            byte arg = probe.Length > 5 ? probe[4] : (byte)0;
            switch (probe[2])
            {
                case 0x04: return () => _fw.Length != 0;
                case 0x05: return () => _hw.Length != 0;
                case 0x06: return () => _uid.Length != 0;
                case 0x07: return () => _model.Length != 0;
                case 0x08 when arg == 1: return () => _hwId.Length != 0;
                case 0x08 when arg == 2: return () => _hwRev.Length != 0;
                default: return null;
            }
        }

        private int CountPresent()
        {
            int n = 0;
            for (int p = 1; p <= MozaMotionProtocol.PortCount; p++)
                if (Volatile.Read(ref _portPresent[p]) == 1) n++;
            return n;
        }

        private IEnumerator<int> EngageSequence()
        {
            // The port may have opened just for this engage, so the setup is first known here.
            var issues = CurrentIssues();
            if (issues.Count > 0)
            {
                Fault("configuration: " + string.Join("; ", issues));
                yield break;
            }

            if (!_selfCheckPassed)
            {
                _phase = MotionLanePhase.SelfChecking;
                _selfCheckSeen = false;
                MozaLog.Info("[AZOM/Motion] Travel self-check");
                Send(MozaMotionProtocol.BuildSetMode(MotionMode.TravelSelfCheck));
                long deadline = _clock.ElapsedMilliseconds + SelfCheckTimeoutMs;
                while (!_selfCheckSeen)
                {
                    if (_clock.ElapsedMilliseconds > deadline) { Fault("travel self-check did not report success"); yield break; }
                    yield return 20;
                }
                yield return 300;
            }

            // Prototype-proven entry: stream neutral frames and repeat set-mode 1 until the
            // controller reports mode 1 (it passes through 5/3); run state 03 after ~300 ms.
            _phase = MotionLanePhase.EnteringGame;
            _nextFrameMs = 0;
            long start = _clock.ElapsedMilliseconds;
            bool runSent = false;
            while (_modeCurrent != (int)MotionMode.Game)
            {
                long elapsed = _clock.ElapsedMilliseconds - start;
                if (elapsed > GameEntryTimeoutMs) { Fault($"controller did not enter game mode (mode {_modeCurrent}{ErrorSuffix()})"); yield break; }
                Send(MozaMotionProtocol.BuildSetMode(MotionMode.Game));
                if (!runSent && elapsed > 300) { SendRunState(MotionRunState.Run); runSent = true; }
                yield return 33;
            }
            if (!runSent) SendRunState(MotionRunState.Run);
            _gameLostSinceMs = -1;
            _phase = MotionLanePhase.Game;
            MozaLog.Info("[AZOM/Motion] Game mode");
        }

        private IEnumerator<int> DisengageSequence()
        {
            _phase = MotionLanePhase.Disengaging;
            _connection.ClearStreamSlots((int)StreamKind.MotionTelemetry, 1);
            SendRunState(MotionRunState.Stop);
            yield return 50;
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.Standby));
            long deadline = _clock.ElapsedMilliseconds + StandbyTimeoutMs;
            while (_modeCurrent != (int)MotionMode.Standby && _clock.ElapsedMilliseconds < deadline)
                yield return 50;
            _phase = MotionLanePhase.Ready;
            MozaLog.Info($"[AZOM/Motion] Disengaged (mode {_modeCurrent})");
        }

        /// <summary>Nothing holds the lane: standby (as Motion Manager's Close), then close the port.</summary>
        private IEnumerator<int> ReleaseSequence()
        {
            _phase = MotionLanePhase.Disengaging;
            if (_modeCurrent != (int)MotionMode.Standby)
            {
                SendRunState(MotionRunState.Stop);
                yield return 50;
                Send(MozaMotionProtocol.BuildSetMode(MotionMode.Standby));
                long deadline = _clock.ElapsedMilliseconds + StandbyTimeoutMs;
                while (_modeCurrent != (int)MotionMode.Standby && _clock.ElapsedMilliseconds < deadline)
                    yield return 50;
            }
            if (Wanted)
            {
                _phase = MotionLanePhase.Ready;
                yield break;
            }
            _lastIssues = CurrentIssues();
            _released = true;
            _connection.Disconnect();
        }

        private IEnumerator<int> TravelSelfCheckSequence()
        {
            _phase = MotionLanePhase.SelfChecking;
            _selfCheckSeen = false;
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.TravelSelfCheck));
            long deadline = _clock.ElapsedMilliseconds + SelfCheckTimeoutMs;
            while (!_selfCheckSeen)
            {
                if (_clock.ElapsedMilliseconds > deadline) { Fault("travel self-check did not report success"); yield break; }
                yield return 20;
            }
            yield return 300;
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.Idle));
            _phase = MotionLanePhase.Ready;
        }

        private IEnumerator<int> PortSelfTestSequence(Dictionary<int, MotionCorner> roles)
        {
            _phase = MotionLanePhase.PortSelfTest;
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.Armed));
            yield return 20;
            foreach (var kv in roles)
            {
                Send(MozaMotionProtocol.BuildPortRoleWrite(kv.Key, kv.Value));
                Send(MozaMotionProtocol.BuildPortRoleRead(kv.Key));
                yield return 1000;
            }
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.PortSelfTest));

            // Each actuator moves in turn; the controller sets flag 0x20 while it runs and
            // clears it when done (~14 s in the captures). MM then returns to idle.
            long deadline = _clock.ElapsedMilliseconds + PortSelfTestTimeoutMs;
            bool pendingSeen = false;
            while (true)
            {
                if (_clock.ElapsedMilliseconds > deadline) { Fault("port self-test did not finish"); yield break; }
                bool pending = (_flags & MozaMotionProtocol.FlagPortConfigPending) != 0;
                if (_modeCurrent == (int)MotionMode.PortSelfTest && pending) pendingSeen = true;
                if (pendingSeen && !pending) break;
                yield return 50;
            }
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.Idle));
            yield return 100;
            for (int p = 1; p <= MozaMotionProtocol.PortCount; p++)
                Send(MozaMotionProtocol.BuildPortRoleRead(p));
            Send(MozaMotionProtocol.BuildRigDimensionsRead());
            _phase = MotionLanePhase.Ready;
            MozaLog.Info("[AZOM/Motion] Port self-test finished");
        }

        // ------------------------------------------------------------ inbound (dispatch thread)

        private void OnMessage(byte[] d)
        {
            if (d == null || d.Length < 2) return;
            try { Parse(d); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/Motion] Parse: {ex.Message}"); }
        }

        private void Parse(byte[] d)
        {
            if (MozaMotionProtocol.TryParseLogFragment(d, out byte src, out string text)) { OnLog(src, text); return; }
            if (MozaMotionProtocol.TryParseNotification(d, out byte b1, out byte code, out byte b3))
            {
                if (d[1] == MozaMotionProtocol.ReplyController)
                    _connection.SendPriority(MozaMotionProtocol.BuildNotificationAck(b1, code, b3));
                return;
            }
            if (MozaMotionProtocol.TryParseMode(d, out byte target, out byte current, out byte flags))
            {
                _modeTarget = target;
                _modeCurrent = current;
                _flags = flags;
                if (current == (byte)MotionMode.Standby || (flags & MozaMotionProtocol.FlagTopologyChanged) != 0)
                    _selfCheckPassed = false;
                return;
            }
            if (MozaMotionProtocol.TryParseControllerStatus(d, out _, out byte present, out byte enrolled, out _))
            {
                _actuatorsPresent = present;
                _actuatorsEnrolled = enrolled;
                return;
            }
            if (MozaMotionProtocol.TryParsePortStatus(d, out int port, out bool portPresent, out byte slave, out uint word))
            {
                if (port < 1 || port > MozaMotionProtocol.PortCount) return;
                Volatile.Write(ref _portPresent[port], portPresent ? 1 : 0);
                Volatile.Write(ref _portSlave[port], slave);
                Interlocked.Exchange(ref _portWord[port], word);
                return;
            }
            if (MozaMotionProtocol.TryParseRunState(d, out byte sub)) { _runState = sub; return; }
            if (MozaMotionProtocol.TryParsePortRole(d, out int rolePort, out byte role))
            {
                if (rolePort < 1 || rolePort > MozaMotionProtocol.PortCount) return;
                Volatile.Write(ref _portRole[rolePort], role);
                int bit = 1 << rolePort, seen;
                do { seen = Volatile.Read(ref _portRolesSeen); }
                while (Interlocked.CompareExchange(ref _portRolesSeen, seen | bit, seen) != seen);
                return;
            }
            if (MozaMotionProtocol.TryParseGlobal(d, out byte index, out float value))
            {
                if (index == MozaMotionProtocol.GlobalMaxStroke) Volatile.Write(ref _maxStroke, value);
                else if (index == MozaMotionProtocol.GlobalHapticIntensity) Volatile.Write(ref _hapticIntensity, value);
                return;
            }
            if (MozaMotionProtocol.TryParseRigDimensions(d, out float length, out float width))
            {
                Volatile.Write(ref _rigLength, length);
                Volatile.Write(ref _rigWidth, width);
                return;
            }
            if (MozaMotionProtocol.TryParseCounts(d, out byte supported, out byte ports))
            {
                _supported = supported;
                _portCount = ports;
                _countsSeen = true;
                return;
            }
            if (MozaMotionProtocol.TryParseEffectSlot(d, out byte slot, out float intensity, out float smoothness))
            {
                int idx = Array.IndexOf(MozaMotionProtocol.EffectSlots, slot);
                if (idx < 0) return;
                Volatile.Write(ref _effectIntensity[idx], intensity);
                Volatile.Write(ref _effectSmoothness[idx], smoothness);
                return;
            }
            if (MozaMotionProtocol.TryParseRoadDetail(d, out float roadIntensity, out float roadSmoothness))
            {
                Volatile.Write(ref _roadIntensity, roadIntensity);
                Volatile.Write(ref _roadSmoothness, roadSmoothness);
                return;
            }
            if (MozaMotionProtocol.TryParseLighting(d, out byte zone, out byte param, out byte[] lightValue))
            {
                _lighting[(zone, param)] = lightValue;
                return;
            }
            if (d[1] == MozaMotionProtocol.ReplyController) ParseIdentity(d);
        }

        private void ParseIdentity(byte[] d)
        {
            switch (d[0])
            {
                case 0x84 when d.Length >= 5: _fw = $"{d[3]}.{d[4]}"; break;
                case 0x85 when d.Length >= 5: _hw = $"{d[3]}.{d[4]}"; break;
                case 0x86 when d.Length >= 14: _uid = BitConverter.ToString(d, 2, 12).Replace("-", ""); break;
                case 0x87 when d.Length >= 3 && d[2] == 0x01: _model = MozaMotionProtocol.IdentityString(d); break;
                case 0x88 when d.Length >= 3 && d[2] == 0x01: _hwId = MozaMotionProtocol.IdentityString(d); break;
                case 0x88 when d.Length >= 3 && d[2] == 0x02: _hwRev = MozaMotionProtocol.IdentityString(d); break;
            }
        }

        private void OnLog(byte source, string fragment)
        {
            string buffered = _logPartial.TryGetValue(source, out var prev) ? prev + fragment : fragment;
            int nl;
            while ((nl = buffered.IndexOf('\n')) >= 0)
            {
                string line = buffered.Substring(0, nl).TrimEnd('\r');
                buffered = buffered.Substring(nl + 1);
                OnLogLine(source, line);
            }
            _logPartial[source] = buffered.Length > 512 ? buffered.Substring(buffered.Length - 512) : buffered;
        }

        private void OnLogLine(byte source, string line)
        {
            if (source == MozaMotionProtocol.ReplyController)
            {
                if (line.Contains("selfCheckSuccess")) { _selfCheckPassed = true; _selfCheckSeen = true; }
                var m = ErrorOccurs.Match(line);
                if (m.Success) _activeErrors[int.Parse(m.Groups[1].Value)] = 0;
                m = ErrorClear.Match(line);
                if (m.Success) _activeErrors.TryRemove(int.Parse(m.Groups[1].Value), out _);
            }
            // The fault dump is 200 numeric lines; keep it out of the ring.
            if (line.Length == 0 || line.StartsWith("[") && char.IsDigit(line.Length > 1 ? line[1] : 'x')) return;
            _logRing.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{source:X2}] {line}");
            while (_logRing.Count > LogRingSize && _logRing.TryDequeue(out _)) { }
        }

        // ------------------------------------------------------------ teardown

        /// <summary>
        /// Shutdown: if the platform is running, stop and drop to standby before the port
        /// closes so the actuators don't sit live with no host.
        /// </summary>
        public void Dispose()
        {
            if (!_running) return;
            var phase = _phase;
            if (_connection.IsConnected
                && (phase == MotionLanePhase.Game || phase == MotionLanePhase.EnteringGame
                    || phase == MotionLanePhase.SelfChecking))
            {
                try
                {
                    _connection.ClearStreamSlots((int)StreamKind.MotionTelemetry, 1);
                    _connection.Send(MozaMotionProtocol.BuildRunState(MotionRunState.Stop));
                    _connection.Send(MozaMotionProtocol.BuildSetMode(MotionMode.Standby));
                    Thread.Sleep(150);
                }
                catch { /* shutting down */ }
            }
            _running = false;
            try { _worker.Join(500); } catch { }
            _connection.MessageReceived -= OnMessage;
            try { _connection.Disconnect(); } catch { }
            try { _connection.Dispose(); } catch { }
        }
    }
}
