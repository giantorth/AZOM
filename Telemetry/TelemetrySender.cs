using System;
using System.Linq;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
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
    /// Periodically encodes game data and sends telemetry frames to the wheel.
    ///
    /// Startup follows PitHouse's observed sequence (from USB capture analysis):
    ///   1. Open management session 0x01 + telemetry session 0x02 directly
    ///   2. Send session preamble (sub-message 1) then tier definition on telemetry session
    ///   3. Ack incoming channel data on telemetry session with fc:00 (~1 second)
    ///   4. Send 0x40 channel config burst (1E enables, 28:00, 28:01, 09:00, 28:02)
    ///   5. Begin 0x41 enable signal + 7d:23 telemetry with flag=0x00+tier
    ///
    /// Session allocation: mirrors PitHouse — sessions 0x01 (mgmt) and 0x02
    /// (telem, also becomes FlagByte) are hardcoded rather than probed. The
    /// session byte identifies which 7c:00 stream carries config data; the
    /// flag bytes inside tier definitions and telemetry frames are always
    /// 0-based (0x00, 0x01, 0x02), independent of the session byte.
    ///
    /// Each tier in the MultiStreamProfile runs at its own rate derived from package_level.
    /// Flag bytes are 0x00 + tier index (sorted by package_level ascending).
    /// </summary>
    public class TelemetrySender : IDisposable, global::MozaPlugin.Telemetry2.IMozaTelemetry
    {
        private readonly MozaSerialConnection _connection;
        private Timer? _sendTimer;
        private TierState[]? _tiers;
        private volatile StatusDataBase? _latestGameData;
        private volatile bool _gameRunning;
        // Set true when SetGameRunning transitions false→true; consumed once
        // by the active-phase tick to fire the PitHouse-observed game-start
        // handshake (see SendGameStartHandshake).
        private volatile bool _gameStartHandshakePending;
        private bool[]? _tierDiagEmitted;
        // Lifecycle state — see TelemetryState. Replaces the prior _enabled /
        // _preambleComplete / _dashSwitchMuted booleans. All reads/writes are
        // through volatile semantics or the TransitionTo helper.
        private volatile TelemetryState _state = TelemetryState.Idle;
        private int _tickCounter;
        private int _slowCounter;
        private int _baseTickMs;  // Timer period derived from fastest tier's package_level
        private byte _sequenceCounter;
        private int _displayConfigPage;

        // Preamble state — _tierDefPreambleSent is a wire-protocol concern (has the
        // tag-0x07/0x03 preamble been emitted on the wire), distinct from the
        // lifecycle TelemetryState.Preamble (are we inside the preamble phase).
        /// <summary>True after the first V2 tier-def preamble (tag 0x07/0x03)
        /// has been sent this session. PitHouse only sends preamble once at
        /// connect; subsequent tier-def re-sends (dashboard switch) omit it.
        /// Wheel rejects/ignores tier-def that follows a duplicate preamble.</summary>
        private bool _tierDefPreambleSent;
        private int _preambleTickTarget;
        private int _sessionAckSeq;

        // Port probing state
        private volatile byte _lastAckedSession;  // Set by OnMessageDuringPreamble when fc:00 arrives
        private readonly ManualResetEventSlim _ackReceived = new ManualResetEventSlim(false);

        // Upload handshake state (legacy, kept for test harness)
        private int _mgmtAckSeq;
        private readonly ManualResetEventSlim _mgmtResponseEvent = new ManualResetEventSlim(false);

        // File-transfer session state. The device initiates one or more sessions
        // in 0x04..0x0a with type=0x81 before we send sub-msg 1; it echoes paths
        // back (sub-msg 1 rsp), then acks the content push (sub-msg 2 rsp), then
        // sends type=0x00 end. Session number is dynamic per firmware:
        //   2025-11: wheel opens 0x04 device-init; plugin uploads on 0x04.
        //   2026-04: wheel still opens 0x04 device-init but new firmware also
        //            accepts uploads on other ports the host requests via
        //            7c:23 46. Tracked here as `_uploadSession`.
        private readonly SessionRegistry _sessions = new SessionRegistry();
        private readonly SessionDispatcher _dispatcher = new SessionDispatcher();
        // Wheel-side mzdash upload coordinator. Owns the FT-eligible session
        // tracking, sub-msg state machine, and dir-listing reassembly that used
        // to live as a dozen scattered fields here. Constructed in the body of
        // the constructor below (after _connection is captured).
        private WheelUploadCoordinator _uploader = null!;

        /// <summary>
        /// Outbound seq counter for session 0x02 (telemetry). Tracks the next
        /// seq to use when sending V0 per-channel value frames in active phase.
        /// V2 telemetry uses group=0x43 cmd=0x7d23 directly (no session seq).
        /// </summary>
        private int _session02OutboundSeq;

        // Reliable-stream retransmit queue for session-data chunks. PitHouse
        // capture (2026-04-29) showed each unique session-02 chunk re-emitted
        // 50× until acked via fc:00 ack_seq; plugin previously fired-and-forgot
        // and ran ~70/s short on session-02 chunk rate. Track each chunk we
        // send, retransmit on tick if not yet acked, drop on ack.
        private readonly global::MozaPlugin.Diagnostics.SessionRetransmitter _retransmitter
            = new global::MozaPlugin.Diagnostics.SessionRetransmitter();

        // Latest chunk seqs of the most-recent FF property push of each
        // (session, kind). Lets SendSessionPropertyBody supersede a pending
        // push of the same kind by dropping its prior seqs from the retransmit
        // queue before queuing the new push.
        //
        // Without this coalescing, when the user drags the wheel-display
        // brightness slider quickly each ValueChanged fires its own FF chunk
        // that retransmits up to 100× (200ms intervals, ~20s total). Every
        // intermediate value — including any momentary pass through
        // brightness=0 — keeps firing alongside the latest, so the wheel sees
        // the stale values interleaved and the display stays blanked. Bundle1
        // of usb-capture/displaybrightnessbug shows exactly this: 88 unacked
        // chunks (ramp 1→100→0) retransmitting 48–68× each over 22s on a
        // wheel that wasn't fully engaged on session 0x02 (no acks at all).
        private readonly System.Collections.Generic.Dictionary<(byte session, uint kind), System.Collections.Generic.List<int>> _propertyPushLastSeqs
            = new System.Collections.Generic.Dictionary<(byte, uint), System.Collections.Generic.List<int>>();

        // Blind retransmission for session 0x01 tier-def chunks. PitHouse
        // retransmits each chunk ~10× regardless of acks (wheel never acks
        // session 0x01). See findings/2026-05-02-tier-def-retransmission.md.
        private byte[][]? _tierDefBlindFrames;
        private int _tierDefBlindSentRounds;
        private int _tierDefBlindLastTickCount;
        private static int TierDefBlindMaxRounds
            => global::MozaPlugin.Protocol.RetryBackoff.TierDefBlindMs.Length;

        // Sess=0x09 establishment retry. Cold-start emits the prime+open-request
        // pair once in PrimeAndOpenSession09; if the wheel doesn't respond with
        // device-init (b2h 7c 00 09 81 ...), the configJson handshake never
        // starts and the dashboard never renders driving telemetry. The tick
        // helper TickRetryS09IfNotEstablished re-emits the pair every
        // S09RetryIntervalMs until either the wheel device-inits 0x09 OR the
        // S09RetryMaxRounds budget is exhausted. Guarded by
        // _sessions.GetOrCreate(0x09).DeviceInitiated so steady-state and
        // post-switch sessions are untouched.
        private int _s09RetryRounds;
        private int _s09RetryLastTickCount;
        // Exponential backoff schedule for sess=0x09 retry. First retry is fast
        // to catch transient drops (Wine SerialPort R/W contention typically
        // recovers within ~250ms); later retries widen so a wheel that's slow
        // to engage isn't pummelled by a fixed-cadence retry storm. Total
        // budget ≈ 56s across 10 rounds vs the old 10s × fixed 1000ms.
        private static readonly int[] S09BackoffMs =
            { 250, 500, 1000, 2000, 3000, 5000, 7000, 10000, 12000, 15000 };
        private const int S09RetryMaxRounds = 10;

        // Wall-clock timestamp (DateTime.UtcNow.Ticks) of the last Stop()
        // completion. StartInner checks this on entry — if a fresh Stop
        // happened within MinSilenceAfterStopMs ago, Start sleeps the
        // remainder before opening sessions. The wheel has a ~10–14s
        // internal timeout on sess=0x09 bindings; firing fresh primes
        // inside that window is silently ignored, which is what makes
        // SimHub game-switch (plugin reload, Stop+Start in <1s) drop the
        // configJson handshake. Static so it survives plugin instance
        // recycle inside the same SimHub process — game-switch reloads
        // the plugin (new instance) but the wheel-side timeout is across
        // both instances.
        private static long _lastStopUtcTicks;
        private const int MinSilenceAfterStopMs = 11000;

        // ConfigJson chunk-drop recovery counters. The first response to a
        // dropped chunk is the cheapest possible: clear the in-progress
        // reassembly buffer and let the wheel's next push repopulate. If we
        // already have LastState cached from earlier in this session,
        // downstream consumers continue to render fine and we don't need to
        // do anything. If we don't have LastState (cold-start / first burst
        // dropped), the recovery escalates: prime + open-request, then after
        // a few failures, a full Stop+Start so the wheel-side state machine
        // resets and burst-from-scratch.
        private int _configJsonGapCount;
        private long _configJsonLastChunkUtcTicks;
        private long _configJsonLastEscalationUtcTicks;
        // Threshold for prime+open-request escalation when LastState is null.
        private const int ConfigJsonGapPrimeRetryAt = 1;   // first cold-start gap fires prime+open
        // Threshold for full RestartForSwitch escalation when prime+open
        // hasn't recovered the burst.
        private const int ConfigJsonGapRestartAt = 4;
        // Min gap between escalations so a chunk-drop storm can't cycle
        // RestartForSwitch faster than the wheel's 11 s sess=0x09 settle.
        private const long ConfigJsonEscalationCooldownTicks =
            15_000 * System.TimeSpan.TicksPerMillisecond;
        // No-progress watchdog: if LastState is still null this long after
        // the first chunk arrived on sess=0x09, the wheel is stuck and a
        // full Restart is the only known recovery.
        private const long ConfigJsonNoStateRestartTimeoutTicks =
            30_000 * System.TimeSpan.TicksPerMillisecond;

        // Wheel-catalog-growth tracking. The wheel pushes its channel URLs
        // in batches over the first ~1-3 seconds after sessions open, but
        // ApplySubscription runs once at end-of-preamble using whatever
        // catalog has arrived by then. URLs that arrive AFTER that initial
        // tier-def emission get chIndex=0 in the wheel's view (BuildTier-
        // DefinitionV2 default for unknown URLs), and dashboard widgets
        // bound to those URLs render frozen at zero — observed 2026-05-09
        // on Grids/Mono dashboards where tire pressure/temp/wear channels
        // (catalog slots 9-20) arrived ~200ms after preamble exited.
        // PitHouse mimics the wheel's growing-catalog by re-emitting tier-
        // def each time its catalog grows; we mirror that here.
        private int _catalogCountAtLastSubscription;
        private const int CatalogGrowthQuietMs = 400;
        private const int CatalogGrowthMinDelta = 1;

        // Count of catalog chunks rejected due to CRC32 mismatch. These
        // chunks would have produced garbled URLs in the parsed catalog
        // (observed 2026-05-09: tire entries showed "v1/gameDa???????t???"
        // mid-string after 1100+ frame-start-scan resyncs in the read path).
        // Surfaced via diagnostic readout so the user can spot link-quality
        // degradation independently of dashboard symptoms.
        private int _catalogCrcRejects;
        public int CatalogCrcRejects => Interlocked.CompareExchange(ref _catalogCrcRejects, 0, 0);
        private int _tileServerCrcRejects;
        public int TileServerCrcRejects => Interlocked.CompareExchange(ref _tileServerCrcRejects, 0, 0);

        // Per-session highest seen seq for sess=0x03 / 0x0b tile-server feed.
        // Mirrors the catalog parser's per-session seq dedup: wheel re-pushes
        // any unacked chunk, and blindly appending each retransmit duplicates
        // bytes in the parser buffer and breaks sentinel-scan alignment.
        private readonly System.Collections.Generic.Dictionary<byte, int> _tileServerHighestSeq
            = new System.Collections.Generic.Dictionary<byte, int>();

        // Catalog re-sync probe. Post-dashboard-switch the wheel sometimes
        // advertises only a subset of the channels its dashboard actually
        // renders (observed 2026-05-09: switching from Mono to Grids
        // resulted in a stuck-at-8 catalog while Grids's tire widgets at
        // slots 9-20 remained un-advertised). Sending a kind=4 dashboard-
        // switch frame for the slot the wheel is ALREADY on tells the
        // firmware to re-init its dashboard binding, which re-runs the
        // catalog-advertise sequence. Throttled so a stuck case can't
        // produce a switch storm.
        private long _lastCatalogResyncProbeUtcTicks;
        private const long CatalogResyncProbeMinIntervalTicks =
            8000 * System.TimeSpan.TicksPerMillisecond;

        /// <summary>Time to let the caller's queued FF kind=4 (and other
        /// in-flight one-shot frames) drain to the wire before Stop's
        /// FlushPendingWrites discards the queue. Used by
        /// <see cref="RestartForSwitch"/>.</summary>
        private const int PreStopDrainMs = 300;

        // True once ResolveAutoPolicy has run for this Start() cycle. Reset on
        // every StartInner so each fresh connect re-evaluates the wheel.
        private bool _autoResolutionDone;

        /// <summary>
        /// Outbound seq counter for session 0x01 (mgmt). Tier-def subscription
        /// flows here per PitHouse capture
        /// `wireshark/csp/startup, change knob colors, ...pcapng` — host sends
        /// `07/03/01/06`-tagged TLV stream on session 0x01 right after open;
        /// session 0x02 is reserved for value frames + wheel state pushes.
        /// </summary>
        private int _session01OutboundSeq;

        /// <summary>
        /// Forces a specific session number for dashboard upload. 0 = auto
        /// (use first device-initiated session in 0x04..0x0a, falling back to
        /// 0x04). Set non-zero to override — useful for testing with new
        /// firmware that prefers 0x07 / 0x09. Pass-through to coordinator.
        /// </summary>
        public byte UploadSessionOverride
        {
            get => _uploader?.UploadSessionOverride ?? 0;
            set { if (_uploader != null) _uploader.UploadSessionOverride = value; }
        }

        // Active per-era policy. All wire-protocol axes (tier-def session,
        // encoding, preamble policy, blind-retransmit, upload header, V0/V2
        // value frames) are derived from this. Set by
        // MozaPlugin.ApplyTelemetrySettings via the Policy property; mutated
        // in place by ResolveAutoPolicy at session start (Auto only) and by
        // the upload sub-msg-1 fallback path (Auto only).
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

        // Session 0x09 configJson RPC state. Device proactively pushes its
        // dashboard state blob; we reply with the canonical dashboard library
        // list so the wheel updates its configJsonList (PitHouse's UI uses this
        // for library filtering / update-availability checks).
        private readonly ConfigJsonClient _configJson = new ConfigJsonClient();
        private int _session09InboundSeq;
        private int _session09OutboundSeq;
        private bool _session09ReplySent;
        public WheelDashboardState? WheelState => _configJson.LastState;

        // IMozaTelemetry impl — wraps WheelState.ConfigJsonList for the auto-test.
        public System.Collections.Generic.IReadOnlyList<string>? WheelReportedDashboards
            => _configJson.LastState?.ConfigJsonList;

        /// <summary>
        /// Canonical dashboard library PitHouse would advertise to the wheel on
        /// session 0x09. Populated by the host from its known profile list. The
        /// wheel echoes these names back in its next state blob's
        /// <c>configJsonList</c>. Empty list disables the proactive reply.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> CanonicalDashboardList { get; set; }
            = System.Array.Empty<string>();

        // Display sub-device detection
        private volatile bool _displayDetected;
        private string _displayModelName = "";

        // Wheel channel catalog parser. Owns the inbound 7c:00 byte buffer, the
        // parsed idx→URL list, and the activity timestamp + last-parse-len used
        // by the post-startup quiet-window wait. See ChannelCatalogParser.cs.
        private readonly ChannelCatalogParser _catalogParser = new();

        // ── Subscription state ─────────────────────────────────────────
        // Immutable snapshot published atomically by ApplySubscription.
        // The tick handler reads this without locks — volatile reference
        // swap guarantees the reader sees a consistent snapshot.
        private sealed class SubscriptionState
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

        // Session-global flag counter. Starts at 0, advances by tierCount
        // after each ApplySubscription. Dashboard switches use the advanced
        // value; initial/re-subscribe resets to 0.
        private byte _nextFlagBase;

        // Monotonic counter incremented every time ApplySubscription sends a
        // tier-def. DashboardSwitchAutoTest uses this to detect renegotiate
        // completion.
        private int _subscriptionGen;
        public int SubscriptionGen => System.Threading.Volatile.Read(ref _subscriptionGen);


        // ── Subscription-exchange diagnostics ──────────────────────────────
        // Captured during preamble for surfacing in the Diagnostics tab. Lets
        // a user (and the maintainer) see exactly what the host subscribed to
        // and how the wheel responded — needed to reverse-engineer V2/Type02
        // tier-def + token-assignment edge cases.
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
        private volatile SubscriptionDiagnostics? _lastSubscriptionDiag;
        public SubscriptionDiagnostics? LastSubscription => _lastSubscriptionDiag;

        /// <summary>Raw hex of inbound chunks on session 0x02 captured during the
        /// 5s window after the most-recent subscription send. The wheel returns
        /// channel-token assignments here (tag <c>0x0c</c> + 4B token per channel)
        /// per CSP firmware — exposed for diag-tab inspection.</summary>
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

        // RPC on 0x09/0x0a (host→device management RPCs such as completelyRemove).
        // Wire-level state and JSON envelope handling live in RpcCallChannel.
        // _session0aInbox stays here because it's also fed by the configJson handler
        // outside the RPC flow (session 0x09 vs 0x0a share reassembly machinery).
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

        // Peripheral output-poll frames. PitHouse polls these continuously to feed
        // its UI; we mirror that for wire parity even when SimHub already provides
        // game-side telemetry. Cadence per PitHouse capture (2026-04-29):
        //   handbrake-presence  0x5A/0x1B 00            ~22 Hz
        //   handbrake-output    0x5D/0x1B 01 00 00      ~10 Hz
        //   pedal-throttle-out  0x25/0x19 01 00 00      ~7 Hz
        //   pedal-brake-out     0x25/0x19 02 00 00      ~7 Hz
        //   pedal-clutch-out    0x25/0x19 03 00 00      ~7 Hz
        private static readonly byte[] _handbrakePresenceFrame = BuildShortFrame(0x5A, 0x1B, new byte[] { 0x00 });
        private static readonly byte[] _handbrakeOutputFrame   = BuildShortFrame(0x5D, 0x1B, new byte[] { 0x01, 0x00, 0x00 });
        private static readonly byte[] _pedalThrottleOutFrame  = BuildShortFrame(0x25, 0x19, new byte[] { 0x01, 0x00, 0x00 });
        private static readonly byte[] _pedalBrakeOutFrame     = BuildShortFrame(0x25, 0x19, new byte[] { 0x02, 0x00, 0x00 });
        private static readonly byte[] _pedalClutchOutFrame    = BuildShortFrame(0x25, 0x19, new byte[] { 0x03, 0x00, 0x00 });

        // LED state read polls — `0x40/0x17 1F 03 [group] 00 00 00 00`. PitHouse
        // capture (2026-04-29) polls group 1 (RPM bar) at ~18 Hz and group 2
        // (Single) at ~1.7 Hz. Bytes after the group ID are the index slot
        // (zeros = read all).
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
                        _tierDiagEmitted = new bool[_tiers?.Length ?? 0];
                    MozaLog.Debug($"[Moza] TestMode changed to {value}");
                }
            }
        }

        // Wire-trace phase marker (see IMozaTelemetry contract). Frame:
        //   7e 03 55 55 4d 4b [phaseId] [chk]
        // grp=0x55 dev=0x55 not used by any wheel command — wheel ignores, but
        // the frame lands in the SerialTrafficCapture wire trace so the v1↔v2
        // diff tool can align both runs by phase id.
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

        /// <summary>Whether to upload the dashboard to the wheel on startup.</summary>
        public bool UploadDashboard
        {
            get => _uploader?.UploadDashboard ?? true;
            set { if (_uploader != null) _uploader.UploadDashboard = value; }
        }


        /// <summary>
        /// Resolver invoked per frame for channels with a non-empty
        /// <see cref="ChannelDefinition.SimHubProperty"/>. Set by MozaPlugin before
        /// assigning <see cref="Profile"/>; bound into each TelemetryFrameBuilder at
        /// profile-assign time so there is no per-frame lookup cost.
        /// </summary>
        public Func<string, double>? PropertyResolver { get; set; }

        public MultiStreamProfile? Profile
        {
            get => _profile;
            set
            {
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
                    for (int b = 0; b < broadcasts; b++)
                    {
                        foreach (var src in subTiers)
                        {
                            expanded.Add(new DashboardProfile
                            {
                                Name = $"{src.Name}@b{b}",
                                Channels = src.Channels,
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
                    };
                }
                _profile = value;
                if (value == null || value.Tiers.Count == 0)
                {
                    _tiers = null;
                    _baseTickMs = 33;
                    return;
                }

                // Base tick = fastest tier's pkg_level (smallest).
                int minPkg = int.MaxValue;
                foreach (var t in value.Tiers)
                    if (t.PackageLevel > 0 && t.PackageLevel < minPkg) minPkg = t.PackageLevel;
                _baseTickMs = (minPkg == int.MaxValue) ? 30 : minPkg;

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
                            type02NConvention: false),
                        TickInterval = tickInterval,
                    };
                }
                MozaLog.Debug(tierDiag.ToString());
            }
        }
        private MultiStreamProfile? _profile;

        private volatile int _framesSent;
        public int FramesSent => _framesSent;
        private volatile int _postRenegDiagTicks; // counts down from 3 after renegotiation
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
        public byte[]? LastFrameSent { get; private set; }
        public TelemetryDiagnostics Diagnostics { get; } = new TelemetryDiagnostics();

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

        public TelemetrySender(MozaSerialConnection connection)
        {
            _connection = connection;
            _rpc = new RpcCallChannel(
                connection,
                shouldAbort: () => _state == TelemetryState.Idle || !_connection.IsConnected);
            _uploader = new WheelUploadCoordinator(
                connection,
                shouldAbort: () => _state == TelemetryState.Idle || !_connection.IsConnected,
                getPolicy: () => _policy,
                getConfigJsonState: () => _configJson.LastState,
                sendSessionAck: SendSessionAck,
                sendSessionEnd: SendSessionEnd);
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
                    name => { plugin.Settings.TelemetryProfileName = name; });
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
        private int _startInProgress;

        // Flipped to 1 by Dispose() so Stop() / SendRpcCall() / handlers can
        // bail without touching disposed ManualResetEventSlim instances.
        private int _disposed;

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                MozaLog.Warn("[Moza] Start() ignored — sender disposed");
                return;
            }
            if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
            {
                // A second Start() landed while the first is still inside StartInner.
                // Force the in-progress run to drop out of any "is running" check by
                // returning to Idle while we wait for it to release _startInProgress.
                TransitionTo(TelemetryState.Idle, "Start() superseded in-progress start");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SpinWait.SpinUntil(() => Volatile.Read(ref _startInProgress) == 0, 10000);
                if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
                {
                    MozaLog.Warn("[Moza] Start() could not acquire start lock after 10s");
                    return;
                }
                MozaLog.Debug($"[Moza] Start() superseded in-progress start (waited {sw.ElapsedMilliseconds}ms)");
            }
            try
            {
                StartInner();
            }
            finally
            {
                Interlocked.Exchange(ref _startInProgress, 0);
            }
        }

        private void StartInner()
        {
            Stop();

            // Enforce minimum host silence since the last Stop() completion.
            // The wheel has a ~10–14s internal timeout on sess=0x09 dashboard-
            // binding state; if Start fires inside that window the wheel
            // silently ignores fresh prime/open-request emissions and the
            // configJson handshake never engages. Wire-trace investigation
            // 2026-05-08 measured failing cycles at 8.4s of silence (wheel
            // ignored), working at 13.9s (wheel re-engaged in 57ms). The
            // `_lastStopUtcTicks` is recorded at the END of Stop() so this
            // measures actual wheel-quiet time, not host clock time. We're
            // on a ThreadPool thread (StartTelemetryIfReady's QueueUserWorkItem),
            // so a synchronous Sleep here doesn't block the UI.
            if (_lastStopUtcTicks != 0)
            {
                long elapsedMs = (System.DateTime.UtcNow.Ticks - _lastStopUtcTicks)
                    / System.TimeSpan.TicksPerMillisecond;
                int waitMs = (int)System.Math.Max(0, MinSilenceAfterStopMs - elapsedMs);
                if (waitMs > 0)
                {
                    MozaLog.Debug(
                        $"[Moza] Start: enforcing {waitMs}ms silence " +
                        $"(elapsed since last Stop: {elapsedMs}ms; min: {MinSilenceAfterStopMs}ms) " +
                        "so wheel sess=0x09 timeout can clear");
                    try { System.Threading.Thread.Sleep(waitMs); } catch { }
                }
            }

            InitTickStateAndTransitionToStarting();
            BuildCachedFrames();

            // Subscribe early so we catch fc:00 acks during port probing AND preamble
            _connection.MessageReceived += OnMessageDuringPreamble;

            // Probe for available ports and open sessions. May run on a
            // background thread (dispatched by StartTelemetryIfReady) so the
            // serial read thread stays free to deliver fc:00 ack responses.
            ProbeAndOpenSessions();
            if (_state == TelemetryState.Idle) return;

            // Universal Hub: PitHouse fires a 5-frame burst enumerating hub
            // slots ~300ms after sessions open. Plugin mirrors this so wheel
            // firmware sees the same handshake and populates per-port device
            // metadata. Skipped when no hub detected.
            if (_connection.HubProbeSucceeded)
                SendHubSlotEnumeration();

            PrimeAndOpenSession09();
            QueueBackgroundUploadIfReady();
            if (_state == TelemetryState.Idle) return;

            // Open session 0x03 (host opens 0x03 150-450ms after 0x01/0x02 on
            // new firmware). Tile-server push deferred until after tier def —
            // pushing immediately after open collided with the wheel's session
            // 0x09 configJson state burst under Wine SerialPort R/W contention.
            SendSessionOpen(0x03, 0x03);

            WaitForChannelCatalogQuiet(quietMs: 200, timeoutMs: 2000);
            _catalogParser.TryParse();
            MaybeSwapProfileForCatalog();

            // Sess=0x02 init handshake (kind=2 nonce + kind=7 slot-index). Sent
            // AFTER WaitForChannelCatalogQuiet so the wheel's initial b2h
            // sess=0x02 TLV state push has fully arrived. Without these the
            // wheel silently ignores dashboard-switch FF records on a fresh
            // session 0x02. See docs/protocol/findings/
            // 2026-05-07-sess02-init-protocol-and-stale-catalog.md.
            SendSessionInitHandshake();

            // Empty-state tile-server blob on session 0x03 (host→wheel only,
            // wheel never echoes). 12-byte envelope. Deferred to after session
            // 0x09 state push so the wheel's burst doesn't get crowded out
            // under Wine SerialPort contention.
            SendTileServerState();

            // Probe the wheel's built-in Display sub-device. Non-blocking:
            // responses are caught by OnMessageDuringPreamble.
            SendDisplayProbe();
            if (_state == TelemetryState.Idle) return;

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
            // can resolve. The wheel uses zero-length backref records (size=1,
            // just the idx byte) post-switch to say "URL at idx X is unchanged
            // from before" — Reset()'ing the catalog made every backref a
            // backRefFail, leaving gaps in the post-switch channel list.
            // ClearBuffer drops the in-progress reassembly buffer + per-session
            // seq dedup map, which IS what we want at the start of a new cycle.
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
            SendConfigJsonOpenRequest(0x09, seq: 0x000B);
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
        /// Send end-marker close frames for sessions 0x01/0x02/0x03 so the wheel
        /// sees a clean shutdown handshake. Mirrors the pre-open close burst in
        /// <see cref="ProbeAndOpenSessions"/> but on the way out.
        ///
        /// Wheel-managed sessions (0x04..0x0a, including 0x09 configJson) are
        /// LEFT ALONE. Wire-trace investigation 2026-05-08 (multiple traces +
        /// post-game-switch evidence) confirmed:
        ///   - The wheel-side sess=0x09 NEVER gets explicitly closed even
        ///     across SimHub plugin reloads — its end stays alive indefinitely.
        ///   - Closing it host-side is either a no-op or a regression; it does
        ///     NOT shorten the wheel's internal timeout, which is the actual
        ///     gate on re-engagement.
        ///   - The wheel has a ~10–14 second internal timeout on sess=0x09
        ///     dashboard-binding state. Before that timeout, fresh prime/open-
        ///     request emissions are ignored regardless of what the host does.
        ///
        /// The reliable bridge across the wheel timeout is host silence. See
        /// <see cref="MinSilenceAfterStopMs"/> and the gate in
        /// <see cref="StartInner"/>.
        /// </summary>
        private void CloseHostSessions()
        {
            if (!_connection.IsConnected) return;
            try { SendSessionClose(0x01); } catch { }
            try { SendSessionClose(0x02); } catch { }
            try { SendSessionClose(0x03); } catch { }
            try { System.Threading.Thread.Sleep(100); } catch { }
        }

        public void Stop()
        {
            TransitionTo(TelemetryState.Idle, "Stop()");
            _connection.MessageReceived -= OnMessageDuringPreamble;
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
            // Reset reply-sent gate so the next sess=0x09 device-init cycle
            // re-confirms canonical dashboard list to the wheel. The LastState
            // cache below is the host-side fallback for downstream consumers,
            // not a substitute for the wheel-side handshake.
            _session09ReplySent = false;
            _s09RetryRounds = 0;
            _s09RetryLastTickCount = 0;
            _session02OutboundSeq = 0;
            _session01OutboundSeq = 0;
            _tierDefPreambleSent = false;
            _autoResolutionDone = false;
            _retransmitter.Clear();
            _propertyPushLastSeqs.Clear();
            _tierDefBlindFrames = null;
            _lastSubscriptionDiag = null;
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = 0;
            lock (_sessionCounts) _sessionCounts.Clear();
            // Reset gap-recovery counters; the new Start cycle gets a fresh
            // tier-1/2 escalation budget.
            _configJsonGapCount = 0;
            _configJsonLastChunkUtcTicks = 0;
            // Reset catalog-growth tracking so the next Start's first
            // subscription (built from whatever catalog has arrived) is
            // treated as the new baseline.
            _catalogCountAtLastSubscription = 0;
            // Drop reassembly buffers — otherwise residual chunks from before
            // Stop persist into the next Start and (combined with the cap in
            // SessionDataReassembler) get logged as overflow. Caches the
            // last-good LastState so dashboard library remains valid through
            // the Stop+Start window (wheel-state doesn't change without a
            // user action — see ConfigJsonClient.ClearBuffer doc).
            try { _configJson.ClearBuffer(); } catch { }
            try { _tileServerParser.Clear(); } catch { }
            try { _session0aInbox.Clear(); } catch { }
            // Reset so StartTelemetryIfReady() won't skip us on re-enable
            _framesSent = 0;

            // Record the moment the close burst left the wire. The next
            // StartInner gates on this — if Start fires within
            // MinSilenceAfterStopMs of this Stop, it sleeps the remainder
            // before opening sessions. The wheel has a ~10–14s internal
            // timeout on sess=0x09 dashboard-binding state; emitting fresh
            // primes inside that window is silently ignored. SimHub plugin
            // reload (game switch) was the failure case — Stop+Start was
            // running back-to-back in <1s, well inside the timeout. Manual
            // disable+enable normally has enough human delay to clear it,
            // but rapid clicks would have the same issue without this gate.
            _lastStopUtcTicks = System.DateTime.UtcNow.Ticks;
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
        {
            if (!_connection.IsConnected) return;
            byte[] body = global::MozaPlugin.Protocol.SessionPropertyPushBuilder.BuildU32Body(kind, value);
            SendSessionPropertyBody(body);
        }

        /// <summary>Push a u64-valued property (e.g. standby in milliseconds).</summary>
        public void SendSessionPropertyU64(uint kind, ulong value)
        {
            if (!_connection.IsConnected) return;
            byte[] body = global::MozaPlugin.Protocol.SessionPropertyPushBuilder.BuildU64Body(kind, value);
            SendSessionPropertyBody(body);
        }

        private void SendSessionPropertyBody(byte[] body)
        {
            // All host-side FF records (init kind=2/7 handshake, dashboard
            // switches kind=4, brightness/standby kind=1/10) are sent on
            // session 0x02. Verified by `tools/bridge-decode-ff-init` against
            // 2026-04-28..05-03 PitHouse captures: every meaningful FF record
            // appears on sess=0x02 in both directions; sess=0x01 carries
            // tier-def TLV traffic only.
            if (body == null) return;
            const byte session = 0x02;

            // Pull the property `kind` out of the FF body so we can supersede
            // any prior outstanding push of the same kind in the retransmit
            // queue. Body layout (from SessionPropertyPushBuilder.WrapFfRecord):
            //   [0]      0xFF
            //   [1..4]   size:u32 LE
            //   [5..8]   inner crc32
            //   [9..12]  kind:u32 LE
            //   [13...]  value bytes
            bool haveKind = body.Length >= 13 && body[0] == 0xFF;
            uint kind = haveKind
                ? (uint)(body[9] | (body[10] << 8) | (body[11] << 16) | (body[12] << 24))
                : 0u;

            // Drop the prior seqs for the same kind before queuing the new
            // chunk. See _propertyPushLastSeqs comment for the user-visible
            // failure this prevents (stale brightness=0 retransmits leaving
            // the display stuck blanked after a quick slider drag).
            if (haveKind && _propertyPushLastSeqs.TryGetValue((session, kind), out var prevSeqs))
            {
                foreach (int s in prevSeqs)
                    _retransmitter.Drop(session, s);
                prevSeqs.Clear();
            }

            int seq = Math.Max(2, _session02OutboundSeq);
            var frames = TierDefinitionBuilder.ChunkMessage(body, session, ref seq);
            var newSeqs = haveKind
                ? new System.Collections.Generic.List<int>(frames.Count)
                : null;
            foreach (var frame in frames)
            {
                SendAndTrackChunk(frame);
                // Frame layout (TierDefinitionBuilder.ChunkMessage):
                //   7E [N] 43 17 7C 00 [session] [type=01] [seq_lo] [seq_hi] [payload] [chk]
                if (newSeqs != null && frame.Length >= 10)
                    newSeqs.Add(frame[8] | (frame[9] << 8));
            }
            _session02OutboundSeq = seq;

            if (haveKind)
                _propertyPushLastSeqs[(session, kind)] = newSeqs!;
        }

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
        public void SendDashboardSwitch(uint slotIndex)
        {
            if (!_connection.IsConnected) return;
            // Block kind=4 emission during the post-Stop silence window or
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
                return;
            }

            byte[] body = global::MozaPlugin.Protocol.SessionPropertyPushBuilder
                .BuildDashboardSwitchBody(slotIndex);
            SendSessionPropertyBody(body);
            MozaLog.Debug(
                $"[Moza] Sent dashboard-switch FF-record: slot={slotIndex} " +
                $"on session 0x02 seq={_session02OutboundSeq - 1}");
        }

        /// <summary>True while the post-Stop silence enforcement gate is
        /// active. UI consumers should reflect this in their dashboard-switch
        /// affordance (disable dropdown / Start Test button) so the user
        /// can't trigger races against the in-flight Stop+Start.</summary>
        public bool IsInSilenceCooldown
        {
            get
            {
                if (_lastStopUtcTicks == 0) return false;
                long elapsedMs = (System.DateTime.UtcNow.Ticks - _lastStopUtcTicks)
                    / System.TimeSpan.TicksPerMillisecond;
                return elapsedMs < MinSilenceAfterStopMs;
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
        /// kind=8 / kind=11 are replayed verbatim from
        /// `Resources/sess02_init_kind8_pithouse.bin` (2059 B) and
        /// `sess02_init_kind11_pithouse.bin` (2581 B), extracted from
        /// `bridge-20260429-163951.jsonl`. CRCs already valid; do not
        /// re-wrap. kind=2 timestamp is regenerated to current Unix time.
        /// </summary>
        private void SendSessionInitHandshake()
        {
            if (_state == TelemetryState.Idle || !_connection.IsConnected) return;

            byte[] init2 = global::MozaPlugin.Protocol.SessionPropertyPushBuilder
                .BuildSessionInitField2Body();
            SendSessionPropertyBody(init2);

            byte[] init7 = global::MozaPlugin.Protocol.SessionPropertyPushBuilder
                .BuildSessionInitField7Body(slotIndex: 0u);
            SendSessionPropertyBody(init7);

            MozaLog.Debug(
                $"[Moza] Sent sess=0x02 init handshake (kind=2 nonce + kind=7 slot=0); " +
                $"next outbound seq={_session02OutboundSeq}");
        }

        public void SwitchToProfile(uint slotIndex, MultiStreamProfile? newProfile)
        {
            SendDashboardSwitch(slotIndex);
            if (newProfile != null) Profile = newProfile;
            RestartForSwitch();
        }

        /// <summary>
        /// Stop+Start cycle for dashboard switches. Used when the kind=4 has
        /// already been sent by the caller (UI knob in MozaWheelSettingsControl,
        /// or auto-test via SwitchToProfile) — we just need to rebind our
        /// session state to match the new dashboard. The wheel's ~10–14s
        /// internal sess=0x09 timeout is the gate on re-engagement; the
        /// silence enforcement inside <see cref="StartInner"/> (via
        /// <c>_lastStopUtcTicks</c>) handles that automatically — no need
        /// for explicit Sleep here.
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
                    Stop();   // CloseHostSessions (01/02/03) + records _lastStopUtcTicks
                    Start();  // StartInner enforces MinSilenceAfterStopMs gate before opening
                }
                catch (Exception ex)
                {
                    MozaLog.Error($"[Moza] RestartForSwitch failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Look up the 0-based configJsonList slot index for a dashboard by
        /// title. Returns -1 if not found or wheel state unavailable.
        /// </summary>
        public int FindDashboardSlot(string title)
        {
            var state = WheelState;
            if (state == null) return -1;
            for (int i = 0; i < state.ConfigJsonList.Count; i++)
            {
                if (string.Equals(state.ConfigJsonList[i], title,
                    System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
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
        /// Wheel-managed sessions (0x04..0x0a) are LEFT ALONE. Wheel device-
        /// inits these to push state (0x05/0x07 file-transfer ack, 0x09 config-
        /// Json state, 0x0a RPC). Closing them severs wheel-side state and
        /// prevents the wheel from re-pushing configJson on session 0x09 —
        /// without that handshake the dashboard never renders. Pithouse never
        /// closes these sessions at startup either; verified in
        /// usb-capture/ksp/mozahubstartup.pcapng (no host close-burst, wheel
        /// device-inits 0x09 t=28.123 after host primes it with data on 0x09
        /// at t=2.345 / 6.346).
        /// </summary>
        private void ProbeAndOpenSessions()
        {
            if (!_connection.IsConnected)
                return;

            const byte MgmtSession = 0x01;
            const byte TelemSession = 0x02;
            const int OpenAckTimeoutMs = 500;

            // Reclaim any HOST-managed sessions left open by a prior SimHub
            // crash/kill. Don't touch 0x04..0x10 — those are wheel-managed.
            MozaLog.Debug("[Moza] Closing any stale host sessions (0x01..0x03)...");
            for (byte port = 1; port <= 0x03; port++)
            {
                if (!_connection.IsConnected) return;
                SendSessionClose(port);
            }
            // Brief settle so the wheel processes the closes before we re-open.
            System.Threading.Thread.Sleep(100);

            byte mgmtPort = TryOpenSession(MgmtSession, OpenAckTimeoutMs);
            if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
            byte telemetryPort = TryOpenSession(TelemSession, OpenAckTimeoutMs);

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
        /// </summary>
        private byte TryOpenSession(byte session, int timeoutMs)
        {
            _ackReceived.Reset();
            _lastAckedSession = 0;

            SendSessionOpen(session, session);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0 || !_ackReceived.Wait(remaining))
                    return 0;

                if (_lastAckedSession == session)
                    return session;

                // Stale ack (different session) — discard and keep waiting.
                MozaLog.Debug(
                    $"[Moza] OpenSession 0x{session:X2}: ignoring stale ack for 0x{_lastAckedSession:X2}");
                _ackReceived.Reset();
                _lastAckedSession = 0;
            }
        }

        /// <summary>
        /// Wait for the wheel's pre-tier-def channel registration burst to stop
        /// arriving. Polls <see cref="ChannelCatalogParser.LastActivityMs"/> — once the
        /// last activity is older than <paramref name="quietMs"/>, we assume the
        /// wheel is done pushing its channel URLs.
        /// </summary>
        private void WaitForChannelCatalogQuiet(int quietMs, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                int lastAct = _catalogParser.LastActivityMs;
                int idle = lastAct == 0 ? 0 : Environment.TickCount - lastAct;
                int bufCount = _catalogParser.BufferLength;
                if (bufCount > 0 && idle >= quietMs)
                    return;
                Thread.Sleep(20);
            }
        }

        /// <summary>
        /// Build a new <see cref="MultiStreamProfile"/> with only the channels
        /// whose <c>Url</c> appears in <paramref name="catalog"/>. Tiers that
        /// end up empty are dropped. URL match is case-insensitive and also
        /// accepts catalog entries matching the last path segment (the wheel
        /// sometimes advertises bare names where the profile uses a full URL).
        /// </summary>
        private static MultiStreamProfile FilterProfileToCatalog(
            MultiStreamProfile profile,
            System.Collections.Generic.IReadOnlyList<string> catalog)
        {
            var set = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                set.Add(entry);
                int slash = entry.LastIndexOf('/');
                if (slash >= 0 && slash < entry.Length - 1)
                    set.Add(entry.Substring(slash + 1));
            }

            bool ChannelMatches(ChannelDefinition ch)
            {
                if (set.Contains(ch.Url)) return true;
                int slash = ch.Url.LastIndexOf('/');
                if (slash >= 0 && slash < ch.Url.Length - 1
                    && set.Contains(ch.Url.Substring(slash + 1))) return true;
                return false;
            }

            var result = new MultiStreamProfile
            {
                Name = profile.Name,
                PageCount = profile.PageCount,
            };
            foreach (var tier in profile.Tiers)
            {
                var kept = new System.Collections.Generic.List<ChannelDefinition>();
                foreach (var ch in tier.Channels)
                    if (ChannelMatches(ch)) kept.Add(ch);
                if (kept.Count == 0) continue;
                result.Tiers.Add(new DashboardProfile
                {
                    Name = tier.Name,
                    Channels = kept,
                    PackageLevel = tier.PackageLevel,
                    TotalBits = tier.TotalBits,
                    TotalBytes = tier.TotalBytes,
                    FlagByte = tier.FlagByte,
                });
            }
            // If filter removed everything, fall back to the original rather
            // than shipping an empty tier def (wheel would reject it anyway).
            return result.Tiers.Count == 0 ? profile : result;
        }

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
        private static MultiStreamProfile BuildV0ProfileFromCatalog(
            MultiStreamProfile hostProfile,
            System.Collections.Generic.IReadOnlyList<string> catalog)
        {
            var hostByUrl = new System.Collections.Generic.Dictionary<string, ChannelDefinition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var tier in hostProfile.Tiers)
                foreach (var ch in tier.Channels)
                    if (!string.IsNullOrEmpty(ch.Url) && !hostByUrl.ContainsKey(ch.Url))
                        hostByUrl[ch.Url] = ch;

            int packageLevel = hostProfile.Tiers.Count > 0
                ? hostProfile.Tiers[0].PackageLevel
                : 30;

            var channels = new System.Collections.Generic.List<ChannelDefinition>();
            foreach (var url in catalog)
            {
                if (string.IsNullOrEmpty(url)) continue;
                if (hostByUrl.TryGetValue(url, out var existing))
                {
                    channels.Add(existing);
                }
                else
                {
                    channels.Add(new ChannelDefinition
                    {
                        Name = url.Substring(url.LastIndexOf('/') + 1),
                        Url = url,
                        Compression = "uint32_t",
                        BitWidth = 32,
                        SimHubField = SimHubField.Zero,
                        SimHubProperty = "",
                        SimHubPropertyScale = 1.0,
                        PackageLevel = packageLevel,
                    });
                }
            }

            return new MultiStreamProfile
            {
                Name = hostProfile.Name,
                PageCount = hostProfile.PageCount,
                Tiers = new System.Collections.Generic.List<DashboardProfile>
                {
                    new DashboardProfile
                    {
                        Name = "V0Catalog",
                        Channels = channels,
                        PackageLevel = packageLevel,
                    },
                },
            };
        }

        /// <summary>
        /// Single point of entry for (re-)subscribing to the wheel's channel
        /// catalog. Swaps the active profile to match the catalog, sends
        /// tier-def + channel config, and atomically publishes the new
        /// subscription state for the telemetry tick handler.
        /// </summary>
        /// <param name="isDashSwitch">True when switching dashboards (advance
        /// flag counter). False for initial connect (reset to 0).</param>
        private void ApplySubscription(bool force)
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
            _propertyPushLastSeqs.Clear();
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = 0;

            SendTierDefinition();
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
            if (catalogCount > 0)
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
        private void SendTierDefinition()
        {
            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;
            if (!_connection.IsConnected)
                return;

            // V0 URL-subscription firmware: build synthetic profile from
            // wheel catalog (PitHouse echoes full catalog back).
            // V2/Type02: send ALL profile channels unfiltered — PitHouse
            // doesn't filter. Channels not in wheel catalog get index 0.
            // V2 legacy (non-Type02): filter to catalog-matched subset.
            //
            // IMPORTANT: never assign to the Profile PROPERTY here — the
            // setter re-expands tiers (broadcasts × subCount) causing
            // exponential growth across repeated calls. Use local var only.
            var catalog = _catalogParser.Catalog;
            if (catalog != null && catalog.Count > 0)
            {
                if (_policy.Encoding == TierDefEncoding.V0Url)
                {
                    profile = BuildV0ProfileFromCatalog(profile, catalog);
                    int catalogCh = profile.Tiers[0].Channels.Count;
                    MozaLog.Debug(
                        $"[Moza] V0 subscription expanded to wheel catalog: " +
                        $"{catalogCh} channels");
                }
                // Catalog-based filtering disabled — wheel pushes a partial
                // catalog post-switch (back-refs only) that drops half the
                // mzdash channels. Plugin now sends the full bundled
                // mzdash tier-def. Wheel decodes per the tier-def we send;
                // widget reads whichever channels it knows of.
            }

            // Era-driven session pick. 2025-era VGS firmware accepts tier-def
            // on FlagByte (probed, typically 0x02) — matches 0.8.0 (commit
            // 5692099) working behavior. 2026-era firmware separates tier-def
            // (mgmt port 0x01) from FF init records (telem port 0x02).
            byte tierDefSession;
            int seq;
            if (_policy.TierDefSession == TierDefSessionPolicy.FlagByte)
            {
                tierDefSession = FlagByte;
                seq = Math.Max(2, _session02OutboundSeq);
            }
            else
            {
                tierDefSession = _mgmtPort != 0 ? _mgmtPort : (byte)0x01;
                seq = Math.Max(2, _session01OutboundSeq);
            }

            // Send wrapper: under blind-retransmit policy (Era2026), every
            // tier-def chunk is also tracked by the session retransmitter and
            // captured for the tick-loop blind-retx replay (~10 rounds at
            // 200ms). Era2024/2025 send raw and skip the capture so the wheel
            // sees a single emission — matches 0.8.0 behavior.
            void Send(byte[] frame)
            {
                if (_policy.BlindRetransmitTierDef)
                    SendAndTrackChunk(frame);
                else
                    _connection.Send(frame);
            }

            switch (_policy.Encoding)
            {
                case TierDefEncoding.V0Url:
                {
                    // V0: URL-based subscription. Sentinel 0xFF + tag 0x03 inline.
                    // No separate tag 0x07/0x03 preamble.
                    byte[] message = TierDefinitionBuilder.BuildV0UrlSubscription(profile);
                    var frames = TierDefinitionBuilder.ChunkMessage(message, tierDefSession, ref seq);

                    int channelCount = 0;
                    foreach (var t in profile.Tiers) channelCount += t.Channels.Count;
                    MozaLog.Debug(
                        $"[Moza] Sending v0 URL subscription: " +
                        $"{message.Length} bytes in {frames.Count} chunks " +
                        $"on session 0x{tierDefSession:X2} ({channelCount} channels)");

                    foreach (var frame in frames)
                        Send(frame);

                    if (_policy.BlindRetransmitTierDef)
                    {
                        _tierDefBlindFrames = frames.ToArray();
                        _tierDefBlindSentRounds = 0;
                        _tierDefBlindLastTickCount = Environment.TickCount;
                    }

                    CaptureSubscriptionDiag(tierDefSession, "v0-url",
                        System.Array.Empty<byte>(), message, profile);
                    break;
                }

                case TierDefEncoding.V2Compact:
                case TierDefEncoding.V2Type02:
                {
                    // V2 preamble: tag 0x07 (version=2), tag 0x03 (value=0).
                    // 2025-era firmware needs it on every tier-def send;
                    // 2026-era firmware only accepts it once per connect.
                    bool emitPreamble = _policy.SendV2PreambleEverySend
                                        || !_tierDefPreambleSent;
                    int preambleChunkCount = 0;
                    if (emitPreamble)
                    {
                        byte[] preambleMsg = new byte[]
                        {
                            0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
                            0x03, 0x00, 0x00, 0x00, 0x00
                        };
                        var preambleFrames = TierDefinitionBuilder.ChunkMessage(preambleMsg, tierDefSession, ref seq);
                        foreach (var frame in preambleFrames)
                            Send(frame);
                        preambleChunkCount = preambleFrames.Count;
                        _tierDefPreambleSent = true;
                    }

                    // Type02 firmware indexes channels by wheel-catalog position.
                    // Without a catalog, fall through to alphabetic indices —
                    // sending the catalog-indexed form against an empty catalog
                    // produces all chIdx=0 entries the wheel can't bind. Under
                    // Auto, ResolveAutoPolicy already downgraded to Era2025 if
                    // no catalog arrived; this guard catches the rare race
                    // where a pinned Era2026 wheel fails to push its catalog
                    // in the WaitForChannelCatalogQuiet window.
                    bool cspIdx = _policy.Encoding == TierDefEncoding.V2Type02;
                    if (cspIdx && (_catalogParser.Catalog == null || _catalogParser.Catalog.Count == 0))
                    {
                        MozaLog.Debug(
                            "[Moza] No wheel catalog — using alphabetic indices for initial tier-def. " +
                            "Wheel will push corrected catalog after receiving this.");
                        cspIdx = false;
                    }
                    byte flagBase = _nextFlagBase;
                    var prevSub = _activeSubscription;

                    byte[] message;
                    if (cspIdx)
                    {
                        message = TierDefinitionBuilder.BuildTierDefinitionMessage(
                            profile, flagBase,
                            includeEnableEntries: true,
                            useWheelCatalogIndices: true,
                            wheelCatalog: _catalogParser.Catalog,
                            prevFlagBase: prevSub?.FlagBase,
                            prevTierCount: prevSub?.TierCount ?? 0,
                            prevSubPerBroadcast: prevSub?.SubTiersPerBroadcast ?? 0);
                    }
                    else
                    {
                        message = TierDefinitionBuilder.BuildTierDefinitionV2(
                            profile, flagBase, wheelCatalog: null);
                    }
                    var frames = TierDefinitionBuilder.ChunkMessage(message, tierDefSession, ref seq);

                    MozaLog.Debug(
                        $"[Moza] Sending {(cspIdx ? "type02-section" : "v2-flat")} tier definition: " +
                        $"flagBase=0x{flagBase:X2}, " +
                        $"prev={(prevSub != null ? $"0x{prevSub.FlagBase:X2}/{prevSub.TierCount}t/{prevSub.SubTiersPerBroadcast}spb" : "none")}, " +
                        $"preamble ({preambleChunkCount} chunks)" +
                        $" + {message.Length} bytes in {frames.Count} chunks " +
                        $"on session 0x{tierDefSession:X2} ({profile.Tiers.Count} tiers, " +
                        $"idx={(cspIdx ? "wheel-catalog" : "alpha")})");

                    _activeSubscription = new SubscriptionState(
                        flagBase: flagBase,
                        tierCount: profile.Tiers.Count,
                        subTiersPerBroadcast: TierDefinitionBuilder.DetectSubTiersPerBroadcast(profile),
                        profileName: profile.Name);
                    System.Threading.Interlocked.Increment(ref _subscriptionGen);
                    _nextFlagBase = (byte)(flagBase + profile.Tiers.Count);
                    // Snapshot catalog size so TickGrowSubscriptionIfCatalogStable
                    // can detect post-emission catalog growth and re-subscribe.
                    _catalogCountAtLastSubscription = _catalogParser.Count;

                    // Diagnose tier-def binding completeness. Channels whose URL
                    // isn't in the wheel's catalog get chIndex=0 from
                    // BuildTierDefinitionV2 and the wheel can't bind them to
                    // dashboard widgets (symptom: widget renders frozen at
                    // zero). Post-switch traces (2026-05-09) show the wheel
                    // sometimes advertises only a subset of the channels its
                    // dashboard actually uses; in that case schedule a kind=4
                    // re-emit for the current slot to nudge the wheel into
                    // re-running its catalog-advertise sequence.
                    if (cspIdx)
                    {
                        var catalogSnapshot = _catalogParser.Catalog;
                        if (catalogSnapshot != null && catalogSnapshot.Count > 0)
                        {
                            var have = new System.Collections.Generic.HashSet<string>(
                                catalogSnapshot, StringComparer.OrdinalIgnoreCase);
                            int unbound = 0, total = 0;
                            string? firstUnboundUrl = null;
                            foreach (var t2 in profile.Tiers)
                            foreach (var c2 in t2.Channels)
                            {
                                total++;
                                if (string.IsNullOrEmpty(c2.Url) || !have.Contains(c2.Url))
                                {
                                    unbound++;
                                    if (firstUnboundUrl == null) firstUnboundUrl = c2.Url;
                                }
                            }
                            if (unbound > 0)
                            {
                                MozaLog.Warn(
                                    $"[Moza] Tier-def has {unbound}/{total} unbound channels " +
                                    $"(chIndex=0; wheel catalog has {catalogSnapshot.Count} entries). " +
                                    $"First unbound: {firstUnboundUrl ?? "(null)"}. " +
                                    "Scheduling kind=4 re-emit to nudge wheel re-advertise.");
                                ScheduleCatalogResyncProbe();
                            }
                        }
                    }

                    foreach (var frame in frames)
                        Send(frame);

                    if (_policy.BlindRetransmitTierDef)
                    {
                        _tierDefBlindFrames = frames.ToArray();
                        _tierDefBlindSentRounds = 0;
                        _tierDefBlindLastTickCount = Environment.TickCount;
                    }

                    CaptureSubscriptionDiag(tierDefSession,
                        cspIdx ? "v2-type02" : "v2-compact",
                        System.Array.Empty<byte>(), message, profile);
                    break;
                }
            }

            // Persist the new seq counter on whichever session we used.
            if (_policy.TierDefSession == TierDefSessionPolicy.FlagByte)
                _session02OutboundSeq = seq;
            else
                _session01OutboundSeq = seq;
        }

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
            // Disabled — plugin uses bundled mzdash exclusively.
            // Wheel-catalog-driven profile synthesis dropped channels post-
            // switch (incomplete back-ref catalog) and clobbered the
            // user-selected profile. mzdash-only path is simpler and the
            // active path the user has decided to support.
            return;
        }

        private void CaptureSubscriptionDiag(byte session, string format,
            byte[] preamble, byte[] body, MultiStreamProfile profile)
        {
            var diag = new SubscriptionDiagnostics
            {
                SessionByte = $"0x{session:X2}",
                Format = format,
                PreambleBytes = preamble,
                BodyBytes = body,
                CapturedAt = System.DateTime.Now,
            };
            // Per-channel rendering of subscription contents (post-filter).
            int idx = 1;
            foreach (var tier in profile.Tiers)
            {
                foreach (var ch in tier.Channels)
                {
                    uint comp = TierDefinitionBuilder.LookupCompressionCode(ch.Compression);
                    diag.Channels.Add((idx, ch.Url, comp, (uint)ch.BitWidth));
                    idx++;
                }
            }
            _lastSubscriptionDiag = diag;

            // Open a 5s capture window for inbound chunks on session 0x02 — the
            // wheel returns its channel-token TLVs there right after subscription.
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = System.Diagnostics.Stopwatch.GetTimestamp()
                + System.Diagnostics.Stopwatch.Frequency * 5;
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
                var frames = TierDefinitionBuilder.ChunkMessage(payload, 0x03, ref seq);
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
            int seq = _session09OutboundSeq + 1;
            var frames = TierDefinitionBuilder.ChunkMessage(reply, session, ref seq);
            foreach (var frame in frames)
            {
                if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                _connection.Send(frame);
            }
            _session09OutboundSeq = seq;
            _session09ReplySent = true;
            MozaLog.Debug(
                $"[Moza] Sent configJson() reply on session 0x{session:X2}: " +
                $"{CanonicalDashboardList.Count} dashboards, {frames.Count} chunks");
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

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private void SendSessionEnd(byte session, ushort seq)
        {
            var end = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
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
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
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
            frame[3] = MozaProtocol.DeviceWheel;
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
        private void OnMessageDuringPreamble(byte[] data)
        {
            if (_state == TelemetryState.Idle)
                return;

            // data layout from MozaSerialConnection: [group, device, cmdPayload...]
            if (data.Length < 4)
                return;

            // Only process 0xC3 (response to 0x43) from device 0x71 (nibble-swapped 0x17)
            if (data[0] != 0xC3 || data[1] != 0x71)
                return;

            byte cmd1 = data[2];
            byte cmd2 = data[3];

            // fc:00 ack — signals a session open was accepted, and (when 7-byte
            // form) carries a session-data ack_seq that drains the retransmit
            // queue. Wire format: `fc 00 [session] [ack_seq:u16 LE]`.
            if (cmd1 == 0xFC && cmd2 == 0x00 && data.Length >= 5)
            {
                _lastAckedSession = data[4];
                if (data.Length >= 7)
                {
                    int ackSeq = data[5] | (data[6] << 8);
                    _retransmitter.Ack(data[4], ackSeq);
                    // Route ack to session owner (downloader, uploader, etc.)
                    _dispatcher.DispatchAck(data[4], ackSeq);
                }
                _ackReceived.Set();
                return;
            }

            // 7c:00 data chunks — ack and buffer during preamble/upload
            if (cmd1 == 0x7C && cmd2 == 0x00 && data.Length >= 8)
            {
                byte session = data[4];
                byte type = data[5];

                // Device-initiated session open (type=0x81). Real wheel opens
                // 0x04/0x06/0x08/0x09/0x0A — mark as seen, ack the open, and
                // trigger any waiters that need that session up before sending.
                if (type == 0x81)
                {
                    int openSeq = data.Length >= 8 ? data[6] | (data[7] << 8) : 0;
                    var info = _sessions.GetOrCreate(session);
                    info.DeviceInitiated = true;
                    info.Port = (byte)(openSeq & 0xFF);
                    SendSessionAck(session, (ushort)openSeq);
                    // Track every device-init session in the file-transfer-
                    // eligible range so ChooseUploadSession() can pick. Signal
                    // the upload-pump waiter on any FT-eligible open — the
                    // upload pump re-runs ChooseUploadSession() after the wait
                    // so bursts that don't include 0x04 (KS Pro on Universal
                    // Hub opens 0x05/0x07/0x09/0x0a) still resolve correctly.
                    // Route device-init through dispatcher first so we can check
                    // ownership before redirecting the upload coordinator's
                    // ActiveSession (we don't want NoteDeviceInit to re-point
                    // ActiveSession to a session that DashboardDownloader has
                    // claimed for its own download flow).
                    _dispatcher.DispatchOpen(session, openSeq);
                    if (session >= 0x04 && session <= 0x0b
                        && _dispatcher.GetOwner(session) == null)
                    {
                        _uploader.NoteDeviceInit(session);
                    }
                    return;
                }

                if (type == 0x01)
                {
                    int seq = data[6] | (data[7] << 8);
                    byte[] chunkPayload = new byte[data.Length - 8];
                    Array.Copy(data, 8, chunkPayload, 0, chunkPayload.Length);

                    // Per-session inbound counter for diag tab.
                    BumpSessionCount(session, outbound: false);

                    // Dispatcher-owned sessions: route exclusively through
                    // dispatcher and ack. Skip all legacy if-chains below.
                    var owner = _dispatcher.GetOwner(session);
                    if (owner != null)
                    {
                        SendSessionAck(session, (ushort)seq);
                        _dispatcher.DispatchData(session, seq, chunkPayload);
                        return;
                    }

                    // Ack on the telemetry session. Send the SPECIFIC seq
                    // received, not a running max — same fix as the mgmt-port
                    // path below. Running max would silently drop retransmits
                    // of older seqs (wheel sees stale ack, keeps re-pushing).
                    // _sessionAckSeq is kept up-to-date for diagnostics and
                    // the periodic SendStatusPush re-ack path.
                    if (session == FlagByte)
                    {
                        if (seq > _sessionAckSeq)
                            _sessionAckSeq = seq;
                        SendSessionAck(FlagByte, (ushort)seq);

                        // Capture wheel's post-subscription response for diag-tab
                        // visibility. Window is opened by SendTierDefinition and
                        // expires after 5 s.
                        if (_subscriptionResponseDeadlineTicks != 0
                            && System.Diagnostics.Stopwatch.GetTimestamp() < _subscriptionResponseDeadlineTicks
                            && chunkPayload.Length > 0)
                        {
                            lock (_subscriptionResponseChunks)
                            {
                                if (_subscriptionResponseChunks.Count < 32)
                                    _subscriptionResponseChunks.Add(chunkPayload);
                            }
                        }
                    }

                    // Ack on the management session (upload handshake AND
                    // wheel-pushed catalog/URL records). The wheel keys each
                    // outbound chunk by its individual seq and expects an
                    // ack with that specific seq number — sending a running
                    // max here meant retransmits of older seqs (5..14 after
                    // we've acked 21) never got cleared, causing the wheel
                    // to re-push the catalog every ~1s indefinitely. Verified
                    // 2026-05-09 trace: 219 b2h sess=01 chunks but only 120
                    // acks because the running-max gate dropped retransmits.
                    if (session == _mgmtPort && _mgmtPort != 0)
                    {
                        if (seq > _mgmtAckSeq)
                            _mgmtAckSeq = seq;
                        SendSessionAck(_mgmtPort, (ushort)seq);
                        _mgmtResponseEvent.Set();
                    }

                    // File-transfer session: ack + count responses. Capture shows
                    // device sends sub-msg 1 echo (6 chunks) then sub-msg 2 ack
                    // (6 chunks) — simplest heuristic is to wait for a quiet period
                    // after some chunks, but sub-msg events fire once enough chunks
                    // arrive to assume the device replied. Session number is
                    // dynamic (see ChooseUploadSession).
                    // Upload-session inbound: ack here so the wheel sees the host
                    // is keeping up, then forward to WheelUploadCoordinator which
                    // owns the sub-msg-1/2 wait events and dir-listing reassembly.
                    //
                    // Ack ALL FT-candidate sessions (0x04..0x08), not just the
                    // uploader's currently-tracked ActiveSession. The wheel
                    // device-inits multiple FT sessions (e.g. 0x05 and 0x07 in
                    // the same connect cycle) and pushes data on whichever it
                    // chose; gating on ActiveSession alone meant chunks on the
                    // *other* session were never acked, and the wheel re-pushed
                    // them every ~20s indefinitely (observed sess=0x05 seq=6..9
                    // re-burst on 20s cadence in 2026-05-09 traces). Forward
                    // to NoteInboundChunk regardless — its own ActiveSession
                    // gate decides what's "real" upload data.
                    if (session >= 0x04 && session <= 0x08)
                    {
                        SendSessionAck(session, (ushort)seq);
                        _uploader.NoteInboundChunk(session, seq, chunkPayload);
                    }

                    // configJson state push from wheel. Older firmware uses
                    // session 0x09; KS Pro / 2026-04+ firmware moved it to
                    // session 0x0a (verified in usb-capture/ksp/mozahubstartup.pcapng:
                    // sess=0x09 carries only empty heartbeats, sess=0x0a carries
                    // 9.5 KB compressed configJson state with `00 [size:4 LE]
                    // [count:4] 78 9c [zlib]` envelope; Pithouse host replies on
                    // sess=0x0a with `{"configJson()": {...dashboards...}, "id": N}`).
                    // Listen on both — the wheel only pushes on whichever its
                    // firmware uses, and the parser drops malformed blobs.
                    if (session == 0x09 || session == 0x0a)
                    {
                        SendSessionAck(session, (ushort)seq);
                        if (session == 0x09) _session09InboundSeq = seq;
                        try
                        {
                            MozaLog.Debug(
                                $"[Moza] session 0x{session:X2} inbound chunk: seq={seq} payload={chunkPayload.Length}B " +
                                $"first8={BitConverter.ToString(chunkPayload, 0, Math.Min(8, chunkPayload.Length))}");
                        }
                        catch { }
                        // Seq-aware path: detect a dropped chunk and recover
                        // depending on whether we have a cached LastState.
                        // Wire-trace analysis (2026-05-08): a single missing
                        // chunk seq corrupts the zlib stream for the whole
                        // burst. (2026-05-09): the wheel does NOT re-burst
                        // on a re-issued OpenRequest once it considers the
                        // session initialised — it only sends short heartbeat
                        // chunks, never the full state. So our recovery has to
                        // be cache-aware:
                        //  - LastState present (we got a complete burst earlier
                        //    this session): no recovery needed. Downstream
                        //    consumers keep using the cached state. The wheel's
                        //    library doesn't change without a user action, so
                        //    a stale-but-correct cache is better than a
                        //    forced re-handshake.
                        //  - LastState absent (cold-start / first burst dropped):
                        //    escalate. First gap → prime+open-request. Repeated
                        //    gaps → full RestartForSwitch (the only known way
                        //    to make the wheel re-burst once it's stuck).
                        _configJsonLastChunkUtcTicks = System.DateTime.UtcNow.Ticks;
                        var result = _configJson.OnChunk(seq, chunkPayload, $"sess=0x{session:X2}");
                        if (result == ConfigJsonClient.ChunkResult.StateReady)
                        {
                            _configJsonGapCount = 0;
                            var state = _configJson.LastState;
                            if (state != null)
                            {
                                MaybeSendConfigJsonReply(state, session);
                                MaybeTriggerDashboardDownload(state);
                            }
                        }
                        else if (result == ConfigJsonClient.ChunkResult.GapDetected)
                        {
                            HandleConfigJsonGap(session, seq);
                        }
                    }

                    // Session 0x0a: RPC reply channel. Host sends `{method(): arg, id}`
                    // calls, wheel replies with `{id, result}` in the same zlib
                    // envelope. Reassemble and hand off to RPC waiters.
                    // Ack already sent by the configJson handler above (sess 0x09/0x0a
                    // share the same handler) — DON'T double-ack here, the wheel sees
                    // the duplicate as a sequence-number violation and retransmits.
                    if (session == 0x0a)
                    {
                        // Seq-aware: a missing chunk corrupts the RPC reply
                        // envelope. Clear and let the calling SendRpcCall time
                        // out — the next RPC will re-establish its own seq train.
                        if (_session0aInbox.AddChunk(seq, chunkPayload, "sess=0x0a"))
                        {
                            byte[]? replyBlob = _session0aInbox.TryDecompress();
                            if (replyBlob != null)
                            {
                                _session0aInbox.Clear();
                                _rpc.HandleReply(replyBlob);
                            }
                        }
                    }

                    // Session 0x03: tile-server state channel. Host opens on
                    // connect; wheel may push tile-server state blobs using a
                    // 12-byte envelope (distinct from 0x04/0x09's 9-byte form):
                    //   FF 01 00 [comp_size+4 u32 LE] FF 00 [uncomp_size u24 BE]
                    // Feed through TileServerStateParser; ack to keep the
                    // session alive regardless.
                    // Tile-server state push from wheel. Older firmware uses
                    // session 0x03 (host-opened); KS Pro / 2026-04+ firmware
                    // moved the wheel-side push to session 0x0b (verified in
                    // usb-capture/ksp/mozahubstartup.pcapng: sess=0x0b carries
                    // 750 B with `ff 01 00 d1 00 00 00 ff 00 00 01 8e 78 9c`
                    // 12-byte envelope decompressing to
                    // `{"map":{...},"root":"/home/moza/resource/tile_map/",...}`).
                    // Listen on both — only one applies per firmware.
                    if (session == 0x03 || session == 0x0b)
                    {
                        SendSessionAck(session, (ushort)seq);
                        // Strip + validate CRC32 trailer before handing to
                        // parser. Without validation, a corrupted chunk
                        // injects random bytes into the parser's sentinel-
                        // scan window and can either drop a real envelope
                        // or hit-spot a spurious sentinel. Silently drop
                        // mismatches; wheel will re-push if we don't ack.
                        if (chunkPayload.Length >= 4)
                        {
                            // 3-byte CRC trailer — see catalog feed above for
                            // the full rationale (2026-05-09 firmware audit).
                            int netLen = chunkPayload.Length - 3;
                            uint wireCrc = (uint)(chunkPayload[netLen]
                                                | (chunkPayload[netLen + 1] << 8)
                                                | (chunkPayload[netLen + 2] << 16));
                            uint calcCrc = TierDefinitionBuilder.Crc32(chunkPayload, 0, netLen) & 0xFFFFFFu;
                            if (calcCrc != wireCrc)
                            {
                                Interlocked.Increment(ref _tileServerCrcRejects);
                                int n = _tileServerCrcRejects;
                                if (n <= 5 || n % 50 == 0)
                                    MozaLog.Debug(
                                        $"[Moza] Tile-server chunk CRC mismatch sess=0x{session:X2} " +
                                        $"seq={seq}: calc=0x{calcCrc:X6} wire=0x{wireCrc:X6} (rejects={n})");
                            }
                            else
                            {
                                // Seq dedup: skip retransmits so the parser
                                // buffer doesn't accumulate duplicate copies
                                // of the same chunk and break sentinel scan.
                                bool isNew;
                                lock (_tileServerHighestSeq)
                                {
                                    isNew = !_tileServerHighestSeq.TryGetValue(session, out int prev) || seq > prev;
                                    if (isNew) _tileServerHighestSeq[session] = seq;
                                }
                                if (isNew)
                                {
                                    byte[] net = new byte[netLen];
                                    Array.Copy(chunkPayload, 0, net, 0, netLen);
                                    var tile = _tileServerParser.OnChunk(net);
                                    if (tile != null)
                                    {
                                        try
                                        {
                                            MozaLog.Debug(
                                                $"[Moza] Tile-server state received on session 0x{session:X2}: " +
                                                $"root='{tile.Root}' version={tile.Version} games={tile.Games.Count} " +
                                                $"any_populated={tile.AnyPopulated}");
                                        }
                                        catch { /* logging optional */ }
                                    }
                                }
                            }
                        }
                    }

                    // Buffer the chunk payload (strip + validate CRC) for
                    // channel catalog parsing. Wheel may push the catalog on
                    // either the telemetry session (FlagByte) or the mgmt
                    // session (0x01) depending on firmware — V0 URL-
                    // subscription firmware (CSP post-2026-04) sends URL
                    // entries on 0x01 while V2-compact firmware uses 0x02.
                    // Collect from both during preamble.
                    //
                    // CRC32 validation: each chunk carries a 4-byte CRC32 LE
                    // trailer covering the prior bytes. Without validation,
                    // chunks corrupted in transit (1100+ frame-start resyncs
                    // observed 2026-05-09 in healthy traces — Wine SerialPort
                    // R/W contention) would feed garbled bytes to the parser
                    // and produce mangled URLs in the diagnostic catalog
                    // (e.g. "v1/gameDa???????t???"). On CRC mismatch we drop
                    // the chunk silently — the wheel will re-push it on its
                    // own retransmit cadence, and the parser stays clean.
                    bool isCatalogSession = session == FlagByte || session == 0x01;
                    if (isCatalogSession && data.Length > 12 && chunkPayload.Length >= 4)
                    {
                        // Inbound chunks from this wheel firmware carry a
                        // 3-byte CRC trailer (low 24 bits of CRC32 LE), not
                        // 4 bytes. Verified 2026-05-09 against captured wheel
                        // catalog/configJson/sess-02 chunks — 154/154 chunks
                        // matched 3-byte CRC, 0/154 matched 4-byte. Stripping
                        // 4 bytes (the prior assumption) chopped the last
                        // data byte of every chunk, which explained the
                        // deterministic `v1/gameDa???\x04` URL corruption:
                        // each chunk-boundary lost the last URL char, and
                        // the next chunk's first byte (0x04 = next record's
                        // tag) bled into where that char should have been.
                        // Outbound chunks (our `TierDefinitionBuilder.Chunk-
                        // Message`) still emit 4-byte CRC and the wheel
                        // accepts them, so the asymmetry is wheel-side.
                        int netLen = chunkPayload.Length - 3;
                        uint wireCrc = (uint)(chunkPayload[netLen]
                                            | (chunkPayload[netLen + 1] << 8)
                                            | (chunkPayload[netLen + 2] << 16));
                        uint calcCrc = TierDefinitionBuilder.Crc32(chunkPayload, 0, netLen) & 0xFFFFFFu;
                        if (calcCrc == wireCrc)
                        {
                            // Seq-aware append: dedup retransmits per session
                            // so the buffer doesn't get the same bytes 2-3×
                            // (the wheel re-pushes catalog chunks until our
                            // ack lands). Without dedup, duplicate appends
                            // misalign the size-prefixed TLV walk and produce
                            // garbled URLs at indexes whose record straddles
                            // a duplicated chunk boundary.
                            _catalogParser.AppendChunkIfNew(session, seq, chunkPayload, 0, netLen);
                        }
                        else
                        {
                            Interlocked.Increment(ref _catalogCrcRejects);
                            // Log first few rejections so we can spot a sudden
                            // surge, then sample at 1-per-50 to avoid flooding.
                            int rejCount = _catalogCrcRejects;
                            if (rejCount <= 5 || rejCount % 50 == 0)
                            {
                                MozaLog.Debug(
                                    $"[Moza] Catalog chunk CRC mismatch sess=0x{session:X2} " +
                                    $"seq={seq}: calc=0x{calcCrc:X6} wire=0x{wireCrc:X6} " +
                                    $"(total rejects: {rejCount})");
                            }
                        }
                    }
                }

                // Type 0x00 = end marker
                if (type == 0x00)
                {
                    int closeSeq = data.Length >= 8 ? data[6] | (data[7] << 8) : 0;
                    // Dispatcher-owned sessions: route exclusively.
                    if (_dispatcher.GetOwner(session) != null)
                    {
                        _dispatcher.DispatchClose(session, closeSeq);
                        return;
                    }
                    // Legacy routing for non-dispatcher sessions.
                    if (session == _mgmtPort) _mgmtResponseEvent.Set();
                    _uploader.NoteEndMarker(session);
                }

                return;
            }

            // Display sub-device responses (identity probe answers)
            // cmd byte is data[2], which is the response group byte (request | 0x80)
            // 0x87 = model name response (to 0x07 query)
            if (data[2] == 0x87 && data.Length >= 5 && data[3] == 0x01)
            {
                // data[3] = sub-device index (0x01), data[4..] = null-terminated ASCII name
                int nameLen = 0;
                for (int k = 4; k < data.Length && data[k] != 0; k++)
                    nameLen++;
                if (nameLen > 0)
                {
                    _displayModelName = System.Text.Encoding.ASCII.GetString(data, 4, nameLen);
                    _displayDetected = true;
                    MozaLog.Debug($"[Moza] Display sub-device detected: \"{_displayModelName}\"");
                }
            }
        }

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
            if (_state == TelemetryState.Idle || !_connection.IsConnected)
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

                // Re-read _tiers: MaybeSwapProfileForCatalog may have rebuilt
                // them above (synthesised from wheel-advertised catalog when
                // no profile was loaded at Start time).
                var tiers = _tiers;
                if (tiers == null || tiers.Length == 0)
                    return;

                TickPostRenegDiagnostic(tiers);
                TickFireGameStartHandshake();
                TickEmitValueFrames(tiers);

                TickEmitEnableAndSequence();
                // Parity polls keep the wheel engaged during idle. Empirically
                // verified: turning them off entirely caused the dashboard to
                // freeze on last-value within ~5 min. ~1 Hz cadence is enough
                // at ~12% of PitHouse's full ~7-22 Hz wire cost.
                TickEmitPeripheralPolls();
                TickEmitLedStatePolls();
                TickEmitRetransmits();
                TickEmitTierDefBlindRetransmits();
                TickRetryS09IfNotEstablished();
                TickConfigJsonStuckWatchdog();
                TickGrowSubscriptionIfCatalogStable();

                _tickCounter++;

                TickEmitWidgetPoll();
                TickEmitSlowPath();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Telemetry send error: {ex.Message}");
            }
        }

        // ── Tick-phase helpers ──────────────────────────────────────────────

        private void TickPreamble()
        {
            _tickCounter++;

            int slowInterval = Math.Max(1, 1000 / _baseTickMs);
            if (_tickCounter % slowInterval == 0)
                SendHeartbeat();

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
                if (_catalogParser.BufferLength > 4096)
                {
                    // Buffer-overrun guard: post-renegotiate noise can fill the
                    // buffer with redundant end-marker bytes; drop them since
                    // the parser keeps the merged catalog cached.
                    _catalogParser.ClearBuffer();
                }
            }
        }

        private void TickPostRenegDiagnostic(TierState[] tiers)
        {
            if (_postRenegDiagTicks <= 0) return;
            bool useV0Diag = _policy.Encoding == TierDefEncoding.V0Url;
            MozaLog.Debug(
                $"[Moza] TICK DIAG: tiers={tiers.Length} " +
                $"testMode={TestMode} gameRunning={_gameRunning} " +
                $"useV0={useV0Diag} tickCounter={_tickCounter} " +
                $"profile={_profile?.Name ?? "null"} " +
                $"catalog={_catalogParser.Catalog?.Count ?? -1} " +
                $"framesSent={_framesSent}");
            _postRenegDiagTicks--;
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

            bool useV0Values = _policy.Encoding == TierDefEncoding.V0Url;
            if (useV0Values)
            {
                if (TestMode || _gameRunning)
                    SendV0ValueFrames(snapshot);
                return;
            }

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
                    LastFrameSent = frame;
                    _framesSent++;
                    Diagnostics.RecordFrame(frame);
                }
            }
        }

        /// <summary>FFB-enable + sequence-counter. Both gated on gameRunning
        /// because PitHouse only emits these while a game is actively driving
        /// telemetry — bursting them at idle is the largest plugin-vs-PitHouse
        /// drift source observed in 2026-04-29 captures.</summary>
        private void TickEmitEnableAndSequence()
        {
            if (!TestMode && !_gameRunning) return;
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
        private void TickEmitTierDefBlindRetransmits()
        {
            if (_tierDefBlindFrames == null) return;
            if (_tierDefBlindSentRounds >= TierDefBlindMaxRounds)
            {
                _tierDefBlindFrames = null;
                return;
            }
            // Early-exit: every tier-def chunk was tracked via SendAndTrack-
            // Chunk, so SessionRetransmitter.Contains tells us per-chunk ack
            // state. If none of the blind seqs are still in the queue, the
            // wheel acked them all and additional rounds are pure waste.
            if (_tierDefBlindSentRounds > 0 && AllBlindChunksAcked())
            {
                MozaLog.Debug(
                    $"[Moza] Blind retransmit early-exit after round {_tierDefBlindSentRounds}/{TierDefBlindMaxRounds} " +
                    "(all blind chunks acked by wheel)");
                _tierDefBlindFrames = null;
                return;
            }
            var schedule = global::MozaPlugin.Protocol.RetryBackoff.TierDefBlindMs;
            int gateMs = schedule[Math.Min(_tierDefBlindSentRounds, schedule.Length - 1)];
            if (Environment.TickCount - _tierDefBlindLastTickCount < gateMs) return;

            _tierDefBlindSentRounds++;
            _tierDefBlindLastTickCount = Environment.TickCount;
            for (int i = 0; i < _tierDefBlindFrames.Length; i++)
            {
                if (_state == TelemetryState.Idle || !_connection.IsConnected) break;
                _connection.Send(_tierDefBlindFrames[i]);
            }
            MozaLog.Debug(
                $"[Moza] Blind retransmit round {_tierDefBlindSentRounds}/{TierDefBlindMaxRounds} " +
                $"({_tierDefBlindFrames.Length} chunks, next gate {gateMs}ms)");
            if (_tierDefBlindSentRounds >= TierDefBlindMaxRounds)
                _tierDefBlindFrames = null;
        }

        /// <summary>True iff every chunk in <see cref="_tierDefBlindFrames"/>
        /// has been acked by the wheel (and therefore removed from the
        /// retransmitter queue). Frame layout per <see cref="TierDefinition-
        /// Builder.ChunkMessage"/> places session at byte 6 and seq at
        /// bytes 8-9 (LE). Returns false if any chunk is still pending.
        /// </summary>
        private bool AllBlindChunksAcked()
        {
            if (_tierDefBlindFrames == null) return true;
            foreach (var frame in _tierDefBlindFrames)
            {
                if (frame == null || frame.Length < 10) continue;
                byte session = frame[6];
                int seq = frame[8] | (frame[9] << 8);
                if (_retransmitter.Contains(session, seq))
                    return false;
            }
            return true;
        }

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
        private void TickRetryS09IfNotEstablished()
        {
            if (_state == TelemetryState.Idle) return;
            if (_s09RetryRounds >= S09RetryMaxRounds) return;
            if (!_connection.IsConnected) return;

            var s09 = _sessions.GetOrCreate(0x09);
            if (s09.DeviceInitiated) return;

            int now = Environment.TickCount;
            if (_s09RetryRounds > 0)
            {
                int gateMs = S09BackoffMs[Math.Min(_s09RetryRounds - 1, S09BackoffMs.Length - 1)];
                if ((now - _s09RetryLastTickCount) < gateMs) return;
            }

            _s09RetryRounds++;
            _s09RetryLastTickCount = now;

            // Use a fresh seq for the OpenRequest so the wheel doesn't dedupe
            // against the prior open. Match the recovery-seq pattern from the
            // A9 gap-recovery path (recoverySeq = seq + 0x100).
            ushort recoverySeq = (ushort)(0x000B + _s09RetryRounds * 0x10);
            MozaLog.Warn(
                $"[Moza] sess=0x09 not yet device-initiated; retry round " +
                $"{_s09RetryRounds}/{S09RetryMaxRounds} (open-seq=0x{recoverySeq:X4})");

            try
            {
                SendSessionPrime(0x09, (ushort)(0x0001 + _s09RetryRounds));
                SendConfigJsonOpenRequest(0x09, recoverySeq);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] sess=0x09 retry emit failed: {ex.Message}");
            }

            if (_s09RetryRounds >= S09RetryMaxRounds)
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x09 retry budget exhausted after {S09RetryMaxRounds} rounds " +
                    "— wheel will not engage configJson handshake. Dashboard rendering may fail. " +
                    "Recovery: disable+re-enable plugin.");
            }
        }

        /// <summary>
        /// When the most-recent tier-def emission had unbound channels,
        /// schedule a kind=4 dashboard-switch re-emit for the slot the
        /// wheel is currently on. Re-applying the same slot tells some
        /// firmwares to re-run their dashboard-load sequence which re-
        /// advertises the full channel catalog. Throttled by
        /// <see cref="_lastCatalogResyncProbeUtcTicks"/> so a stuck case
        /// can't produce a switch storm.
        /// </summary>
        private void ScheduleCatalogResyncProbe()
        {
            long now = System.DateTime.UtcNow.Ticks;
            if (now - _lastCatalogResyncProbeUtcTicks < CatalogResyncProbeMinIntervalTicks)
                return;
            _lastCatalogResyncProbeUtcTicks = now;

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
            if (cur - _catalogCountAtLastSubscription < CatalogGrowthMinDelta) return;

            int idle = _catalogParser.LastActivityMs == 0
                ? int.MaxValue
                : Environment.TickCount - _catalogParser.LastActivityMs;
            if (idle < CatalogGrowthQuietMs) return;

            int prev = _catalogCountAtLastSubscription;
            MozaLog.Debug(
                $"[Moza] Catalog grew {prev}→{cur} since last subscription " +
                $"(idle {idle}ms ≥ {CatalogGrowthQuietMs}ms) — re-applying tier-def");
            try
            {
                ApplySubscription(force: true);
                _catalogCountAtLastSubscription = _catalogParser.Count;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Catalog-growth re-subscribe failed: {ex.Message}");
            }
        }

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
        private void TickConfigJsonStuckWatchdog()
        {
            if (_state == TelemetryState.Idle) return;
            if (!_connection.IsConnected) return;
            if (_configJson.LastState != null) return;
            if (_configJsonLastChunkUtcTicks == 0) return; // never received any chunk yet

            // Skip when we have a working dashboard despite the missing
            // library list. Catalog + active subscription is enough for
            // dashboard rendering; configJson library is a UI concern.
            bool haveCatalog = (_catalogParser?.Count ?? 0) > 0;
            bool haveSubscription = _activeSubscription != null;
            if (haveCatalog && haveSubscription)
                return;

            long now = System.DateTime.UtcNow.Ticks;
            if (now - _configJsonLastChunkUtcTicks < ConfigJsonNoStateRestartTimeoutTicks)
                return;
            if (now - _configJsonLastEscalationUtcTicks < ConfigJsonEscalationCooldownTicks)
                return;

            _configJsonLastEscalationUtcTicks = now;
            _configJsonGapCount = 0;
            // Reset the chunk-time so the next restart cycle gets a fresh
            // window before this watchdog fires again.
            _configJsonLastChunkUtcTicks = now;
            MozaLog.Warn(
                "[Moza] configJson stuck-state watchdog: " +
                $"chunks arrived but no valid state for {ConfigJsonNoStateRestartTimeoutTicks / System.TimeSpan.TicksPerMillisecond / 1000}s, " +
                "and no catalog/subscription either — triggering full RestartForSwitch");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { RestartForSwitch(); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] RestartForSwitch from stuck-state watchdog failed: {ex.Message}");
                }
            });
        }

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
            SendStatusPush();
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
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
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
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
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
        private void SendSessionPrime(byte session, ushort seq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
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

            int seq = _session02OutboundSeq;
            bool anySent = false;
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
                    value = ch != null
                        ? GenerateV0TestValue(ch, _testPhaseV0)
                        : GenerateV0TestValueDefault(_testPhaseV0);
                }
                else
                {
                    value = ch != null ? ResolveV0ChannelValue(ch, snapshot) : 0.0;
                }

                byte[] valueBytes = TelemetryFrameBuilder.EncodeV0Value(compression, value);
                byte[] vframe = TelemetryFrameBuilder.BuildV0ValueFrame(wheelIdx, valueBytes);
                var frames = TierDefinitionBuilder.ChunkMessage(vframe, FlagByte, ref seq);
                foreach (var frame in frames)
                {
                    if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                    SendAndTrackChunk(frame);
                }
                anySent = true;
            }

            _session02OutboundSeq = seq;
            if (anySent) _framesSent++;
            if (TestMode) _testPhaseV0 = (_testPhaseV0 + 1) % 100;
        }

        private static double GenerateV0TestValueDefault(int phase)
        {
            const int period = 100;
            double t = 1.0 - Math.Abs(phase * 2.0 / period - 1.0);
            return t * 100.0; // 0..100 sweep — covers most percent / RPM-scaled channels
        }

        /// <summary>Phase counter for V0 test pattern triangle wave.</summary>
        private int _testPhaseV0;

        private static double GenerateV0TestValue(ChannelDefinition ch, int phase)
        {
            const int period = 100;
            double t = 1.0 - Math.Abs(phase * 2.0 / period - 1.0); // 0 → 1 → 0
            (double min, double max) = TelemetryEncoder.GetTestRange(ch.Compression);
            return min + (max - min) * t;
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
            int seq = ++_session09OutboundSeq;
            SendSessionPrime(0x09, (ushort)seq);
        }

        /// <summary>
        /// Tiered chunk-drop recovery for sess=0x09 / 0x0a configJson. Tier
        /// chosen by gap count and cached state:
        ///
        ///   Tier 0 (LastState present, any gap count): no action required.
        ///   The cached state is still authoritative for downstream consumers
        ///   (dashboard library list); the wheel doesn't change state without
        ///   a user action so a stale-but-correct cache is preferable to a
        ///   forced re-handshake (which the wheel often ignores anyway).
        ///
        ///   Tier 1 (LastState absent, first gap): prime + open-request. Some
        ///   firmwares respond to the prime by resetting their sess=0x09 state
        ///   machine and re-bursting on next OpenRequest.
        ///
        ///   Tier 2 (LastState absent, repeated gaps): full RestartForSwitch.
        ///   The Stop+11s-settle+Start sequence is the only reliable way to
        ///   force a wheel that's stuck mid-burst back to a cold-start where
        ///   it'll definitely emit the full state again.
        ///
        /// Cooldown gates Tier 2 so a chunk-drop storm can't cycle Restart
        /// faster than the wheel's settle window.
        /// </summary>
        private void HandleConfigJsonGap(byte session, int seq)
        {
            _configJsonGapCount++;
            bool haveCachedState = _configJson.LastState != null;
            string tag = $"sess=0x{session:X2}";
            string cachedTag = haveCachedState ? "cached-state" : "no-state";

            if (haveCachedState)
            {
                MozaLog.Warn(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"buffer cleared, keeping cached state — no recovery action");
                return;
            }

            if (_configJsonGapCount <= ConfigJsonGapPrimeRetryAt)
            {
                try
                {
                    int recoveryOpenSeq = unchecked((ushort)(seq + 0x100 * _configJsonGapCount));
                    int primeSeq = unchecked((ushort)(seq + 0x200 + _configJsonGapCount));
                    MozaLog.Warn(
                        $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                        $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, prime seq=0x{primeSeq:X4})");
                    SendSessionPrime(session, (ushort)primeSeq);
                    SendConfigJsonOpenRequest(session, (ushort)recoveryOpenSeq);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] {tag} configJson recovery emit failed: {ex.Message}");
                }
                return;
            }

            if (_configJsonGapCount >= ConfigJsonGapRestartAt)
            {
                long now = System.DateTime.UtcNow.Ticks;
                if (now - _configJsonLastEscalationUtcTicks < ConfigJsonEscalationCooldownTicks)
                {
                    MozaLog.Warn(
                        $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                        "in escalation cooldown — deferring full restart");
                    return;
                }
                _configJsonLastEscalationUtcTicks = now;
                _configJsonGapCount = 0;
                MozaLog.Warn(
                    $"[Moza] {tag} configJson recovery escalation: " +
                    "no cached state and prime+open-request didn't recover the burst — " +
                    "triggering full RestartForSwitch");
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { RestartForSwitch(); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[Moza] {tag} RestartForSwitch from gap recovery failed: {ex.Message}");
                    }
                });
                return;
            }

            // Mid-tier (gap count between prime-retry and restart): just log;
            // the wheel may still be flushing chunks and a heartbeat could
            // reach us before the next escalation tier fires.
            MozaLog.Warn(
                $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                "waiting for next chunk before escalating");
        }

        /// <summary>
        /// Host-initiated session-open request for the configJson channel
        /// (port 9). PitHouse capture
        /// (`wireshark/csp/startup, change knob colors, ...pcapng` pno~97431)
        /// shows it uses a distinct magic <c>7c 1e 6c 80</c> for this port —
        /// upload-style <c>7c 23 46 80</c> does NOT trigger wheel device-init
        /// for 0x09. Without this prompt CSP firmware never opens the
        /// configJson channel, leaving plugin "Wheel Files" tab empty.
        /// </summary>
        private void SendConfigJsonOpenRequest(byte port, ushort seq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x1E, 0x6C, 0x80,           // configJson host-init magic
                (byte)(seq & 0xFF), (byte)(seq >> 8),
                port, 0x00,                        // port (LE)
                0xFE, 0x01,
                0x00                               // checksum placeholder
            };
            frame[14] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

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
            //   74..79  = 6 grp 0x3F dev 0x17 1901/1903/1a01/1a03/1f00/2100 display variants
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
                // grp 0x3F dev 0x17 display variants
                int s = slot - 74;
                frame = s switch
                {
                    0 => BuildGenericFrame(0x3F, 0x17, new byte[] { 0x19, 0x01, 0x00 }),
                    1 => BuildGenericFrame(0x3F, 0x17, new byte[] { 0x19, 0x03, 0x00 }),
                    2 => BuildGenericFrame(0x3F, 0x17, new byte[] { 0x1A, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
                    3 => BuildGenericFrame(0x3F, 0x17, new byte[] { 0x1A, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
                    4 => BuildGenericFrame(0x3F, 0x17, new byte[] { 0x1F, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00 }),
                    5 => BuildGenericFrame(0x3F, 0x17, new byte[] { 0x21, 0x00, 0x00 }),
                    _ => null,
                };
            }

            if (frame != null) _connection.Send(frame);
        }

        // Build any group/dev frame from raw payload bytes.
        private byte[] BuildGenericFrame(byte grp, byte dev, byte[] payload)
        {
            var frame = new byte[payload.Length + 4];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = grp;
            frame[3] = dev;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        // Build grp=0x40 dev=0x17 frame from raw payload bytes.
        // Wraps with start byte, length, cmd bytes, checksum.
        private byte[] BuildGroup40Bytes(byte[] payload)
        {
            var frame = new byte[payload.Length + 4];
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

        private void SendStatusPush()
        {
            // Disabled. Per-chunk acks at the inbound dispatch (line 2315
            // SendSessionAck on session==FlagByte) cover steady-state ack
            // requirements. The 1Hz re-ack here was emitting a stale running-
            // max ack — useful when ack reliability was poor under the old
            // running-max ack bug, but redundant now and a footgun for
            // cross-epoch staleness (running-max from a prior session
            // re-acked into a new session). 2026-05-09 trace analysis
            // showed this was the only remaining stale-running-max ack site.
        }

        public void Dispose()
        {
            // Idempotent: SimHub may invoke Dispose more than once during plugin
            // reload; double-dispose on ManualResetEventSlim throws.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            try { _ackReceived.Dispose(); } catch { }
            try { _mgmtResponseEvent.Dispose(); } catch { }
            try { _uploader?.Dispose(); } catch { }
            try { _dashboardDownloader?.Dispose(); } catch { }
            try { _rpc?.Dispose(); } catch { }
        }

        private class TierState
        {
            public TelemetryFrameBuilder Builder = null!;
            public int TickInterval;
        }
    }
}
