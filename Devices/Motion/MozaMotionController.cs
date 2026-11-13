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
        private const int ModeLossToleranceMs = 1_500;
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
        private const int ManualModeTimeoutMs = 10_000;
        // Actuators follow the controller into mode 3 ~3.3 s after it (capture 2026-11-11).
        private const int ManualActuatorSettleMs = 3_500;
        private const float DefaultEngineStrength = DefaultEngineStrengthPercent / 100f, DefaultTactile = 0.4f;

        /// <summary>Engine vibration strength used while the profile sets none (the "iRacing gt3" preset's).</summary>
        public const int DefaultEngineStrengthPercent = 50;

        /// <summary>
        /// Effect values written when the controller holds zero intensity on every motion slot and the
        /// profile sets none (the "iRacing gt3" preset's), per <see cref="MozaMotionProtocol.EffectSlots"/>.
        /// </summary>
        private static readonly MotionEffect[] DefaultEffects =
        {
            new MotionEffect(0.28f, 0.90f),
            new MotionEffect(0.72f, 0.84f),
            new MotionEffect(0.54f, 0.80f),
            new MotionEffect(0.53f, 1.00f),
            new MotionEffect(0.39f, 1.00f),
        };

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
        private volatile bool _seedDefaultEffects;
        private float _desiredEngineStrength = float.NaN, _desiredTactile = float.NaN;
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
        private long _modeLostSinceMs = -1;
        private long _framesSent;
        private bool _vibrating;
        private int _frameIntervalMs = 30;
        private bool _wasConnected;
        private bool _released;
        private long _lastConnectAttemptMs = -ConnectRetryMs;
        private IReadOnlyList<MotionConfigIssue> _lastIssues = Array.Empty<MotionConfigIssue>();
        // Manual mode (controller mode 2) while SimHub's Manual control or Hop-in position is the
        // motion source: wanted = the output's request, driven = the lane has left game mode for it.
        private volatile bool _manualDriven;
        private volatile bool _manualWanted;
        // Pose streaming in the controller's AI mode (mode 11) instead of game mode: wanted = the
        // output's request, _aiMode = what the current / last engage entered.
        private volatile bool _aiWanted;
        private volatile bool _aiMode;
        private MotionMode StreamMode => _aiMode ? MotionMode.AiPose : MotionMode.Game;
        private float[]? _manualPose;          // pitch, roll, heave −1..1, posted by the output
        private long _manualPosedAtMs;
        private readonly float[] _manualSent = new float[3];
        private long _nextManualMs;
        private const int ManualSetpointIntervalMs = 100;
        private const float ManualSetpointStep = 0.005f;
        private static readonly byte[] ManualAxes =
            { MozaMotionProtocol.ManualAxisPitch, MozaMotionProtocol.ManualAxisRoll, MozaMotionProtocol.ManualAxisHeave };

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

        /// <summary>Something holds the lane or the platform is running: keep it across a plugin reload.</summary>
        public bool InUse
        {
            get
            {
                var phase = _phase;
                return Wanted || phase == MotionLanePhase.Game || phase == MotionLanePhase.EnteringGame
                    || phase == MotionLanePhase.SelfChecking || phase == MotionLanePhase.ManualTest;
            }
        }

        public bool IsDisposed => !_running;

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
            if (phase == MotionLanePhase.ManualTest && _manualDriven) return;
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

        /// <summary>Manual mode: SimHub's pose as setpoints, −1..+1 each (+ = pitch up, roll right, heave up).</summary>
        public void PostManualPose(float pitch, float roll, float heave)
        {
            Volatile.Write(ref _manualPose, new[] { pitch, roll, heave });
            Interlocked.Exchange(ref _manualPosedAtMs, _clock.ElapsedMilliseconds);
        }

        /// <summary>True while the lane is in, entering or leaving manual mode for the output.</summary>
        public bool ManualDriven => _manualDriven;

        /// <summary>
        /// The output's motion source is SimHub's Manual control or Hop-in position: while true the
        /// lane leaves game mode for manual mode (mode 2) and follows <see cref="PostManualPose"/>;
        /// false returns to game mode.
        /// </summary>
        public void SetManualWanted(bool wanted) => _manualWanted = wanted;

        /// <summary>
        /// Stream in the controller's AI mode (mode 11), where the frame's pitch, roll and f6 are
        /// followed as a held pose, instead of game mode. A change while streaming re-enters.
        /// </summary>
        public void SetAiPoseWanted(bool wanted) => _aiWanted = wanted;

        /// <summary>
        /// AI mode: false (default) keeps every actuator within game mode's travel and caps speed and
        /// acceleration (<see cref="AiPoseLimiter"/>); true follows the pose as posted, each of pitch
        /// and roll held only to the angle that moves an actuator 65 mm, leaving the rest to the
        /// controller's own soft limit.
        /// </summary>
        public void SetAiMaxPerformance(bool on) => _aiMaxPerformance = on;
        private volatile bool _aiMaxPerformance;

        // The controller levels AI mode heave off as 25.5·tanh(f6 / 25.5) mm; f6 = 25.5·atanh(mm / 25.5) undoes it.
        private const double AiHeaveScaleMm = 25.5, AiHeaveMaxShare = 0.9;
        private const double MaxPerformanceAxisTravelMm = 65.0;
        private readonly AiPoseLimiter _aiLimiter = new AiPoseLimiter();

        /// <summary>Max Available Stroke as read from the controller (0.9 until it is), which scales the AI mode travel limits.</summary>
        private double AiStrokeShare()
        {
            float stroke = Volatile.Read(ref _maxStroke);
            return float.IsNaN(stroke) ? 0.9 : Math.Max(0f, Math.Min(1f, stroke));
        }

        /// <summary>Half the actuator spacing along and across the rig (mm); a 1000 × 700 rig until read.</summary>
        private void AiRigHalves(out double halfLength, out double halfWidth)
        {
            float l = Volatile.Read(ref _rigLength), w = Volatile.Read(ref _rigWidth);
            halfLength = (float.IsNaN(l) || l < 100f ? 1000f : l) / 2.0;
            halfWidth = (float.IsNaN(w) || w < 100f ? 700f : w) / 2.0;
        }

        /// <summary>AI mode: multiplier on the engine vibration's frequency (1 = Motion Manager's 25–60 Hz).</summary>
        public void SetEngineVibrationPitch(float factor)
            => _enginePitch = float.IsNaN(factor) ? 1f : Math.Max(0.5f, Math.Min(4f, factor));
        private volatile float _enginePitch = 1f;

        /// <summary>AI mode: height (mm) of a heave tick-over pulse made by the lane at low revs; 0 = none.</summary>
        public void SetEngineTickover(float mm)
            => _tickoverMm = float.IsNaN(mm) ? 0f : Math.Max(0f, Math.Min(3f, mm));
        private volatile float _tickoverMm;
        private double _tickPhase;
        private long _lastStreamMs;
        private readonly Random _tickRandom = new Random();
        // AI mode: last pose sent (f0, f1, f6) and the ease between a live pose and neutral frames.
        private const int PoseBlendMs = 2500;
        private const float EngineRunningRpm = 500f;
        private readonly float[] _poseSent = new float[3];
        private readonly float[] _poseBlendFrom = new float[3];
        private bool _poseLive;
        private long _poseBlendStartMs = -100_000;
        private float _tickHeight = 1f;
        private double _tickRate = 1.0, _tickDuty = 0.45;
        private bool _engineWasOn = true;   // armed only once the engine has been seen stopped
        private long _engineStartMs = -100_000;

        /// <summary>True while frames are being followed as a pose (AI mode reached and streaming).</summary>
        public bool AiPoseActive => _aiMode && _phase == MotionLanePhase.Game && _modeCurrent == (int)MotionMode.AiPose;

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

        /// <summary>True once the controller reports mode 2 in manual mode; setpoints are sent only then.</summary>
        public bool ManualTestActive => _phase == MotionLanePhase.ManualTest && _modeCurrent == (int)MotionMode.ManualTest;

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
        /// entry then road detail; null entries are left as the controller holds them, unless it
        /// holds no motion at all (<see cref="SeededEffect"/>). Written
        /// now if enrolled, otherwise once the next enrol has read the current values.
        /// <paramref name="tactile"/> (0..1, null = leave) is also written as the controller's
        /// Haptic Feedback Intensity; with <paramref name="engineStrength"/> it scales the
        /// engine vibration gains.
        /// </summary>
        public void SetDesiredEffects(MotionEffect?[] desired, float? engineStrength, float? tactile)
        {
            if (desired == null || desired.Length != MozaMotionProtocol.EffectSlots.Length + 1)
                throw new ArgumentException("one entry per effect slot plus road detail", nameof(desired));
            Volatile.Write(ref _desiredEffects, (MotionEffect?[])desired.Clone());
            Volatile.Write(ref _desiredEngineStrength, engineStrength ?? float.NaN);
            Volatile.Write(ref _desiredTactile, tactile ?? float.NaN);
            _requests.Enqueue(() => { _desiredPushes = 0; PushDesiredEffects(); });
        }

        /// <summary>
        /// Engine vibration scale: the profile's values, else the controller's haptic intensity
        /// and the strength of the "iRacing gt3" preset the base gains were captured with.
        /// </summary>
        private (float Strength, float Tactile) VibrationScale()
        {
            float s = Volatile.Read(ref _desiredEngineStrength);
            float t = Volatile.Read(ref _desiredTactile);
            if (float.IsNaN(t)) t = Volatile.Read(ref _hapticIntensity);
            return (float.IsNaN(s) ? DefaultEngineStrength : s, float.IsNaN(t) ? DefaultTactile : t);
        }

        /// <summary>
        /// Queue a write + readback for every desired value the controller doesn't hold.
        /// Re-run each second from the tick (capped) so a write the controller dropped is retried.
        /// </summary>
        private void PushDesiredEffects()
        {
            if (!_connection.IsConnected) return;
            if (_phase == MotionLanePhase.Enrolling || _phase == MotionLanePhase.Disconnected) return;
            if (_desiredPushes >= DesiredPushAttempts) return;
            var desired = Volatile.Read(ref _desiredEffects);
            int slots = MozaMotionProtocol.EffectSlots.Length;
            bool any = false;
            for (int i = 0; i <= slots; i++)
            {
                var target = desired?[i] ?? SeededEffect(i);
                if (!target.HasValue) continue;
                var want = target.Value;
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
            float tactile = Volatile.Read(ref _desiredTactile);
            if (!float.IsNaN(tactile) && !(Math.Abs(Volatile.Read(ref _hapticIntensity) - tactile) < 0.001f))
            {
                any = true;
                _paced.Enqueue(MozaMotionProtocol.BuildGlobalWrite(MozaMotionProtocol.GlobalHapticIntensity, tactile));
                _paced.Enqueue(MozaMotionProtocol.BuildGlobalRead(MozaMotionProtocol.GlobalHapticIntensity));
            }
            if (any) _desiredPushes++;
        }

        /// <summary>
        /// The default for effect <paramref name="index"/> (road detail = last) when this enrol found
        /// every motion slot at zero intensity; null otherwise. Road detail has no default: 0 is a
        /// normal setting for it.
        /// </summary>
        public MotionEffect? SeededEffect(int index)
            => _seedDefaultEffects && index >= 0 && index < DefaultEffects.Length ? DefaultEffects[index] : (MotionEffect?)null;

        private bool MotionSlotsBlank()
        {
            for (int i = 0; i < MozaMotionProtocol.EffectSlots.Length; i++)
                if (!(Volatile.Read(ref _effectIntensity[i]) == 0f)) return false;
            return true;
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
                    bool streaming = _phase == MotionLanePhase.Game || _phase == MotionLanePhase.EnteringGame
                        || _phase == MotionLanePhase.ManualTest;
                    _sequence = null;
                    ResetDeviceState();
                    _phase = MotionLanePhase.Disconnected;
                    if (streaming) _faultReason = "Motion controller disconnected while running";
                    MozaLog.Info(_released ? "[AZOM/Motion] Port released" : "[AZOM/Motion] Controller disconnected");
                    _released = false;
                }
                while (_requests.TryDequeue(out _)) { }
                TryConnect(now);
                // Nothing holds the lane (the usual case with no controller): poll slowly.
                Thread.Sleep(Wanted ? 50 : PacedWorkerLoop.IdleSleepMs);
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
                    if (_manualWanted)
                    {
                        // SimHub's Manual control is already open: skip game mode, which it would
                        // only pass through; the lane enters it when Manual control closes.
                        _manualDriven = true;
                        Volatile.Write(ref _manualPose, null);
                        StartSequence(ManualTestSequence(fromGame: false));
                    }
                    else
                        StartSequence(EngageSequence());
                }
                else if (!Wanted)
                    StartSequence(ReleaseSequence());
            }

            if (_phase == MotionLanePhase.Game || _phase == MotionLanePhase.EnteringGame)
                StreamTick(now);
            if (_phase == MotionLanePhase.Game)
                WatchMode(now, StreamMode, _aiMode ? "AI" : "game");
            else if (_phase == MotionLanePhase.ManualTest && _manualDriven && _sequence == null)
                WatchMode(now, MotionMode.ManualTest, "manual");
            // The output's source changed between game mode and AI mode while streaming: re-enter.
            if (_sequence == null && _phase == MotionLanePhase.Game && !_manualWanted && _aiWanted != _aiMode)
                StartSequence(EngageSequence());
            // SimHub's Manual control or Hop-in went on or off: swap game mode for manual mode and back.
            if (_sequence == null)
            {
                if (_manualWanted && _phase == MotionLanePhase.Game)
                {
                    _manualDriven = true;
                    Volatile.Write(ref _manualPose, null);
                    StartSequence(ManualTestSequence(fromGame: true));
                }
                else if (!_manualWanted && _manualDriven && _phase == MotionLanePhase.ManualTest)
                    StartSequence(ManualTestExitSequence());
            }
            if (_manualDriven && _sequence == null && ManualTestActive)
                ManualSetpointTick(now);
        }

        /// <summary>
        /// Manual mode: send each axis's target when it has moved, at most every 100 ms; SimHub idle
        /// or a stale pose holds the centre. The controller ramps to each target at its own rate.
        /// </summary>
        private void ManualSetpointTick(long now)
        {
            if (now < _nextManualMs) return;
            _nextManualMs = now + ManualSetpointIntervalMs;
            var pose = Volatile.Read(ref _manualPose);
            bool fresh = pose != null && now - Interlocked.Read(ref _manualPosedAtMs) <= StaleFrameMs;
            for (int i = 0; i < ManualAxes.Length; i++)
            {
                float target = _idle || !fresh ? 0f : Math.Max(-1f, Math.Min(1f, pose![i]));
                if (float.IsNaN(target)) continue;
                if (Math.Abs(target - _manualSent[i]) < ManualSetpointStep
                    && !(target == 0f && _manualSent[i] != 0f)) continue;
                Send(MozaMotionProtocol.BuildManualSetpoint(ManualAxes[i], target));
                _manualSent[i] = target;
            }
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
            // AI mode: the controller's engine vibration plays at the chosen pitch. With a tick-over
            // strength set, a heave pulse made here, in step with the frames, covers low revs (gone
            // between 40 % and 70 % of the redline) and the vibration fades in over it (silent below
            // 30 %, full from 55 %). A pulse rendered by SimHub's own engine effect is sampled at the
            // 33 Hz frame rate and arrives uneven (capture 13 Nov 2026), hence the one made here.
            float level = 1f, pitch = 1f, tickMm = 0f;
            long dtMs = _lastStreamMs > 0 ? Math.Min(200, now - _lastStreamMs) : _frameIntervalMs;
            _lastStreamMs = now;
            // iRacing reports a few hundred rpm for a car that is not running yet (capture 13 Nov 2026),
            // so in AI mode the engine counts as running from EngineRunningRpm.
            bool engineRunning = frame.EngineRpm >= EngineRunningRpm;
            if (_aiMode && !engineRunning) level = 0f;
            if (_aiMode && engineRunning)
            {
                pitch = _enginePitch;
                if (_tickoverMm > 0f)
                {
                    float x = Math.Max(0f, Math.Min(1f, (frame.EngineRpmFraction - 0.30f) / 0.25f));
                    level = x * x * (3f - 2f * x);
                }
                // The tick-over lasts longer than the buzz takes to arrive: gone between 40 % and 70 % of the redline.
                float y = Math.Max(0f, Math.Min(1f, (frame.EngineRpmFraction - 0.40f) / 0.30f));
                float amp = _tickoverMm * (1f - y * y * (3f - 2f * y));
                // Start-up: the first 2.5 s after the engine fires kick up to 2.5 times as hard.
                if (!_engineWasOn) { _engineStartMs = now; _engineWasOn = true; }
                long sinceStart = now - _engineStartMs;
                if (sinceStart < 2500) amp *= 1f + 1.5f * (1f - sinceStart / 2500f);
                // Load: up to 40 % more with the throttle open.
                amp *= 1f + 0.4f * Math.Max(0f, Math.Min(1f, frame.Throttle));
                if (amp > 0f)
                {
                    // Pulse rate follows the revs (about 7 Hz at a 1600 rpm idle), within what 33 frames a
                    // second can draw. Each pulse gets its own height and length so it does not drone.
                    double hz = Math.Max(4.0, Math.Min(11.0, frame.EngineRpm / 230.0)) * _tickRate;
                    _tickPhase += hz * dtMs / 1000.0;
                    if (_tickPhase >= 1.0)
                    {
                        _tickPhase %= 1.0;
                        _tickHeight = 0.65f + 0.5f * (float)_tickRandom.NextDouble();
                        _tickRate = 0.88 + 0.24 * _tickRandom.NextDouble();
                        _tickDuty = 0.35 + 0.2 * _tickRandom.NextDouble();
                    }
                    if (_tickPhase < _tickDuty) tickMm = Math.Min(4f, amp * _tickHeight);
                }
            }
            // Only a live frame with the engine stopped arms the start-up kick: a pause, a stale feed or
            // SimHub idle (neutral frames) leaves it as it was, so resuming does not kick again.
            if (ReferenceEquals(frame, posted) && !engineRunning) _engineWasOn = false;
            if (_aiMode)
            {
                // AI mode follows the pose as sent, so a change of source (live pose <-> neutral: game
                // start, pause, exit, SimHub idle, stale data) is eased over PoseBlendMs instead of jumping.
                bool live = ReferenceEquals(frame, posted);
                if (live != _poseLive)
                {
                    _poseLive = live;
                    _poseBlendStartMs = now;
                    Array.Copy(_poseSent, _poseBlendFrom, 3);
                }
                float p = frame.Fields[0], r = frame.Fields[1], h = frame.Fields[6];
                float op = p, or = r, oh = h;
                long since = now - _poseBlendStartMs;
                if (since < PoseBlendMs)
                {
                    float k = since / (float)PoseBlendMs;
                    k = k * k * (3f - 2f * k);
                    op = _poseBlendFrom[0] + (p - _poseBlendFrom[0]) * k;
                    or = _poseBlendFrom[1] + (r - _poseBlendFrom[1]) * k;
                    oh = _poseBlendFrom[2] + (h - _poseBlendFrom[2]) * k;
                }
                _poseSent[0] = op; _poseSent[1] = or; _poseSent[2] = oh;
                // Travel, speed and acceleration limits (AiPoseLimiter), then heave through the inverse
                // of the controller's soft limit. The tick-over pulse rides on top, inside the travel.
                AiRigHalves(out double halfLength, out double halfWidth);
                if (_aiMaxPerformance)
                {
                    // As posted: only each angle's own bound (65 mm at an actuator), no combined travel or rate cap.
                    double maxPitch = Math.Asin(Math.Min(1.0, MaxPerformanceAxisTravelMm / halfLength));
                    double maxRoll = Math.Asin(Math.Min(1.0, MaxPerformanceAxisTravelMm / halfWidth));
                    _aiLimiter.Reset(Math.Max(-maxPitch, Math.Min(maxPitch, op)), Math.Max(-maxRoll, Math.Min(maxRoll, or)), oh);
                }
                else
                    _aiLimiter.Step(op, or, oh, dtMs / 1000.0, halfLength, halfWidth, AiStrokeShare(),
                        AiPoseLimiter.SafeMaxSpeedMmS, AiPoseLimiter.SafeMaxAccelMmS2);
                double mm = _aiLimiter.HeaveMm + tickMm;
                double u = Math.Max(-AiHeaveMaxShare, Math.Min(AiHeaveMaxShare, mm / AiHeaveScaleMm));
                frame.Fields[0] = (float)_aiLimiter.Pitch;
                frame.Fields[1] = (float)_aiLimiter.Roll;
                frame.Fields[6] = (float)(AiHeaveScaleMm * 0.5 * Math.Log((1.0 + u) / (1.0 - u)));
                _connection.SendStream(StreamKind.MotionTelemetry, MozaMotionProtocol.BuildTelemetryFrame(frame));
                frame.Fields[0] = p; frame.Fields[1] = r; frame.Fields[6] = h;
            }
            else
                _connection.SendStream(StreamKind.MotionTelemetry, MozaMotionProtocol.BuildTelemetryFrame(frame));
            Interlocked.Increment(ref _framesSent);

            // Engine vibration rides along with live frames; when it stops, one zero-gain
            // frame silences the last tone.
            if (frame.EngineRpm >= 0)
            {
                var (strength, tactile) = VibrationScale();
                _connection.SendStream(StreamKind.MotionEngineVibration, MozaMotionProtocol.BuildEngineVibration(
                    MozaMotionProtocol.EngineVibrationHz(frame.EngineRpm), frame.Throttle, strength, tactile, pitch, level));
                _vibrating = true;
            }
            else if (_vibrating)
                SilenceVibration();
        }

        private void SilenceVibration()
        {
            _vibrating = false;
            _connection.SendStream(StreamKind.MotionEngineVibration,
                MozaMotionProtocol.BuildEngineVibrationSilent());
        }

        private void WatchMode(long now, MotionMode mode, string name)
        {
            if (_modeCurrent == (int)mode) { _modeLostSinceMs = -1; return; }
            if (_modeLostSinceMs < 0) { _modeLostSinceMs = now; return; }
            if (now - _modeLostSinceMs > ModeLossToleranceMs)
                Fault($"controller left {name} mode (mode {_modeCurrent}, flags 0x{_flags:X2}{ErrorSuffix()})");
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
            _manualDriven = false;
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
            _seedDefaultEffects = false;
            _desiredPushes = 0;
            _vibrating = false;
            while (_paced.TryDequeue(out _)) { }
            _activeErrors.Clear();
            _logPartial.Clear();
            _modeLostSinceMs = -1;
            _manualDriven = false;
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
            // Zero intensity on every slot: the controller moves nothing.
            _seedDefaultEffects = MotionSlotsBlank();
            _phase = MotionLanePhase.Ready;
            MozaLog.Info($"[AZOM/Motion] Enrolled {_model} fw {_fw}, mode {_modeCurrent}, "
                + $"{CountPresent()} actuators");
            if (_seedDefaultEffects)
                MozaLog.Info("[AZOM/Motion] Controller holds no motion effects; defaults apply where the profile sets none");
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
            // AI mode (11) is entered the same way (bench 12 Nov 2026: passes through 3, ~3.5 s).
            _phase = MotionLanePhase.EnteringGame;
            _aiMode = _aiWanted;
            var target = StreamMode;
            // Mode 3 on the way in levels the platform, so the limiter starts from a level pose.
            _aiLimiter.Reset();
            Array.Clear(_poseSent, 0, 3);
            _poseLive = false;
            _nextFrameMs = 0;
            long start = _clock.ElapsedMilliseconds;
            bool runSent = false;
            while (_modeCurrent != (int)target)
            {
                long elapsed = _clock.ElapsedMilliseconds - start;
                if (elapsed > GameEntryTimeoutMs) { Fault($"controller did not enter {(_aiMode ? "AI" : "game")} mode (mode {_modeCurrent}{ErrorSuffix()})"); yield break; }
                Send(MozaMotionProtocol.BuildSetMode(target));
                if (!runSent && elapsed > 300) { SendRunState(MotionRunState.Run); runSent = true; }
                yield return 33;
            }
            if (!runSent) SendRunState(MotionRunState.Run);
            _modeLostSinceMs = -1;
            _phase = MotionLanePhase.Game;
            MozaLog.Info(_aiMode ? "[AZOM/Motion] AI mode (SimHub pose as positions)" : "[AZOM/Motion] Game mode");
        }

        private IEnumerator<int> DisengageSequence()
        {
            _phase = MotionLanePhase.Disengaging;
            _manualDriven = false;
            _connection.ClearStreamSlots((int)StreamKind.MotionTelemetry, 2);
            // Motion Manager's last 0x22/09 when it leaves game mode has every gain at zero.
            if (_vibrating)
            {
                _vibrating = false;
                Send(MozaMotionProtocol.BuildEngineVibrationSilent());
            }
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

        /// <summary>
        /// Into manual mode as Motion Manager does it (capture 2026-11-11): Start = self-check (mode 7),
        /// mode 0, run state hold, mode 3; ticking "Manual Motion Test" = mode 2. Leaving game mode
        /// skips straight to hold + mode 3, the mode the controller passes through on the way into
        /// game mode.
        /// </summary>
        private IEnumerator<int> ManualTestSequence(bool fromGame)
        {
            // Same port-setup gate as EngageSequence; from game mode it already passed.
            if (!fromGame)
            {
                var issues = CurrentIssues();
                if (issues.Count > 0)
                {
                    Fault("configuration: " + string.Join("; ", issues));
                    yield break;
                }
            }
            _phase = MotionLanePhase.ManualTest;
            Array.Clear(_manualSent, 0, _manualSent.Length);
            _nextManualMs = 0;
            if (fromGame)
            {
                _connection.ClearStreamSlots((int)StreamKind.MotionTelemetry, 2);
                if (_vibrating) SilenceVibration();
                MozaLog.Info("[AZOM/Motion] Manual mode: leaving game mode");
            }
            if (!_selfCheckPassed)
            {
                _selfCheckSeen = false;
                MozaLog.Info("[AZOM/Motion] Manual mode: travel self-check");
                Send(MozaMotionProtocol.BuildSetMode(MotionMode.TravelSelfCheck));
                long checkDeadline = _clock.ElapsedMilliseconds + SelfCheckTimeoutMs;
                while (!_selfCheckSeen)
                {
                    if (_clock.ElapsedMilliseconds > checkDeadline) { Fault("travel self-check did not report success"); yield break; }
                    yield return 20;
                }
                yield return 300;
            }
            if (!fromGame)
            {
                Send(MozaMotionProtocol.BuildSetMode(MotionMode.Idle));
                yield return 30;
            }
            SendRunState(MotionRunState.Hold);
            if (!fromGame) yield return 15;
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.PositionCalibration));
            long deadline = _clock.ElapsedMilliseconds + ManualModeTimeoutMs;
            while (_modeCurrent != (int)MotionMode.PositionCalibration)
            {
                if (_clock.ElapsedMilliseconds > deadline) { Fault($"controller did not reach the ready position (mode {_modeCurrent}{ErrorSuffix()})"); yield break; }
                yield return 50;
            }
            yield return ManualActuatorSettleMs;

            Send(MozaMotionProtocol.BuildSetMode(MotionMode.ManualTest));
            deadline = _clock.ElapsedMilliseconds + ManualModeTimeoutMs;
            long resendAt = _clock.ElapsedMilliseconds + 1_000;
            while (_modeCurrent != (int)MotionMode.ManualTest)
            {
                if (_clock.ElapsedMilliseconds > deadline) { Fault($"controller did not enter manual mode (mode {_modeCurrent}{ErrorSuffix()})"); yield break; }
                if (_clock.ElapsedMilliseconds >= resendAt)
                {
                    Send(MozaMotionProtocol.BuildSetMode(MotionMode.ManualTest));
                    resendAt = _clock.ElapsedMilliseconds + 1_000;
                }
                yield return 50;
            }
            _modeLostSinceMs = -1;
            MozaLog.Info("[AZOM/Motion] Manual mode (SimHub Manual control / Hop-in)");
        }

        /// <summary>
        /// Out of manual mode as Motion Manager's toggle off does (mode 3, the ready position), then
        /// the normal engage into game mode. One sequence, so an output stop replaces all of it.
        /// </summary>
        private IEnumerator<int> ManualTestExitSequence()
        {
            Send(MozaMotionProtocol.BuildSetMode(MotionMode.PositionCalibration));
            long deadline = _clock.ElapsedMilliseconds + ManualModeTimeoutMs;
            while (_modeCurrent != (int)MotionMode.PositionCalibration && _clock.ElapsedMilliseconds < deadline)
                yield return 50;
            _manualDriven = false;
            MozaLog.Info($"[AZOM/Motion] Manual mode off (mode {_modeCurrent})");
            var engage = EngageSequence();
            while (engage.MoveNext()) yield return engage.Current;
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
            // Stop the worker first: it would otherwise re-send game mode or refill the
            // stream slots after the standby below. A timed-out join still gets standby.
            _running = false;
            try { _worker.Join(500); } catch { }
            var phase = _phase;
            if (_connection.IsConnected
                && (phase == MotionLanePhase.Game || phase == MotionLanePhase.EnteringGame
                    || phase == MotionLanePhase.SelfChecking || phase == MotionLanePhase.ManualTest))
            {
                try
                {
                    _connection.ClearStreamSlots((int)StreamKind.MotionTelemetry, 2);
                    _connection.Send(MozaMotionProtocol.BuildEngineVibrationSilent());
                    _connection.Send(MozaMotionProtocol.BuildRunState(MotionRunState.Stop));
                    _connection.Send(MozaMotionProtocol.BuildSetMode(MotionMode.Standby));
                    Thread.Sleep(150);
                }
                catch { /* shutting down */ }
            }
            _connection.MessageReceived -= OnMessage;
            try { _connection.Disconnect(); } catch { }
            try { _connection.Dispose(); } catch { }
        }
    }
}
