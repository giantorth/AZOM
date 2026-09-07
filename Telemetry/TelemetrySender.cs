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
using Timer = System.Timers.Timer;
using MozaPlugin.Telemetry.Lifecycle;

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
    public partial class TelemetrySender : IDisposable
    {
        private MozaSerialConnection _connection;
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
        private int _displayConfigPage;

        // _tierDefPreambleSent moved to TierDefinitionEmitter.
        private int _preambleTickTarget;
        internal int _sessionAckSeq;

        // Session open/close state machine + ack latch + contig-ack tracking —
        // see Sessions/SessionLifecycle.cs. Constructed in the ctor before the
        // uploader (whose delegates point at it).
        private readonly Sessions.SessionLifecycle _sessionLife;
        internal Sessions.SessionLifecycle SessionLife => _sessionLife;

        // Cold-start preamble gate. Set true in StartInner ONLY on a true
        // cold start (first start in this SimHub process); read by
        // TickPreamble to hold the preamble→Active transition (and thus the
        // first tier-def emission) until the wheel signals session-layer
        // readiness via sess=0x09 device-init (_wheelReadyObserved). On a cold
        // base the wheelbase acks our session opens and advertises a few
        // intrinsic channels (RPM/Gear/Speed) immediately, but its DISPLAY
        // sub-device is still booting — emitting tier-def then triggers the
        // sess=0x01 CLOSE storm and the dashboard never binds. Warm/hot-switch
        // reloads leave this false so they add ZERO latency. Cleared in
        // InitTickStateAndTransitionToStarting (so a stale cold flag can't
        // survive into a later warm Start) and re-set in StartInner.
        private volatile bool _coldStartWheelGatePending;
        // One-shot guard so the "waiting for sess=0x09" hold logs exactly once
        // per cold start rather than on every held tick. Reset alongside the
        // gate flag in InitTickStateAndTransitionToStarting.
        private bool _coldStartGateLogged;

        // Catalog-on-wrong-session wedge recovery. When the cold-start gate times
        // out with the wheel's real catalog on the FLAG session (not the tier-def
        // session), emitting the tier-def carries END=0 against a real END — the
        // wheel either CLOSEs (REJECT) or silently ignores it (stuck Active + blank).
        // The only fix that binds is the wheel re-advertising on the tier-def session,
        // which a full cold re-provoke (Stop → ~11 s sess=0x09 settle → Start) re-rolls.
        // _forceColdReprovoke (1) tells the next StartInner to take the cold path even
        // on a warm restart; _coldStartReprovokeAttempts bounds the retry so a
        // genuinely-stuck wheel still proceeds instead of looping forever.
        private int _forceColdReprovoke;
        private int _coldStartReprovokeAttempts;
        private const int MaxColdStartReprovokes = 3;


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
        internal int _session02OutboundSeq;

        // Guard for the read-chunk-send-write of _session02OutboundSeq +
        // _propertyPushLastSeqs. Without it, the timer thread (V0 value
        // frames), the UI thread (brightness / dashboard switch property
        // pushes), and background StartInner (session-init handshake,
        // tier-def) can race: two threads reading the same seq each emit
        // their N frames, and whichever finishes last overwrites the
        // higher value with a lower one. The wheel keys retransmit
        // suppression per literal seq, so a regression makes it drop
        // chunks as duplicates and the upstream message stays stuck.
        internal readonly object _session02SeqLock = new object();

        // Same rationale as _session02SeqLock but for the mgmt session.
        // SendTierDefinition targets the session ResolveTierDefSession()
        // returns (0x01 unless the wheel's real catalog lands on the flag
        // session).
        private readonly object _session01SeqLock = new object();

        // Per-chunk retransmit until fc:00 ack drains the queue.
        // See docs/protocol/sessions/chunk-format.md.
        private readonly global::MozaPlugin.Telemetry.Sessions.SessionRetransmitter _retransmitter
            = new global::MozaPlugin.Telemetry.Sessions.SessionRetransmitter();

        // Property-push coalescing moved to PropertyPushQueue.

        // Blind retransmit state moved to TierDefinitionEmitter.

        // Sess=0x09 retry + engagement watchdog state moved to
        // DisplayWatchdog. See Telemetry/Watchdog/DisplayWatchdog.cs.

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

        /// <summary>Resolves the connected wheel's model info (null until
        /// identity resolves). Set by MozaPlugin at sender wiring; used to
        /// force display rotation off on non-VGS display wheels at session
        /// init. Only wired on the main wheel sender, never the CM2 sender.</summary>
        internal Func<Devices.WheelModelInfo?>? WheelModelInfoProvider { get; set; }

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
        // Content hash of the catalog URLs at the last synthesis. The wheel
        // streams its catalog via abbreviated/back-ref chunks the parser
        // reconstructs and corrects IN PLACE, so the channel set can change
        // while count and END marker stay constant. Without tracking content,
        // an early synth built against an incomplete catalog (channels whose
        // URLs hadn't yet resolved → empty SimHubProperty → live value 0) was
        // never refreshed until a dashboard switch bumped the END marker —
        // live game data stayed blank while test mode (which bypasses property
        // resolution) worked. Re-synthesise when this hash changes too.
        // FNV-1a hash, not a joined string: this compare runs every tick on
        // the timer thread for catalog-synthesised profiles, and joining a
        // 200-URL catalog allocated ~KBs per tick.
        private ulong _catalogHashAtSynthesis;

        // Catalog-stability debounce. The wheel advertises its catalog
        // PROGRESSIVELY at startup / dashboard switch (END 6→80→97 over ~3 s,
        // with URL back-refs corrected in place), so the count/signature change
        // many times in a burst. Synthesising + emitting a tier-def on every one
        // of those steps "storms" the wheel — it re-binds the dashboard dozens of
        // times while trying to render, which freezes/garbles widgets (e.g. the
        // radar). PitHouse emits ONE tier-def. We mirror that: when the catalog
        // changes we record it and wait until it has held steady for
        // CatalogDebounceTicks before synthesising, collapsing the burst into a
        // single emit. force=true (explicit swap / hot-switch) bypasses this.
        private ulong _pendingCatalogHash;
        private int _pendingCatalogCount = -1;
        private long _pendingCatalogSinceTicks;
        private static readonly long CatalogDebounceTicks =
            TimeSpan.FromMilliseconds(750).Ticks;

        // FNV-1a over every catalog URL + a separator — the allocation-free
        // equivalent of comparing string.Join("\n", catalog) values.
        private static ulong ComputeCatalogHash(System.Collections.Generic.IReadOnlyList<string> catalog)
        {
            ulong h = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            for (int i = 0; i < catalog.Count; i++)
            {
                var s = catalog[i];
                if (s != null)
                {
                    for (int j = 0; j < s.Length; j++)
                    {
                        h ^= s[j];
                        h *= prime;
                    }
                }
                h ^= 0x0A;
                h *= prime;
            }
            return h;
        }

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

        // Post-switch catalog convergence watcher. Armed by every committed
        // dashboard switch; polls the host's catalog view and nudges the
        // wheel with kind=4 re-emits until N consecutive identical
        // signatures arrive. Belt-and-suspenders against a missed catalog
        // chunk leaving the host with an incomplete URL set the rest of
        // the static defences would never notice on their own.
        private readonly Lifecycle.PostSwitchCatalogConvergence _postSwitchConvergence
            = new Lifecycle.PostSwitchCatalogConvergence();

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
                _configJson.CacheKey = next;
                _frames.InvalidateDisplayConfig();
                // Rebuild per-tier frame builders with the new dev_id so value
                // frames address the right device on the next tick.
                RebuildFrameBuildersForTargetDevice();
                MozaLog.Debug($"[AZOM] Telemetry target device set to {TargetDescription}");
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
            _builtWithResolverTarget = PropertyResolver?.Target;
        }

        // Delegate Target (= the SimHubPropertyResolver instance) that the current
        // tier frame builders captured their value resolver from. On a SimHub
        // plugin reload the persistent sender is reused but its builders still hold
        // the dead OLD instance's resolver, and keepExistingSynth skips the Profile
        // setter that would rebuild them — so live channels resolved through the
        // dead plugin and froze at 0 (test mode unaffected: it reads ch.TestSignal,
        // not the resolver). Tracked so RebindFrameBuildersToResolver re-points them
        // exactly once when the resolver instance actually changes.
        private object? _builtWithResolverTarget;

        /// <summary>Re-point each tier frame builder's captured value resolver to
        /// the current <see cref="PropertyResolver"/>, host-side only — no tier-def
        /// re-emit, no wire traffic. No-op unless the resolver INSTANCE changed, so
        /// the common same-instance ApplyTelemetrySettings path costs one ref
        /// compare. Call after (re)assigning PropertyResolver; the load-bearing case
        /// is the persistent sender reused across a plugin reload under
        /// keepExistingSynth (which otherwise leaves the builders on the dead
        /// instance's resolver and freezes the live dashboard).</summary>
        internal void RebindFrameBuildersToResolver()
        {
            if (ReferenceEquals(PropertyResolver?.Target, _builtWithResolverTarget)) return;
            RebuildFrameBuildersForTargetDevice();
        }

        // Session 0x09 configJson RPC. Device pushes dashboard state; we reply
        // with the canonical library list. See docs/protocol/dashboard-upload/config-rpc-session-09.md.
        private readonly ConfigJsonClient _configJson = new ConfigJsonClient();
        internal int _session09InboundSeq;
        // NEXT h2b data seq to use on sess=0x09. The wheel tracks h2b data
        // only from devinit open-seq + 3; seqs below that base are ignored,
        // and a SKIPPED seq pins the wheel's cumulative rx ack at the hole so
        // nothing behind it is ever accepted. Seeded by
        // SeedSession09OutboundSeq from the wheel's 0x81 device-init.
        private int _session09OutboundSeq;
        private volatile bool _session09SeqSeeded;
        private int _session09SeedOpenSeq = -1;

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

        /// <summary>Host-sent data chunks on a session this Start cycle. Read by
        /// <see cref="Lifecycle.DisplayWatchdog"/> Context C so it never judges a
        /// lane the host has not actually driven.</summary>
        internal int SessionOutboundCount(byte session)
        {
            lock (_sessionCounts)
                return _sessionCounts.TryGetValue(session, out var pair) ? pair.Out : 0;
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

        /// <summary>Pre-calibration hint for which u32 field the type-04 slot
        /// record uses; <see cref="WheelSlotTracker"/> auto-detects the real
        /// value from the wheel's kind=4 echo and overrides this. W13/FSR2 is the
        /// LONE other-format wheel (slot in field B [5..9]); W17/W18/CS/KS and
        /// every other — including future — 2026-era wheel use field A [1..5]. So
        /// default to field A and treat W13/FSR as the exception.</summary>
        internal bool SlotInFieldA
        {
            get
            {
                string m = (MozaPlugin.Instance?.Data?.WheelModelName ?? "")
                    .ToUpperInvariant();
                return !(m.Contains("W13") || m.Contains("FSR"));
            }
        }

        // Cached + static frame construction — see Frames/TelemetryFrameCache.cs.
        // Allocated in the constructor; BuildCachedFrames() runs per Start.
        private readonly Frames.TelemetryFrameCache _frames;

        // Session ports determined during port probing.
        // MgmtPort = first acked port (session 0x01, used for dashboard upload).
        // FlagByte = second acked port (session 0x02, used for tier definitions and fc:00 acks).
        internal byte _mgmtPort;
        public byte FlagByte { get; set; } = 0x02;

        /// <summary>Which session the configJson exchange latched to: 0 =
        /// unresolved, 0x0a = modern channel (latched at first sight of a
        /// wheel push there), 0x09 stays implicit for legacy wheels. Set by
        /// the inbound dispatcher; reset on Stop.</summary>
        internal volatile byte ConfigSessionLatch;
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
                    MozaLog.Debug($"[AZOM] TestMode changed to {value}");
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
        // Tick of last "value-frame emission suppressed because telemetry is
        // disabled for the active overlay" reminder log. Driven by
        // TickEmitValueFrames so users with a disabled overlay see in
        // SimHub.txt (INFO level) that the dash isn't updating BECAUSE of
        // the per-overlay toggle, not because the plugin is broken. Reminder
        // fires every SuppressedReminderMs while the gate stays closed.
        private int _profileTelemetryDisabledLastReminderTickMs;
        private const int SuppressedReminderMs = 30_000;
        public bool ProfileTelemetryEnabled
        {
            get => _profileTelemetryEnabled;
            set
            {
                if (_profileTelemetryEnabled != value)
                {
                    _profileTelemetryEnabled = value;
                    // INFO level so the transition lands in SimHub.txt by
                    // default — users debugging "my dash stopped updating
                    // after I changed profiles" need to see this without
                    // having to enable DEBUG logging. The per-overlay
                    // telemetry-enable bit defaults to false for any overlay
                    // that hasn't had the toggle flipped on, so switching
                    // SimHub profiles can silently kill emission. Observed
                    // 2026-05-27 CS-Pro bundle: user switched profiles, dash
                    // froze, fixed it by toggling telemetry back on.
                    if (value)
                    {
                        MozaLog.Info("[AZOM] Wheel telemetry enabled — resuming value-frame emission");
                        // Reset reminder window so the next disable-cycle's
                        // first reminder doesn't fire instantly.
                        _profileTelemetryDisabledLastReminderTickMs = 0;
                    }
                    else
                    {
                        MozaLog.Info(
                            "[AZOM] Wheel telemetry DISABLED for the active SimHub overlay — " +
                            "value frames will be suppressed until you toggle telemetry on for this overlay. " +
                            "Wheel-side keepalives continue but the dashboard will not update.");
                        // Stamp NOW so the periodic reminder doesn't fire
                        // before SuppressedReminderMs has elapsed since the
                        // disable event itself.
                        _profileTelemetryDisabledLastReminderTickMs = Environment.TickCount;
                    }
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
            MozaLog.Debug($"[AZOM] phase-marker phaseId=0x{phaseId:X2} ({phaseId})");
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

        /// <summary>Channel URLs for the dashboard the wheel currently has
        /// loaded. LiveCatalog, not Catalog, for the same reason
        /// <see cref="Frames.TierDefinitionEmitter"/> uses it: Catalog is the
        /// never-pruned union of every generation this connection has seen, so
        /// after a dashboard switch it still carries the previous dash's URLs at
        /// any index the new generation didn't overwrite. Rendering that union
        /// made the diagnostics tab and the channel-mapper show one URL at two
        /// indices — bundles EJ92X08Y / W0V1PF9V (2026-08-23) showed Gear at
        /// idx 5 and 8 and SpeedKmh at 7 and 9 when the wheel had only
        /// advertised each once. Falls back to the union before the first
        /// generation commits. Null until parsed.</summary>
        public System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalog =>
            _catalogParser.LiveCatalog ?? _catalogParser.Catalog;

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
            // Serialized: the setter runs on UI/init, the serial read thread
            // (OnWheelInitiatedSwitch → null) and the tick thread
            // (MaybeSwapProfileForCatalog); unserialized runs interleave and
            // can publish one caller's _profile with another's _tiers. Body
            // is pure computation + logging — no I/O waits (leaf lock).
            set { lock (_profileSetLock) SetProfileLocked(value); }
        }
        private readonly object _profileSetLock = new object();

        public TelemetrySender(MozaSerialConnection connection)
        {
            _connection = connection;
            _frames = new Frames.TelemetryFrameCache(this);
            _silenceGate = new Lifecycle.SilenceGate(() => EnableHotRenegotiation);
            _recovery = new Lifecycle.RecoveryDispatcher(this);
            _watchdog = new DisplayWatchdog(this);
            _slotTracker = new Display.WheelSlotTracker(this);
            // Wire the catalog parser to HotSwitchCoordinator's arm count.
            // This is the switch-boundary signal that gates REPLACE vs UNION
            // in CommitLiveSet — see ChannelCatalogParser._getArmCount field
            // docs. Every ArmBurst (host- or wheel-initiated switch) bumps
            // the count, so even rapid-fire switches get clean boundaries.
            _catalogParser.SetArmCountProvider(() => _hotSwitch.ArmCount);
            _propertyPushQueue = new PropertyPushQueue(this);
            _tierDefEmitter = new Frames.TierDefinitionEmitter(this);
            _inboundDispatcher = new Lifecycle.TelemetryInboundDispatcher(this);
            _sessionLife = new Sessions.SessionLifecycle(this);
            _rpc = new RpcCallChannel(
                connection,
                shouldAbort: () => _state == TelemetryState.Idle || !_connection.IsConnected);
            _uploader = new WheelUploadCoordinator(
                connection,
                shouldAbort: () => _state == TelemetryState.Idle || !_connection.IsConnected,
                getPolicy: () => _policy,
                getConfigJsonState: () => _configJson.LastState,
                sendSessionAck: _sessionLife.SendSessionAck,
                sendSessionEnd: _sessionLife.SendSessionEnd,
                sendAndTrackChunk: SendAndTrackChunk,
                sendSessionOpen: _sessionLife.SendSessionOpen,
                sendFileTransferActivate: _sessionLife.SendFileTransferActivate,
                getRetransmitBacklog: () => Retransmitter.QueueSize);

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
                        MozaLog.Info($"[AZOM] Dashboard upload \"{name}\": Succeeded");
                        // Uploaded dashboards land in the wheel's
                        // disabledManager; the wheel enables the ones present
                        // in the host's configJson() library list (ground
                        // truth: PitHouse re-sends its list right after an
                        // upload — the dash in its list flipped enabled, the
                        // one missing from it stayed disabled). Add the name
                        // and re-send the list so the wheel's picker offers
                        // the new dash.
                        EnableUploadedDashboard(_uploader.MzdashName);
                        // No reconnect after uploads — enable works live via
                        // the staged-install + list declaration; the reconcile
                        // bounce is reserved for deletes (dead-slot cleanup).
                        break;
                    case WheelUploadCoordinator.UploadOutcome.SkippedHashMatch:
                        MozaLog.Info($"[AZOM] Dashboard upload \"{name}\": SkippedHashMatch");
                        break;
                    case WheelUploadCoordinator.UploadOutcome.Aborted:
                        MozaLog.Debug($"[AZOM] Dashboard upload \"{name}\": Aborted");
                        break;
                    default:
                        MozaLog.Warn($"[AZOM] Dashboard upload \"{name}\": {outcome}");
                        break;
                }
            };
        }

        /// <summary>
        /// Repoint the sender at a different serial connection — used when the
        /// active dashboard sink moves between the wheelbase connection
        /// (wheel-hosted 0x17 / base-bridged CM2 0x14) and a dedicated
        /// standalone-USB dashboard connection (CM2 0x12). MUST be called while
        /// Idle (no live session on the old connection); the caller checks
        /// <see cref="StateIsIdle"/>. The inbound-dispatcher subscription is
        /// (re)attached on the next Start; defensively detach from the old
        /// connection here in case one lingers.
        /// </summary>
        internal void Rebind(MozaSerialConnection connection)
        {
            if (ReferenceEquals(connection, _connection)) return;
            try { _connection.MessageReceived -= _inboundDispatcher.OnMessageDuringPreamble; } catch { }
            _connection = connection;
            _rpc.Rebind(connection);
            _uploader.Rebind(connection);
            MozaLog.Debug($"[AZOM] TelemetrySender rebound to {connection.CaptureLabel} connection");
        }

        // Caller passes the MozaPlugin instance directly because Init may call
        // ApplyTelemetrySettings BEFORE MozaPlugin.Instance is assigned, so the
        // static accessor is null at this point and the delegate binding throws
        // ArgumentException("Delegate to an instance method cannot have null 'this'").
        internal void EnableAutoTest(MozaPlugin plugin)
        {
            // Always rebuild: the delegates capture the plugin instance, and this
            // sender survives the game-switch reload while that instance does not —
            // a Reset() here kept the first Init's plugin pinned for the process.
            _autoTest = new DashboardSwitchAutoTest(
                this,
                plugin.ResolveDashboardProfileByName,
                () => plugin.DashCache,
                name => { plugin.ActiveTelemetryProfileName = name; });
        }

        internal void DisableAutoTest() => _autoTest = null;

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
            _sessionLife.DisposeAckEvent();
            try { _mgmtResponseEvent.Dispose(); } catch { }
            try { _uploader?.Dispose(); } catch { }
            try { _dashboardDownloader?.Dispose(); } catch { }
            try { _rpc?.Dispose(); } catch { }
            try { _recovery?.Dispose(); } catch { }
            // _startSemaphore is deliberately not disposed: a Start() can still be
            // inside Wait()/Release() (callers catch the ObjectDisposedException,
            // but there is nothing to reclaim — a SemaphoreSlim whose WaitHandle
            // was never touched holds no OS resource).
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
            // Lowest radar/track-map per-car slot index this tier carries (ri{N} /
            // Location_{N}), or 0 if the tier has none (normal channels / the radar
            // fast tier's slot-0 set). Used to skip emitting overflow / high
            // track-map sub-tiers per-tick when no live car reaches their slot range
            // (dynamic, grid-sized emission). 0 => always emitted.
            public int MinRadarSlot;
        }
    }
}
