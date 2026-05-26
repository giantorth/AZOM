using System;
using System.Linq;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;
using MozaPlugin.Telemetry.TestMode;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.Telemetry.Watchdog;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Lifecycle state of <see cref="TelemetrySender"/>. Replaces the prior
    /// 3-boolean soup (_enabled / _preambleComplete / _dashSwitchMuted) with
    /// a single explicit enum so the legal transitions are obvious.
    ///
    /// Linear progression: Idle → Starting → Preamble → Active. Stop()
    /// returns from any state to Idle. Dashboard switches go through a full
    /// pipeline cycle (Stop+Start) via <c>RestartForSwitch</c> rather than
    /// any in-place state transition; the prior <c>DashSwitchMuted</c>
    /// sub-state was retired with the renegotiate-in-place code path.
    /// </summary>
    internal enum TelemetryState
    {
        /// <summary>Stop() / initial. Timer not running. No outbound traffic.</summary>
        Idle,
        /// <summary>StartInner() before the timer kicks. Sessions opening, preamble
        /// frames staged but tick-driven emission has not started.</summary>
        Starting,
        /// <summary>Timer running, ~1s catalog absorption + heartbeats. No value frames yet.</summary>
        Preamble,
        /// <summary>Steady state — value frames + heartbeats + periodic streams.</summary>
        Active,
    }

    /// <summary>
    /// Read-only flattened view of pipeline status for UI / coordinator
    /// consumers. <see cref="TelemetryState"/> only covers the core
    /// lifecycle (Idle/Starting/Preamble/Active); this enum extends it with
    /// the orthogonal sub-states UI/dispatchers actually care about
    /// (silence cooldown, hot-switch burst in flight, recovery restart in
    /// flight, post-rate-limit park).
    ///
    /// Callers should read this single property instead of stitching their
    /// own predicate from <c>IsActive</c> / <c>IsInSilenceCooldown</c> /
    /// <c>HotSwitchBurstPending</c> / etc., so that the combinations stay
    /// consistent across consumers. Derived; no separate state to maintain.
    /// </summary>
    public enum PipelinePhase
    {
        /// <summary>Telemetry sender not started, no recovery in flight,
        /// silence gate not active. UI shows "disabled".</summary>
        Idle,
        /// <summary>Stopped but inside the wheel's ~11 s sess=0x09 settle
        /// window. UI should reflect "waiting to reconnect" and disable
        /// switch controls.</summary>
        SilenceWait,
        /// <summary>Mid-StartInner: opening sessions, emitting preamble.
        /// UI shows "connecting".</summary>
        Starting,
        /// <summary>Steady state — value frames flowing, watchdogs armed,
        /// no recovery in flight. UI shows "connected".</summary>
        Active,
        /// <summary>Active and a hot-switch tier-def burst is mid-flight.
        /// UI may want to keep dashboard switch affordances disabled
        /// until the burst completes (~3-8 s) so the user can't trigger
        /// a second switch that races the first.</summary>
        HotSwitchBurst,
        /// <summary>A watchdog escalation has queued a full Stop+Start
        /// cycle and the debounce window is still active. UI shows
        /// "recovering".</summary>
        Recovery,
        /// <summary>Recovery rate-limit hit (3 restarts in 5 min) or
        /// sess=0x09 retry budget exhausted. Pipeline won't auto-recover;
        /// UI should surface "parked — toggle telemetry to retry".</summary>
        Parked,
    }

    /// <summary>
    /// Periodically encodes game data and sends telemetry frames to the wheel.
    /// See docs/protocol/plugin/startup-phases.md for the startup sequence and
    /// docs/protocol/sessions/lifecycle.md for session allocation.
    ///
    /// Sessions 0x01 (mgmt) and 0x02 (telem, also FlagByte) are hardcoded.
    /// Tier flag bytes are 0-based and independent of the session byte.
    /// Each tier runs at its own rate derived from package_level.
    /// </summary>
    public class TelemetrySender : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private Timer? _sendTimer;
        private TierState[]? _tiers;
        private volatile StatusDataBase? _latestGameData;
        private volatile bool _gameRunning;
        // Set true on game-running false→true; consumed once by the active-phase
        // tick to fire the game-start handshake. See SendGameStartHandshake.
        private volatile bool _gameStartHandshakePending;
        private bool[]? _tierDiagEmitted;
        // Lifecycle state — see TelemetryState. All reads/writes go through
        // volatile semantics or TransitionTo.
        private volatile TelemetryState _state = TelemetryState.Idle;
        private int _tickCounter;
        private int _slowCounter;
        // Timer period derived from fastest tier's package_level. Default 33 ms
        // (~30 Hz) covers the no-profile and empty-profile branches in the Profile
        // setter, and — critically — also covers the case where the setter's
        // idempotency short-circuit returns before the null-branch assignment
        // runs (first Profile=null on a fresh sender). InitTickStateAndTransitionToStarting
        // and StartTickTimer both depend on this being non-zero (1000 / _baseTickMs;
        // new Timer(_baseTickMs)).
        private int _baseTickMs = 33;
        private byte _sequenceCounter;
        private int _displayConfigPage;

        // _tierDefPreambleSent moved to TierDefinitionEmitter.
        private int _preambleTickTarget;
        internal int _sessionAckSeq;

        // Port probing state. _lastAckedSeq=-1 signals "ack present but seq unknown"
        // (5-byte fc:00 form). See docs/protocol/sessions/chunk-format.md.
        internal volatile byte _lastAckedSession;
        internal volatile int _lastAckedSeq = -1;
        private readonly ManualResetEventSlim _ackReceived = new ManualResetEventSlim(false);

        // Wheel session-layer readiness latch. Set by TelemetryInboundDispatcher
        // when the wheel pushes its first spontaneous sess=0x09 device-init
        // (type=0x81) — the most reliable "I'm alive and ready for session-level
        // traffic" signal the wheel emits during a slow hot-attach boot
        // (~20s after wheel-telemetry-mode reads first succeed). Consumed by
        // ProbeAndOpenSessions to retry sess=0x01/0x02 opens that timed out
        // while the wheel was still booting.
        internal volatile bool _wheelReadyObserved;


        // Upload handshake state.
        internal int _mgmtAckSeq;
        private readonly ManualResetEventSlim _mgmtResponseEvent = new ManualResetEventSlim(false);

        // File-transfer session state. See docs/protocol/dashboard-upload/.
        private readonly SessionRegistry _sessions = new SessionRegistry();
        private readonly SessionDispatcher _dispatcher = new SessionDispatcher();
        // mzdash upload coordinator. Constructed in the ctor after _connection.
        private WheelUploadCoordinator _uploader = null!;

        /// <summary>
        /// Outbound seq counter for session 0x02 (telemetry). Tracks the next
        /// seq to use when sending V0 per-channel value frames in active phase.
        /// V2 telemetry uses group=0x43 cmd=0x7d23 directly (no session seq).
        /// </summary>
        private int _session02OutboundSeq;

        // Guard for the read-chunk-send-write of _session02OutboundSeq +
        // _propertyPushLastSeqs. Without it, the timer thread (V0 value
        // frames), the UI thread (brightness / dashboard switch property
        // pushes), and background StartInner (session-init handshake,
        // tier-def) can race: two threads reading the same seq each emit
        // their N frames, and whichever finishes last overwrites the
        // higher value with a lower one. The wheel keys retransmit
        // suppression per literal seq, so a regression makes it drop
        // chunks as duplicates and the upstream message stays stuck.
        private readonly object _session02SeqLock = new object();

        // Same rationale as _session02SeqLock but for the mgmt session.
        // SendTierDefinition can target either 0x01 or 0x02 depending on
        // _policy.TierDefSession.
        private readonly object _session01SeqLock = new object();

        // Per-chunk retransmit until fc:00 ack drains the queue.
        // See docs/protocol/sessions/chunk-format.md.
        private readonly global::MozaPlugin.Diagnostics.SessionRetransmitter _retransmitter
            = new global::MozaPlugin.Diagnostics.SessionRetransmitter();

        // Property-push coalescing moved to PropertyPushQueue.

        // Blind retransmit state moved to TierDefinitionEmitter.

        // Sess=0x09 retry + sess=0x02 engagement watchdog state moved to
        // SessionWatchdogManager. See Telemetry/Watchdog/SessionWatchdogManager.cs.

        // Two host-side silence gates (Stop→Start ~11s + post-switch UI
        // cooldown) live in SilenceGate. The instance carries no per-gate
        // state of its own — the timestamps are static on the helper class
        // so they survive plugin recycle within the same SimHub process.
        // See docs/protocol/plugin/session-management.md and the
        // SilenceGate class doc-comment.
        private readonly Lifecycle.SilenceGate _silenceGate;

        // Single funnel for all watchdog-driven RestartForSwitch escalations.
        // Debounces near-simultaneous escalations from multiple watchdogs
        // (sess=0x01 + sess=0x02 + configJson-gap can all fire in the same
        // tick) and rate-limits so an escalation storm parks the pipeline
        // instead of looping forever. See RecoveryDispatcher class doc.
        private readonly Lifecycle.RecoveryDispatcher _recovery;
        internal Lifecycle.RecoveryDispatcher Recovery => _recovery;

        // Hot-renegotiation burst state machine. When Enabled, SwitchToProfile
        // keeps sessions 0x01/0x02/0x03 open and re-emits tier-def in place
        // instead of Stop+11s+Start. See docs/protocol/tier-definition/hot-switch.md.
        private readonly Lifecycle.HotSwitchCoordinator _hotSwitch
            = new Lifecycle.HotSwitchCoordinator();

        /// <summary>Feature flag — when true, dashboard switches use the hot
        /// path; when false, they cycle the full pipeline. Set from
        /// <c>MozaPluginSettings.EnableHotRenegotiation</c>.</summary>
        public bool EnableHotRenegotiation
        {
            get => _hotSwitch.Enabled;
            set => _hotSwitch.Enabled = value;
        }

        // Wheel-reported current dashboard slot — ground truth, parsed from
        // type-04 records on sess=0x02 b2h. See WheelSlotTracker for parsing
        // and docs/protocol/dashboard-upload/wheel-pushed-slot.md.
        public int WheelReportedSlot => _slotTracker.WheelReportedSlot;

        // Last slot host emitted FF kind=4 to. STATIC: survives plugin recycle
        // within one SimHub process so game-switch can skip the 11s restart
        // when the new game's profile targets the same dashboard.
        public int LastEmittedKind4Slot => _slotTracker.LastEmittedKind4Slot;

        /// <summary>Reset per-instance kind=4 slot tracking on hot-swap.</summary>
        internal void ResetBindingTracking()
        {
            _slotTracker.Reset();
            // Hot-swap may target a different wheel with a different ConfigJsonList.
            try { _configJson.HardReset(); } catch { }
            // Fresh hardware = fresh restart budget; clear any park state so
            // the new wheel gets a clean engagement attempt rather than
            // inheriting the prior wheel's exhausted budget.
            try { _recovery.Reset(); } catch { }
        }

        /// <summary>
        /// Raised when the wheel hardware initiates a dashboard switch (user
        /// pressed a wheel-side control). Slot is 0-based into configJsonList.
        /// Filtered against <see cref="LastEmittedKind4Slot"/> to exclude
        /// echoes of host-initiated switches.
        /// </summary>
        public event Action<int>? WheelInitiatedSwitch;

        /// <summary>
        /// True if a catalog re-sync probe has fired this instance — the
        /// wheel's catalog was incomplete at tier-def time and needs a full
        /// Stop+Start. The probe alone (kind=4 to current slot) does NOT cause
        /// the wheel to re-push its catalog.
        /// </summary>
        internal bool HasCatalogResyncProbeFired => _catalogResyncProbe.HasFired;

        // Tier-def binding completeness moved to TierDefinitionEmitter.
        internal bool IsTierDefFullyBound => _tierDefEmitter.IsTierDefFullyBound;

        // Wheel-catalog growth tracking. Late-arriving URLs would otherwise get
        // chIndex=0 in the wheel's view; we re-emit tier-def on catalog growth.
        // See docs/protocol/tier-definition/session-02-channel-catalog.md.
        private int _catalogCountAtLastSubscription;
        private const int CatalogGrowthQuietMs = 400;
        private const int CatalogGrowthMinDelta = 1;

        // Catalog-only profile synthesis: track the catalog generation we last
        // synthesised against so dashboard switches (wheel emits new catalog,
        // bumps END marker) cause re-synthesis instead of holding the old
        // tiers. State only meaningful when _profile.Name == CatalogProfileName.
        internal const string CatalogProfileName = "WheelCatalog";
        private uint _catalogEndMarkerAtSynthesis;
        private int _catalogCountAtSynthesis;

        // CRC32 reject counters for catalog (sess=0x01/FlagByte) and tile-server
        // (sess=0x03/0x0b) chunks. Surfaced via diagnostics for link-quality.
        private int _catalogCrcRejects;
        public int CatalogCrcRejects => Interlocked.CompareExchange(ref _catalogCrcRejects, 0, 0);
        private int _tileServerCrcRejects;
        public int TileServerCrcRejects => Interlocked.CompareExchange(ref _tileServerCrcRejects, 0, 0);

        // Per-session highest seen seq for tile-server sess=0x03 / 0x0b. Dedup
        // retransmits so the parser buffer doesn't accumulate duplicate bytes
        // (breaks sentinel-scan alignment).
        private readonly System.Collections.Generic.Dictionary<byte, int> _tileServerHighestSeq
            = new System.Collections.Generic.Dictionary<byte, int>();

        // Catalog re-sync probe — kind=4 to current slot tells the wheel to
        // re-init its dashboard binding and re-run the catalog advertise.
        // Throttle + has-fired state lives on the helper; emission logic
        // stays in ScheduleCatalogResyncProbe below.
        private readonly Lifecycle.CatalogResyncProbe _catalogResyncProbe
            = new Lifecycle.CatalogResyncProbe();

        /// <summary>Drain window for queued kind=4 / one-shot frames before
        /// Stop's FlushPendingWrites discards the queue.</summary>
        private const int PreStopDrainMs = 300;

        // True once ResolveAutoPolicy has run this Start() cycle. Reset per StartInner.
        private bool _autoResolutionDone;

        /// <summary>
        /// Outbound seq for session 0x01 (mgmt). Tier-def subscription rides
        /// here; session 0x02 is reserved for value frames + wheel state.
        /// </summary>
        private int _session01OutboundSeq;

        /// <summary>
        /// Forces a specific session number for dashboard upload. 0 = auto
        /// (first device-initiated in 0x04..0x0a, fallback 0x04).
        /// </summary>
        public byte UploadSessionOverride
        {
            get => _uploader?.UploadSessionOverride ?? 0;
            set { if (_uploader != null) _uploader.UploadSessionOverride = value; }
        }

        // Active per-era policy. All wire-protocol axes derive from this.
        // Set by MozaPlugin.ApplyTelemetrySettings; mutated in place by
        // ResolveAutoPolicy / upload sub-msg-1 fallback (Auto only).
        private EraPolicy _policy = EraPolicy.For(MozaWheelEra.Auto);

        /// <summary>
        /// Active wheel-firmware era policy. Setter never accepts null —
        /// substitutes Auto's optimistic Era2026 default if passed null.
        /// </summary>
        internal EraPolicy Policy
        {
            get => _policy;
            set => _policy = value ?? EraPolicy.For(MozaWheelEra.Auto);
        }

        // Target device id for screen-telemetry and session-control frames.
        // Default = DeviceWheel (0x17). Switched to DeviceMain (0x12) by
        // DashboardBindingCoordinator when a standalone dashboard (CM2) is
        // connected without a wheel. The setter invalidates per-tier
        // frame builders + cached display frames so the new dev_id takes
        // effect on the next frame emit.
        private byte _targetDeviceId = MozaProtocol.DeviceWheel;

        internal byte TargetDeviceId
        {
            get => _targetDeviceId;
            set
            {
                byte next = value == 0 ? MozaProtocol.DeviceWheel : value;
                if (_targetDeviceId == next) return;
                _targetDeviceId = next;
                _cachedDisplayConfigFrames = null;
                _cachedDisplayConfigPageCount = 0;
                // Rebuild per-tier frame builders with the new dev_id so value
                // frames address the right device on the next tick.
                RebuildFrameBuildersForTargetDevice();
                MozaLog.Debug($"[Moza] Telemetry target device set to {TargetDescription}");
            }
        }

        internal byte TargetDeviceIdSwapped => MozaProtocol.SwapNibbles(_targetDeviceId);

        private bool _standaloneDashboardMode;
        internal bool StandaloneDashboardMode
        {
            get => _standaloneDashboardMode;
            set => _standaloneDashboardMode = value;
        }

        /// <summary>True when the telemetry target is a standalone dashboard
        /// (CM2 bridge/main or legacy dash device). Drives the inbound
        /// dispatcher's broader device-id fan-in.</summary>
        internal bool IsStandaloneDashboardTarget =>
            _standaloneDashboardMode
            || _targetDeviceId == MozaProtocol.DeviceMain
            || _targetDeviceId == MozaProtocol.DeviceDash;

        internal string TargetDescription
        {
            get
            {
                string label;
                if (IsStandaloneDashboardTarget && _targetDeviceId == MozaProtocol.DeviceMain)
                    label = "CM2 bridge/main";
                else if (_targetDeviceId == MozaProtocol.DeviceDash)
                    label = "dashboard";
                else if (_targetDeviceId == MozaProtocol.DeviceWheel)
                    label = "wheel";
                else
                    label = "custom";
                return $"0x{_targetDeviceId:X2} ({label})";
            }
        }

        private void RebuildFrameBuildersForTargetDevice()
        {
            var profile = _profile;
            var tiers = _tiers;
            if (profile == null || tiers == null) return;
            for (int i = 0; i < tiers.Length && i < profile.Tiers.Count; i++)
            {
                if (tiers[i] == null) continue;
                tiers[i].Builder = new TelemetryFrameBuilder(
                    profile.Tiers[i], PropertyResolver,
                    type02NConvention: false,
                    deviceId: _targetDeviceId);
            }
        }

        // Session 0x09 configJson RPC. Device pushes dashboard state; we reply
        // with the canonical library list. See docs/protocol/dashboard-upload/config-rpc-session-09.md.
        private readonly ConfigJsonClient _configJson = new ConfigJsonClient();
        internal int _session09InboundSeq;
        private int _session09OutboundSeq;

        // Guard for the read-chunk-send-write of _session09OutboundSeq.
        // Without it, MaybeSendConfigJsonReply (runs on the serial-read thread
        // via TelemetryInboundDispatcher when the wheel completes a sess=0x09
        // configJson chunk burst) races SendSession09Keepalive (runs on the
        // System.Timers ThreadPool tick via OnTimerElapsed → TickEmitSlowPath).
        // The reply is a multi-chunk emission so its read-modify-write window
        // is wide enough to interleave with a keepalive ++seq, producing seq
        // skew that the wheel sees as a gap.
        private readonly object _session09SeqLock = new object();

        private bool _session09ReplySent;
        public WheelDashboardState? WheelState => _configJson.LastState;

        // Wraps WheelState.ConfigJsonList for the auto-test.
        public System.Collections.Generic.IReadOnlyList<string>? WheelReportedDashboards
            => _configJson.LastState?.ConfigJsonList;

        /// <summary>
        /// Canonical dashboard library advertised to the wheel on session 0x09.
        /// Wheel echoes these in its next state blob's configJsonList.
        /// Empty list disables the proactive reply.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> CanonicalDashboardList { get; set; }
            = System.Array.Empty<string>();

        // Display sub-device detection
        private volatile bool _displayDetected;
        private string _displayModelName = "";

        // Wheel channel catalog parser. See ChannelCatalogParser.
        private readonly ChannelCatalogParser _catalogParser = new();

        // Subscription state — immutable snapshot published atomically by
        // ApplySubscription. Volatile-ref swap = lock-free reader.
        internal sealed class SubscriptionState
        {
            public readonly byte FlagBase;
            public readonly int TierCount;
            public readonly int SubTiersPerBroadcast;
            public readonly string ProfileName;

            public SubscriptionState(byte flagBase, int tierCount,
                int subTiersPerBroadcast, string profileName)
            {
                FlagBase = flagBase;
                TierCount = tierCount;
                SubTiersPerBroadcast = subTiersPerBroadcast;
                ProfileName = profileName;
            }
        }
        private volatile SubscriptionState? _activeSubscription;

        // Session-global flag counter. Advances by tierCount after each
        // ApplySubscription; initial/re-subscribe resets to 0.
        private byte _nextFlagBase;

        // Monotonic counter — incremented per tier-def emit. Used by
        // DashboardSwitchAutoTest to detect renegotiate completion.
        private int _subscriptionGen;
        public int SubscriptionGen => System.Threading.Volatile.Read(ref _subscriptionGen);

        // Subscription-exchange diagnostics — captured for the Diagnostics tab.
        public sealed class SubscriptionDiagnostics
        {
            public string SessionByte = "";          // e.g. "0x01"
            public string Format = "";               // "v0-url" or "v2-compact" / "v2-type02"
            public byte[] PreambleBytes = System.Array.Empty<byte>();
            public byte[] BodyBytes = System.Array.Empty<byte>();
            public System.Collections.Generic.List<(int Idx, string Url, uint Comp, uint Width)> Channels =
                new System.Collections.Generic.List<(int, string, uint, uint)>();
            public System.DateTime CapturedAt;
        }
        public SubscriptionDiagnostics? LastSubscription => _tierDefEmitter.LastSubscription;

        /// <summary>Raw hex of inbound sess=0x02 chunks captured in the 5 s window
        /// after the most-recent subscription send. Wheel returns channel-token
        /// assignments here (tag 0x0c + 4B per channel).</summary>
        private readonly System.Collections.Generic.List<byte[]> _subscriptionResponseChunks = new();
        private long _subscriptionResponseDeadlineTicks;
        public System.Collections.Generic.IReadOnlyList<byte[]> LastSubscriptionResponse
        {
            get { lock (_subscriptionResponseChunks) return _subscriptionResponseChunks.ToArray(); }
        }

        /// <summary>Per-session chunk counters (in/out). Keyed by session id.
        /// Useful for diag tab to see which sessions are alive.</summary>
        private readonly System.Collections.Generic.Dictionary<byte, (int In, int Out)> _sessionCounts =
            new System.Collections.Generic.Dictionary<byte, (int, int)>();
        public System.Collections.Generic.IReadOnlyDictionary<byte, (int In, int Out)> SessionCounts
        {
            get { lock (_sessionCounts) return new System.Collections.Generic.Dictionary<byte, (int, int)>(_sessionCounts); }
        }
        internal void BumpSessionCount(byte session, bool outbound)
        {
            lock (_sessionCounts)
            {
                _sessionCounts.TryGetValue(session, out var pair);
                _sessionCounts[session] = outbound ? (pair.In, pair.Out + 1) : (pair.In + 1, pair.Out);
            }
        }

        // Upload-session dir-listing tracking is owned by _uploader; expose
        // its refresh flag here for the diag tab via a thin pass-through.
        public bool Session04DirListingRefreshed => _uploader?.DirListingRefreshed ?? false;

        // RPC on 0x09/0x0a (host→device management). See RpcCallChannel.
        // _session0aInbox is shared with the configJson handler (0x09/0x0a
        // share reassembly machinery).
        private RpcCallChannel _rpc = null!;
        private readonly SessionDataReassembler _session0aInbox = new();
        // Session 0x03 inbound: 12-byte envelope tile-server state parser.
        private readonly TileServerStateParser _tileServerParser = new();
        public TileServerState? TileServerState => _tileServerParser.LastState;

        // Dashboard download (session 0x0B): downloader + cache.
        private DashboardDownloader? _dashboardDownloader;
        private DashboardCache? _dashboardCache;
        private volatile bool _dashboardDownloadTriggered;

        /// <summary>Set the dashboard cache for download-on-connect.</summary>
        public DashboardCache? DashCache
        {
            get => _dashboardCache;
            set
            {
                _dashboardCache = value;
                var prior = _dashboardDownloader;
                if (value != null)
                    _dashboardDownloader = new DashboardDownloader(
                        _connection, value,
                        MozaPlugin.Instance?.DashProfileStore ?? new DashboardProfileStore(),
                        _retransmitter, _dispatcher);
                else
                    _dashboardDownloader = null;
                // Dispose AFTER swapping so a concurrent observer never sees a
                // disposed instance through _dashboardDownloader.
                try { prior?.Dispose(); } catch { }
            }
        }

        public void SetDownloadEnabled(bool enabled)
        {
            var dl = _dashboardDownloader;
            if (dl != null) dl.Enabled = enabled;
        }

        /// <summary>
        /// True if the wheel's internal Display sub-device responded to identity probe.
        /// Use this to gate dashboard telemetry features in the UI — wheels without
        /// a display (e.g. CS V2.1 with RPM LEDs only) won't have this set.
        /// </summary>
        public bool DisplayDetected => _displayDetected;

        /// <summary>Display sub-device model name, e.g. "Display". Empty if not detected.</summary>
        public string DisplayModelName => _displayModelName;

        // Pre-cached frames (built once, reused every tick)
        private byte[] _cachedEnableFrame = null!;
        private byte[] _cachedModeFrame = null!;
        private byte[] _cachedSequenceFrame = null!;
        private byte[][] _cachedHeartbeatFrames = null!;

        // Group 0x43 N=1 device-ping frames sent ~1 Hz. Static — device IDs and
        // checksums never vary, so the byte[]s outlive any sender instance.
        private static readonly byte[] _dashKeepaliveFrameDash = BuildKeepaliveFrame(MozaProtocol.DeviceDash);
        private static readonly byte[] _dashKeepaliveFrame15 = BuildKeepaliveFrame(0x15);
        private static readonly byte[] _dashKeepaliveFrameWheel = BuildKeepaliveFrame(MozaProtocol.DeviceWheel);
        // CM2 standalone dashboard pings its bridge/main at 0x12; the dash/15/wheel
        // trio above never reaches CM2's expected target.
        private static readonly byte[] _dashKeepaliveFrameMain = BuildKeepaliveFrame(MozaProtocol.DeviceMain);

        // Peripheral output-poll frames — wire-parity polls (handbrake/pedals).
        // Cadence: presence ~22 Hz, handbrake-output ~10 Hz, pedals ~7 Hz.
        private static readonly byte[] _handbrakePresenceFrame = BuildShortFrame(0x5A, 0x1B, new byte[] { 0x00 });
        private static readonly byte[] _handbrakeOutputFrame   = BuildShortFrame(0x5D, 0x1B, new byte[] { 0x01, 0x00, 0x00 });
        private static readonly byte[] _pedalThrottleOutFrame  = BuildShortFrame(0x25, 0x19, new byte[] { 0x01, 0x00, 0x00 });
        private static readonly byte[] _pedalBrakeOutFrame     = BuildShortFrame(0x25, 0x19, new byte[] { 0x02, 0x00, 0x00 });
        private static readonly byte[] _pedalClutchOutFrame    = BuildShortFrame(0x25, 0x19, new byte[] { 0x03, 0x00, 0x00 });

        // LED state read polls (`0x40/0x17 1F 03 [group] 00 00 00 00`).
        // Group 1 (RPM bar) ~18 Hz; group 2 (Single) ~1.7 Hz.
        private static readonly byte[] _ledStatePollGroup1 = BuildShortFrame(0x40, 0x17, new byte[] { 0x1F, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00 });
        private static readonly byte[] _ledStatePollGroup2 = BuildShortFrame(0x40, 0x17, new byte[] { 0x1F, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00 });

        private static byte[] BuildShortFrame(byte group, byte dev, byte[] payload)
        {
            var frame = new byte[payload.Length + 5];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = group;
            frame[3] = dev;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        // Lazy per-page cache for the 7C:27/7C:23 display-config frames sent
        // ~1 Hz. Invalidated when the profile's page count changes; rebuilt on
        // first SendDisplayConfig() after that.
        private byte[][]? _cachedDisplayConfigFrames;
        private int _cachedDisplayConfigPageCount;

        // Session ports determined during port probing.
        // MgmtPort = first acked port (session 0x01, used for dashboard upload).
        // FlagByte = second acked port (session 0x02, used for tier definitions and fc:00 acks).
        private byte _mgmtPort;
        public byte FlagByte { get; set; } = 0x02;
        public bool SendTelemetryMode { get; set; } = true;
        public bool SendSequenceCounter { get; set; } = true;
        private bool _testMode;
        public bool TestMode
        {
            get => _testMode;
            set
            {
                if (_testMode != value)
                {
                    _testMode = value;
                    if (value)
                    {
                        _tierDiagEmitted = new bool[_tiers?.Length ?? 0];
                        // Reset the Elapsed-kind clock so any timer-typed
                        // channel (CurrentLapTime, TimeOfDay, etc.) restarts
                        // at 00:00:00 on every Test Start.
                        long nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L /
                                     System.Diagnostics.Stopwatch.Frequency;
                        TestSignalGenerator.ResetEpoch(nowMs);
                    }
                    MozaLog.Debug($"[Moza] TestMode changed to {value}");
                }
            }
        }

        // Per-profile telemetry enable. Reflects the active SimHub overlay's
        // TelemetryEnabled flag — falls to false when the user switches to a
        // game whose profile disabled live telemetry. We *do not* stop the
        // tick timer for this: parity polls (handbrake/pedal/LED/widget)
        // keep the wheel engaged at idle, and the hot-switch tier-def burst
        // + TestMode override both ride the same tick. Instead the live
        // value-frame / string / enable+sequence emit gates check this flag
        // alongside _gameRunning, so a disabled profile suppresses live
        // emission while the timer keeps running.
        private volatile bool _profileTelemetryEnabled = true;
        public bool ProfileTelemetryEnabled
        {
            get => _profileTelemetryEnabled;
            set
            {
                if (_profileTelemetryEnabled != value)
                {
                    _profileTelemetryEnabled = value;
                    MozaLog.Debug($"[Moza] ProfileTelemetryEnabled changed to {value}");
                }
            }
        }

        // Wire-trace phase marker. Frame:
        //   7e 03 55 55 4d 4b [phaseId] [chk]
        // grp=0x55 dev=0x55 not used by any wheel command — wheel ignores, but
        // the frame lands in the SerialTrafficCapture wire trace so post-mortem
        // tooling can align runs by phase id.
        public void SendPhaseMarker(byte phaseId)
        {
            if (!_connection.IsConnected) return;
            byte[] frame = BuildPhaseMarkerFrame(phaseId);
            _connection.Send(frame);
            MozaLog.Debug($"[Moza] phase-marker phaseId=0x{phaseId:X2} ({phaseId})");
        }

        private static byte[] BuildPhaseMarkerFrame(byte phaseId)
        {
            var f = new byte[] { 0x7e, 0x03, 0x55, 0x55, 0x4d, 0x4b, phaseId, 0x00 };
            f[7] = MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        // ProtocolVersion / UploadWireFormat / AutoFallbackWireFormat removed —
        // read from _policy directly (e.g. _policy.Encoding for V0 vs V2,
        // _policy.UploadWireFormat for upload header, _policy.AutoFallbackUploadWireFormat
        // for fallback gating). Value-frame paths use _policy.Encoding ==
        // TierDefEncoding.V0Url instead of ProtocolVersion == 0.

        /// <summary>Channel URLs reported by the wheel during session startup. Null until parsed.</summary>
        public System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalog => _catalogParser.Catalog;

        /// <summary>Raw .mzdash file content for upload to the wheel. Set by
        /// ApplyTelemetrySettings; consumed by WheelUploadCoordinator.</summary>
        public byte[]? MzdashContent
        {
            get => _uploader?.MzdashContent;
            set { if (_uploader != null) _uploader.MzdashContent = value; }
        }

        /// <summary>Dashboard name (used for logging). Set by ApplyTelemetrySettings.</summary>
        public string MzdashName
        {
            get => _uploader?.MzdashName ?? "";
            set { if (_uploader != null) _uploader.MzdashName = value ?? ""; }
        }

        /// <summary>Directory the active mzdash was loaded from (used to find
        /// sibling PNG widget assets at <c>&lt;dir&gt;/Resource/MD5/&lt;hex&gt;.png</c>
        /// when building the multi-file upload bundle). Empty when the mzdash
        /// came from an embedded resource — upload will be single-file.</summary>
        public string MzdashSourceDirectory
        {
            get => _uploader?.MzdashSourceDirectory ?? "";
            set { if (_uploader != null) _uploader.MzdashSourceDirectory = value ?? ""; }
        }

        /// <summary>Whether to upload the dashboard to the wheel on startup.</summary>
        public bool UploadDashboard
        {
            get => _uploader?.UploadDashboard ?? true;
            set { if (_uploader != null) _uploader.UploadDashboard = value; }
        }

        // ── Upload diagnostics surfaced for the Dashboard Upload UI ──────────

        /// <summary>True while a dashboard upload is mid-flight. Cleared on
        /// completion / abort.</summary>
        public bool IsUploadInFlight => _uploader?.IsUploadInFlight ?? false;
        /// <summary>Last <c>bytes_written:u32 BE</c> from a wheel ack sub-msg.</summary>
        public uint UploadLastBytesWritten => _uploader?.LastBytesWritten ?? 0;
        /// <summary>Last <c>total_size:u32 BE</c> from a wheel ack sub-msg.</summary>
        public uint UploadLastTotalSize => _uploader?.LastTotalSize ?? 0;
        /// <summary>Last XOR status byte from a wheel ack sub-msg.</summary>
        public byte UploadLastStatusByte => _uploader?.LastStatusByte ?? 0;

        /// <summary>
        /// Trigger a manual upload of <paramref name="content"/> to the wheel.
        /// Replaces any active <see cref="MzdashContent"/> + <see cref="MzdashName"/>
        /// + <see cref="MzdashSourceDirectory"/> on the uploader so the in-flight
        /// upload uses the new bytes (and PNGs from the given source dir), then
        /// queues <c>RunBackgroundUpload</c> on the thread pool. Returns
        /// immediately; the UI should poll <see cref="IsUploadInFlight"/> /
        /// <see cref="UploadLastBytesWritten"/> for progress. No-op when not
        /// connected, no mgmt port has been negotiated, or the content is empty.
        /// </summary>
        /// <param name="sourceDirectory">Directory the mzdash file was loaded
        /// from (used to find sibling PNGs at
        /// <c>&lt;dir&gt;/Resource/MD5/&lt;hex&gt;.png</c>). Pass <c>null</c>
        /// or empty for builtin/embedded uploads — the bundle will ship as
        /// single-file.</param>
        public bool TriggerManualUpload(byte[] content, string name, string? sourceDirectory = null)
        {
            if (_uploader == null) return false;
            if (content == null || content.Length == 0) return false;
            if (_mgmtPort == 0) return false;
            if (!_connection.IsConnected) return false;
            _uploader.MzdashContent = content;
            _uploader.MzdashName = name ?? "";
            _uploader.MzdashSourceDirectory = sourceDirectory ?? "";
            ThreadPool.QueueUserWorkItem(_ => _uploader.RunBackgroundUpload());
            return true;
        }

        /// <summary>
        /// Resolver invoked per frame for channels with a non-empty
        /// <see cref="ChannelDefinition.SimHubProperty"/>. Set by MozaPlugin before
        /// assigning <see cref="Profile"/>; bound into each TelemetryFrameBuilder at
        /// profile-assign time so there is no per-frame lookup cost.
        /// </summary>
        public Func<string, double>? PropertyResolver { get; set; }

        /// <summary>
        /// String-valued sibling of <see cref="PropertyResolver"/>. Used by the
        /// sess=0x01 type=0x05 string-channel emitter to read a SimHub property as
        /// a string (game-running mode). Set by MozaPlugin alongside
        /// <see cref="PropertyResolver"/>. Returns <c>null</c> when the path is
        /// missing or the read throws; callers treat null as empty.
        /// </summary>
        public Func<string, string?>? PropertyStringResolver { get; set; }

        public MultiStreamProfile? Profile
        {
            get => _profile;
            set
            {
                // Idempotency guard. Callers (ApplyTelemetrySettings,
                // OnWheelInitiatedSwitch, OnDashboardSwitched,
                // MaybeSwapProfileForCatalog) can fire this setter several
                // times in close succession with the same source profile —
                // e.g. UI combo click → ApplyTelemetrySettings → next tick
                // MaybeSwapProfileForCatalog sees catalog unchanged but
                // assigns the cached synthesised profile back in. Each
                // re-assignment re-runs the multi-broadcast expansion (~N
                // new DashboardProfile allocations), rebuilds every per-
                // tier FrameBuilder, and resets the OriginalChannels
                // pristine snapshot. Reference-compare so the same input
                // → no-op; null-to-null also short-circuits cleanly.
                if (ReferenceEquals(value, _lastProfileSourceRef)) return;
                // Off→on transition (telemetry was disabled, user re-enabled
                // it / selected a profile from the empty state): treat as an
                // explicit "fresh attempt" signal and forgive any prior
                // recovery-budget exhaustion so the new attempt starts with a
                // clean budget. The existing wheel-hot-swap path covers the
                // hardware-change case (ResetBindingTracking); this covers
                // the user-driven case.
                if (value != null && _lastProfileSourceRef == null)
                {
                    try { _recovery.Reset(); } catch { }
                }
                _lastProfileSourceRef = value;

                if (value != null && value.Tiers.Count > 0)
                {
                    // Multi-broadcast: replicate each sub-tier 3-N+1 times with
                    // consecutive flag bytes. Without this, slow-pkg tiers
                    // (pkg=500, pkg=2000) only fire at their nominal rate (2Hz,
                    // 0.5Hz) → visible test-mode lag for slow-tier channels.
                    // Replication forces every channel to update at the fast
                    // base tick rate via parallel flag bytes.
                    var subTiers = new System.Collections.Generic.List<DashboardProfile>(value.Tiers);
                    subTiers.Sort((a, b) => a.PackageLevel.CompareTo(b.PackageLevel));
                    int subCount = subTiers.Count;
                    int broadcasts = subCount == 1 ? 3 : System.Math.Max(4, subCount + 1);
                    var expanded = new System.Collections.Generic.List<DashboardProfile>(subCount * broadcasts);
                    // One Channels copy per source sub-tier, shared across
                    // its broadcast replicas. COPY so the in-place mutate in
                    // SortTierChannelsByCatalogIdx doesn't corrupt the cached
                    // profile; SHARE so the dedup-by-reference in that sort
                    // still mutates each unique list exactly once.
                    var copiedChannelsForSrc =
                        new System.Collections.Generic.Dictionary<DashboardProfile, System.Collections.Generic.List<ChannelDefinition>>();
                    foreach (var src in subTiers)
                    {
                        copiedChannelsForSrc[src] =
                            new System.Collections.Generic.List<ChannelDefinition>(src.Channels);
                    }
                    for (int b = 0; b < broadcasts; b++)
                    {
                        foreach (var src in subTiers)
                        {
                            expanded.Add(new DashboardProfile
                            {
                                Name = $"{src.Name}@b{b}",
                                Channels = copiedChannelsForSrc[src],
                                TotalBits = src.TotalBits,
                                TotalBytes = src.TotalBytes,
                                PackageLevel = src.PackageLevel,
                                FlagByte = src.FlagByte,
                            });
                        }
                    }
                    value = new MultiStreamProfile
                    {
                        Name = value.Name,
                        PageCount = value.PageCount,
                        Tiers = expanded,
                        // Strings are out-of-band; carry through unchanged
                        // (the expanded profile becomes the live _profile).
                        StringChannels = value.StringChannels,
                    };
                }
                _profile = value;
                if (value == null || value.Tiers.Count == 0)
                {
                    _tiers = null;
                    _baseTickMs = 33;
                    return;
                }

                if (value.StringChannels.Count > 0)
                {
                    var urls = string.Join(", ", value.StringChannels.Select(c =>
                        string.IsNullOrEmpty(c.SimHubProperty)
                            ? c.Url
                            : $"{c.Url}→{c.SimHubProperty}"));
                    MozaLog.Debug(
                        $"[Moza] Profile '{value.Name}' has {value.StringChannels.Count} " +
                        $"string channels (sess=0x01 type=0x05): {urls}");
                }

                // Base tick = fastest tier's pkg_level (smallest).
                int minPkg = int.MaxValue;
                foreach (var t in value.Tiers)
                    if (t.PackageLevel > 0 && t.PackageLevel < minPkg) minPkg = t.PackageLevel;
                _baseTickMs = (minPkg == int.MaxValue) ? 30 : minPkg;

                // Do NOT apply the catalog-driven sort+filter here. The
                // catalog state at Profile-set time is often stale (cold
                // start: catalog hasn't arrived; post-dashboard-switch:
                // catalog still has the PREVIOUS dashboard's URLs because
                // the wheel sends new catalog over ~1s after the
                // Stop+Start cycle). Defer to ApplySubscription which
                // (a) has a fresher catalog and (b) re-runs from the
                // pristine OriginalChannels every time, so a stale-catalog
                // filter result doesn't permanently strip channels.

                _tiers = new TierState[value.Tiers.Count];
                var tierDiag = new System.Text.StringBuilder();
                tierDiag.Append($"[Moza] Profile setter: \"{value.Name}\" {value.Tiers.Count}t baseTickMs={_baseTickMs}");
                for (int i = 0; i < value.Tiers.Count; i++)
                {
                    var tier = value.Tiers[i];
                    int tickInterval = Math.Max(1, tier.PackageLevel / _baseTickMs);
                    tierDiag.Append($" | t[{i}]={tier.Name} {tier.Channels.Count}ch pkg={tier.PackageLevel} bits={tier.TotalBits} bytes={tier.TotalBytes}");
                    _tiers[i] = new TierState
                    {
                        // PitHouse capture 2026-04-29 in-game shows N=14 (legacy
                        // convention 8+data) on this firmware, NOT Type02 N=16.
                        // Hardcoding type02NConvention=false until per-firmware
                        // detection is correct — the previous heuristic wrongly
                        // pinned Type02 N for this wheel.
                        Builder = new TelemetryFrameBuilder(tier, PropertyResolver,
                            type02NConvention: false,
                            deviceId: _targetDeviceId),
                        TickInterval = tickInterval,
                        // Snapshot the pristine (pre-filter) channel list so
                        // ApplySubscription can refilter from scratch each call.
                        OriginalChannels = new System.Collections.Generic.List<ChannelDefinition>(tier.Channels),
                        OriginalTotalBits = tier.TotalBits,
                        OriginalTotalBytes = tier.TotalBytes,
                    };
                }
                MozaLog.Debug(tierDiag.ToString());

                // Apply the catalog-driven filter + sort + FrameBuilder
                // rebuild NOW if the catalog is available. Each Profile
                // setter call builds initial FrameBuilders from the
                // UNFILTERED 10-channel profile (so that OriginalChannels
                // captures the pristine state). Without this immediate
                // re-filter, value frames between Profile setter and the
                // next ApplySubscription leak out at the wrong (10-channel,
                // 37-byte) size — verified 2026-05-15. ApplySubscription
                // will reset + re-filter again later when needed; calls
                // are idempotent.
                if (_catalogParser.Catalog != null
                    && _catalogParser.Catalog.Count > 0)
                {
                    _tierDefEmitter.SortTierChannelsByCatalogIdx(value, _catalogParser.Catalog);
                    _tierDefEmitter.RebuildFrameBuildersFromProfile();
                }
            }
        }
        private MultiStreamProfile? _profile;
        // Last UNEXPANDED profile reference passed to the Profile setter.
        // Used for the idempotency short-circuit at the top of the setter so
        // repeat calls with the same source don't re-expand. Tracked separately
        // from _profile because the setter mutates value (multi-broadcast
        // expansion) before assigning to _profile, so we'd never match the
        // original input against the post-expansion stored profile.
        private MultiStreamProfile? _lastProfileSourceRef;

        // Per-string-channel emission state: last-sent value and tick timestamp.
        // Keyed by channel URL (case-insensitive). The wheel re-indexes URLs per
        // dashboard so idx is volatile, but the URL string is stable across
        // catalog updates — keying by URL keeps the cadence/dedup state correct
        // when a dashboard switch reshuffles idx assignments.
        private readonly System.Collections.Generic.Dictionary<string, (string lastValue, int lastTickMs)>
            _stringChannelState =
                new System.Collections.Generic.Dictionary<string, (string, int)>(
                    System.StringComparer.OrdinalIgnoreCase);

        private volatile int _framesSent;
        public int FramesSent => _framesSent;
        /// <summary>True between Start() and Stop(). Exposed for diagnostics panel.
        /// Preserves the prior `_enabled` boolean's external semantics — anything
        /// other than Idle counts as "running".</summary>
        public bool Enabled => _state != TelemetryState.Idle;

        /// <summary>
        /// Atomic state transition with audit logging. Use this everywhere instead
        /// of writing to <see cref="_state"/> directly so every change is visible
        /// in the debug log with its trigger reason. Idempotent: a no-op transition
        /// (next == current) is silently dropped.
        /// </summary>
        private void TransitionTo(TelemetryState next, string reason)
        {
            var prev = _state;
            if (prev == next) return;
            _state = next;
            try { MozaLog.Debug($"[Moza] state {prev} → {next} ({reason})"); }
            catch { /* logging may not be initialised in tests */ }
        }
        // Read-only accessors for DashboardSwitchAutoTest
        internal byte? ActiveFlagBase => _activeSubscription?.FlagBase;
        internal int ActiveTierCount => _tiers?.Length ?? 0;
        public string? ActiveProfileName => _profile?.Name;
        internal int CatalogChannelCount => _catalogParser.Count;

        // Catalog-parser internals for the Diagnostics tab. Exposes "why is the
        // wheel-catalog list empty in diag" answers without forcing the user to
        // enable debug logging — at a glance you can tell whether chunks are
        // arriving (BufferLength>0), being rejected (CrcRejects>0), or simply
        // never reaching the catalog session in the first place (LastActivity
        // is "never").
        internal int CatalogBufferLength => _catalogParser.BufferLength;
        internal int CatalogLastParsedBufferLen => _catalogParser.LastParsedBufferLen;
        internal int CatalogLastActivityTickMs => _catalogParser.LastActivityMs;

        private DashboardSwitchAutoTest? _autoTest;

        /// <summary>
        /// Raised once when the sess=0x09 retry budget exhausts and the dashboard
        /// pipeline is parked. The plugin uses this to clear its
        /// <c>_telemetryStartRequested</c> gate so a future
        /// <c>StartTelemetryIfReady</c> (e.g. wheel hot-swap, user toggle) can
        /// re-attempt cleanly. Fires after the sender has called <see cref="Stop"/>
        /// internally.
        /// </summary>
        public event EventHandler? DashboardPipelineParked;

        public TelemetrySender(MozaSerialConnection connection)
        {
            _connection = connection;
            _silenceGate = new Lifecycle.SilenceGate(() => EnableHotRenegotiation);
            _recovery = new Lifecycle.RecoveryDispatcher(this);
            _watchdog = new SessionWatchdogManager(this);
            _slotTracker = new Display.WheelSlotTracker(this);
            _propertyPushQueue = new PropertyPushQueue(this);
            _tierDefEmitter = new Frames.TierDefinitionEmitter(this);
            _inboundDispatcher = new Inbound.TelemetryInboundDispatcher(this);
            _rpc = new RpcCallChannel(
                connection,
                shouldAbort: () => _state == TelemetryState.Idle || !_connection.IsConnected);
            _uploader = new WheelUploadCoordinator(
                connection,
                shouldAbort: () => _state == TelemetryState.Idle || !_connection.IsConnected,
                getPolicy: () => _policy,
                getConfigJsonState: () => _configJson.LastState,
                sendSessionAck: SendSessionAck,
                sendSessionEnd: SendSessionEnd,
                sendAndTrackChunk: SendAndTrackChunk,
                sendSessionOpen: SendSessionOpen);

            // Single-line outcome log per upload attempt. Without this, a
            // silent failure (e.g. NoFtSession) only shows up as a Warn deep
            // in the worker thread and is easy to miss in the diagnostics
            // bundle. Logged at Info for Succeeded/Skipped (visible at the
            // default level) and Warn for everything else.
            _uploader.UploadCompleted += outcome =>
            {
                string name = string.IsNullOrEmpty(_uploader.MzdashName) ? "dashboard" : _uploader.MzdashName;
                switch (outcome)
                {
                    case WheelUploadCoordinator.UploadOutcome.Succeeded:
                        MozaLog.Info($"[Moza] Dashboard upload \"{name}\": Succeeded");
                        break;
                    case WheelUploadCoordinator.UploadOutcome.SkippedHashMatch:
                        MozaLog.Info($"[Moza] Dashboard upload \"{name}\": SkippedHashMatch");
                        break;
                    case WheelUploadCoordinator.UploadOutcome.Aborted:
                        MozaLog.Debug($"[Moza] Dashboard upload \"{name}\": Aborted");
                        break;
                    default:
                        MozaLog.Warn($"[Moza] Dashboard upload \"{name}\": {outcome}");
                        break;
                }
            };
        }

        // Caller passes the MozaPlugin instance directly because Init may call
        // ApplyTelemetrySettings BEFORE MozaPlugin.Instance is assigned, so the
        // static accessor is null at this point and the delegate binding throws
        // ArgumentException("Delegate to an instance method cannot have null 'this'").
        internal void EnableAutoTest(MozaPlugin plugin)
        {
            if (_autoTest == null)
            {
                _autoTest = new DashboardSwitchAutoTest(
                    this,
                    plugin.ResolveDashboardProfileByName,
                    () => plugin.DashCache,
                    name => { plugin.ActiveTelemetryProfileName = name; });
            }
            else
            {
                _autoTest.Reset();
            }
        }

        // Serializes Start() against concurrent callers. Without this, two
        // Start() work items on the ThreadPool (e.g. rapid Test-button double-
        // click routing through StartTelemetryIfReady's QueueUserWorkItem) each
        // run Stop() then `new Timer()`; the losing thread's timer gets
        // orphaned but keeps OnTimerElapsed subscribed, multiplying the tick
        // rate for the lifetime of the session.
        //
        // Supersession model: a second Start() arriving while the first is
        // mid-StartInner cancels the first's CancellationTokenSource so
        // StartInner bails at its next gate, then waits on _startSemaphore
        // for the first run's finally block to release. This replaces the
        // earlier _startInProgress int + SpinWait.SpinUntil pattern which
        // (a) burned ThreadPool workers for up to 10 s of busy-wait, and
        // (b) reached into the in-progress run by setting _state=Idle from
        // outside, mixing supersession with the Stop signal.
        private readonly SemaphoreSlim _startSemaphore = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _startCts;
        private readonly object _startCtsLock = new object();

        // Flipped to 1 by Dispose() so Stop() / SendRpcCall() / handlers can
        // bail without touching disposed ManualResetEventSlim instances.
        private int _disposed;

        /// <summary>True once Dispose() has run on this sender. MozaPlugin's
        /// persistent-singleton reuse check reads this to decide whether to
        /// reuse the prior instance or build a fresh one.</summary>
        public bool IsDisposedFlag => System.Threading.Volatile.Read(ref _disposed) != 0;

        /// <summary>Current lifecycle state. Exposed for diagnostic / reuse
        /// logging from MozaPlugin.</summary>
        internal TelemetryState State => _state;

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                MozaLog.Warn("[Moza] Start() ignored — sender disposed");
                return;
            }
            // Persistent-sender reuse: when MozaPlugin reuses this sender
            // across plugin reload (game switch with sessions kept alive),
            // the new plugin instance's StartTelemetryIfReady fires when
            // wheel-detected, eventually calling Start() here. The sender
            // is already running (state=Active, sessions open, tick timer
            // alive); a Stop+Start cycle here would close sessions and
            // pay the 11s sess=0x09 settle wait, defeating the whole point
            // of keeping the wire persistent. Short-circuit when already
            // Active and connected.
            if (_state == TelemetryState.Active && _connection.IsConnected)
            {
                MozaLog.Debug(
                    "[Moza] Start() skipped — sender already Active with live connection " +
                    "(persistent-sender reuse path)");
                return;
            }

            // Supersede any in-progress Start: cancel its CTS so StartInner
            // bails at its next gate, then queue behind it on the semaphore.
            // The prior run owns the disposal of its own CTS; we just signal.
            CancellationTokenSource? prior;
            lock (_startCtsLock) { prior = _startCts; }
            if (prior != null)
            {
                MozaLog.Debug("[Moza] Start() cancelling prior in-progress start");
                try { prior.Cancel(); } catch (ObjectDisposedException) { }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!_startSemaphore.Wait(10000))
            {
                MozaLog.Warn("[Moza] Start() could not acquire start lock after 10s");
                return;
            }
            if (prior != null)
                MozaLog.Debug($"[Moza] Start() superseded in-progress start (waited {sw.ElapsedMilliseconds}ms)");

            // Re-check disposal after the (potentially long) wait — Dispose
            // may have arrived while we were queued.
            if (Volatile.Read(ref _disposed) != 0)
            {
                MozaLog.Warn("[Moza] Start() ignored — sender disposed during wait");
                _startSemaphore.Release();
                return;
            }

            CancellationTokenSource newCts = new CancellationTokenSource();
            lock (_startCtsLock) { _startCts = newCts; }

            try
            {
                StartInner(newCts.Token);
            }
            catch (OperationCanceledException)
            {
                MozaLog.Debug("[Moza] StartInner cancelled by supersession / disposal");
            }
            finally
            {
                lock (_startCtsLock)
                {
                    if (ReferenceEquals(_startCts, newCts))
                        _startCts = null;
                }
                try { newCts.Dispose(); } catch { }
                _startSemaphore.Release();
            }
        }

        private void StartInner(CancellationToken cancel)
        {
            // Capture pre-Stop state. We need TWO things from
            // _silenceGate.LastStopUtcTicks BEFORE the Stop() call below
            // resets it to the current time:
            //   1. Whether this is a true cold-start in a fresh SimHub
            //      process (preStopTicks == 0).
            //   2. The actual timestamp of the PRIOR Stop (typically
            //      from End() during plugin reload), so the elapsed
            //      calculation reflects real time since that close —
            //      not the millisecond-since-StartInner's-internal-Stop
            //      (which is always ~0 and made the gate trivially
            //      always wait the full 11 s).
            long preStopTicks = _silenceGate.LastStopUtcTicks;
            bool isFirstStartInProcess = (preStopTicks == 0);
            Stop();

            // Enforce minimum host silence since the last Stop() completion.
            // The wheel maintains an internal ~10-14 s timeout on its
            // sess=0x09 dashboard-binding state — during that window the
            // wheel ignores host re-opens. Verified 2026-05-08 wire
            // trace: failing cycles at 8.4 s of silence, working at
            // 13.9 s. This gate is the host-side enforcement.
            //
            // Cold-start in a fresh SimHub process skips the gate: the
            // wheel either has no prior session (clean state) or any
            // stale session from a previous process has long since timed
            // out. The gate only matters for fast plugin reload within
            // the same process (e.g., SimHub game switch).
            int waitMs = _silenceGate.RemainingStopReopenWaitMs(preStopTicks);
            if (!isFirstStartInProcess)
            {
                long elapsedMs = (System.DateTime.UtcNow.Ticks - preStopTicks)
                    / System.TimeSpan.TicksPerMillisecond;
                if (waitMs > 0)
                {
                    MozaLog.Debug(
                        $"[Moza] Start: enforcing {waitMs}ms silence " +
                        $"(elapsed since last Stop: {elapsedMs}ms; min: {Lifecycle.SilenceGate.StopReopenSilenceMs}ms) " +
                        "so wheel session state can settle before reopen");
                    try { System.Threading.Thread.Sleep(waitMs); } catch { }
                }
            }
            else
            {
                MozaLog.Debug(
                    "[Moza] Start: first start in this SimHub process — " +
                    "skipping silence gate (no prior Stop to settle from)");
            }

            InitTickStateAndTransitionToStarting();
            BuildCachedFrames();

            // Subscribe early so we catch fc:00 acks during port probing AND preamble
            _connection.MessageReceived += _inboundDispatcher.OnMessageDuringPreamble;

            // Probe for available ports and open sessions. May run on a
            // background thread (dispatched by StartTelemetryIfReady) so the
            // serial read thread stays free to deliver fc:00 ack responses.
            //
            // Pass through isFirstStartInProcess so cold starts get the wide
            // session close (0x01..0x0a) that flushes any stale wheel-side
            // session state from a prior SimHub process. Game-switch reloads
            // skip the wide close to preserve the configJson handshake on
            // the persistent wire.
            ProbeAndOpenSessions(isFirstStartInProcess);
            // Supersession gate: a new Start() arriving mid-probe cancels
            // our token. An external Stop() during the same window pushes
            // state to Idle — both signal "abandon this StartInner".
            if (cancel.IsCancellationRequested || _state == TelemetryState.Idle) return;

            // Universal Hub: 5-frame slot enumeration burst so the wheel
            // populates per-port device metadata. Skipped when no hub.
            if (_connection.HubProbeSucceeded)
                SendHubSlotEnumeration();

            PrimeAndOpenSession09();
            QueueBackgroundUploadIfReady();
            if (cancel.IsCancellationRequested || _state == TelemetryState.Idle) return;

            // Open session 0x03 (tile-server). Tile-server push deferred until
            // after tier-def — earlier push collided with the wheel's
            // sess=0x09 state burst under Wine SerialPort contention.
            SendSessionOpen(0x03, 0x03);

            _tierDefEmitter.WaitForChannelCatalogQuiet(quietMs: 200, timeoutMs: 2000);
            _catalogParser.TryParse();
            MaybeSwapProfileForCatalog();

            // Sess=0x02 init handshake (kind=2 nonce + kind=7 slot-index).
            // Required: without it, wheel ignores dashboard-switch FF records
            // on a fresh session 0x02.
            SendSessionInitHandshake();

            // Empty-state tile-server blob on session 0x03 (host→wheel only).
            SendTileServerState();

            // Probe the wheel's Display sub-device. Non-blocking — responses
            // arrive via the inbound dispatcher.
            SendDisplayProbe();
            if (cancel.IsCancellationRequested || _state == TelemetryState.Idle) return;

            StartTickTimer();
        }

        // ── StartInner phase helpers ────────────────────────────────────────

        /// <summary>Reset per-session counters, parsers, and subscription state,
        /// and TransitionTo Starting. The state stays Starting through session
        /// probes and frame staging; <see cref="StartTickTimer"/> transitions
        /// to Preamble once the tick timer is armed.</summary>
        private void InitTickStateAndTransitionToStarting()
        {
            TransitionTo(TelemetryState.Starting, "StartInner: begin");
            _tickCounter = 0;
            _framesSent = 0;
            _sequenceCounter = 0;
            _slowCounter = 0;
            _displayConfigPage = 0;
            // ClearBuffer keeps the resolved _catalog so cross-switch backrefs
            // resolve (wheel uses size=1 backref records post-switch). Drops
            // in-progress reassembly buffer + per-session seq dedup.
            _catalogParser.ClearBuffer();
            _nextFlagBase = 0;
            _activeSubscription = null;
            _sessionAckSeq = 0;
            _dashboardDownloadTriggered = false;
            _preambleTickTarget = Math.Max(1, 1000 / _baseTickMs);
        }

        /// <summary>Prime session 0x09 (configJson state push) plus the
        /// post-2026-04 CSP host-init open request. Wheels we've observed
        /// (KS Pro on Universal Hub) only open 0x05/0x07 in their device-init
        /// burst, NOT 0x09 — leaving the configJson handshake stuck. Pithouse
        /// encourages 0x09 by sending an empty data frame on it before any
        /// clean session opens; post-2026-04 CSP firmware also needs an
        /// explicit host-init open with a port-9-specific magic before it
        /// will device-init the channel.</summary>
        private void PrimeAndOpenSession09()
        {
            SendSessionPrime(0x09, 0x0001);
            _watchdog.SendConfigJsonOpenRequest(0x09, seq: 0x000B);
        }

        /// <summary>Dispatch the dashboard upload to the ThreadPool. Different
        /// wheel firmwares device-init the upload session (0x04..0x0a) at very
        /// different times — observed 40 ms (older direct-base firmware) up
        /// to ~11 s (KS Pro on RS21-W18-MC SW). A foreground wait long enough
        /// to cover the slow case would stall tier def + telemetry timer for
        /// the same duration. Decoupled: upload waits in background for an
        /// FT-eligible device-init, then sends; on 60 s timeout exits silently
        /// and the wheel renders previously-cached dashboard.</summary>
        private void QueueBackgroundUploadIfReady()
        {
            if (UploadDashboard && MzdashContent != null && _mgmtPort != 0)
                ThreadPool.QueueUserWorkItem(_ => _uploader.RunBackgroundUpload());
        }

        /// <summary>Final phase: arm the tick timer and transition to Preamble.
        /// The first ~_preambleTickTarget ticks run heartbeat-only frames in
        /// <see cref="TickPreamble"/>; once the tick counter reaches that
        /// target the state flips to Active and value frames begin.</summary>
        private void StartTickTimer()
        {
            double intervalMs = _baseTickMs;
            _sendTimer = new Timer(intervalMs) { AutoReset = true };
            _sendTimer.Elapsed += OnTimerElapsed;
            _sendTimer.Start();
            TransitionTo(TelemetryState.Preamble, "StartInner: timer started");
        }

        /// <summary>
        /// Close sessions 0x01/0x02/0x03 (host-owned) on shutdown. Wheel-owned
        /// 0x04..0x0a / 0x09 configJson are LEFT ALONE — wheel never closes
        /// 0x09 host-side; closing it would be a no-op or regression. The
        /// wheel's ~10–14s internal sess=0x09 timeout is the actual re-engage
        /// gate; <see cref="Lifecycle.SilenceGate.StopReopenSilenceMs"/> bridges it.
        ///
        /// Gated on <see cref="MozaPlugin.ShouldDriveDashboard"/>: on screenless
        /// wheels Start() never ran, the host never opened any session, and the
        /// closes would just emit three dashboard-session frames (group 0x43
        /// dev=0x17 cmd `7C 00`) to a wheel that doesn't speak the session
        /// layer. Captured as ~15 `7c 00` leak frames per long bundle (3 per
        /// wheel-redetect cycle).
        /// </summary>
        private void CloseHostSessions()
        {
            if (!_connection.IsConnected) return;
            if (MozaPlugin.Instance?.ShouldDriveDashboard() == false) return;
            try { SendSessionClose(0x01); } catch { }
            try { SendSessionClose(0x02); } catch { }
            try { SendSessionClose(0x03); } catch { }
            try { System.Threading.Thread.Sleep(100); } catch { }
        }

        public void Stop()
        {
            TransitionTo(TelemetryState.Idle, "Stop()");
            _connection.MessageReceived -= _inboundDispatcher.OnMessageDuringPreamble;
            if (_sendTimer != null)
            {
                _sendTimer.Stop();
                _sendTimer.Elapsed -= OnTimerElapsed;
                _sendTimer.Dispose();
                _sendTimer = null;
            }

            // Drop anything already queued or sitting in the OS write buffer —
            // otherwise frames keep flowing to the wheel for ~1.4 s after stop
            // (16 KB WriteBufferSize at 115200 baud).
            _connection.FlushPendingWrites();

            // Now that the queue is clear and the timer can't enqueue more,
            // emit the shutdown SessionClose triplet so the wheel sees a clean
            // close. Done AFTER FlushPendingWrites (which calls DiscardOutBuffer)
            // so the closes aren't dropped along with the in-flight value frames.
            CloseHostSessions();

            // Wake any blocked SendRpcCall waiters so they unblock with a null
            // reply rather than sit on Wait() until their per-call timeout fires
            // (those callers may be on the SimHub UI thread).
            _rpc.DrainWaiters();

            try { _ackReceived.Reset(); } catch (ObjectDisposedException) { }
            try { _mgmtResponseEvent.Reset(); } catch (ObjectDisposedException) { }
            try { _uploader?.Reset(); } catch { }
            _sessions.Reset();
            _dispatcher.Reset();
            _session09InboundSeq = 0;
            _session09OutboundSeq = 0;
            // Re-arm so the next sess=0x09 device-init re-confirms the
            // canonical dashboard list to the wheel.
            _session09ReplySent = false;
            _watchdog.Reset();
            _session02OutboundSeq = 0;
            _session01OutboundSeq = 0;
            // Reset 0x0a seq for symmetry — fresh Start re-opens 0x0a from
            // zero wheel-side. Prevents stale-seq retransmits re-emitting
            // into a new session. See docs/protocol/sessions/chunk-format.md.
            _rpc.OutboundSeq = 0;
            _tierDefEmitter.Reset();
            _autoResolutionDone = false;
            _retransmitter.Clear();
            // Take the seq lock so this Clear can't race a mid-flight
            // enumeration of the property-push dict. Pre-fix the unsynchronised
            // narrow window made the race invisible, but the new
            // _session02SeqLock holds the dict-touching code longer and
            // expanded the window enough to surface ConcurrentModification
            // exceptions during Stop+Start cycles under load.
            _propertyPushQueue.Clear();
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = 0;
            lock (_sessionCounts) _sessionCounts.Clear();
            // gap-recovery counter reset handled by _watchdog.Reset() above.
            // Reset catalog-growth tracking so the next Start's first
            // subscription (built from whatever catalog has arrived) is
            // treated as the new baseline.
            _catalogCountAtLastSubscription = 0;
            // Drop reassembly buffers (residual chunks would overflow on
            // next Start). Caches last-good LastState through Stop+Start;
            // wheel-state doesn't change without user action.
            try { _configJson.ClearBuffer(); } catch { }
            try { _tileServerParser.Clear(); } catch { }
            try { _session0aInbox.Clear(); } catch { }
            // Reset so StartTelemetryIfReady() won't skip us on re-enable
            _framesSent = 0;

            // Arm the StartInner silence gate. Every Stop — user disable,
            // shutdown, cold-start reset inside StartInner, RestartForSwitch
            // — must precede the next reopen by ~11 s or the wheel never
            // completes its sess=0x09 device-init. This is the host's only
            // lever on that interlock; the wheel does not signal when its
            // internal state is settled.
            _silenceGate.MarkStopped(System.DateTime.UtcNow.Ticks);
        }

        public void UpdateGameData(StatusDataBase? data)
        {
            _latestGameData = data;
        }

        /// <summary>
        /// Mirror SimHub's GameRunning flag so V0 value-frame loop can stay
        /// silent when no game is active. PitHouse only emits V0 channel
        /// values during gameplay; bursting them at idle stomps property
        /// pushes (brightness/standby) that share the same `ff &lt;kind&gt;
        /// &lt;value&gt;` wire format on session 0x02.
        /// </summary>
        public void SetGameRunning(bool running)
        {
            if (running && !_gameRunning)
                _gameStartHandshakePending = true;
            _gameRunning = running;
        }

        /// <summary>
        /// Push a wheel-integrated dashboard property update on session 0x01
        /// using the `ff`-tagged property-push record (PitHouse runtime
        /// settings format — see
        /// <c>docs/protocol/findings/2026-04-29-session-01-property-push.md</c>).
        /// </summary>
        /// <param name="kind">Property `kind` (1=display brightness, 10=standby).</param>
        /// <param name="value">u32 value (e.g. brightness 0–100).</param>
                public void SendSessionPropertyU32(uint kind, uint value)
            => _propertyPushQueue.SendU32(kind, value);

        /// <summary>Push a u64-valued property (e.g. standby in milliseconds).</summary>
                public void SendSessionPropertyU64(uint kind, ulong value)
            => _propertyPushQueue.SendU64(kind, value);

        /// <summary>
        /// Send a session-data chunk via <see cref="_connection"/> and register
        /// it with the retransmit queue so it gets re-emitted until acked. For
        /// non-chunk frames the retransmitter Track() is a no-op (ignored by
        /// shape check), so this is safe to call broadly.
        /// </summary>
        private void SendAndTrackChunk(byte[] frame)
        {
            _connection.Send(frame);
            _retransmitter.Track(frame);
        }

        /// <summary>
        /// Push wheel-integrated dashboard display brightness (0–100; 0 turns
        /// the display off entirely).
        ///
        /// <para>brightness=0 is destructive in the user-experience sense: on
        /// the CSP-on-hub firmware seen in
        /// <c>usb-capture/displaybrightnessbug</c> a single 0-push leaves the
        /// display blanked, and even though our retransmit-coalescing fix
        /// (<see cref="SendSessionPropertyBody"/>) lets a subsequent
        /// brightness>0 unblank it, transient 0s during startup/profile
        /// switching scare users. So unless <paramref name="allowZero"/> is
        /// explicitly set, a 0 value is silently skipped — only the
        /// debounced UI slider path opts in to actually pushing 0
        /// (<c>MozaWheelSettingsControl.DisplayBrightnessDebounce_Tick</c>).
        /// Storage isn't touched: callers' settings/profile keep their 0,
        /// only the wire push is suppressed.</para>
        /// </summary>
        /// <param name="percent">Brightness 0..100 (clamped).</param>
        /// <param name="allowZero">
        /// When false (default), a value of 0 is skipped. When true, 0 is
        /// pushed verbatim — used for explicit user intent (slider committed
        /// at 0).
        /// </param>
        public void SendDashDisplayBrightness(int percent, bool allowZero = false)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (percent == 0 && !allowZero)
            {
                global::MozaPlugin.MozaLog.Debug(
                    "[Moza] SendDashDisplayBrightness: skipping non-explicit 0 push " +
                    "(use allowZero=true for deliberate display-off)");
                return;
            }
            SendSessionPropertyU32(
                global::MozaPlugin.Protocol.SessionPropertyPushBuilder.KindDashBrightness,
                (uint)percent);
        }

        /// <summary>
        /// Send the dashboard-switch FF-record on session 0x02 to activate
        /// a stored dashboard by its <b>0-based</b> index in the wheel's
        /// <c>configJsonList</c> (alphabetical dashboard name list from
        /// session 0x09 state push).
        ///
        /// Verified 2026-04-30: slot=1 activates <c>configJsonList[1]</c>
        /// (Grids), NOT <c>enableManager.dashboards[1]</c> (Rally V5).
        /// Wheel uses configJsonList ordering, 0-based.
        /// See <c>docs/protocol/findings/2026-04-30-dashboard-switch-3f27.md</c>.
        /// </summary>
        /// <returns><c>true</c> if a kind=4 frame was actually emitted on
        /// the wire (so callers know a Stop+Start cycle is now needed and
        /// the wheel's sess=0x09 timeout has been re-armed). <c>false</c>
        /// when emission was suppressed — disconnected, non-Active state,
        /// or still inside the post-emit cooldown window — in which case
        /// the wheel state has not changed and no follow-up restart is
        /// required.</returns>
        public bool SendDashboardSwitch(uint slotIndex)
        {
            if (!_connection.IsConnected) return false;
            // Block kind=4 emission during the post-emit silence window or
            // any non-Active state. Sending kind=4 mid-restart races with
            // the wheel's session re-handshake — observed 2026-05-09: a
            // user's rapid double-click during the silence wait leaked a
            // kind=4 onto the wire BEFORE Start re-opened sessions, putting
            // the wheel into a state where it pushed corrupt backref-style
            // catalog records the parser couldn't decode.
            if (_state != TelemetryState.Active || IsInSilenceCooldown)
            {
                MozaLog.Debug(
                    $"[Moza] SendDashboardSwitch slot={slotIndex} suppressed: " +
                    $"state={_state} cooldown={IsInSilenceCooldown}. " +
                    "User must wait for restart cycle to complete.");
                return false;
            }

            byte[] body = global::MozaPlugin.Protocol.SessionPropertyPushBuilder
                .BuildDashboardSwitchBody(slotIndex);
            _propertyPushQueue.SendBody(body);
            // Arm the UI cooldown (IsInSilenceCooldown) and the
            // SendDashboardSwitch self-gate above. The wheel's sess=0x09
            // binding-state timeout begins when it receives the kind=4,
            // so we also need to keep the UI from initiating another
            // switch until the wheel's window closes. The StartInner
            // silence sleep is armed separately by Stop() (see
            // SilenceGate.MarkStopped) — that handles the host-side reopen
            // protocol; this field handles UI affordances and double-
            // click suppression on SendDashboardSwitch itself.
            _silenceGate.MarkSwitchEmitted(System.DateTime.UtcNow.Ticks);
            // Record the slot we just bound so callers can detect
            // redundant subsequent emits (catalog probe + profile-apply
            // racing to the same slot is the common case).
            _slotTracker.NoteHostEmittedKind4((int)slotIndex);
            MozaLog.Debug(
                $"[Moza] Sent dashboard-switch FF-record: slot={slotIndex} " +
                $"on session 0x02 seq={_session02OutboundSeq - 1}");
            return true;
        }

        /// <summary>True while the post-emit silence enforcement gate is
        /// active (a kind=4 dashboard-switch frame went out on the wire
        /// within the last silence window and the wheel's sess=0x09
        /// binding-state timeout is still running). UI consumers should
        /// reflect this in their dashboard-switch affordance (disable
        /// dropdown / Start Test button) so the user can't trigger races
        /// against the in-flight Stop+Start.</summary>
        public bool IsInSilenceCooldown => _silenceGate.IsInSilenceCooldown;

        /// <summary>True only when the telemetry pipeline has completed
        /// its preamble and is delivering value frames. Callers gating
        /// dashboard-apply on channel readiness check this before
        /// emitting a kind=4 — if false, the wheel hasn't yet bound to
        /// the channel catalog and the switch would be silently lost.</summary>
        public bool IsActive => _state == TelemetryState.Active;

        /// <summary>
        /// Read-only flattened pipeline status — see <see cref="PipelinePhase"/>.
        /// Derived from <see cref="_state"/>, <see cref="_recovery"/>,
        /// <see cref="_hotSwitch"/>, and <see cref="_silenceGate"/>; no separate
        /// state is maintained. Callers should prefer this over stitching
        /// their own predicate from <c>IsActive</c> / <c>IsInSilenceCooldown</c>
        /// / <c>HotSwitchBurstPending</c> so all consumers agree on the same
        /// flattened status.
        /// </summary>
        public PipelinePhase Phase
        {
            get
            {
                // Park beats everything — recovery rate-limit exhausted /
                // sess=0x09 retry exhausted both land here, and the pipeline
                // won't auto-recover until the user toggles telemetry or hot-
                // swaps the wheel.
                if (_recovery.IsParked) return PipelinePhase.Parked;

                // Recovery debounce window — a watchdog has queued a Stop+Start;
                // the actual state may still be Active for a few ticks until
                // the worker thread lands.
                if (_recovery.IsRecoveryInFlight) return PipelinePhase.Recovery;

                // Steady-state sub-cases.
                if (_state == TelemetryState.Active)
                {
                    return _hotSwitch.IsBurstPending
                        ? PipelinePhase.HotSwitchBurst
                        : PipelinePhase.Active;
                }
                if (_state == TelemetryState.Starting
                    || _state == TelemetryState.Preamble)
                    return PipelinePhase.Starting;

                // Idle — distinguish "in silence wait" from "fully idle"
                // so UI can show "waiting for wheel to reconnect" vs.
                // "telemetry disabled".
                if (_silenceGate.IsInSilenceCooldown) return PipelinePhase.SilenceWait;
                return PipelinePhase.Idle;
            }
        }

        // ===== Internal accessors for SessionWatchdogManager =====
        internal bool StateIsIdle => _state == TelemetryState.Idle;
        internal bool StateIsActive => _state == TelemetryState.Active;
        internal bool ConnectionIsConnected => _connection.IsConnected;
        internal Sessions.SessionInfo SessionsGetOrCreate(byte session) => _sessions.GetOrCreate(session);
        internal bool ConfigJsonHasLastState => _configJson.LastState != null;
        internal long ConfigJsonLastForwardGapUtcTicks => _configJson.LastForwardGapUtcTicks;
        internal int Session09InboundSeq => _session09InboundSeq;
        internal int CatalogCount => _catalogParser?.Count ?? 0;
        internal bool HasActiveSubscription => _activeSubscription != null;
        internal void SendRawFrame(byte[] frame) => _connection.Send(frame);
        internal void RaiseDashboardPipelineParked()
        {
            try { DashboardPipelineParked?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private SessionWatchdogManager _watchdog = null!;
        internal SessionWatchdogManager Watchdog => _watchdog;
        internal bool HotSwitchBurstPending => _hotSwitch.IsBurstPending;
        private readonly Display.WheelSlotTracker _slotTracker;
        private readonly PropertyPushQueue _propertyPushQueue;
        private readonly Frames.TierDefinitionEmitter _tierDefEmitter;
        internal Frames.TierDefinitionEmitter TierDefEmitter => _tierDefEmitter;
        private readonly Inbound.TelemetryInboundDispatcher _inboundDispatcher;
        internal Display.WheelSlotTracker SlotTracker => _slotTracker;

        // ── Property/TierDef accessors ───────────────────────────────────

        /// <summary>Lock guarding the read-emit-write cycle on
        /// <see cref="Session02OutboundSeq"/>. Callers that mutate the seq
        /// via the property MUST hold this lock for the entire
        /// read-chunk-send-write region — the property's setter is a bare
        /// assignment because locking only the assignment leaves the
        /// read-then-emit window unprotected, which is what actually races.
        /// In-class writers (TickEmitValueFrames, Stop reset) and external
        /// writers (PropertyPushQueue, TierDefinitionEmitter) all conform.</summary>
        internal object Session02SeqLock => _session02SeqLock;

        /// <summary>Next outbound seq for session 0x02 (telemetry / FlagByte).
        /// MUST be read AND written under <see cref="Session02SeqLock"/> —
        /// see the lock's doc-comment for the why.</summary>
        internal int Session02OutboundSeq
        {
            get => _session02OutboundSeq;
            set => _session02OutboundSeq = value;
        }

        /// <summary>Lock guarding the read-emit-write cycle on
        /// <see cref="Session01OutboundSeq"/>. Same contract as
        /// <see cref="Session02SeqLock"/>; see its doc-comment.</summary>
        internal object Session01SeqLock => _session01SeqLock;

        /// <summary>Next outbound seq for session 0x01 (mgmt). MUST be read
        /// AND written under <see cref="Session01SeqLock"/>.</summary>
        internal int Session01OutboundSeq
        {
            get => _session01OutboundSeq;
            set => _session01OutboundSeq = value;
        }
        internal global::MozaPlugin.Diagnostics.SessionRetransmitter Retransmitter => _retransmitter;
        internal void SendAndTrackChunkInternal(byte[] frame) => SendAndTrackChunk(frame);
        internal MultiStreamProfile? ProfileRef => _profile;
        internal TierState[]? Tiers => _tiers;
        internal ChannelCatalogParser CatalogParser => _catalogParser;
        internal byte MgmtPort => _mgmtPort;
        internal byte NextFlagBase
        {
            get => _nextFlagBase;
            set => _nextFlagBase = value;
        }
        internal SubscriptionState? ActiveSubscription
        {
            get => _activeSubscription;
            set => _activeSubscription = value;
        }
        internal void IncrementSubscriptionGen() =>
            System.Threading.Interlocked.Increment(ref _subscriptionGen);
        internal int CatalogCountAtLastSubscription
        {
            get => _catalogCountAtLastSubscription;
            set => _catalogCountAtLastSubscription = value;
        }
        internal void ScheduleCatalogResyncProbeInternal() => ScheduleCatalogResyncProbe();
        internal void OpenSubscriptionResponseCapture(long deadlineTicks)
        {
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = deadlineTicks;
        }

        // ── Inbound-dispatcher accessors ─────────────────────────────────
        internal SessionDispatcher Dispatcher => _dispatcher;
        internal SessionRegistry Sessions => _sessions;
        internal WheelUploadCoordinator Uploader => _uploader;
        internal ConfigJsonClient ConfigJson => _configJson;
        internal RpcCallChannel Rpc => _rpc;
        internal SessionDataReassembler Session0aInbox => _session0aInbox;
        internal TileServerStateParser TileServerParser => _tileServerParser;
        internal ManualResetEventSlim AckReceived => _ackReceived;
        internal ManualResetEventSlim MgmtResponseEvent => _mgmtResponseEvent;

        /// <summary>Latch set by <see cref="Inbound.TelemetryInboundDispatcher"/>
        /// the first time the wheel pushes a spontaneous sess=0x09 device-init
        /// (type=0x81). Consumed by <see cref="ProbeAndOpenSessions"/> to detect
        /// the slow-bring-up hot-attach case where the wheel's session layer
        /// comes online after the initial 500 ms open-ack budget but before the
        /// 20 s extended-wait timeout. Also wakes <see cref="_ackReceived"/> so
        /// the extended wait returns immediately.</summary>
        internal void MarkWheelReadyObserved()
        {
            _wheelReadyObserved = true;
            _ackReceived.Set();
        }

        /// <summary>Clear the wheel-ready latch — called at Start/Stop
        /// boundaries via <see cref="Watchdog.SessionWatchdogManager.Reset"/>
        /// so a subsequent reconnect re-arms detection from a clean slate.</summary>
        internal void ResetWheelReadyObserved() => _wheelReadyObserved = false;
        internal long SubscriptionResponseDeadlineTicksField
        {
            get => _subscriptionResponseDeadlineTicks;
            set => _subscriptionResponseDeadlineTicks = value;
        }
        internal System.Collections.Generic.List<byte[]> SubscriptionResponseChunksList => _subscriptionResponseChunks;
        internal System.Collections.Generic.Dictionary<byte, int> TileServerHighestSeqMap => _tileServerHighestSeq;
        internal int IncrementCatalogCrcRejects() => Interlocked.Increment(ref _catalogCrcRejects);
        internal int IncrementTileServerCrcRejects() => Interlocked.Increment(ref _tileServerCrcRejects);
        internal void SendSessionAckInternal(byte session, ushort ackSeq) => SendSessionAck(session, ackSeq);
        internal void MaybeSendConfigJsonReplyInternal(WheelDashboardState state, byte session) =>
            MaybeSendConfigJsonReply(state, session);
        internal void MaybeTriggerDashboardDownloadInternal(WheelDashboardState state) =>
            MaybeTriggerDashboardDownload(state);
        internal void SetDisplayDetected(string modelName)
        {
            _displayModelName = modelName;
            _displayDetected = true;
        }

        /// <summary>Clear the display-detected latch on wheel hot-swap /
        /// disconnect. Without this, the next wheel's
        /// <c>StartTelemetryIfReady</c> gate (which keys on
        /// <c>MozaPlugin.IsDisplayDetected</c>) reads the prior wheel's stale
        /// detection and starts the session pipeline before the new wheel's
        /// display sub-device has finished booting — re-creating the original
        /// hot-attach failure mode. Not called from <c>Stop()</c> directly:
        /// game-switch / dashboard-switch cycles reuse the same wheel and
        /// must NOT re-pay the ~20 s display-probe wait every time.</summary>
        internal void ResetDisplayDetection()
        {
            _displayDetected = false;
            _displayModelName = "";
        }
        // Promote ack/seq fields to internal so dispatcher can read/write directly.
        // (These map to the existing _lastAckedSession etc. — see field declarations below.)

        // ── WheelSlotTracker accessors ───────────────────────────────────
        internal Dashboard.WheelDashboardState? ConfigJsonLastState => _configJson.LastState;

        /// <summary>Arm the hot-switch tier-def re-emission burst. Called
        /// from <see cref="Display.WheelSlotTracker"/> when the wheel
        /// initiates a switch via the on-wheel controls.</summary>
        internal void ArmHotSwitchBurst() => _hotSwitch.ArmBurst();

        internal void RaiseWheelInitiatedSwitch(int slot)
        {
            try { WheelInitiatedSwitch?.Invoke(slot); }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] WheelInitiatedSwitch handler threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Cold-start session 0x02 init handshake. PitHouse bridge captures
        /// (verified across 2026-04-28..05-03 via
        /// <c>tools/bridge-decode-ff-init</c>) emit four FF records on sess=
        /// 0x02 shortly after open:
        ///   <list type="bullet">
        ///     <item>kind=2: 16-byte timestamp/nonce record</item>
        ///     <item>kind=7: 12-byte slot-index record</item>
        ///     <item>kind=8: ~1.7 KB zlib-compressed channel catalog</item>
        ///     <item>kind=11: ~2.5 KB zlib-compressed FFB-property catalog</item>
        ///   </list>
        /// The wheel echoes back kind=10 + kind=16 ~3.5 s later as an ack and
        /// only then accepts dashboard-switch FF kind=4 records (echoing
        /// each within ~77 ms). Without this handshake the wheel ignores
        /// FF kind=4 entirely and post-switch tier-defs never bind to
        /// display elements — symptom: switch is visual but new dash never
        /// shows test data.
        ///
        /// kind=2 timestamp is regenerated to the current Unix time. kind=8
        /// and kind=11 are NOT emitted — see docs/protocol/sessions/
        /// session-0x02-ff-init.md before adding them. Verbatim replay of
        /// captured kind=8/11 bytes was tested 2026-05-13 and locked the
        /// wheel (required power-cycle); the records carry session-bound
        /// state and have to be regenerated per cold-start, not replayed.
        /// </summary>
        internal void SendSessionInitHandshake()
        {
            if (_state == TelemetryState.Idle || !_connection.IsConnected) return;

            byte[] init2 = global::MozaPlugin.Protocol.SessionPropertyPushBuilder
                .BuildSessionInitField2Body();
            _propertyPushQueue.SendBody(init2);

            byte[] init7 = global::MozaPlugin.Protocol.SessionPropertyPushBuilder
                .BuildSessionInitField7Body(slotIndex: 0u);
            _propertyPushQueue.SendBody(init7);

            // kind=8 / kind=11 deliberately not emitted — see method-level
            // comment above and docs/protocol/sessions/session-0x02-ff-init.md
            // for the required body-decode work before re-attempting.

            MozaLog.Debug(
                $"[Moza] Sent sess=0x02 init handshake (kind=2 nonce + kind=7 slot=0); " +
                $"next outbound seq={_session02OutboundSeq}");
        }

        public void SwitchToProfile(uint slotIndex, MultiStreamProfile? newProfile)
        {
            bool emitted = SendDashboardSwitch(slotIndex);
            if (newProfile != null) Profile = newProfile;
            if (!emitted) return;

            if (EnableHotRenegotiation)
            {
                // Hot path: emit kind=4, queue N paced tier-def re-emissions
                // (matches PitHouse's 3-13 emissions ~1s apart). Sessions
                // 0x01/0x02/0x03 stay open. Preamble skipped because
                // _tierDefPreambleSent stays true.
                _hotSwitch.ArmBurst();
                MozaLog.Info(
                    $"[Moza] SwitchToProfile slot={slotIndex}: HOT path — " +
                    $"{Lifecycle.HotSwitchCoordinator.MinEmissions}-" +
                    $"{Lifecycle.HotSwitchCoordinator.MaxEmissions} tier-def emissions queued " +
                    $"~{Lifecycle.HotSwitchCoordinator.EmissionSpacingMs}ms apart (adaptive on bind state)");
            }
            else
            {
                MozaLog.Info(
                    $"[Moza] SwitchToProfile slot={slotIndex}: STOP+START path " +
                    $"(EnableHotRenegotiation=false)");
                RestartForSwitch();
            }
        }

        /// <summary>
        /// Stop+Start cycle for dashboard switches. Used when the kind=4 has
        /// already been sent by the caller (UI knob in MozaWheelSettingsControl,
        /// or auto-test via SwitchToProfile) — we just need to rebind our
        /// session state to match the new dashboard. The wheel's ~10–14s
        /// internal sess=0x09 timeout is the gate on re-engagement; the
        /// silence enforcement inside <see cref="StartInner"/> (via
        /// SilenceGate, which Stop arms unconditionally) handles that
        /// automatically — no need for explicit Sleep here.
        ///
        /// PreStopDrainMs: critical. The caller's FF kind=4 frame is in the
        /// one-shot queue when this runs; Stop's <c>FlushPendingWrites</c>
        /// would discard it before the TX thread writes it to the wire
        /// (symptom: "wheel doesn't even switch dashboards visually"). Sleep
        /// first to let the queue drain naturally — ~300ms covers the queued
        /// kind=4 plus any other in-flight one-shot frames.
        /// </summary>
        public void RestartForSwitch()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Let the caller's kind=4 (and any other in-flight one-shot
                    // frames) actually transmit before Stop's FlushPendingWrites
                    // discards the queue.
                    System.Threading.Thread.Sleep(PreStopDrainMs);
                    Stop();   // CloseHostSessions (01/02/03)
                    Start();  // StartInner enforces SilenceGate.StopReopenSilenceMs gate before opening
                }
                catch (Exception ex)
                {
                    MozaLog.Error($"[Moza] RestartForSwitch failed: {ex.Message}");
                }
            });
        }

        /// <summary>Convenience: push display standby timeout in minutes (converts to ms).</summary>
        public void SendDashDisplayStandbyMinutes(int minutes)
        {
            if (minutes < 1) minutes = 1;
            ulong ms = (ulong)minutes * 60_000UL;
            SendSessionPropertyU64(
                global::MozaPlugin.Protocol.SessionPropertyPushBuilder.KindDashStandbyMs,
                ms);
        }

        /// <summary>
        /// Drop queued and in-flight writes on the serial connection. Exposed so
        /// the UI Test Stop button can halt wire traffic immediately even when
        /// the sender itself is left running (telemetry remains enabled).
        /// </summary>
        public void FlushPendingOutput() => _connection.FlushPendingWrites();

        /// <summary>
        /// Stop the tick timer without tearing down session state. Use for UI
        /// Test Stop so the wheel goes quiet immediately; call Resume to kick
        /// the timer back on. Full Stop() is the destructive teardown path.
        /// </summary>
        public void Pause() => _sendTimer?.Stop();

        /// <summary>Re-enable a paused tick timer. No-op if never started.</summary>
        public void Resume() => _sendTimer?.Start();

        // ── Port probing ────────────────────────────────────────────────────

        /// <summary>
        /// Open management + telemetry sessions PitHouse-style: directly open
        /// session 0x01 (mgmt) and 0x02 (telem) rather than probing 48 ports.
        ///
        /// Why this isn't a probe loop: PitHouse never probes. It opens 0x01/0x02
        /// after a power-cycle and relies on them. The old 48-port probe existed
        /// to co-exist with a concurrent PitHouse instance, but SimHub + PitHouse
        /// can't share the serial port anyway, and the burst of 96 close+open
        /// frames at 4ms pacing saturated the write queue for 4s. During that
        /// window the <see cref="MozaPlugin.PollStatus"/> watchdog (2s interval,
        /// 3-miss threshold) would fire mid-handshake and reset the wheel state,
        /// looping forever before telemetry could start.
        ///
        /// Pre-probe close is targeted to host-managed sessions only (0x01..0x03):
        /// if the previous SimHub instance crashed without sending end markers,
        /// the wheel firmware still holds those sessions as open and a fresh
        /// SendSessionOpen would be ignored. We close just enough to reclaim
        /// the host-managed slots.
        ///
        /// Wheel-managed sessions (0x04..0x0a) are LEFT ALONE during a plugin
        /// reload (game switch) — wheel device-inits these to push state
        /// (0x05/0x07 file-transfer ack, 0x09 configJson state, 0x0a RPC).
        /// Closing them severs wheel-side state and prevents the wheel from
        /// re-pushing configJson on session 0x09 — without that handshake the
        /// dashboard never renders. Pithouse never closes these sessions
        /// mid-session either; verified in
        /// usb-capture/ksp/mozahubstartup.pcapng (no host close-burst, wheel
        /// device-inits 0x09 t=28.123 after host primes it with data on 0x09
        /// at t=2.345 / 6.346).
        ///
        /// On a COLD START (fresh SimHub process — see
        /// <paramref name="isColdStart"/>), we DO close the full host-touchable
        /// range 0x01..0x0a. The wheel may still be holding sessions open from
        /// a prior SimHub instance that exited without a clean Stop(), and on
        /// CS-Pro / KS-Pro that stale state silently swallows our session-open
        /// frames until manual user intervention (toggling the plugin
        /// "Connection enabled" off and on, which closes the port and forces
        /// the wheel to reset). The wider close on cold start mimics that
        /// recovery proactively. It re-pushes configJson on reconnect because
        /// the wheel re-emits its state burst once we send a fresh open
        /// request — the cold-start path always runs through that burst
        /// anyway, so there's no handshake to disturb.
        /// </summary>
        private void ProbeAndOpenSessions(bool isColdStart)
        {
            if (!_connection.IsConnected)
                return;

            const byte MgmtSession = 0x01;
            const byte TelemSession = 0x02;
            const int OpenAckTimeoutMs = 500;
            const int CloseAckTimeoutMs = 500;

            // Reclaim any sessions left open by a prior SimHub crash/kill.
            // - Cold start in a fresh SimHub process: close 0x01..0x0A (wide).
            //   The wide range covers host-managed slots PLUS wheel-managed
            //   ones (0x04..0x0a) that the prior process may have left
            //   half-engaged. CS-Pro / KS-Pro have been observed to silently
            //   ignore fresh opens with that residual state in place; manually
            //   toggling the plugin off/on recovers it because that path
            //   closes the OS serial port and forces the wheel to drop its
            //   sessions. The wide close emulates that recovery without
            //   needing user intervention.
            // - Plugin reload mid-SimHub (game switch via persistent wire):
            //   close 0x01..0x03 only. Wheel-managed 0x04..0x0a are kept
            //   intact so the configJson handshake (sess=0x09) stays bound —
            //   if we closed it the wheel would need to re-emit its full
            //   configJson burst and the dashboard would re-render. Verified
            //   safe behaviour matches PitHouse's reload pattern.
            //
            // TryCloseSession waits up to 500ms for the fc:00 ack: when the
            // wheel acks, the close has definitively been processed and we
            // can re-open against a clean state immediately. Silent closes
            // are accepted and we proceed regardless.
            byte lastClosePort = isColdStart ? (byte)0x0A : (byte)0x03;
            MozaLog.Debug(
                $"[Moza] Closing any stale {(isColdStart ? "cold-start" : "host")} sessions " +
                $"(0x01..0x{lastClosePort:X2}{(isColdStart ? " — wide" : "")})...");
            for (byte port = 1; port <= lastClosePort; port++)
            {
                if (!_connection.IsConnected) return;
                bool acked = TryCloseSession(port, CloseAckTimeoutMs);
                MozaLog.Debug(
                    $"[Moza] SessionClose 0x{port:X2} {(acked ? "acked" : "no ack within " + CloseAckTimeoutMs + "ms")}");
            }

            byte mgmtPort = TryOpenSession(MgmtSession, OpenAckTimeoutMs);
            if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
            byte telemetryPort = TryOpenSession(TelemSession, OpenAckTimeoutMs);

            // Slow-bring-up hardware (CS-Pro on Universal Hub): wheel takes
            // 12-15 s to first ack a session-control frame after device-lock.
            // If both opens stayed silent within the 500 ms budget, block here
            // for the engagement signal before letting the rest of cold-start
            // (hub enum, session 0x09 prime, tier-def emission, etc.) blast
            // state into a wheel that hasn't woken yet. Health wheels never
            // hit this branch; for CS-Pro the wheel eventually fc:00-acks
            // sess=0x02 (~14 s on the 0.9.3-dev capture) and we proceed from
            // a known-awake state. Pairs with SessionWatchdogManager's 20 s
            // sess=0x01 grace — the watchdog catches the rarer case where the
            // wheel acks something but never engages sess=0x01 specifically.
            if (mgmtPort == 0 && telemetryPort == 0 && _connection.IsConnected)
            {
                const int ExtendedAckWaitMs = 20_000;
                MozaLog.Info(
                    $"[Moza] Both sess=0x{MgmtSession:X2}/0x{TelemSession:X2} opens silent within " +
                    $"{OpenAckTimeoutMs}ms — waiting up to {ExtendedAckWaitMs}ms for slow-bring-up " +
                    "wheel (CS-Pro on Universal Hub takes ~14 s)");
                bool gotLateAck;
                _wheelReadyObserved = false;
                try { _ackReceived.Reset(); }
                catch (ObjectDisposedException) { return; }
                try { gotLateAck = _ackReceived.Wait(ExtendedAckWaitMs); }
                catch (ObjectDisposedException) { return; }
                if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                // Belt-and-suspenders: even if the Wait timed out, the
                // dispatcher may have flipped _wheelReadyObserved in the
                // microseconds between timeout-fire and our read. Honour it.
                byte ackedSession = _lastAckedSession;
                bool wheelReadyWake = _wheelReadyObserved
                    && ackedSession != MgmtSession
                    && ackedSession != TelemSession;
                if (wheelReadyWake)
                {
                    // Wheel just came online via sess=0x09 device-init in
                    // response to our nudge. Its initial sess=0x01/0x02 opens
                    // (sent before the wheel's session layer was up) were
                    // dropped on the wheel side — re-issue both with a wider
                    // budget now that the wheel is demonstrably listening.
                    // Verified 2026-05-25 W17 hot-attach: first fc:00 on
                    // sess=0x02 follows within ~1 s of a fresh open frame
                    // from the wheel-ready point.
                    const int WheelReadyRetryMs = 2_000;
                    MozaLog.Info(
                        "[Moza] Wheel session-layer ready observed (sess=0x09 device-init) — " +
                        $"retrying sess=0x{MgmtSession:X2}/0x{TelemSession:X2} opens with " +
                        $"{WheelReadyRetryMs}ms budget");
                    if (_connection.IsConnected)
                        mgmtPort = TryOpenSession(MgmtSession, WheelReadyRetryMs);
                    if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                    if (_connection.IsConnected)
                        telemetryPort = TryOpenSession(TelemSession, WheelReadyRetryMs);
                }
                else if (gotLateAck)
                {
                    MozaLog.Info(
                        $"[Moza] Late ack on sess=0x{ackedSession:X2} after extended wait " +
                        "— wheel is alive, proceeding with cold-start");
                    if (ackedSession == MgmtSession) mgmtPort = MgmtSession;
                    else if (ackedSession == TelemSession) telemetryPort = TelemSession;
                    // Retry the still-unacked side with a 1 s budget — wheel
                    // should ack promptly now that it's awake. CS-Pro famously
                    // never acks sess=0x01, so the mgmt retry will time out
                    // and we fall through to the MgmtSession default below.
                    if (mgmtPort == 0 && _connection.IsConnected)
                        mgmtPort = TryOpenSession(MgmtSession, 1_000);
                    if (telemetryPort == 0 && _connection.IsConnected)
                        telemetryPort = TryOpenSession(TelemSession, 1_000);
                }
                else
                {
                    MozaLog.Warn(
                        $"[Moza] No ack within {ExtendedAckWaitMs}ms extended wait — " +
                        "proceeding with defaults; session watchdog will retry post-Active. " +
                        "If this recurs after a SimHub restart, the cold-start wide close " +
                        "(0x01..0x0a above) should already have cleared any stale wheel " +
                        "session state — escalate via wire trace if the wedge persists.");
                }
            }

            _mgmtPort = mgmtPort;

            // Session-open frames use seq=port. Data chunks must start
            // AFTER the open seq. PitHouse bridge capture shows first
            // session 0x02 data at seq=4 (not 2). Initialize outbound
            // seq counters so SendSessionPropertyBody (Math.Max(2, seq))
            // and SendTierDefinition (Math.Max(2, seq+1)) produce
            // correct first-use values.
            _session02OutboundSeq = TelemSession + 1; // port=2 → first data seq=3

            if (telemetryPort != 0)
            {
                FlagByte = telemetryPort;
                MozaLog.Debug(
                    $"[Moza] Sessions opened: mgmt=0x{mgmtPort:X2} telem=0x{telemetryPort:X2}");
            }
            else if (mgmtPort != 0)
            {
                FlagByte = mgmtPort;
                MozaLog.Warn(
                    $"[Moza] Telem session 0x{TelemSession:X2} did not ack, using mgmt 0x{mgmtPort:X2} for telemetry");
            }
            else
            {
                // No acks — proceed anyway using PitHouse defaults. Real wheels
                // may silently accept data on 0x02 even without an explicit ack.
                FlagByte = TelemSession;
                MozaLog.Warn(
                    "[Moza] No session acks received, proceeding with defaults mgmt=0x01 telem=0x02");
                _mgmtPort = MgmtSession;
            }
        }

        /// <summary>
        /// Send a SESSION_OPEN for the given session byte and wait up to
        /// <paramref name="timeoutMs"/> for a matching fc:00 ack. Returns the
        /// session byte on success, 0 on timeout.
        ///
        /// Single-attempt: an earlier revision added a 3× retry loop with
        /// Thread.Sleep between attempts, but every Reset()/Wait() on
        /// <see cref="_ackReceived"/> opened a window where <see cref="Dispose"/>
        /// running on the UI thread (SimHub plugin teardown / game-switch
        /// reload) could dispose the event mid-Wait, throwing
        /// <see cref="ObjectDisposedException"/> out of the bg StartInner
        /// thread up through <see cref="ThreadPool.QueueUserWorkItem"/> —
        /// unhandled in .NET Framework 4.8 plugin hosts. The retry was also
        /// speculative: under normal conditions the wheel acks promptly, and
        /// genuine drops cause the wheel itself to retransmit its own opens.
        ///
        /// The wheel's echoed ack_seq is parsed (<see cref="_lastAckedSeq"/>)
        /// for diagnostic logging but not used to gate the open — firmware
        /// variants legitimately echo non-matching seqs and rejecting those
        /// breaks disable+re-enable recovery in the field.
        /// </summary>
        internal byte TryOpenSession(byte session, int timeoutMs)
        {
            try { _ackReceived.Reset(); } catch (ObjectDisposedException) { return 0; }
            _lastAckedSession = 0;
            _lastAckedSeq = -1;

            SendSessionOpen(session, session);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) return 0;

                bool gotSignal;
                try { gotSignal = _ackReceived.Wait(remaining); }
                catch (ObjectDisposedException) { return 0; }
                if (!gotSignal) return 0;

                if (_lastAckedSession == session)
                {
                    int gotAckSeq = _lastAckedSeq;
                    if (gotAckSeq != -1 && gotAckSeq != session)
                    {
                        MozaLog.Debug(
                            $"[Moza] OpenSession 0x{session:X2}: ack_seq={gotAckSeq} " +
                            $"(expected {session}); accepting (firmware may use own port counter)");
                    }
                    return session;
                }

                // Stale ack (different session) — discard and keep waiting.
                MozaLog.Debug(
                    $"[Moza] OpenSession 0x{session:X2}: ignoring stale ack for 0x{_lastAckedSession:X2}");
                try { _ackReceived.Reset(); } catch (ObjectDisposedException) { return 0; }
                _lastAckedSession = 0;
            }
        }

        /// <summary>
        /// Send a SessionClose for the given session and wait up to
        /// <paramref name="timeoutMs"/> for the matching fc:00 ack. Returns
        /// true on ack, false on timeout. Reuses the same ack path as
        /// <see cref="TryOpenSession"/> — the wheel signals close
        /// acceptance with fc:00 [session] just as it does for open
        /// acceptance.
        ///
        /// Best-effort: a timeout is NOT fatal. Callers proceed with the
        /// subsequent open regardless; firmwares that omit close-acks
        /// degrade to the prior blind-blast behavior.
        /// </summary>
        internal bool TryCloseSession(byte session, int timeoutMs)
        {
            try { _ackReceived.Reset(); } catch (ObjectDisposedException) { return false; }
            _lastAckedSession = 0;
            _lastAckedSeq = -1;

            SendSessionClose(session);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) return false;

                bool gotSignal;
                try { gotSignal = _ackReceived.Wait(remaining); }
                catch (ObjectDisposedException) { return false; }
                if (!gotSignal) return false;

                if (_lastAckedSession == session) return true;

                // Stale ack for a different session — discard and keep waiting.
                try { _ackReceived.Reset(); } catch (ObjectDisposedException) { return false; }
                _lastAckedSession = 0;
            }
        }

        /// <summary>
        /// Wait for the wheel's pre-tier-def channel registration burst to stop
        /// arriving. Polls <see cref="ChannelCatalogParser.LastActivityMs"/> — once the
        /// last activity is older than <paramref name="quietMs"/>, we assume the
        /// wheel is done pushing its channel URLs.
        /// </summary>
        
        /// <summary>
        /// Build a new <see cref="MultiStreamProfile"/> with only the channels
        /// whose <c>Url</c> appears in <paramref name="catalog"/>. Tiers that
        /// end up empty are dropped. URL match is case-insensitive and also
        /// accepts catalog entries matching the last path segment (the wheel
        /// sometimes advertises bare names where the profile uses a full URL).
        /// </summary>
        
        /// <summary>
        /// Build a V0 subscription profile from the wheel's full channel catalog.
        /// Each catalog URL becomes a channel; metadata (compression, SimHub
        /// property/field, scale) is borrowed from the host's profile when a
        /// URL match exists, otherwise sane defaults (uint32_t, Zero field) are
        /// applied so the channel is still subscribed and value frames go out.
        /// Single tier at the host profile's base PackageLevel — V0 firmware
        /// resolves per-channel update cadence internally so per-tier scheduling
        /// is irrelevant.
        /// </summary>
        
        /// <summary>
        /// PitHouse byte-faithful tier-channel transform: for each unique
        /// Channels list in the profile, REMOVE channels whose URLs aren't
        /// in the wheel's catalog (chIdx would be 0 → tier-def can't
        /// reference them) and SORT the remaining channels by catalog idx
        /// ascending. Recomputes <c>tier.TotalBits</c>/<c>tier.TotalBytes</c>
        /// so downstream FrameBuilders size their bit-pack buffers
        /// correctly.
        ///
        /// MultiStreamProfile's tier-broadcast expansion shares Channels
        /// lists by reference across tier replicas, so mutating each unique
        /// list once propagates to every replica.
        ///
        /// IMPORTANT: this method does NOT touch <see cref="_tiers"/> or
        /// rebuild FrameBuilders — callers do that explicitly. Two call
        /// sites:
        ///   * Profile setter (pre-FrameBuilder-construction): mutate the
        ///     incoming profile in-place so the immediately-following
        ///     `new TelemetryFrameBuilder(tier, …)` loop builds sized-
        ///     correctly buffers against the filtered Channels list.
        ///   * ApplySubscription (post-catalog-grow): mutate the live
        ///     profile and then rebuild <see cref="_tiers"/> Builders via
        ///     <see cref="RebuildFrameBuildersFromProfile"/>.
        /// </summary>
        
        /// <summary>
        /// Rebuild per-tier FrameBuilders from <see cref="_profile"/>.
        /// Called after <see cref="SortTierChannelsByCatalogIdx"/> mutates
        /// the live profile post-Profile-setter (e.g. ApplySubscription
        /// after catalog growth). Safe to call when _tiers is null/empty.
        /// </summary>
        
        /// <summary>
        /// Single point of entry for (re-)subscribing to the wheel's channel
        /// catalog. Swaps the active profile to match the catalog, sends
        /// tier-def + channel config, and atomically publishes the new
        /// subscription state for the telemetry tick handler.
        /// </summary>
        /// <param name="isDashSwitch">True when switching dashboards (advance
        /// flag counter). False for initial connect (reset to 0).</param>
        internal void ApplySubscription(bool force)
        {
            // First-call era resolution. Auto-mode picks Era2024/2025/2026
            // here based on the wheel's catalog push (or absence thereof) and
            // its identity probe. After this returns, _policy is the final
            // policy used for tier-def emission and value-frame routing.
            ResolveAutoPolicy();

            MaybeSwapProfileForCatalog(force: force);
            if (_profile == null || _profile.Tiers.Count == 0)
                return;

            // Note: don't defer on missing URLs. Wheel only pushes the new
            // dashboard's URL→idx mapping AFTER it sees the plugin's tier-def
            // (presumably as a correction). Deferring creates deadlock: wheel
            // waits for tier-def, plugin waits for catalog (verified
            // moza-wire 164047 — post-FF wheel sent only end-marker 06 04 ...
            // val=8 until plugin emitted, then nothing came back). Send with
            // whatever catalog we have; renegotiate-on-grow re-emits when
            // wheel pushes corrected mappings.

            // Preamble is one-shot per session (captures: bridge-20260503-*).
            // Don't reset _tierDefPreambleSent here — session start handles it.
            _retransmitter.Clear();
            _propertyPushQueue.Clear();
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = 0;

            _tierDefEmitter.SendTierDefinition();
            SendChannelConfig();

            int chCount = 0;
            foreach (var t in _profile.Tiers) chCount += t.Channels.Count;
            MozaLog.Debug(
                $"[Moza] Subscription applied: \"{_profile.Name}\" " +
                $"{chCount}ch/{_profile.Tiers.Count}t " +
                $"catalog={_catalogParser.Count}");
        }

        /// <summary>
        /// One-shot auto-era resolution. When the user picked
        /// <see cref="MozaWheelEra.Auto"/>, walk the available signals and
        /// replace the provisional policy with a pinned one. Idempotent:
        /// guarded by <see cref="_autoResolutionDone"/> so subsequent
        /// dashboard-switch re-applications don't re-resolve mid-session.
        /// </summary>
        /// <remarks>
        /// Decision order (per plan §3):
        ///   1. <c>_catalogParser.Catalog</c> non-empty → Era2026 (catalog push
        ///      is the strongest signal that the wheel speaks Type02).
        ///   2. <c>EraPolicy.GuessFromWheelModel(WheelModelName)</c> hits →
        ///      use that.
        ///   3. Default to Era2025 (most-likely VGS-class wheel, matches 0.8.0
        ///      working behavior for users with no catalog and no model match).
        /// </remarks>
        private void ResolveAutoPolicy()
        {
            if (!_policy.IsAuto) return;
            if (_autoResolutionDone) return;
            _autoResolutionDone = true;

            MozaWheelEra resolved;
            string reason;
            int catalogCount = _catalogParser.Count;
            if (IsStandaloneDashboardTarget)
            {
                // CM2 standalone dashboard runs Type02-era firmware (verified by
                // dev_id=0x12 / group=0x32 protocol surface). Without this pin,
                // the policy resolver waits for a wheel catalog that never arrives.
                resolved = MozaWheelEra.Era2026;
                reason = $"standalone dashboard target {TargetDescription}";
            }
            else if (catalogCount > 0)
            {
                resolved = MozaWheelEra.Era2026;
                reason = $"wheel-catalog={catalogCount}";
            }
            else
            {
                string modelName = MozaPlugin.Instance?.Data?.WheelModelName ?? "";
                var guess = EraPolicy.GuessFromWheelModel(modelName);
                if (guess.HasValue)
                {
                    resolved = guess.Value;
                    reason = $"wheel-model=\"{modelName}\"";
                }
                else
                {
                    resolved = MozaWheelEra.Era2025;
                    reason = $"default (no catalog, model=\"{modelName}\" unmatched)";
                }
            }

            var newPolicy = EraPolicy.For(resolved);
            // Preserve the Auto-mark so the upload-wire-format fallback stays
            // available even after resolving. The wheel may accept tier-def
            // under one wire format and need the other for the dashboard
            // upload (different sub-msg layouts on different boards).
            newPolicy.IsAuto = true;
            if (resolved == MozaWheelEra.Era2026)
                newPolicy.AutoFallbackUploadWireFormat = true;

            _policy = newPolicy;
            MozaLog.Info($"[Moza] Auto era resolved → {resolved} ({reason})");
        }

        /// <summary>
        /// Send the tier definition message on the telemetry session.
        /// This is the critical config data that tells the wheel firmware how to
        /// decode each flag byte's bit-packed telemetry data: which channels are
        /// in each tier, their compression codes, and bit widths.
        ///
        /// Pithouse sends this as 7c:00 data chunks (type=0x01) on session 0x02
        /// during the first ~1s after session open. Without it, the wheel silently
        /// ignores all 7d:23 telemetry frames.
        /// </summary>
        
        /// <summary>
        /// Swap the active <see cref="Profile"/> for one synthesized from the
        /// wheel's advertised channel catalog when the Type02 wire format is in
        /// use. Removes the mzdash dependency on the subscription axis — plugin
        /// subscribes to whatever the wheel declared and feeds those channels
        /// from SimHub via the URL→property mapping in
        /// <see cref="DashboardProfileStore"/>. No-op when catalog isn't parsed
        /// yet, when era isn't Type02, or when the synthesized profile would be
        /// empty (e.g. wheel advertised zero channels).
        /// </summary>
        /// <remarks>
        /// Public entry point for <see cref="MozaPlugin.ApplyTelemetrySettings"/>
        /// to swap immediately after setting Profile, so the UI reads the
        /// catalog-based channel list instead of the builtin's fixed set.
        /// </remarks>
        public void SwapProfileForCatalogIfType02() => MaybeSwapProfileForCatalog(force: true);

        private void MaybeSwapProfileForCatalog(bool force = false)
        {
            // Fallback path: synthesise a profile from the wheel-advertised
            // channel catalog when no mzdash-derived profile is loaded.
            // Existing mzdash flow (Profile != null after ApplyTelemetrySettings)
            // takes precedence — this hook does nothing when the user has a
            // local mzdash folder configured. For users with no folder
            // configured, this lets telemetry flow from whatever the wheel
            // declared in its tag=0x04 catalog on b2h sess=0x01.
            //
            // The earlier disable (clobbered user-selected profile, dropped
            // channels post-switch on incomplete back-ref catalog) doesn't
            // apply: (a) we only synthesise when _profile is null, so a
            // user-selected mzdash profile is never replaced; (b) we gate
            // on LastWheelEndMarker != 0 so the wheel has committed to a
            // tier-def generation before we build — back-ref-only catalogs
            // (END marker still 0) defer until the wheel emits full URLs.
            bool currentIsSynthesised =
                _profile != null && _profile.Name == CatalogProfileName;

            // User-loaded mzdash profile: never replace.
            if (_profile != null && _profile.Tiers.Count > 0 && !currentIsSynthesised)
                return;

            // Prefer LiveCatalog (the wheel's currently-loaded dashboard's
            // idxs only, with stale prior-dash slots blanked) so dashboard
            // switches produce a profile sized to the new dash. Fall back
            // to Catalog on cold start before the first END-marker commit.
            var catalog = _catalogParser.LiveCatalog ?? _catalogParser.Catalog;
            if (catalog == null || catalog.Count == 0)
                return;

            if (_catalogParser.LastWheelEndMarker == 0)
                return;

            // Re-synthesis trigger: catalog count grew OR wheel committed a new
            // tier-def generation (END marker bumped — happens on dashboard
            // switch). Skip when neither moved.
            if (currentIsSynthesised
                && _catalogCountAtSynthesis == catalog.Count
                && _catalogEndMarkerAtSynthesis == _catalogParser.LastWheelEndMarker
                && !force)
                return;

            var store = MozaPlugin.Instance?.DashProfileStore;
            if (store == null)
                return;

            MultiStreamProfile synthesised;
            try
            {
                synthesised = store.BuildProfileFromCatalog(catalog, CatalogProfileName);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Catalog-only profile synthesis failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (synthesised?.Tiers == null || synthesised.Tiers.Count == 0)
                return;

            // Apply user channel overrides (same selector the binding
            // coordinator uses for the mzdash path). DashboardBindingCoordinator
            // skipped this branch when profile was null at apply time, so the
            // synthesised path owns it. Resolved per active dashboard key
            // candidate (wheel:<id> > file:<name>:<sha> > builtin:<name>).
            var plugin = MozaPlugin.Instance;
            int mappedCount = 0;
            if (plugin != null)
            {
                var channelMap = plugin.GetActiveChannelMappings();
                if (channelMap != null)
                {
                    foreach (var dashKey in plugin.GetActiveDashboardKeyCandidates())
                    {
                        if (channelMap.TryGetValue(dashKey, out var overrides) && overrides != null)
                        {
                            DashboardProfileStore.ApplyUserMappings(synthesised, overrides);
                            mappedCount = overrides.Count;
                            break;
                        }
                    }
                }
            }

            int chCount = 0;
            foreach (var t in synthesised.Tiers) chCount += t.Channels.Count;
            MozaLog.Info(
                $"[Moza] Synthesised catalog-only profile: {chCount}ch in {synthesised.Tiers.Count}t " +
                $"+ {synthesised.StringChannels.Count} strings (catalog={catalog.Count}, " +
                $"endMarker={_catalogParser.LastWheelEndMarker}, userMappings={mappedCount})");

            _catalogEndMarkerAtSynthesis = _catalogParser.LastWheelEndMarker;
            _catalogCountAtSynthesis = catalog.Count;

            // Going through the Profile setter so multi-broadcast expansion
            // and _tiers allocation match the mzdash path exactly. The setter
            // preserves Name, so subsequent calls can detect the synthesised
            // profile by Profile.Name == CatalogProfileName.
            Profile = synthesised;

            // Notify the UI that the channel set has changed. The
            // DashboardSelectionChanged event is normally raised at the
            // moment the switch is INITIATED (by either the dropdown handler
            // or OnWheelInitiatedSwitch), but at that point sender.Profile
            // is either null or the prior dash's profile — so the channel-
            // mapping grid populates with stale data and waits on the 500 ms
            // RefreshTelemetryStatus polling tick to notice the new synth.
            // Hash collisions in ComputeMappingDataSignature (24-bit hash +
            // count masks) or a second switch arriving inside the 500 ms
            // window can leave the grid stuck on the prior dash's channels.
            // Raising here means the UI re-runs PopulateChannelMappingList
            // against the live Profile deterministically. The early return
            // at the top of MaybeSwapProfileForCatalog (catalog count +
            // endMarker dedup) ensures this only fires when the wheel
            // actually advanced state, not on every timer tick.
            //
            // Previously rolled back when the wheel was independently
            // wedged at startup, but the wedge had a different root cause
            // (sess=0x01/0x02 not engaging — now handled by the hard-
            // recovery path in ProbeAndOpenSessions). Restoring with the
            // same gating: only fires after a real catalog/endMarker advance.
            MozaPlugin.Instance?.RaiseDashboardSelectionChangedInternal();
        }

        /// <summary>
        /// Probe the Display sub-device inside the wheel.
        /// Pithouse sends the same identity commands used for the main wheel
        /// (0x09, 0x04, 0x06, 0x02, 0x05) but via group 0x43 to route them
        /// through the SerialStream to the Display sub-module.
        ///
        /// Responses arrive asynchronously via OnMessageDuringPreamble:
        /// - 0x87 data=01 "Display" → model name (confirms display present)
        /// - 0x89 data=00:01 → presence check (1 sub-device)
        /// - 0x82 data=02 → product type
        /// </summary>

        /// <summary>
        /// Push an empty-state tile-server blob on session 0x03. Matches
        /// PitHouse behaviour observed in 5 captures — PitHouse sends this on
        /// every connect; wheel never pushes back (session 0x03 is host→wheel
        /// only). Envelope is the 12-byte variant (distinct from session
        /// 0x04/0x09 9-byte form). See § Session 0x03 tile-server envelope.
        /// </summary>
        private void SendTileServerState()
        {
            try
            {
                byte[] json = TileServerStateBuilder.BuildEmptyStateJson();
                byte[] payload = TileServerStateBuilder.BuildFullBlob(json);
                int seq = 1;
                var frames = TierDefinitionBuilder.ChunkMessage(payload, 0x03, ref seq, _targetDeviceId);
                foreach (var frame in frames)
                    _connection.Send(frame);
                MozaLog.Debug(
                    $"[Moza] Sent empty tile-server state on session 0x03: " +
                    $"{json.Length}B JSON → {payload.Length}B (12B env + zlib) → " +
                    $"{frames.Count} chunk(s)");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] SendTileServerState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fire once per session: reply to the wheel's configJson state blob
        /// with a <c>configJson()</c> canonical library list. Wheel uses this
        /// to refresh its <c>configJsonList</c> field, which PitHouse reads
        /// back from <see cref="WheelDashboardState.ConfigJsonList"/>. Sent
        /// on the SAME session the wheel pushed state on (older firmware =
        /// 0x09; KS Pro / 2026-04+ firmware = 0x0a per
        /// usb-capture/ksp/mozahubstartup.pcapng OUT seq=0x0010..0x0017,
        /// decompressed: <c>{"configJson()":{"dashboards":[...]},"id":11}</c>).
        /// </summary>
        private void MaybeSendConfigJsonReply(WheelDashboardState state, byte session)
        {
            if (_session09ReplySent) return;
            if (CanonicalDashboardList == null || CanonicalDashboardList.Count == 0)
            {
                // Fall back to whatever the wheel currently reports — that
                // way the wheel's configJsonList survives at least one more
                // connect cycle unchanged. Skip if wheel sent nothing.
                if (state.ConfigJsonList == null || state.ConfigJsonList.Count == 0) return;
                CanonicalDashboardList = state.ConfigJsonList;
            }

            byte[] reply = ConfigJsonClient.BuildConfigJsonReply(CanonicalDashboardList);
            int chunkCount;
            // Hold _session09SeqLock across the entire read-chunk-send-write
            // so SendSession09Keepalive (timer thread) can't slip a ++seq in
            // mid-emission and break the wheel's gap detector. Early-return
            // on disconnect still persists the partial advance — any frames
            // already on the wire have consumed those seqs wheel-side.
            lock (_session09SeqLock)
            {
                int seq = _session09OutboundSeq + 1;
                var frames = TierDefinitionBuilder.ChunkMessage(reply, session, ref seq, _targetDeviceId);
                chunkCount = frames.Count;
                foreach (var frame in frames)
                {
                    if (_state == TelemetryState.Idle || !_connection.IsConnected)
                    {
                        _session09OutboundSeq = seq;
                        return;
                    }
                    _connection.Send(frame);
                }
                _session09OutboundSeq = seq;
            }
            _session09ReplySent = true;
            MozaLog.Debug(
                $"[Moza] Sent configJson() reply on session 0x{session:X2}: " +
                $"{CanonicalDashboardList.Count} dashboards, {chunkCount} chunks");
        }

        /// <summary>
        /// Check cache for missing dashboard hashes and trigger background download
        /// via session 0x0B if needed. Called when a new WheelDashboardState arrives.
        /// </summary>
        private void MaybeTriggerDashboardDownload(WheelDashboardState state)
        {
            var cache = _dashboardCache;
            var downloader = _dashboardDownloader;
            if (cache == null) return;

            // Always update the name→hash mapping so TryGetByName works
            // even when downloads are disabled or already triggered.
            var missing = cache.UpdateFromWheelState(state);

            if (downloader == null || _dashboardDownloadTriggered) return;
            if (missing.Count == 0) return;

            _dashboardDownloadTriggered = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int ingested = downloader.Execute(state, missing);
                    if (ingested > 0)
                        MozaLog.Debug(
                            $"[Moza] Dashboard download complete: {ingested} dashboards cached");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] Dashboard download failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Send a host→wheel JSON RPC call on session 0x0a and wait for the wheel's
        /// reply. Thin pass-through to <see cref="RpcCallChannel.Call"/> — kept on
        /// TelemetrySender so external callers (Settings UI, completelyRemove flow)
        /// don't need to know about the helper.
        /// </summary>
        public byte[]? SendRpcCall(string method, object arg, int timeoutMs = 2000)
        {
            if (Volatile.Read(ref _disposed) != 0) return null;
            return _rpc.Call(method, arg, timeoutMs);
        }

        private void SendSessionEnd(byte session, ushort seq)
        {
            var end = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, _targetDeviceId,
                0x7C, 0x00,
                session, 0x00,
                (byte)(seq & 0xFF), (byte)((seq >> 8) & 0xFF),
                0x00
            };
            end[end.Length - 1] = MozaProtocol.CalculateWireChecksum(end);
            _connection.Send(end);
        }

        private void SendDisplayProbe()
        {
            if (!_connection.IsConnected) return;

            // Heartbeat/ping first
            _connection.Send(BuildDisplayFrame(0x00));

            // Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05
            _connection.Send(BuildDisplayFrame(0x09));
            _connection.Send(BuildDisplayFrameWithData(0x04, new byte[] { 0x00, 0x00, 0x00, 0x00 }));
            _connection.Send(BuildDisplayFrame(0x06));
            _connection.Send(BuildDisplayFrameWithData(0x02, new byte[] { 0x00 }));
            _connection.Send(BuildDisplayFrameWithData(0x05, new byte[] { 0x00, 0x00, 0x00, 0x00 }));

            // Version queries: 0x07, 0x0F, 0x11, 0x08, 0x10 (sub-device 1)
            _connection.Send(BuildDisplayFrameWithData(0x07, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x0F, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x11, new byte[] { 0x04 }));
            _connection.Send(BuildDisplayFrameWithData(0x08, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x10, new byte[] { 0x00 }));
        }

        private byte[] BuildDisplayFrame(byte cmd)
        {
            var frame = new byte[] { MozaProtocol.MessageStart, 0x01,
                MozaProtocol.TelemetrySendGroup, _targetDeviceId,
                cmd, 0x00 };
            frame[5] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        private byte[] BuildDisplayFrameWithData(byte cmd, byte[] data)
        {
            var frame = new byte[4 + 1 + data.Length + 1]; // start+N+grp+dev + cmd + data + checksum
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)(1 + data.Length); // N = cmd + data
            frame[2] = MozaProtocol.TelemetrySendGroup;
            frame[3] = _targetDeviceId;
            frame[4] = cmd;
            Array.Copy(data, 0, frame, 5, data.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        // ── Preamble message handling ───────────────────────────────────────

        /// <summary>
        /// Handle incoming messages during port probing and the ~1s preamble phase.
        /// Detects fc:00 session acks (for port probing) and acks incoming 7c:00
        /// channel data on the telemetry session.
        /// </summary>
        
        // ── Timer loop ──────────────────────────────────────────────────────

        // Re-entry guard. System.Timers.Timer fires Elapsed on the ThreadPool,
        // so a handler that overruns its interval gets concurrent invocations.
        // Without this, _tickCounter/_slowCounter all race and
        // non-coalesced one-shot frames (heartbeat, display_cfg) fire 2–3× the
        // intended rate. Stream-lane traffic is coalesced so it's immune, but
        // the counter races still skew scheduling. Drop overlapping ticks —
        // the missed tick's data is re-covered by the next tick's fresh
        // snapshot via the latest-wins stream slots.
        private int _tickInProgress;

        // Frame-build / tick failure escalation. The outer catch in
        // OnTimerElapsedInner used to swallow every exception as Warn — a
        // repeatable bug (null resolver entry, malformed channel def, …)
        // would freeze the dashboard with no recovery attempt. Streak counter
        // resets on the first successful tick body; once it hits the
        // threshold we hand off to RecoveryDispatcher (which has its own
        // debounce + rate-cap + park) and reset the counter so the same
        // flap doesn't immediately re-escalate inside the debounce window.
        private int _consecutiveTickFailures;
        private const int TickFailureRestartThreshold = 10;

        // Tracks whether the previous tick observed a live connection.
        // Used to detect the disconnected→connected transition so we can
        // forgive any prior recovery-budget exhaustion: a wheel that just
        // came back is observably a different situation than the one whose
        // earlier failures parked the pipeline.
        private bool _lastTickSawConnected;

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _tickInProgress, 1, 0) != 0)
                return;
            try
            {
                OnTimerElapsedInner();
            }
            finally
            {
                Interlocked.Exchange(ref _tickInProgress, 0);
            }
        }

        private void OnTimerElapsedInner()
        {
            bool currentlyConnected = _connection.IsConnected;
            bool wasConnected = _lastTickSawConnected;
            _lastTickSawConnected = currentlyConnected;
            if (currentlyConnected && !wasConnected)
            {
                // Reconnect transition: forgive any prior restart-budget
                // exhaustion / park. The wheel is observably back; if the
                // root cause is still present the watchdogs will re-escalate
                // from a clean budget.
                try { _recovery.Reset(); } catch { }
            }

            if (_state == TelemetryState.Idle || !currentlyConnected)
                return;

            try
            {
                // Preamble: ~1 second of heartbeats while the wheel acks our
                // session opens and pushes its initial catalog + state. No
                // telemetry, no value frames; once the tick countdown elapses
                // we transition to Active and fall into the steady-state path.
                if (_state == TelemetryState.Preamble)
                {
                    TickPreamble();
                    return;
                }

                // Steady-state (Active).
                TickAbsorbCatalogIfChanged();
                _autoTest?.Tick(_baseTickMs);

                // Catalog-only mode upkeep: when the user has no mzdash folder
                // configured, ApplyTelemetrySettings may have just wiped the
                // synthesised profile (passes profile=null because the file
                // it would have loaded doesn't exist). Re-synthesise from the
                // wheel-advertised catalog here so value-frame emission
                // doesn't stall after game/profile/dashboard switches. The
                // method early-returns when a real mzdash profile is loaded
                // and is a no-op when catalog state hasn't moved since the
                // last synthesis, so the cost is one ref + count compare in
                // the steady case.
                MaybeSwapProfileForCatalog();

                // Re-read _tiers: MaybeSwapProfileForCatalog may have rebuilt
                // them above (synthesised from wheel-advertised catalog when
                // no profile was loaded at Start time).
                var tiers = _tiers;
                if (tiers == null || tiers.Length == 0)
                    return;

                TickFireGameStartHandshake();
                TickEmitValueFrames(tiers);
                TickEmitStringValues();

                TickEmitEnableAndSequence();
                // Parity polls keep the wheel engaged during idle. Empirically
                // verified: turning them off entirely caused the dashboard to
                // freeze on last-value within ~5 min. ~1 Hz cadence is enough
                // at ~12% of PitHouse's full ~7-22 Hz wire cost.
                TickEmitPeripheralPolls();
                TickEmitLedStatePolls();
                TickEmitRetransmits();
                _tierDefEmitter.TickEmitTierDefBlindRetransmits();
                _watchdog.TickRetryS09IfNotEstablished();
                _watchdog.TickConfigJsonGapEscalation();
                _watchdog.TickConfigJsonStuckWatchdog();
                _watchdog.TickSession02EngagementWatchdog();
                _watchdog.TickSession01EngagementWatchdog();
                TickGrowSubscriptionIfCatalogStable();

                _tickCounter++;

                TickEmitWidgetPoll();
                TickEmitSlowPath();

                // Successful tick — clear the failure streak so a single
                // transient throw doesn't add to a stale earlier streak.
                _consecutiveTickFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveTickFailures++;
                // First throw of a streak: include the full stack so
                // post-mortem analysis has something to bite into. Later
                // throws in the same streak log only the message to keep
                // the ring buffer / log file from drowning.
                if (_consecutiveTickFailures == 1)
                    MozaLog.Warn($"[Moza] Telemetry send error: {ex.GetType().Name}: {ex}");
                else
                    MozaLog.Warn(
                        $"[Moza] Telemetry send error #{_consecutiveTickFailures}: " +
                        $"{ex.GetType().Name}: {ex.Message}");

                if (_consecutiveTickFailures >= TickFailureRestartThreshold)
                {
                    // Hand the restart decision to RecoveryDispatcher — its
                    // debounce + rate-cap keep this from looping forever if
                    // the bug is persistent (parks the pipeline instead).
                    _recovery.RequestRestart(
                        $"tick body threw {_consecutiveTickFailures} times in a row " +
                        $"({ex.GetType().Name}: {ex.Message})");
                    _consecutiveTickFailures = 0;
                }
            }
        }

        // ── Tick-phase helpers ──────────────────────────────────────────────

        private void TickPreamble()
        {
            _tickCounter++;

            int slowInterval = Math.Max(1, 1000 / _baseTickMs);
            if (_tickCounter % slowInterval == 0)
                SendHeartbeat();

            // Drain the retransmit queue and tier-def blind schedule
            // during preamble too. Cold-start traffic (session-open, FF
            // init records, tier-def, configJson reply, upload sub-msgs)
            // is all sent during this phase; if the wheel drops a chunk
            // the queue accumulates but goes unserviced until we hit
            // Active, by which time the wheel has typically given up.
            // The per-chunk backoff inside SessionRetransmitter keeps
            // this from flooding the wire when nothing is due.
            TickEmitRetransmits();
            _tierDefEmitter.TickEmitTierDefBlindRetransmits();

            if (_tickCounter >= _preambleTickTarget)
            {
                // Try a fresh parse before checking catalog readiness — a
                // burst that arrived in the last tick may not yet be merged.
                _catalogParser.TryParse();

                // Hold preamble open if the wheel hasn't yet advertised any
                // catalog entries. Going Active with catalog=0 means the
                // initial tier-def emits with idx=alpha (all chIndex=0
                // bindings the wheel ignores) and the post-Active catalog-
                // growth re-apply has to clean up the mess. Cap the wait so
                // we don't stall indefinitely on a broken wheel.
                int extendedCap = Math.Max(_preambleTickTarget * 4,
                    PreambleCatalogWaitMaxMs / Math.Max(1, _baseTickMs));
                if (_catalogParser.Count == 0 && _tickCounter < extendedCap)
                {
                    if (_tickCounter == _preambleTickTarget)
                    {
                        MozaLog.Debug(
                            "[Moza] Preamble extended: waiting for catalog (count=0). " +
                            $"Cap {extendedCap} ticks ({extendedCap * _baseTickMs} ms).");
                    }
                    return;
                }

                TransitionTo(TelemetryState.Active, "preamble countdown elapsed");
                // Anchor for TickSession02EngagementWatchdog's grace
                // window — the watchdog only starts counting against the
                // wheel once we've actually entered Active (and thus
                // emitted tier-def + initial value frames).
                _watchdog.NoteActiveStateEntered();
                ApplySubscription(force: false);

                _tickCounter = 0;
                _slowCounter = 0;
            }
        }

        /// <summary>Hard ceiling on preamble extension when the wheel hasn't
        /// pushed any catalog entries. Beyond this we proceed with whatever
        /// we have (likely empty → idx=alpha tier-def + the catalog-growth
        /// re-apply path will eventually pick up the slack).</summary>
        private const int PreambleCatalogWaitMaxMs = 3000;

        /// <summary>Continuous catalog absorption. Wheel pushes URL records
        /// in batches with ~1.2s gaps; parse every time the buffer grows and
        /// merge non-destructively so URLs are never dropped.</summary>
        private void TickAbsorbCatalogIfChanged()
        {
            int curLen = _catalogParser.BufferLength;
            if (curLen > _catalogParser.LastParsedBufferLen)
            {
                _catalogParser.TryParse();
                // Buffer-overrun guard: post-renegotiate noise can fill a
                // session's buffer with redundant end-marker bytes; drop only
                // the overflowing session(s) since the parser keeps the merged
                // catalog cached. Per-session so end-marker spam on one
                // session can't wipe another session's still-unparsed records.
                if (_catalogParser.MaxSessionBufferLength > 4096)
                {
                    _catalogParser.ClearOverflowingSessions(4096);
                }
            }
        }

        private void TickFireGameStartHandshake()
        {
            if (!_gameStartHandshakePending) return;
            _gameStartHandshakePending = false;
            SendGameStartHandshake();
        }

        /// <summary>Active-phase value frame emission. PitHouse captures
        /// confirm V2 (Type02 firmware) host telemetry uses the bit-packed
        /// 7d:23 group=0x43 path; V0 (Era2024 URL subscription) uses per-
        /// channel FF records on session 0x02. Game-running gating: V0 is
        /// idle-silent (PitHouse stays quiet on sess=02 at idle); V2 always
        /// emits (BuildTestFrame vs BuildFrameFromSnapshot differentiates
        /// test/live within the loop).</summary>
        private void TickEmitValueFrames(TierState[] tiers)
        {
            GameDataSnapshot snapshot = TestMode
                ? default
                : GameDataSnapshot.FromStatusData(_latestGameData);

            bool liveOk = _gameRunning && _profileTelemetryEnabled;
            bool useV0Values = _policy.Encoding == TierDefEncoding.V0Url;
            if (useV0Values)
            {
                if (TestMode || liveOk)
                    SendV0ValueFrames(snapshot);
                return;
            }

            // V2 normally emits every tick (PitHouse parity — see comment
            // above). Only suppress when the active overlay disabled
            // telemetry; TestMode override re-enables emission so the user
            // can verify wheel rendering.
            if (!TestMode && !_profileTelemetryEnabled)
                return;

            byte subFlagBase = _activeSubscription?.FlagBase ?? 0;
            for (int i = 0; i < tiers.Length; i++)
            {
                var tier = tiers[i];
                if (_tickCounter % tier.TickInterval != 0)
                    continue;

                // Match flag byte to the tier-def we last sent: each tier-def
                // claims `flagBase + tierIdx` (BuildTierDefinitionMessage). Wheel
                // routes value frames by flag byte → registered tier.
                byte flagByte = (byte)(subFlagBase + i);
                byte[] frame = TestMode
                    ? tier.Builder.BuildTestFrame(flagByte)
                    : tier.Builder.BuildFrameFromSnapshot(snapshot, flagByte);

                if (TestMode && _tierDiagEmitted != null && i < _tierDiagEmitted.Length && !_tierDiagEmitted[i])
                {
                    _tierDiagEmitted[i] = true;
                    var p = tier.Builder.Profile;
                    MozaLog.Debug(
                        $"[Moza] TIER-EMIT t[{i}] flag=0x{flagByte:X2} " +
                        $"tickInterval={tier.TickInterval} " +
                        $"name={p?.Name ?? "?"} ch={p?.Channels?.Count ?? 0} " +
                        $"bits={p?.TotalBits ?? 0} bytes={p?.TotalBytes ?? 0} " +
                        $"frameLen={frame.Length}");
                }

                // Latest-wins per tier: if the last frame for this tier is still
                // queued (e.g. write thread stalled under Wine syscall overhead),
                // overwrite it so the wheel gets the freshest snapshot instead
                // of a growing backlog.
                if (i < 8)
                    _connection.SendStream((StreamKind)((int)StreamKind.TierDash0 + i), frame);
                else
                    _connection.Send(frame);

                if (i == 0)
                {
                    _framesSent++;
                }
            }
        }

        /// <summary>Out-of-band string-channel value push on sess=0x01
        /// type=0x05. Strings (Telemetry.json compression=string — TrackId,
        /// CarModel, SessionTypeName, etc.) cannot be bit-packed into the
        /// value frame; they ride a separate sub-msg on the management
        /// session. Cadence: emit immediately on value change with a
        /// 15-second keepalive floor for unchanged channels — matches the
        /// 14.76 s mean cadence observed in PitHouse capture
        /// bridge-20260514-204307.jsonl. Format and discovery in
        /// docs/protocol/sessions/session-0x01-channel-protocol.md.
        ///
        /// _session01SeqLock is acquired around each emit. Today the only
        /// other writer of _session01OutboundSeq is SendTierDefinition() also
        /// on the tick handler (single-entry via _tickInProgress), so a race
        /// is not reachable — but locking here matches SendTierDefinition's
        /// pattern and is future-proof against off-tick sess=0x01 writers
        /// being added later.</summary>
        private void TickEmitStringValues()
        {
            if (!TestMode && (!_gameRunning || !_profileTelemetryEnabled)) return;
            EmitStringChannels(force: false);
        }

        private const int StringKeepaliveFloorMs = 15000; // PitHouse mean cadence

        /// <summary>Iterate the active profile's string channels and emit each
        /// one via sess=0x01 type=0x05. When <paramref name="force"/> is false,
        /// emits only on value change or 15 s keepalive expiry and skips
        /// fully-unmapped channels with no prior state. When true, emits every
        /// catalog-bound channel regardless — used by the auto-test burst.</summary>
        private void EmitStringChannels(bool force)
        {
            var profile = _profile;
            if (profile == null || profile.StringChannels.Count == 0) return;
            var catalog = _catalogParser.Catalog;
            if (catalog == null || catalog.Count == 0) return;

            int nowMs = Environment.TickCount;
            long signalNowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L /
                               System.Diagnostics.Stopwatch.Frequency;

            foreach (var ch in profile.StringChannels)
            {
                int idx = _catalogParser.FindIdxByUrl(ch.Url);
                if (idx < 1 || idx > 255) continue;

                string value = ResolveStringChannelValue(ch, signalNowMs);

                if (!force)
                {
                    // Unmapped channels with no prior state would otherwise
                    // blast "" at the wheel every 15 s. Wait for a UI mapping.
                    if (value.Length == 0 && !_stringChannelState.ContainsKey(ch.Url))
                        continue;

                    bool send;
                    if (!_stringChannelState.TryGetValue(ch.Url, out var st))
                        send = true;
                    else if (!string.Equals(st.lastValue, value, System.StringComparison.Ordinal))
                        send = true;
                    else
                        send = nowMs - st.lastTickMs >= StringKeepaliveFloorMs;
                    if (!send) continue;
                }

                EmitOneStringValue((byte)idx, value);
                _stringChannelState[ch.Url] = (value, nowMs);
            }
        }

        /// <summary>Resolve a string channel's current value. Test mode pulls
        /// from the channel's resolved TestSignal (typically "STR-Name");
        /// game-running mode reads the bound SimHub property via
        /// <see cref="PropertyStringResolver"/>. Returns "" when no source is
        /// available — caller decides whether to emit it.</summary>
        private string ResolveStringChannelValue(ChannelDefinition ch, long nowMs)
        {
            if (TestMode)
            {
                return TestSignalGenerator.ComputeString(ch.TestSignal, nowMs);
            }
            if (!string.IsNullOrEmpty(ch.SimHubProperty) && PropertyStringResolver != null)
            {
                try
                {
                    var resolved = PropertyStringResolver(ch.SimHubProperty);
                    if (!string.IsNullOrEmpty(resolved)) return resolved!;
                }
                catch
                {
                    // Resolver swallows internally; defensive in case a future
                    // override throws. Fall through to empty string.
                }
            }
            return "";
        }

        /// <summary>Build the type=0x05 sub-msg and ship it through the chunker
        /// + connection under the sess=0x01 seq lock. Caller updates the
        /// <c>_stringChannelState</c> dedup entry afterwards.</summary>
        private void EmitOneStringValue(byte channelIdx, string value)
        {
            byte[] msg = Frames.StringValueBuilder.Build(channelIdx, value);
            lock (_session01SeqLock)
            {
                var frames = Frames.TierDefinitionBuilder.ChunkMessage(
                    msg, session: 0x01, seq: ref _session01OutboundSeq, deviceId: _targetDeviceId);
                foreach (var f in frames)
                    _connection.Send(f);
            }
        }

        /// <summary>Diagnostic: emit every string channel in the current
        /// profile right now, bypassing the change-detect + keepalive gate.
        /// Used by the auto-test harness (PhaseStringBurst) to produce a
        /// clearly-labelled wire-trace window even when no game is running
        /// and steady-state cadence is silent. Updates dedup state so the
        /// subsequent tick doesn't immediately re-emit.</summary>
        public void ForceStringEmitAll() => EmitStringChannels(force: true);

        /// <summary>FFB-enable + sequence-counter. Both gated on gameRunning
        /// because PitHouse only emits these while a game is actively driving
        /// telemetry — bursting them at idle is the largest plugin-vs-PitHouse
        /// drift source observed in 2026-04-29 captures.</summary>
        private void TickEmitEnableAndSequence()
        {
            if (!TestMode && (!_gameRunning || !_profileTelemetryEnabled)) return;
            _connection.SendStream(StreamKind.Enable, _cachedEnableFrame);
            if (SendSequenceCounter)
                _connection.SendStream(StreamKind.Sequence, BuildSequenceCounterFrame());
        }

        /// <summary>Peripheral output polls (handbrake + pedals) at ~1 Hz,
        /// staggered across ticks so the writes don't pile into a single
        /// 4ms-paced burst.</summary>
        private void TickEmitPeripheralPolls()
        {
            int slow = Math.Max(8, 1000 / _baseTickMs); // ~1Hz cycle (33 ticks @ 30ms base)
            int phase = _tickCounter % slow;
            if (phase == 0)             _connection.Send(_handbrakePresenceFrame);
            else if (phase == slow / 5) _connection.Send(_handbrakeOutputFrame);
            else if (phase == 2 * slow / 5)
            {
                _connection.Send(_pedalThrottleOutFrame);
                _connection.Send(_pedalBrakeOutFrame);
                _connection.Send(_pedalClutchOutFrame);
            }
        }

        /// <summary>LED state polls. Group 1 ~1 Hz, group 2 ~0.2 Hz.</summary>
        private void TickEmitLedStatePolls()
        {
            int slow = Math.Max(8, 1000 / _baseTickMs);
            if (_tickCounter % slow == 3 * slow / 5)
                _connection.Send(_ledStatePollGroup1);
            if (_tickCounter % (slow * 5) == 4 * slow / 5)
                _connection.Send(_ledStatePollGroup2);
        }

        /// <summary>Retransmit unacked session-data chunks. Per-chunk
        /// exponential backoff (100ms → 200 → 400 … capped at 2s) so a stuck
        /// chunk doesn't keep flooding the link at fixed cadence. PitHouse
        /// captures show 50× retransmit over 37s for genuinely stuck chunks;
        /// 30 attempts × max-2s-backoff gives ~53s budget which covers the
        /// observed pattern without unbounded retry. The previous 8-attempt
        /// budget (≈9s total) was too tight: a configJson chunk drop on
        /// sess=0x09 under post-switch saturation could not survive the
        /// 11s session-silence settle without being abandoned.</summary>
        private void TickEmitRetransmits()
        {
            foreach (var chunk in _retransmitter.DueRetransmits(maxRetries: 30))
            {
                if (_state == TelemetryState.Idle || !_connection.IsConnected) break;
                _connection.Send(chunk);
            }
        }

        /// <summary>Tier-def blind retransmit rounds. Some firmwares need the
        /// tier-def re-sent a few times during cold-start before it sticks;
        /// fire each blind round at exponential backoff up to
        /// <see cref="TierDefBlindMaxRounds"/>, then stop (and free the buffer).
        ///
        /// Early-exit when the retransmit queue no longer contains any of the
        /// blind tier-def chunks — that means the wheel acked them all and
        /// re-sending would just waste bandwidth. Trace analysis 2026-05-09
        /// showed the prior catalog-activity-timestamp gate never tripped
        /// (catalog activity is timestamped before tier-def sends), so all
        /// 6 rounds always fired. Switching to ack-state lets us stop after
        /// the first round on healthy connects, eliminating the cold-start
        /// saturation event (~6 KB extra h2b per connect).</summary>
        
        /// <summary>True iff every chunk in <see cref="_tierDefBlindFrames"/>
        /// has been acked by the wheel (and therefore removed from the
        /// retransmitter queue). Frame layout per <see cref="TierDefinition-
        /// Builder.ChunkMessage"/> places session at byte 6 and seq at
        /// bytes 8-9 (LE). Returns false if any chunk is still pending.
        /// </summary>
        
        /// <summary>Re-emit the sess=0x09 prime + ConfigJson open request until
        /// the wheel device-inits 0x09 (b2h <c>7c 00 09 81 ...</c>) or the retry
        /// budget is exhausted. Cold-start fires the pair once from
        /// <see cref="PrimeAndOpenSession09"/>; if the wheel doesn't respond
        /// (Wine SerialPort R/W contention, dropped chunk, slow firmware) the
        /// configJson handshake never starts and the dashboard never renders.
        ///
        /// Guarded by <c>_sessions.GetOrCreate(0x09).DeviceInitiated</c> — once
        /// the wheel emits its device-init the retry stops naturally and never
        /// fires again for this Start cycle. Steady-state and post-switch
        /// sessions are untouched (we never close 0x09 host-side, so
        /// DeviceInitiated stays true across switches).</summary>
        
        /// <summary>
        /// When the most-recent tier-def emission had unbound channels,
        /// schedule a kind=4 dashboard-switch re-emit for the slot the
        /// wheel is currently on. Re-applying the same slot tells some
        /// firmwares to re-run their dashboard-load sequence which re-
        /// advertises the full channel catalog. Throttled via
        /// <see cref="_catalogResyncProbe"/> so a stuck case can't produce
        /// a switch storm.
        /// </summary>
        private void ScheduleCatalogResyncProbe()
        {
            long now = System.DateTime.UtcNow.Ticks;
            if (!_catalogResyncProbe.IsThrottleClear(now)) return;

            // Resolve the current slot from the wheel-reported configJsonList
            // by matching profile name. Without LastState we don't know which
            // slot the wheel thinks it's on; skip silently in that case.
            var state = _configJson.LastState;
            string? profileName = _profile?.Name;
            if (state == null || state.ConfigJsonList == null || state.ConfigJsonList.Count == 0
                || string.IsNullOrEmpty(profileName))
                return;
            int slot = -1;
            for (int i = 0; i < state.ConfigJsonList.Count; i++)
            {
                if (string.Equals(state.ConfigJsonList[i], profileName,
                    System.StringComparison.OrdinalIgnoreCase))
                { slot = i; break; }
            }
            if (slot < 0)
            {
                MozaLog.Debug(
                    $"[Moza] Catalog re-sync probe skipped: profile '{profileName}' " +
                    "not found in wheel-reported configJsonList");
                return;
            }

            // Wheel-on-target shortcut: the wheel emits a type-04 record on
            // sess=0x02 b2h announcing its current slot at startup (BEFORE
            // any host kind=4 — observed t=11.5 s in 2026-05-14 wire trace).
            // If that matches what we'd be emitting to, the probe is pure
            // noise — the catalog incompleteness will resolve via
            // TickGrowSubscriptionIfCatalogStable's natural re-emit as the
            // wheel pushes more channel URLs. Importantly we DON'T call
            // _catalogResyncProbe.MarkFired here, so HasCatalogResyncProbeFired
            // stays false and ApplyTelemetryDashboardFromProfile's slot-match
            // path takes the "no wire action needed" branch instead of
            // pointlessly cycling the pipeline.
            if (_slotTracker.WheelReportedSlot == slot)
            {
                MozaLog.Debug(
                    $"[Moza] Catalog re-sync probe skipped: wheel already on " +
                    $"slot {slot} ('{profileName}') per wheel-reported state");
                return;
            }

            // From here on we're committing to emit. Arm the timestamp now
            // so the min-interval throttle counts from a real emission, and
            // HasCatalogResyncProbeFired reflects a probe that actually
            // changed wheel state.
            _catalogResyncProbe.MarkFired(now);

            int slotCapture = slot;
            string nameCapture = profileName!;
            // Defer the kind=4 emission so it lands AFTER the just-sent tier-
            // def chunks finish hitting the wire. 800ms covers the largest
            // observed tier-def burst (Grids: 26 chunks * 4ms one-shot pace
            // ≈ 100ms with budget pacing absorbed).
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    System.Threading.Thread.Sleep(800);
                    if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                    SendDashboardSwitch((uint)slotCapture);
                    MozaLog.Debug(
                        $"[Moza] Catalog re-sync probe: re-emitted kind=4 " +
                        $"slot={slotCapture} ('{nameCapture}')");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] Catalog re-sync probe failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Re-emit tier-def when the wheel's channel catalog has grown since
        /// the last subscription emission. Mirrors PitHouse's growing-
        /// subscription pattern: as the wheel pushes additional URL records
        /// over the first few seconds (and again post-dashboard-switch), we
        /// re-subscribe so late-arriving channels acquire correct chIndex
        /// bindings instead of being stuck at chIndex=0.
        ///
        /// Without this, dashboard widgets bound to URLs that arrive after
        /// the initial preamble→Active tier-def render frozen at zero —
        /// observed 2026-05-09 with Grids tire channels (catalog slots 9-20
        /// arrived after preamble exit) and Mono test channels.
        ///
        /// Quiet-window gating: only re-emit when the catalog has been
        /// stable for <see cref="CatalogGrowthQuietMs"/>. Re-emitting mid-
        /// burst would race the wheel's continuing advertisements and
        /// fragment the tier-def across two emissions.
        /// </summary>
        private void TickGrowSubscriptionIfCatalogStable()
        {
            if (_state != TelemetryState.Active) return;
            if (!_connection.IsConnected) return;
            int cur = _catalogParser.Count;
            bool hotSwitchPending = _hotSwitch.IsBurstPending;
            bool catalogGrew = (cur - _catalogCountAtLastSubscription) >= CatalogGrowthMinDelta;
            if (!hotSwitchPending && !catalogGrew) return;

            int act = _catalogParser.LastActivityMs;
            int idle = act == 0
                ? int.MaxValue
                : Environment.TickCount - act;

            if (hotSwitchPending)
            {
                // Burst pacing: PitHouse fires 3-13 tier-def emissions
                // ~1s apart post-switch. Each emission rebuilds with the
                // wheel's most-recent END marker, so even if the first
                // emission echoes a stale END (wheel hadn't pushed the
                // new one yet), a later emission picks up the updated
                // value and the wheel binds then. Gating logic for first
                // emission (END handshake + fallback) and pacing for
                // subsequent emissions both live in the helper.
                if (!_hotSwitch.ShouldEmitThisTick(act, _catalogParser.LastWheelEndMarkerTickMs))
                    return;

                int now = Environment.TickCount;
                int sinceArm = now - _hotSwitch.ArmTickMs;
                bool isFirstEmission = _hotSwitch.LastEmissionTickMs == 0;
                int prev = _catalogCountAtLastSubscription;
                int emissionIdx = _hotSwitch.EmissionsSent + 1;
                MozaLog.Debug(
                    $"[Moza] Re-applying tier-def (hot-switch burst " +
                    $"#{emissionIdx}, cap {Lifecycle.HotSwitchCoordinator.MinEmissions}-" +
                    $"{Lifecycle.HotSwitchCoordinator.MaxEmissions}): " +
                    $"catalog {prev}→{cur}, wheel END={_catalogParser.LastWheelEndMarker}, " +
                    $"sinceArm={sinceArm}ms, sinceLast={(isFirstEmission ? -1 : now - _hotSwitch.LastEmissionTickMs)}ms");
                try
                {
                    ApplySubscription(force: true);
                    _catalogCountAtLastSubscription = _catalogParser.Count;
                    // Pass bind info from the most-recent emission. Total>0
                    // means cspIdx ran (Type02); for older eras LastTierDefTotalCount
                    // stays at -1 → pass null and the coordinator keeps the
                    // legacy fixed-length cap.
                    bool? boundComplete = (_tierDefEmitter.LastTierDefTotalCount > 0)
                        ? (bool?)_tierDefEmitter.IsTierDefFullyBound
                        : null;
                    int newRemaining = _hotSwitch.MarkEmission(boundComplete);
                    if (newRemaining == 0)
                    {
                        MozaLog.Debug(
                            $"[Moza] Hot-switch burst complete after " +
                            $"{_hotSwitch.EmissionsSent} emissions (boundComplete={boundComplete?.ToString() ?? "n/a"})");
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] Tier-def re-apply (hot-switch) failed: {ex.Message}");
                }
                return;
            }

            // Pure catalog-growth path (no hot switch pending).
            if (idle < CatalogGrowthQuietMs) return;
            int p = _catalogCountAtLastSubscription;
            MozaLog.Debug(
                $"[Moza] Re-applying tier-def: catalog grew {p}→{cur} " +
                $"(idle {idle}ms ≥ {CatalogGrowthQuietMs}ms)");
            try
            {
                ApplySubscription(force: true);
                _catalogCountAtLastSubscription = _catalogParser.Count;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Tier-def re-apply (catalog growth) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tick-driven gap escalation. When a forward gap was detected on
        /// sess=0x09 but no further chunks have arrived (the wheel's auto-
        /// retransmit timer didn't fire, or the retransmit itself dropped),
        /// the chunk-arrival-driven <see cref="HandleConfigJsonGap"/> path
        /// never re-runs. This watchdog notices a stale gap and escalates
        /// from passive-wait to active prime+open-request from the tick
        /// loop instead.
        ///
        /// Cheap: short-circuits the moment LastState is non-null OR no
        /// forward gap was ever observed OR the configJson session isn't in
        /// play yet. Only runs the soft-watchdog logic when there's an
        /// outstanding gap to recover from.
        /// </summary>
        
        /// <summary>
        /// Stuck-state watchdog for sess=0x09 configJson. Restart-escalates
        /// ONLY when nothing else is working — a chunk drop on the configJson
        /// burst is "nice to lose" if the catalog and tier-def are healthy,
        /// because configJson state is for dashboard-library UI, not for
        /// rendering the active dashboard itself. Forcing a 11s Stop+Start
        /// just because LastState is null while streams are alive throws away
        /// a working session.
        ///
        /// Skip conditions:
        ///   - LastState already populated (steady state — no problem)
        ///   - No chunks ever received (wheel hasn't started — TickRetryS09
        ///     already handles this with shorter retry cadence)
        ///   - Catalog is populated AND we have a tier-def emitted (dashboard
        ///     can render fine without configJson library list — observed
        ///     2026-05-09: catalog 0→20 + working test pattern despite
        ///     stuck configJson)
        ///   - Within escalation cooldown
        ///
        /// Only fires when ALL of: chunks were arriving, then went silent
        /// for ConfigJsonNoStateRestartTimeoutTicks AND we have nothing else
        /// (no catalog, no tier-def). That's the genuine stuck case where
        /// only a Stop+Start can recover.
        /// </summary>
        
        /// <summary>
        /// Stuck-state watchdog for sess=0x02 engagement. The wheel must
        /// send at least one inbound chunk on sess=FlagByte (channel-
        /// token assignments / post-subscription state) for value-frame
        /// rendering to be alive. When it doesn't, dashboard layout still
        /// renders but every channel sits at its initial/zero default —
        /// see diag bundle CS-Pro-1stLaunchAfterDll-20260518.
        ///
        /// Re-arm sequence (close+open+init+resubscribe) is the same
        /// handshake <see cref="ProbeAndOpenSessions"/> runs at start,
        /// replayed against the now-alive wheel. Budget capped at
        /// <see cref="S02ReArmMaxRounds"/>; on exhaustion escalates to
        /// <see cref="RestartForSwitch"/> (matches the precedent in
        /// <see cref="TickConfigJsonStuckWatchdog"/>).
        ///
        /// Skip conditions:
        ///   - Not in Active state (the start path owns its own probe)
        ///   - Engagement already confirmed (_session02FirstInboundUtcTicks
        ///     non-zero — any prior inbound on sess=FlagByte sets it)
        ///   - Re-arm budget exhausted (escalation already queued)
        ///   - Within initial grace OR backoff window
        /// </summary>
        
        /// <summary>Widget-state poll cycle. Cycle of 80 slots at one frame per
        /// 10 ticks gives ~0.4/s per slot; PitHouse capture cadence is ~0.2/s
        /// per slot, within tolerable range.</summary>
        /// <summary>Widget-state poll cycle at ~1 Hz. The cycle rotates
        /// through 80 probes, so each individual probe gets covered every
        /// ~80 seconds.</summary>
        private void TickEmitWidgetPoll()
        {
            int slow = Math.Max(8, 1000 / _baseTickMs);
            if (_tickCounter % slow == slow / 2)
                SendWidgetStatePoll();
        }

        /// <summary>~1 Hz slow path: dash keepalive, mode frame, display
        /// config, 28x poll, status push, session 0x09 keepalive. Display
        /// config is throttled to every other slow tick (~0.5 Hz) to match
        /// PitHouse cadence.</summary>
        private void TickEmitSlowPath()
        {
            int slow = Math.Max(1, 1000 / _baseTickMs);
            if (_slowCounter++ % slow != 0) return;

            // SendHeartbeat() emits group-0 length-0 presence pings; PitHouse
            // capture (2026-04-29) shows none of these on the wire — PitHouse
            // uses 0x43-keepalives (SendDashKeepalive below) instead. Skipping
            // SendHeartbeat here removes ~4 frames/s of plugin-only noise.
            // Hot-swap detection still works via PollStatus's wheel-model probe.
            SendDashKeepalive();
            if (SendTelemetryMode)
                _connection.SendStream(StreamKind.Mode, _cachedModeFrame);
            if ((_slowCounter & 1) == 1)
                SendDisplayConfig();
            else if (_slowCounter % 8 == 0)
                Send28xPoll();
            SendSession09Keepalive();
        }

        // ── Session management ──────────────────────────────────────────────

        /// <summary>
        /// Send a type=0x00 end-marker on the given session. Used to reclaim sessions
        /// left open after a previous SimHub crash/kill, where End() did not run.
        /// If the session is already closed, the wheel silently ignores this frame.
        /// </summary>
        private void SendSessionClose(byte session)
        {
            // Length byte is the payload count (cmd + data, not incl. group/dev/cksum).
            // Payload is 6 bytes: 7C 00 <session> 00 <ack_lo> <ack_hi>. Must match
            // len=6 — a shorter frame with len=6 caused the wheel/sim to over-read
            // and corrupt the next frame in the stream, breaking the read loop.
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session, 0x00,          // type=0x00 (end marker)
                0x00, 0x00,             // ack_seq = 0 (LE)
                0x00                    // checksum placeholder
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        private void SendSessionOpen(byte session, byte port)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, _targetDeviceId,
                0x7C, 0x00,
                session, 0x81,          // session byte + type (channel open)
                port, 0x00,             // seq = port (LE)
                port, 0x00,             // session_id = port (LE)
                0xFD, 0x02,             // receive_window = 765 (LE)
                0x00                    // checksum placeholder
            };
            frame[14] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        private void SendSessionAck(byte session, ushort ackSeq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x05,
                MozaProtocol.TelemetrySendGroup, _targetDeviceId,
                0xFC, 0x00,
                session,
                (byte)(ackSeq & 0xFF),
                (byte)(ackSeq >> 8),
                0x00
            };
            frame[9] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        /// <summary>
        /// Prime a wheel-managed session with a zero-length data frame to
        /// encourage the wheel to device-init its end. Pithouse does this on
        /// session 0x09 (configJson state push) at startup — verified in
        /// usb-capture/ksp/mozahubstartup.pcapng frames 639/1211 (host sends
        /// `7c 00 09 01 [seq] [ack] 00 00` at t=2.345/6.346, wheel device-inits
        /// 0x09 type=0x81 at t=28.123 as part of its 0x05/0x07/0x09/0x0a burst).
        /// Wheels that have never had the host prime 0x09 only open 0x05/0x07
        /// in the burst, leaving configJson handshake stuck and dashboard
        /// rendering blocked.
        /// </summary>
        internal void SendSessionPrime(byte session, ushort seq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, _targetDeviceId,
                0x7C, 0x00,
                session, 0x01,                  // type=0x01 (data chunk)
                (byte)(seq & 0xFF),
                (byte)(seq >> 8),
                0x00, 0x00,                     // ack_seq = 0
                0x00, 0x00,                     // 2 bytes of empty data (matches Pithouse)
                0x00                            // checksum placeholder
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        /// <summary>
        /// Per-tick V0 telemetry: one value frame per profile channel on
        /// session 0x02. Wheel firmware indexes channels 1-based by their
        /// position in the catalog it advertised during subscription.
        /// Each frame format documented in
        /// <see cref="TelemetryFrameBuilder.BuildV0ValueFrame"/>; chunked
        /// through the session-data layer with monotonically advancing seq.
        /// </summary>
        private void SendV0ValueFrames(GameDataSnapshot snapshot)
        {
            var profile = _profile;
            var catalog = _catalogParser.Catalog;

            // Catalog-less fallback: in TestMode without a wheel-advertised
            // channel catalog, iterate the loaded profile's channels and
            // synthesize 1-based indices. Lets the test button exercise every
            // channel the host knows about even if the wheel hasn't (or
            // hasn't yet) advertised its URL list. Lives mode without catalog
            // still returns silent — no point sending zeroed V0 frames at idle.
            if (catalog == null || catalog.Count == 0)
            {
                if (!TestMode) return;
                if (profile == null || profile.Tiers == null) return;
                var profileChannels = new System.Collections.Generic.List<string>();
                foreach (var tier in profile.Tiers)
                    foreach (var ch in tier.Channels)
                        if (!string.IsNullOrEmpty(ch.Url))
                            profileChannels.Add(ch.Url);
                if (profileChannels.Count == 0) return;
                catalog = profileChannels;
            }

            // Build per-URL host channel lookup once per tick. Channels not in
            // the host profile still get a frame — wheel's dashboard may bind
            // to URLs the host doesn't have local metadata for, and missing
            // values block widget render. Default compression = uint32_t,
            // resolved value = 0 (live) or test triangle.
            var byUrl = new System.Collections.Generic.Dictionary<string, ChannelDefinition>(
                StringComparer.OrdinalIgnoreCase);
            if (profile != null)
            {
                foreach (var tier in profile.Tiers)
                    foreach (var ch in tier.Channels)
                        if (!string.IsNullOrEmpty(ch.Url) && !byUrl.ContainsKey(ch.Url))
                            byUrl[ch.Url] = ch;
            }

            // Compute every per-channel value frame BEFORE entering the lock
            // so SimHub property resolution (PluginManager.GetPropertyValue
            // — can hit slow paths under load) doesn't block UI-thread
            // brightness/dashboard-switch property pushes that contend for
            // _session02SeqLock. Holding the lock over IO-bound work was
            // observable as a UI freeze during teardown.
            var prebuilt = new System.Collections.Generic.List<byte[]>(catalog.Count);
            for (int i = 0; i < catalog.Count; i++)
            {
                string url = catalog[i];
                if (string.IsNullOrEmpty(url)) continue;
                uint wheelIdx = (uint)(i + 1); // 1-based per docs

                ChannelDefinition? ch = byUrl.TryGetValue(url, out var found) ? found : null;
                string compression = ch?.Compression ?? "uint32_t";

                double value;
                if (TestMode)
                {
                    if (ch != null)
                    {
                        long nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L /
                                     System.Diagnostics.Stopwatch.Frequency;
                        value = TestSignalGenerator.Compute(ch.TestSignal, nowMs);
                    }
                    else
                    {
                        // Unknown URL not in host profile — emit 0 so the
                        // wheel sees "nothing mapped" rather than a fake percent.
                        value = 0.0;
                    }
                }
                else
                {
                    value = ch != null ? ResolveV0ChannelValue(ch, snapshot) : 0.0;
                }

                byte[] valueBytes = TelemetryFrameBuilder.EncodeV0Value(compression, value);
                prebuilt.Add(TelemetryFrameBuilder.BuildV0ValueFrame(wheelIdx, valueBytes));
            }

            if (prebuilt.Count == 0)
            {
                return;
            }

            // Reserve the seq range for the whole burst under the session
            // lock so a concurrent FF property push (UI thread) or tier-def
            // re-emit (background thread) can't slip a seq into the middle
            // of our per-channel value frame train. Lock scope is now bounded
            // to the chunking + send, not the value-resolution loop above.
            bool anySent = false;
            lock (_session02SeqLock)
            {
                int seq = _session02OutboundSeq;
                foreach (var vframe in prebuilt)
                {
                    var frames = TierDefinitionBuilder.ChunkMessage(vframe, FlagByte, ref seq, _targetDeviceId);
                    foreach (var frame in frames)
                    {
                        if (_state == TelemetryState.Idle || !_connection.IsConnected)
                        {
                            _session02OutboundSeq = seq;
                            if (anySent) _framesSent++;
                            return;
                        }
                        SendAndTrackChunk(frame);
                    }
                    anySent = true;
                }
                _session02OutboundSeq = seq;
            }
            if (anySent) _framesSent++;
        }

        private double ResolveV0ChannelValue(ChannelDefinition ch, GameDataSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(ch.SimHubProperty) && PropertyResolver != null)
            {
                double scale = ch.SimHubPropertyScale == 0.0 ? 1.0 : ch.SimHubPropertyScale;
                return PropertyResolver(ch.SimHubProperty) * scale;
            }
            return snapshot.GetField(ch.SimHubField);
        }

        /// <summary>
        /// Periodic empty-data ping on session 0x09 to keep the configJson channel
        /// alive. Mirrors PitHouse start-game capture
        /// (`wireshark/csp/start-game-change-dash.pcapng`) which emits
        /// `7c 00 09 01 [seq++] 00 00 00 00 00` at ~1Hz; without it the wheel
        /// closes session 0x09 and stops pushing dashboard state, leaving the
        /// plugin's "Wheel Files" tab empty. Fires once per active-phase slow
        /// tick alongside other 1Hz heartbeats.
        /// </summary>
        private void SendSession09Keepalive()
        {
            // Reserve seq + emit under _session09SeqLock so a concurrent
            // MaybeSendConfigJsonReply (read-thread) can't slip its multi-chunk
            // train in between this seq reservation and the wire send.
            lock (_session09SeqLock)
            {
                int seq = ++_session09OutboundSeq;
                SendSessionPrime(0x09, (ushort)seq);
            }
        }

        /// <summary>
        /// Tiered chunk-drop recovery for sess=0x09 / 0x0a configJson. Tier
        /// chosen by gap count, cached state, and elapsed time since the gap
        /// was first observed:
        ///
        ///   Tier 0 (LastState present, any gap count): no action required.
        ///   The cached state is still authoritative for downstream consumers
        ///   (dashboard library list); the wheel doesn't change state without
        ///   a user action so a stale-but-correct cache is preferable to a
        ///   forced re-handshake (which the wheel often ignores anyway).
        ///
        ///   Tier 0.5 (LastState absent, gap fresh): passive wait. The
        ///   cumulative ACK the caller just sent points at HighWaterSeq
        ///   instead of the just-received seq, so the wheel's outstanding-
        ///   ack timer will retransmit the missing chunk in ~1.3 s. We give
        ///   it ConfigJsonGapPassiveWaitMs (5 s) before escalating to an
        ///   active prime+open-request — most gaps self-heal in that window.
        ///
        ///   Tier 1 (LastState absent, passive wait expired): prime +
        ///   open-request. Some firmwares respond to the prime by resetting
        ///   their sess=0x09 state machine and re-bursting on next
        ///   OpenRequest.
        ///
        ///   Tier 2 (LastState absent, repeated gaps after prime+open): full
        ///   RestartForSwitch. The Stop+11s-settle+Start sequence is the only
        ///   reliable way to force a wheel that's stuck mid-burst back to a
        ///   cold-start where it'll definitely emit the full state again.
        ///
        /// Cooldown gates Tier 2 so a chunk-drop storm can't cycle Restart
        /// faster than the wheel's settle window.
        /// </summary>
        
        /// <summary>
        /// Host-initiated session-open request for the configJson channel
        /// (port 9). PitHouse capture
        /// (`wireshark/csp/startup, change knob colors, ...pcapng` pno~97431)
        /// shows it uses a distinct magic <c>7c 1e 6c 80</c> for this port —
        /// upload-style <c>7c 23 46 80</c> does NOT trigger wheel device-init
        /// for 0x09. Without this prompt CSP firmware never opens the
        /// configJson channel, leaving plugin "Wheel Files" tab empty.
        /// </summary>
        
        /// <summary>
        /// Universal-Hub 5-slot enumeration burst. Sends `7E 03 64 12 01 NN 00 [chk]`
        /// for slots 1..5 in a tight burst (PitHouse fires all 5 in a single USB
        /// packet). Hub answers with `7E 03 E4 21 01 NN VV [chk]` per slot, where
        /// VV = device-type code on that port (0x00 = empty). Mirrors PitHouse's
        /// wire pattern observed in `usb-capture/ksp/gfdsgfd.pcapng` at f54501.
        ///
        /// Distinct from the legacy <c>hub-port[1..3]-power</c> reads (group
        /// 0x64, cmds 0x03/0x04/0x05) — those poll power level on individual
        /// ports; this enumerates device-types across all 5 slots.
        /// </summary>
        private void SendHubSlotEnumeration()
        {
            if (!_connection.IsConnected) return;

            for (byte slot = 0x01; slot <= 0x05; slot++)
            {
                var frame = new byte[]
                {
                    MozaProtocol.MessageStart, 0x03,
                    0x64, MozaProtocol.DeviceMain,
                    0x01, slot, 0x00,
                    0x00, // checksum placeholder
                };
                frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
                _connection.Send(frame);
            }
        }

        // ── Channel configuration ───────────────────────────────────────────

        private void SendChannelConfig()
        {
            if (!_connection.IsConnected)
                return;

            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;

            // Pages 0/1/3 × channels 2..6: matches PitHouse capture (2026-04-29).
            // Page 2 is unused; channel 6 was previously omitted, leaving 7 (page,channel)
            // combos un-enabled and breaking widgets bound to those slots.
            // See docs/protocol/findings/2026-04-29-dashboard-initial-sync.md.
            foreach (int page in new[] { 0, 1, 3 })
            {
                for (byte cc = 2; cc <= 6; cc++)
                    _connection.Send(BuildChannelEnableFrame((byte)page, cc));
            }

            // 28:00 = WheelGetCfg_GetMultiFunctionSwitch — query active dashboard mode
            // 28:01 = WheelGetCfg_GetMultiFunctionNum — query active page number
            // (rs21_parameter.db [64,40,0/1]). The wheel retains the last loaded
            // dashboard across disconnections; Pithouse reads the current state before
            // setting 28:02 (telemetry channel mode: 01=multi-channel, 00=RPM only).
            _connection.Send(BuildGroup40Frame3(0x28, 0x00, 0x00));
            _connection.Send(BuildGroup40Frame3(0x28, 0x01, 0x00));
            _connection.Send(BuildGroup40Frame(0x09, 0x00));
            _connection.Send(_cachedModeFrame);
        }

        private byte[] BuildChannelEnableFrame(byte page, byte channelIndex)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 5,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x1E, page,
                channelIndex, 0x00, 0x00,
            };
            frame.Add(MozaProtocol.CalculateWireChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        // Game-start handshake: PitHouse re-fires a small set of frames within
        // ~1.5 s of the first game-tick frame. Mirroring this lets the wheel
        // re-read its base parameters (steering limit / FFB strength / max angle)
        // and resync the channel-mode bit, matching what PitHouse-managed
        // dashboards see at game start. See:
        //   docs/protocol/findings/2026-04-29-dashboard-initial-sync.md
        //   docs/protocol/periodic/group-0x28.md
        //   docs/protocol/startup-timeline.md (Game-start handshake)
        // Triggered by SetGameRunning(false → true); fires once per game start.
        private void SendGameStartHandshake()
        {
            if (!_connection.IsConnected)
                return;

            // 0x28/DeviceBase reads — three base-param slots PitHouse re-reads at
            // game start. Wheel responds on 0xA8/0x31 with BE u16 values.
            _connection.Send(BuildBaseRead(0x01));   // limit            → 0x01c2 (450)
            _connection.Send(BuildBaseRead(0x17));   // max-angle        → 0x01c2 (450)
            _connection.Send(BuildBaseRead(0x02));   // ffb-strength     → 0x03e8 (1000)

            // 0x2B/DeviceBase set: hub set/ack observed at game start.
            // Semantics still TBD (see docs/protocol/periodic/group-0x2B.md);
            // PitHouse always emits this so we mirror.
            _connection.Send(BuildBaseSet2B(0x02, 0x00, 0x00));

            // 0x40/0x17 27 02 01 00 — channel-mode set companion to the
            // existing 28 02 01 00 read (cached _cachedModeFrame). PitHouse
            // emits both at game start.
            _connection.Send(BuildGroup40Frame4(0x27, 0x02, 0x01, 0x00));
        }

        // 7E 03 28 13 [cmd] 00 00 [cs] — base-param read (group 0x28 / DeviceBase).
        private byte[] BuildBaseRead(byte cmd)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x03,
                0x28, MozaProtocol.DeviceBase,
                cmd, 0x00, 0x00,
                0x00,
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        // 7E 03 2B 13 [cmd] [a] [b] [cs] — base-set on group 0x2B.
        private byte[] BuildBaseSet2B(byte cmd, byte a, byte b)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x03,
                0x2B, MozaProtocol.DeviceBase,
                cmd, a, b,
                0x00,
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private byte[] BuildGroup40Frame4(byte cmd1, byte cmd2, byte cmd3, byte cmd4)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x04,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2, cmd3, cmd4,
                0x00,
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private byte[] BuildGroup40Frame(byte cmd1, byte cmd2)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 2,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2,
            };
            frame.Add(MozaProtocol.CalculateWireChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildGroup40Frame3(byte cmd1, byte cmd2, byte cmd3)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 3,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2, cmd3,
            };
            frame.Add(MozaProtocol.CalculateWireChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        // ── Cached frame construction ───────────────────────────────────────

        private void BuildCachedFrames()
        {
            _cachedModeFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 4,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x28, 0x02, 0x01, 0x00 });

            _cachedEnableFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                MozaProtocol.BaseSendTelemetry, MozaProtocol.DeviceWheel,
                0xFD, 0xDE, 0x00, 0x00, 0x00, 0x00 });

            _cachedSequenceFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                0x2D, MozaProtocol.DeviceBase,
                0xF5, 0x31, 0x00, 0x00, 0x00, 0x00 });

            _cachedHeartbeatFrames = new byte[13][];
            for (int i = 0; i < 13; i++)
            {
                byte dev = (byte)(18 + i);
                var frame = new byte[] { MozaProtocol.MessageStart, 0x00, 0x00, dev, 0x00 };
                frame[4] = MozaProtocol.CalculateWireChecksum(frame);
                _cachedHeartbeatFrames[i] = frame;
            }
        }

        private static byte[] BuildStaticFrame(byte[] body)
        {
            var frame = new byte[body.Length + 1];
            Array.Copy(body, 0, frame, 0, body.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(body);
            return frame;
        }

        private byte[] BuildSequenceCounterFrame()
        {
            byte seq = _sequenceCounter++;
            _cachedSequenceFrame[9] = seq;
            _cachedSequenceFrame[10] = MozaProtocol.CalculateWireChecksum(
                _cachedSequenceFrame, _cachedSequenceFrame.Length - 1);
            // Return a copy: the write queue holds a reference until the write thread
            // drains it, and we mutate _cachedSequenceFrame on the next tick.
            var copy = new byte[_cachedSequenceFrame.Length];
            Array.Copy(_cachedSequenceFrame, copy, copy.Length);
            return copy;
        }

        // ── Periodic streams ────────────────────────────────────────────────

        public volatile int DetectedDeviceMask;

        private void SendHeartbeat()
        {
            int mask = DetectedDeviceMask;
            for (int i = 0; i < _cachedHeartbeatFrames.Length; i++)
            {
                if (mask == 0 || (mask & (1 << i)) != 0)
                    _connection.Send(_cachedHeartbeatFrames[i]);
            }
        }

        private void SendDashKeepalive()
        {
            // TelemetryServer periodic connection ping (group 0x43, N=1, data=0x00).
            // Pithouse sends to 0x14 (dash), 0x15, and 0x17 (wheel) every ~1.1s.
            // Distinct from group 0x00 heartbeats and SerialStream fc:00 acks.
            // Unclear whether the wheel requires this for telemetry to flow, but
            // Pithouse sends it consistently (~15× per session).
            _connection.Send(_dashKeepaliveFrameDash);
            _connection.Send(_dashKeepaliveFrame15);
            _connection.Send(_dashKeepaliveFrameWheel);
            // CM2 standalone path: add a ping at 0x12 (CM2 bridge/main) so the
            // device keeps its connection state warm against the active target.
            if (IsStandaloneDashboardTarget && _targetDeviceId == MozaProtocol.DeviceMain)
                _connection.Send(_dashKeepaliveFrameMain);
        }

        private static byte[] BuildKeepaliveFrame(byte dev)
        {
            var frame = new byte[] { MozaProtocol.MessageStart, 0x01, MozaProtocol.TelemetrySendGroup, dev, 0x00, 0x00 };
            frame[5] = MozaProtocol.CalculateWireChecksum(frame);
            return frame;
        }

        /// <summary>
        /// Re-send the 28:00 + 28:01 read commands matching PitHouse's
        /// observed cadence. Across all four bridge captures
        /// (sim/logs/bridge-20260503-*.jsonl) PitHouse polls these channels
        /// at ~1 Hz throughout the active phase; plugin currently sends
        /// each only once at preamble (SendChannelConfig line 3219-3220).
        /// Replies are captured raw in MozaData by the inbound filter at
        /// MozaPlugin.cs:1280 — semantics not yet decoded, so the bytes
        /// surface in Diagnostics for offline correlation.
        /// </summary>
        private void Send28xPoll()
        {
            // Past-preamble guard: only valid once we've reached Active.
            if (_state != TelemetryState.Active) return;
            if (!_connection.IsConnected) return;
            _connection.Send(BuildGroup40Frame3(0x28, 0x00, 0x00));
            _connection.Send(BuildGroup40Frame3(0x28, 0x01, 0x00));
        }

        // Widget-state poll cycle. Per bridge capture
        // (sim/logs/bridge-20260503-115840.jsonl) PitHouse continuously emits
        // a family of grp 0x40 dev 0x17 polls every dash phase at ~0.2/s
        // each. Plugin previously sent none; wheel widget likely treats
        // their absence as "host not actively managing widget" and stays
        // inactive after a dash switch.
        //
        // Three categories observed in the capture:
        //   STATIC: identical payload across 95+ frames per session — looks
        //     like state polls / status reads. Sub-cmds 00/01/03 (skip 02)
        //     suggest 3 page/zone slots.
        //   SCAN-1e: byte 4 cycles 02..06 — 5-index sweep, payload 1e0X 0Y 00 00
        //   SCAN-1f: byte 5 cycles 02..0f with byte 4 = 0xff — 14-index sweep
        //
        // Implementation: emit BURST of widget-poll frames per slow tick to
        // match PitHouse's ~0.2/s per-frame cadence. Cycle 58 slots; with
        // 10 emits per slow tick (~1Hz), each fires ~0.17/s ≈ PitHouse.
        private int _widgetPollIndex;
        private void SendWidgetStatePoll()
        {
            // Past-preamble guard: only valid once we've reached Active.
            if (_state != TelemetryState.Active) return;
            if (!_connection.IsConnected) return;
            SendOneWidgetPoll();
        }

        private void SendOneWidgetPoll()
        {
            int idx = _widgetPollIndex++;
            // Cycle layout (slot ranges):
            //   0..14   = 15 grp 0x40 dev 0x17 static polls
            //   15..29  = 15 grp 0x40 dev 0x17 1e0x scan (5×3)
            //   30..43  = 14 grp 0x40 dev 0x17 1f00 scan
            //   44..57  = 14 grp 0x40 dev 0x17 1f01 scan
            //   58..69  = 12 grp 0x0E dev 0x12/13/17/19 discovery probes
            //   70..73  = 4 grp 0x1F dev 0x12 4f08-4f0b LED state reads
            //   74..79  = 2 grp 0x3F dev 0x17 1a01/1a03 display variants (slots
            //             76/77; 74/75/78/79 deleted — see block below for why).
            //             The 80-slot total is kept so other slots keep their
            //             modulo offsets stable.
            const int totalCycle = 80;
            int slot = idx % totalCycle;

            byte[]? frame = null;
            if (slot < 15)
            {
                frame = slot switch
                {
                    0 => BuildGroup40Bytes(new byte[] { 0x1B, 0x00, 0xFF, 0x00, 0x00 }),
                    1 => BuildGroup40Bytes(new byte[] { 0x1B, 0x01, 0xFF, 0x00, 0x00 }),
                    2 => BuildGroup40Bytes(new byte[] { 0x1B, 0x03, 0xFF, 0x00, 0x00 }),
                    3 => BuildGroup40Bytes(new byte[] { 0x1C, 0x00, 0x00 }),
                    4 => BuildGroup40Bytes(new byte[] { 0x1C, 0x01, 0x00 }),
                    5 => BuildGroup40Bytes(new byte[] { 0x1C, 0x03, 0x00 }),
                    6 => BuildGroup40Bytes(new byte[] { 0x1D, 0x00, 0x00 }),
                    7 => BuildGroup40Bytes(new byte[] { 0x1D, 0x01, 0x00 }),
                    8 => BuildGroup40Bytes(new byte[] { 0x1D, 0x03, 0x00 }),
                    9 => BuildGroup40Bytes(new byte[] { 0x20, 0x00 }),
                    10 => BuildGroup40Bytes(new byte[] { 0x21, 0x00, 0x00 }),
                    11 => BuildGroup40Bytes(new byte[] { 0x27, 0x00, 0x00, 0x00, 0x00, 0x00 }),
                    12 => BuildGroup40Bytes(new byte[] { 0x28, 0x00, 0x00 }),
                    13 => BuildGroup40Bytes(new byte[] { 0x29, 0x00, 0x00 }),
                    14 => BuildGroup40Bytes(new byte[] { 0x2A, 0x00, 0x00 }),
                    _ => null,
                };
            }
            else if (slot < 30)
            {
                int s = slot - 15;
                byte sub = (byte)((s / 5) == 0 ? 0x00 : (s / 5) == 1 ? 0x01 : 0x03);
                byte b4 = (byte)(0x02 + (s % 5));
                frame = BuildGroup40Bytes(new byte[] { 0x1E, sub, b4, 0x00, 0x00 });
            }
            else if (slot < 44)
            {
                byte b5 = (byte)(0x02 + (slot - 30));
                frame = BuildGroup40Bytes(new byte[] { 0x1F, 0x00, 0xFF, b5, 0x00, 0x00, 0x00 });
            }
            else if (slot < 58)
            {
                byte b5 = (byte)(0x02 + (slot - 44));
                frame = BuildGroup40Bytes(new byte[] { 0x1F, 0x01, 0xFF, b5, 0x00, 0x00, 0x00 });
            }
            else if (slot < 70)
            {
                // grp 0x0E discovery probes — wheel/base device discovery
                int s = slot - 58;
                byte dev = (byte)(s / 3 switch
                {
                    0 => 0x12, 1 => 0x13, 2 => 0x17, _ => 0x19,
                });
                int sub = s % 3;
                byte cmd = (byte)(sub == 0 ? 0x00 : sub == 1 ? 0x01 :
                    (dev == 0x12 ? 0x03 : dev == 0x13 ? 0x07 :
                     dev == 0x17 ? 0x0F : 0x13));
                frame = BuildGenericFrame(0x0E, dev, new byte[] { 0x00, cmd });
            }
            else if (slot < 74)
            {
                // grp 0x1F dev 0x12 cmd 4f08-4f0b — LED state reads
                byte cmd2 = (byte)(0x08 + (slot - 70));
                frame = BuildGenericFrame(0x1F, 0x12, new byte[] { 0x4F, cmd2, 0x00 });
            }
            else
            {
                // grp 0x3F dev 0x17 display variants. Only slots 2 (buttons-
                // bitmask) and 3 (knob-bitmask) survive, both gated on
                // IsLiveAnywhere() — PitHouse-derived bridge captures only
                // emit these with active=0/window=0 (286/286 in
                // bridge-20260517-081336.jsonl) because PitHouse drives no
                // dynamic knob telemetry. Our plugin does; writing those bytes
                // on top of an active session briefly drops the firmware out
                // of "telemetry owns the LEDs" → revert to EEPROM defaults
                // until the next non-zero frame, which the user sees as a
                // ~87 s default-colour flash.
                //
                // Slots 0/1/4/5 were here too at one point but a sweep across
                // 55 bridge captures showed PitHouse never emits the exact
                // bytes the plugin was sending: slot 0 (19 01 00) / slot 1
                // (19 03 00) are mis-sized button/knob colour-chunk writes
                // that the firmware would interpret as "set LED 0 to black"
                // per the colour-commands padding rule. Slot 4
                // (1F 00 FF 00 00 00 00) writes to an idx=0xFF padding slot
                // and PitHouse never sends it. Slot 5 (21 00 00,
                // wheel-idle-timeout=0) IS something PitHouse sends, but
                // sporadically on user setting change, not periodically —
                // emitting it every ~87 s silently overrides whatever
                // idle-timeout the user set in the plugin UI. The legitimate
                // path (UI / saved-settings apply) writes wheel-idle-timeout
                // with the user's chosen value via
                // MozaWheelSettingsControl.cs:1180 and HardwareApplier.cs:181;
                // the widget-poll slot has no business reasserting 0 on top.
                int s = slot - 74;
                bool liveActive = Devices.MozaLedDeviceManager.IsLiveAnywhere();
                frame = s switch
                {
                    2 => liveActive ? null : BuildGenericFrame(0x3F, 0x17, new byte[] { 0x1A, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
                    3 => liveActive ? null : BuildGenericFrame(0x3F, 0x17, new byte[] { 0x1A, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
                    _ => null,
                };
            }

            if (frame != null) _connection.Send(frame);
        }

        // Build any group/dev frame from raw payload bytes. Wire layout is
        // [start, length, grp, dev, payload..., checksum] — total = payload.Length + 5.
        private byte[] BuildGenericFrame(byte grp, byte dev, byte[] payload)
        {
            var frame = new byte[payload.Length + 5];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = grp;
            frame[3] = dev;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        // Build grp=0x40 dev=0x17 frame from raw payload bytes.
        private byte[] BuildGroup40Bytes(byte[] payload)
        {
            var frame = new byte[payload.Length + 5];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = 0x40;
            frame[3] = MozaProtocol.DeviceWheel;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private void SendDisplayConfig()
        {
            int pageCount = _profile?.PageCount ?? 1;
            if (pageCount < 1) pageCount = 1;
            EnsureDisplayConfigCache(pageCount);

            int page = _displayConfigPage % pageCount;
            _displayConfigPage++;

            var frames = _cachedDisplayConfigFrames!;
            int baseIdx = page * 3;
            _connection.Send(frames[baseIdx + 0]);
            // 7C:23 dashboard-activate: tells the wheel which dashboard pages are
            // active. PitHouse sends one per page interleaved with 7C:27 at ~1 Hz.
            _connection.Send(frames[baseIdx + 1]);
            _connection.Send(frames[baseIdx + 2]);
        }

        private void EnsureDisplayConfigCache(int pageCount)
        {
            if (_cachedDisplayConfigFrames != null && _cachedDisplayConfigPageCount == pageCount)
                return;

            var frames = new byte[pageCount * 3][];
            for (int page = 0; page < pageCount; page++)
            {
                byte b2 = (byte)(0x05 + 2 * page);
                byte b4 = (byte)(0x03 + 2 * page);
                byte z  = (byte)(0x06 + 2 * page);
                byte ab2 = (byte)(0x07 + 2 * page);
                byte ab4 = (byte)(0x05 + 2 * page);

                var configFrame = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                    MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x80, b2, 0x00, b4, 0x00, 0xFE, 0x01, 0x00 };
                configFrame[14] = MozaProtocol.CalculateWireChecksum(configFrame);

                var activateFrame = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                    MozaProtocol.DeviceWheel, 0x7C, 0x23, 0x46, 0x80, ab2, 0x00, ab4, 0x00, 0xFE, 0x01, 0x00 };
                activateFrame[14] = MozaProtocol.CalculateWireChecksum(activateFrame);

                var configFrame2 = new byte[] { MozaProtocol.MessageStart, 0x06, MozaProtocol.TelemetrySendGroup,
                    MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x00, z, 0x00, 0x00 };
                configFrame2[10] = MozaProtocol.CalculateWireChecksum(configFrame2);

                frames[page * 3 + 0] = configFrame;
                frames[page * 3 + 1] = activateFrame;
                frames[page * 3 + 2] = configFrame2;
            }

            _cachedDisplayConfigFrames = frames;
            _cachedDisplayConfigPageCount = pageCount;
        }

        public void Dispose()
        {
            // Idempotent: SimHub may invoke Dispose more than once during plugin
            // reload; double-dispose on ManualResetEventSlim throws.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Cancel any in-progress Start so StartInner bails out promptly
            // rather than continuing to touch about-to-be-disposed sessions/
            // events. The CTS itself is owned by the Start() finally block;
            // we just signal.
            CancellationTokenSource? cts;
            lock (_startCtsLock) { cts = _startCts; }
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }

            Stop();
            try { _ackReceived.Dispose(); } catch { }
            try { _mgmtResponseEvent.Dispose(); } catch { }
            try { _uploader?.Dispose(); } catch { }
            try { _dashboardDownloader?.Dispose(); } catch { }
            try { _rpc?.Dispose(); } catch { }
            try { _startSemaphore.Dispose(); } catch { }
        }

        internal class TierState
        {
            public TelemetryFrameBuilder Builder = null!;
            public int TickInterval;
            // Pristine copy of the tier's channels as set up by the Profile
            // setter, BEFORE SortTierChannelsByCatalogIdx mutated them.
            // ApplySubscription resets tier.Channels from this on each call
            // so the filter sees the full channel set every time and can
            // pick up additions from a recently-updated catalog. Without
            // this, an early ApplySubscription against a stale/partial
            // catalog would strip channels permanently — verified 2026-05-15
            // post-dashboard-switch where the wheel sends SR catalog over
            // ~1.2s after the Stop+Start cycle, but plugin's first
            // ApplySubscription fired with Mono's catalog still in place.
            public System.Collections.Generic.List<ChannelDefinition>? OriginalChannels;
            // Pristine TotalBits/TotalBytes paired with OriginalChannels.
            public int OriginalTotalBits;
            public int OriginalTotalBytes;
        }
    }
}
