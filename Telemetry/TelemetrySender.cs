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
        private volatile bool _dashSwitchMuted;
        private bool[]? _tierDiagEmitted;
        private volatile bool _enabled;
        private int _tickCounter;
        private int _slowCounter;
        private int _baseTickMs;  // Timer period derived from fastest tier's package_level
        private byte _sequenceCounter;
        private int _displayConfigPage;

        // Preamble state
        private bool _preambleComplete;
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
        // Sessions in 0x04..0x0a that came up device-initiated. The first one
        // observed wins as the upload target unless overridden via
        // `UploadSessionOverride` (UI / test setting).
        private readonly System.Collections.Generic.HashSet<byte> _ftCandidateSessions = new();
        private byte _uploadSession = 0x04;  // default; updated when wheel device-inits a candidate session
        private readonly ManualResetEventSlim _uploadSessionOpened = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _uploadSubMsg1Response = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _uploadSubMsg2Response = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _uploadEndReceived = new ManualResetEventSlim(false);
        private int _uploadInboundSeq;
        private int _uploadOutboundSeq;
        private int _uploadInboundMsgCount;

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

        // Blind retransmission for session 0x01 tier-def chunks. PitHouse
        // retransmits each chunk ~10× regardless of acks (wheel never acks
        // session 0x01). See findings/2026-05-02-tier-def-retransmission.md.
        private byte[][]? _tierDefBlindFrames;
        private int _tierDefBlindSentRounds;
        private int _tierDefBlindLastTickCount;
        private const int TierDefBlindMaxRounds = 12;
        private const int TierDefBlindIntervalMs = 200;

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
        /// firmware that prefers 0x07 / 0x09.
        /// </summary>
        public byte UploadSessionOverride { get; set; } = 0;

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

        // Wheel channel catalog (parsed from incoming 7c:00 session data during preamble)
        private System.Collections.Generic.List<byte> _incomingSessionBuffer = new();
        private volatile System.Collections.Generic.List<string>? _wheelChannelCatalog;
        private volatile int _channelBufferLastActivityMs;
        // Tracks buffer length at last successful catalog parse — drives
        // continuous parse-on-tick. Wheel announces URLs in batches with up
        // to ~1.2s gaps (verified against bridge-20260503-112940.jsonl: idx
        // 1-8 at t+7.124s, idx 9-16 at t+8.328s). Parse on every buffer
        // growth + merge non-destructively so no URLs get dropped.
        private int _lastCatalogParseLen;

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

        // Upload-session inbound dir-listing buffer. After upload, wheel pushes
        // a fresh directory listing on the same session which lets us detect
        // when the upload is actually live on the device (rather than just
        // transmitted).
        private readonly SessionDataReassembler _uploadInbox = new();
        private volatile bool _uploadDirListingRefreshed;
        public bool Session04DirListingRefreshed => _uploadDirListingRefreshed;

        // RPC on 0x09/0x0a (host→device management RPCs such as completelyRemove).
        // Replies come back from device in same zlib envelope as configJson state.
        private int _rpcNextId = 1000;
        private readonly object _rpcLock = new object();
        private readonly System.Collections.Generic.Dictionary<int, ManualResetEventSlim> _rpcWaiters
            = new System.Collections.Generic.Dictionary<int, ManualResetEventSlim>();
        private readonly System.Collections.Generic.Dictionary<int, byte[]> _rpcReplies
            = new System.Collections.Generic.Dictionary<int, byte[]>();
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
                if (value != null)
                    _dashboardDownloader = new DashboardDownloader(
                        _connection, value,
                        MozaPlugin.Instance?.DashProfileStore ?? new DashboardProfileStore(),
                        _retransmitter, _dispatcher);
                else
                    _dashboardDownloader = null;
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
        public System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalog => _wheelChannelCatalog;

        /// <summary>Raw .mzdash file content for upload to the wheel. Set by ApplyTelemetrySettings.</summary>
        public byte[]? MzdashContent { get; set; }

        /// <summary>Dashboard name (used for logging). Set by ApplyTelemetrySettings.</summary>
        public string MzdashName { get; set; } = "";

        /// <summary>Whether to upload the dashboard to the wheel on startup.</summary>
        public bool UploadDashboard { get; set; } = true;


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
        /// <summary>True between Start() and Stop(). Exposed for diagnostics panel.</summary>
        public bool Enabled => _enabled;
        public byte[]? LastFrameSent { get; private set; }
        public TelemetryDiagnostics Diagnostics { get; } = new TelemetryDiagnostics();

        // Read-only accessors for DashboardSwitchAutoTest
        internal byte? ActiveFlagBase => _activeSubscription?.FlagBase;
        internal int ActiveTierCount => _tiers?.Length ?? 0;
        public string? ActiveProfileName => _profile?.Name;
        internal int CatalogChannelCount => _wheelChannelCatalog?.Count ?? 0;
        private DashboardSwitchAutoTest? _autoTest;

        public TelemetrySender(MozaSerialConnection connection)
        {
            _connection = connection;
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
                _enabled = false;
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
            _enabled = true;
            _tickCounter = 0;
            _framesSent = 0;
            _sequenceCounter = 0;
            _slowCounter = 0;
            _displayConfigPage = 0;
            _preambleComplete = false;
            lock (_incomingSessionBuffer) { _incomingSessionBuffer.Clear(); }
            _lastCatalogParseLen = 0;
            _wheelChannelCatalog = null;
            _dashSwitchMuted = false;
            _nextFlagBase = 0;
            _activeSubscription = null;
            _sessionAckSeq = 0;
            _dashboardDownloadTriggered = false;
            _preambleTickTarget = Math.Max(1, 1000 / _baseTickMs);

            BuildCachedFrames();

            // Subscribe early so we catch fc:00 acks during port probing AND preamble
            _connection.MessageReceived += OnMessageDuringPreamble;

            // Probe for available ports and open sessions.
            // This may run on a background thread (dispatched by StartTelemetryIfReady)
            // so the serial read thread stays free to deliver fc:00 ack responses.
            ProbeAndOpenSessions();

            // Bail out if Stop() was called while we were probing
            if (!_enabled) return;

            // Universal Hub: PitHouse fires a 5-frame burst enumerating hub
            // slots ~300ms after sessions open (gfdsgfd.pcapng f54501 t=17.08s,
            // sessions opened at t=16.77s). Each frame asks for the device-type
            // code on a slot; hub responds with `e4/0x21 cmd 01 NN VV`. Plugin
            // mirrors this so wheel firmware sees the same handshake and
            // populates per-port device metadata. Skipped when no hub detected.
            if (_connection.HubProbeSucceeded)
                SendHubSlotEnumeration();

            // Prime session 0x09 (configJson state push channel). Wheels we've
            // observed (KS Pro on Universal Hub, plugin v0.8.3) only open
            // 0x05/0x07 in their device-init burst, NOT 0x09 — leaving the
            // configJson handshake stuck and the wheel display unable to
            // resolve which dashboard is active. Pithouse encourages 0x09 by
            // sending an empty data frame on it before any clean session
            // opens (mozahubstartup.pcapng frame 639 t=2.345). Mirror that
            // here so wheels include 0x09 in their burst.
            SendSessionPrime(0x09, 0x0001);

            // Post-2026-04 CSP firmware needs an explicit host-init session-open
            // request with a port-9-specific magic before it will device-init
            // the configJson channel. Verified in
            // `wireshark/csp/startup, change knob colors, ...pcapng` — wheel
            // opens 0x09 (`7c 00 09 81 ...`) only after host emits
            // `7c 1e 6c 80 [seq] 00 09 00 fe 01`. The legacy `SendSessionPrime`
            // alone does NOT trigger device-init on this firmware.
            SendConfigJsonOpenRequest(0x09, seq: 0x000B);

            // Dashboard upload runs as a background task, NOT inline. Reasons:
            //   1. Different wheel firmwares device-init the upload session
            //      (0x04..0x0a) at very different times — observed 40 ms (older
            //      direct-base firmware) up to ~11 s (KS Pro on RS21-W18-MC SW,
            //      direct base; diagnostics bundle 20260426-115430o). A
            //      foreground wait long enough to cover the slow case stalls
            //      tier def + telemetry timer for the same duration.
            //   2. Host-opening the upload session as a fallback races the
            //      wheel's eventual late device-init burst — wheel responds by
            //      closing session 0x02 mid-tier-def, killing telemetry.
            //
            // Decoupled flow: tier def + display config + telemetry timer fire
            // immediately. Upload waits in the background for the wheel to
            // device-init an FT session, then sends. If the wheel never opens
            // one (very old firmware or wheels without dash) the background
            // task times out at 60 s and exits silently.
            if (UploadDashboard && MzdashContent != null && _mgmtPort != 0)
                ThreadPool.QueueUserWorkItem(_ => RunBackgroundDashboardUpload());

            if (!_enabled) return;

            // Open session 0x03 (doc [docs/protocol/sessions/lifecycle.md]: host opens 0x03
            // 150-450ms after 0x01/0x02 on new firmware). Sim stubs this but real
            // hardware expects it. Fire-and-forget: we don't rely on its ack.
            // Tile-server data push deferred until after tier def — pushing
            // immediately after open collided with the wheel's session 0x09
            // configJson state burst (under Wine SerialPort R/W contention),
            // costing 6 of 7 state chunks.
            SendSessionOpen(0x03, 0x03);

            WaitForChannelCatalogQuiet(quietMs: 200, timeoutMs: 2000);
            ParseWheelChannelCatalog();
            MaybeSwapProfileForCatalog();

            // Session 0x02 init handshake. PitHouse bridge captures show two
            // small FF records sent on sess=0x02 (kind=2 timestamp/nonce,
            // kind=7 slot-index) shortly after open. Without these the wheel
            // silently ignores dashboard-switch FF records on a fresh session
            // 0x02 — visible as a missing FF kind=4 echo and post-switch
            // test-data not displaying on the new dashboard. See
            // `docs/protocol/findings/2026-05-07-sess02-init-protocol-and-stale-catalog.md`.
            //
            // Sent AFTER `WaitForChannelCatalogQuiet` so the wheel's initial
            // b2h sess=0x02 TLV state push has fully arrived and our auto-FC-
            // acks have flowed back. PitHouse delays ~3 s between sess=0x02
            // open and the first kind=2 — the post-startup quiet window
            // gives an equivalent settling period.
            SendSessionInitHandshake();

            // Push empty-state tile-server blob on session 0x03 (matches
            // PitHouse behaviour: always pushed on connect, wheel never
            // echoes back — host→wheel only). 12-byte envelope
            // `FF 01 00 [comp_sz+4 LE] FF 00 [uncomp_sz BE24]` + zlib.
            // Deferred to here so session 0x09 state push has completed
            // arriving first.
            SendTileServerState();

            // Probe the Display sub-device inside the wheel.
            // Pithouse sends this at t=9.97 (after telemetry starts at t=9.88).
            // The response tells us if the wheel has a built-in display.
            // Non-blocking: responses are caught by OnMessageDuringPreamble.
            SendDisplayProbe();

            // Final check before creating the timer — if Stop() was called during
            // tier definition or display probe, don't create an orphaned timer.
            if (!_enabled) return;

            double intervalMs = _baseTickMs;
            _sendTimer = new Timer(intervalMs) { AutoReset = true };
            _sendTimer.Elapsed += OnTimerElapsed;
            _sendTimer.Start();
        }

        public void Stop()
        {
            // Stop trace: log a synthetic stack via MozaLog so we can find
            // every Stop call, including silent ones from inside StartInner's
            // pre-init cleanup. Without this we can't tell why subscribers
            // alternate between 1 and 2 mid-session.
            try
            {
                var st = new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: false);
                var frames = st.GetFrames();
                var sb = new System.Text.StringBuilder();
                int max = Math.Min(6, frames?.Length ?? 0);
                for (int i = 0; i < max; i++)
                {
                    var f = frames![i];
                    sb.Append(f.GetMethod()?.DeclaringType?.Name).Append('.').Append(f.GetMethod()?.Name);
                    if (i < max - 1) sb.Append(" ← ");
                }
                MozaLog.Info($"[Moza] DIAG: TelemetrySender.Stop() called. caller chain: {sb}");
            }
            catch { }

            _enabled = false;
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

            // Wake any blocked SendRpcCall waiters so they unblock with a null
            // reply rather than sit on Wait() until their per-call timeout fires
            // (those callers may be on the SimHub UI thread).
            DrainRpcWaiters();

            try { _ackReceived.Reset(); } catch (ObjectDisposedException) { }
            try { _mgmtResponseEvent.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadSessionOpened.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadSubMsg1Response.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadSubMsg2Response.Reset(); } catch (ObjectDisposedException) { }
            try { _uploadEndReceived.Reset(); } catch (ObjectDisposedException) { }
            _sessions.Reset();
            _dispatcher.Reset();
            _ftCandidateSessions.Clear();
            _uploadSession = 0x04;
            _uploadInboundSeq = 0;
            _uploadOutboundSeq = 0;
            _uploadInboundMsgCount = 0;
            _session09InboundSeq = 0;
            _session09OutboundSeq = 0;
            _session09ReplySent = false;
            _session02OutboundSeq = 0;
            _session01OutboundSeq = 0;
            _tierDefPreambleSent = false;
            _autoResolutionDone = false;
            _retransmitter.Clear();
            _tierDefBlindFrames = null;
            _lastSubscriptionDiag = null;
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = 0;
            lock (_sessionCounts) _sessionCounts.Clear();
            // Drop reassembly buffers — otherwise residual chunks from before
            // Stop persist into the next Start and (combined with the cap in
            // SessionDataReassembler) get logged as overflow.
            try { _configJson.Reset(); } catch { }
            try { _tileServerParser.Clear(); } catch { }
            try { _session0aInbox.Clear(); } catch { }
            // Reset so StartTelemetryIfReady() won't skip us on re-enable
            _framesSent = 0;
        }

        /// <summary>
        /// Wake every outstanding RPC waiter with a null reply. Called from Stop()
        /// and Dispose() so callers blocked in SendRpcCall.Wait() return promptly
        /// instead of hitting their own timeout.
        /// </summary>
        private void DrainRpcWaiters()
        {
            ManualResetEventSlim[] waiters;
            lock (_rpcLock)
            {
                if (_rpcWaiters.Count == 0) return;
                waiters = new ManualResetEventSlim[_rpcWaiters.Count];
                _rpcWaiters.Values.CopyTo(waiters, 0);
                // Don't clear the dictionary here — the waiting SendRpcCall will
                // remove its own entry under the lock and dispose its waiter.
            }
            foreach (var w in waiters)
            {
                try { w.Set(); } catch (ObjectDisposedException) { }
            }
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
            const byte session = 0x02;
            int seq = Math.Max(2, _session02OutboundSeq);
            var frames = TierDefinitionBuilder.ChunkMessage(body, session, ref seq);
            foreach (var frame in frames)
                SendAndTrackChunk(frame);
            _session02OutboundSeq = seq;
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

        /// <summary>Convenience: push wheel-integrated dashboard display brightness (0–100).</summary>
        public void SendDashDisplayBrightness(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
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

            byte[] body = global::MozaPlugin.Protocol.SessionPropertyPushBuilder
                .BuildDashboardSwitchBody(slotIndex);
            SendSessionPropertyBody(body);
            MozaLog.Debug(
                $"[Moza] Sent dashboard-switch FF-record: slot={slotIndex} " +
                $"on session 0x02 seq={_session02OutboundSeq - 1}");
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
            if (!_enabled || !_connection.IsConnected) return;

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

        /// <summary>
        /// Mute value frame emission. Called before the Profile is changed
        /// so the wheel never receives frames with a mismatched data layout
        /// (new profile channels packed into old tier-def flag slots).
        /// </summary>
        internal void MuteForDashSwitch()
        {
            _dashSwitchMuted = true;
        }

        internal void RenegotiateForDashboardSwitch()
        {
            if (!_enabled || !_connection.IsConnected)
            {
                _dashSwitchMuted = false;
                return;
            }
            try
            {
                ParseWheelChannelCatalog();
                MozaLog.Debug(
                    $"[Moza] Dashboard switch renegotiation: " +
                    $"catalog={_wheelChannelCatalog?.Count ?? -1} " +
                    $"flagBase=0x{_nextFlagBase:X2}");
                ApplySubscription(force: true);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Dashboard switch renegotiation failed: {ex}");
            }
            finally
            {
                _dashSwitchMuted = false;
            }
        }

        internal void RenegotiateForDashboardSwitch(Action? applyProfile, bool waitForCatalog)
        {
            if (!_enabled || !_connection.IsConnected)
            {
                MozaLog.Debug($"[Moza] DashSwitch renegotiation skipped: enabled={_enabled} connected={_connection.IsConnected}");
                return;
            }

            int t0 = Environment.TickCount;

            try
            {
                var oldProfile = _profile?.Name ?? "null";
                int oldTierCount = _tiers?.Length ?? 0;
                byte oldFlagBase = _activeSubscription?.FlagBase ?? 0;

                MozaLog.Debug(
                    $"[Moza] DashSwitch: pre-swap state: profile=\"{oldProfile}\" " +
                    $"tiers={oldTierCount} flagBase=0x{oldFlagBase:X2} " +
                    $"catalog={_wheelChannelCatalog?.Count ?? -1} " +
                    $"nextFlagBase=0x{_nextFlagBase:X2}");

                int muteStart = Environment.TickCount;
                _dashSwitchMuted = true;

                // Apply the new profile FIRST so we know which channel URLs the
                // wheel must advertise before we can build a valid tier-def.
                applyProfile?.Invoke();

                var newProfile = _profile?.Name ?? "null";
                int newTierCount = _tiers?.Length ?? 0;
                MozaLog.Debug($"[Moza] DashSwitch: profile applied: \"{newProfile}\" tiers={newTierCount}");

                if (waitForCatalog)
                {
                    WaitForCatalogCoverage(t0);
                }

                ApplySubscription(force: true);

                int muteMs = Environment.TickCount - muteStart;
                MozaLog.Debug(
                    $"[Moza] DashSwitch: subscription applied, unmuting after {muteMs}ms mute. " +
                    $"newFlagBase=0x{(_activeSubscription?.FlagBase ?? 0):X2} " +
                    $"totalElapsed={Environment.TickCount - t0}ms");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] DashSwitch renegotiation failed after {Environment.TickCount - t0}ms: {ex}");
            }
            finally
            {
                _dashSwitchMuted = false;
            }
        }

        /// <summary>
        /// Wait until the wheel-advertised channel catalog covers every URL the
        /// new <see cref="_profile"/> needs, or until the timeout expires.
        /// Without this, we race the wheel — it pushes the new dashboard's
        /// catalog ~400 ms after a DASH_SWITCH FF record, but the post-switch
        /// renegotiation runs immediately and would otherwise build the
        /// tier-def against the prior dashboard's catalog. Channel URLs not
        /// found there encode as <c>chIndex=0</c> in
        /// <see cref="TierDefinitionBuilder.BuildTierDefinitionV2"/>, which the
        /// wheel cannot bind to display elements.
        ///
        /// Clears <see cref="_incomingSessionBuffer"/> on entry so we only see
        /// activity that arrives AFTER the profile swap. Polls every 20 ms,
        /// re-parses on every buffer growth, and exits as soon as every
        /// required URL is present in <see cref="_wheelChannelCatalog"/>.
        /// </summary>
        private void WaitForCatalogCoverage(int renegotiateStartTickMs)
        {
            int oldBufLen;
            lock (_incomingSessionBuffer)
            {
                oldBufLen = _incomingSessionBuffer.Count;
                _incomingSessionBuffer.Clear();
            }
            // Do NOT clear _wheelChannelCatalog: post-switch parse stats show
            // the wheel uses BACKREFS heavily (backref=16 in the 17:46 trace)
            // to keep prior URLs alive at high indices while only re-announcing
            // the changed low-index slots. ParseWheelChannelCatalog needs the
            // existing entries to resolve those backrefs.
            //
            // Stale-slot duplicates are handled in
            // BuildTierDefinitionMessageType02 / BuildTierDefinitionV2 via
            // first-occurrence-wins (lowest matching idx wins) so a URL that
            // appears at both a fresh low position and a stale high position
            // resolves to the fresh one.
            _lastCatalogParseLen = 0;
            _channelBufferLastActivityMs = 0;

            // Required URLs are the de-duplicated set of channel URLs across
            // the new profile's tiers. Profile.Tiers is already broadcast-
            // expanded by the Profile setter (each sub-tier replicated 3-N
            // times) so the same URLs repeat — dedupe to keep the coverage
            // check meaningful.
            var requiredUrls = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var profileSnapshot = _profile;
            if (profileSnapshot != null)
            {
                foreach (var tier in profileSnapshot.Tiers)
                {
                    foreach (var ch in tier.Channels)
                    {
                        if (!string.IsNullOrEmpty(ch.Url))
                            requiredUrls.Add(ch.Url);
                    }
                }
            }

            int waitStart = Environment.TickCount;
            const int catalogWaitTimeoutMs = 5000;
            int deadline = waitStart + catalogWaitTimeoutMs;

            MozaLog.Debug(
                $"[Moza] DashSwitch: cleared catalog buffer (was {oldBufLen}B), " +
                $"waiting for {requiredUrls.Count} required URL(s) from new profile " +
                $"(timeout {catalogWaitTimeoutMs}ms)");

            int lastParseLen = 0;
            int finalCovered = 0;
            int finalCatalogCount = 0;
            bool covered = false;

            while (Environment.TickCount < deadline)
            {
                if (!_enabled || !_connection.IsConnected)
                {
                    MozaLog.Debug("[Moza] DashSwitch: catalog wait aborted (disconnected/disabled)");
                    return;
                }

                int curBufLen;
                lock (_incomingSessionBuffer) curBufLen = _incomingSessionBuffer.Count;

                if (curBufLen > lastParseLen)
                {
                    ParseWheelChannelCatalog();
                    lastParseLen = curBufLen;
                }

                var catalog = _wheelChannelCatalog;
                finalCatalogCount = catalog?.Count ?? 0;
                if (catalog != null && requiredUrls.Count > 0)
                {
                    int countCovered = 0;
                    foreach (var url in requiredUrls)
                    {
                        for (int i = 0; i < catalog.Count; i++)
                        {
                            if (string.Equals(catalog[i], url, StringComparison.OrdinalIgnoreCase))
                            {
                                countCovered++;
                                break;
                            }
                        }
                    }
                    finalCovered = countCovered;
                    if (countCovered >= requiredUrls.Count)
                    {
                        covered = true;
                        break;
                    }
                }
                else if (requiredUrls.Count == 0)
                {
                    covered = true;
                    break;
                }

                System.Threading.Thread.Sleep(20);
            }

            // Final parse pass in case the buffer grew during the last sleep.
            int tailBufLen;
            lock (_incomingSessionBuffer) tailBufLen = _incomingSessionBuffer.Count;
            if (tailBufLen > lastParseLen)
            {
                ParseWheelChannelCatalog();
                var catalog = _wheelChannelCatalog;
                finalCatalogCount = catalog?.Count ?? 0;
                if (catalog != null && requiredUrls.Count > 0)
                {
                    int countCovered = 0;
                    foreach (var url in requiredUrls)
                    {
                        for (int i = 0; i < catalog.Count; i++)
                        {
                            if (string.Equals(catalog[i], url, StringComparison.OrdinalIgnoreCase))
                            {
                                countCovered++;
                                break;
                            }
                        }
                    }
                    finalCovered = countCovered;
                    if (countCovered >= requiredUrls.Count) covered = true;
                }
            }

            int waitMs = Environment.TickCount - waitStart;
            if (covered)
            {
                MozaLog.Debug(
                    $"[Moza] DashSwitch: catalog coverage met after {waitMs}ms — " +
                    $"{finalCovered}/{requiredUrls.Count} URLs present, catalog size={finalCatalogCount}");
            }
            else
            {
                // Build a list of which URLs were missing for diagnostics.
                var missing = new System.Text.StringBuilder();
                int missCount = 0;
                var catalog = _wheelChannelCatalog;
                foreach (var url in requiredUrls)
                {
                    bool found = false;
                    if (catalog != null)
                    {
                        for (int i = 0; i < catalog.Count; i++)
                        {
                            if (string.Equals(catalog[i], url, StringComparison.OrdinalIgnoreCase))
                            {
                                found = true; break;
                            }
                        }
                    }
                    if (!found)
                    {
                        if (missCount > 0) missing.Append(", ");
                        missing.Append(url);
                        missCount++;
                        if (missCount >= 5) { missing.Append(", ..."); break; }
                    }
                }
                MozaLog.Warn(
                    $"[Moza] DashSwitch: catalog wait TIMED OUT after {waitMs}ms — only " +
                    $"{finalCovered}/{requiredUrls.Count} URLs covered (catalog size={finalCatalogCount}). " +
                    $"Sending tier-def with stale catalog; missing: {missing}");
            }
        }

        public void SwitchToProfile(uint slotIndex, MultiStreamProfile? newProfile)
        {
            SendDashboardSwitch(slotIndex);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                RenegotiateForDashboardSwitch(
                    applyProfile: newProfile != null ? () => { Profile = newProfile; } : null,
                    waitForCatalog: true);
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
            if (!_enabled || !_connection.IsConnected) return;
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
        /// arriving. Polls <see cref="_channelBufferLastActivityMs"/> — once the
        /// last activity is older than <paramref name="quietMs"/>, we assume the
        /// wheel is done pushing its channel URLs.
        /// </summary>
        private void WaitForChannelCatalogQuiet(int quietMs, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (!_enabled || !_connection.IsConnected) return;
                int lastAct = _channelBufferLastActivityMs;
                int idle = lastAct == 0 ? 0 : Environment.TickCount - lastAct;
                int bufCount;
                lock (_incomingSessionBuffer) bufCount = _incomingSessionBuffer.Count;
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
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            _subscriptionResponseDeadlineTicks = 0;

            SendTierDefinition();
            SendChannelConfig();

            int chCount = 0;
            foreach (var t in _profile.Tiers) chCount += t.Channels.Count;
            MozaLog.Debug(
                $"[Moza] Subscription applied: \"{_profile.Name}\" " +
                $"{chCount}ch/{_profile.Tiers.Count}t " +
                $"catalog={_wheelChannelCatalog?.Count ?? -1}");
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
        ///   1. <c>_wheelChannelCatalog</c> non-empty → Era2026 (catalog push
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
            int catalogCount = _wheelChannelCatalog?.Count ?? 0;
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
            var catalog = _wheelChannelCatalog;
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
                seq = Math.Max(2, _session02OutboundSeq + 1);
            }
            else
            {
                tierDefSession = _mgmtPort != 0 ? _mgmtPort : (byte)0x01;
                seq = Math.Max(2, _session01OutboundSeq + 1);
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
                    if (cspIdx && (_wheelChannelCatalog == null || _wheelChannelCatalog.Count == 0))
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
                            wheelCatalog: _wheelChannelCatalog,
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
        /// Pick the file-transfer session number to upload on. Priority:
        /// <list type="number">
        ///   <item><see cref="UploadSessionOverride"/> if non-zero.</item>
        ///   <item>0x04 if the wheel device-initiated it (legacy behaviour, all
        ///   firmwares observed).</item>
        ///   <item>The first session in 0x04..0x0a the wheel device-initiated
        ///   (covers new firmware that may shift the file-transfer session).</item>
        ///   <item>0x04 fallback if no candidate seen yet — the upload waiter
        ///   will then either time out or proceed via host-initiated open.</item>
        /// </list>
        /// </summary>
        private byte ChooseUploadSession()
        {
            if (UploadSessionOverride != 0) return UploadSessionOverride;
            lock (_ftCandidateSessions)
            {
                if (_ftCandidateSessions.Contains((byte)0x04)) return 0x04;
                foreach (byte b in new byte[] { 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a })
                    if (_ftCandidateSessions.Contains(b)) return b;
            }
            return 0x04;
        }

        /// <summary>
        /// Upload the .mzdash dashboard file via the file-transfer protocol.
        /// Wheel opens the upload session from its own side shortly after the
        /// host brings up mgmt + telemetry; we wait up to 2 s for that open,
        /// then send:
        ///
        ///   1. Sub-msg 1 (path registration) — wait for device echo (~6 chunks)
        ///   2. Sub-msg 2 (file content push) — wait for device ack (~6 chunks)
        ///   3. Type=0x00 end marker — wait for device end reply
        ///
        /// Sizing follows PitHouse's observed 64-byte max chunk size; CRC32 per
        /// chunk via <see cref="TierDefinitionBuilder.ChunkMessage"/>.
        ///
        /// The session number is dynamic. 2025-11 firmware uses 0x04; 2026-04
        /// firmware has been observed using 0x05 / 0x07 / 0x09 depending on
        /// what the host requests via 7c:23 46. Plugin selects via
        /// <see cref="ChooseUploadSession"/>.
        /// </summary>
        /// <summary>
        /// Background upload entry point. Runs on a worker thread so a slow-to-
        /// open file-transfer session (KS Pro on RS21-W18-MC SW: ~11 s) doesn't
        /// stall tier def + telemetry start. Waits up to 60 s for the wheel to
        /// device-init any session in 0x04..0x0a, then runs the legacy upload
        /// path. If the wait expires, logs and bails — the wheel will render a
        /// previously-cached dashboard, or nothing if it has none.
        ///
        /// Stop() flips _enabled to false and the next checkpoint exits cleanly.
        /// </summary>
        private void RunBackgroundDashboardUpload()
        {
            try
            {
                if (!_enabled || !_connection.IsConnected) return;

                // 60 s ceiling: covers the slowest firmware observed (~11 s) with
                // headroom. If the wheel hasn't opened an FT session by then it
                // either doesn't support uploads on this firmware or is wedged —
                // either way, retrying won't help and host-opening 0x04 races the
                // wheel's eventual late burst (closes session 0x02, kills telemetry).
                const int FtBurstWaitMs = 60000;
                if (!_uploadSessionOpened.Wait(FtBurstWaitMs))
                {
                    MozaLog.Warn(
                        $"[Moza] No file-transfer session device-opened within " +
                        $"{FtBurstWaitMs}ms — skipping dashboard upload. " +
                        "Wheel may render previously-cached dashboard.");
                    return;
                }

                if (!_enabled || !_connection.IsConnected) return;
                SendDashboardUpload();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Background dashboard upload failed: {ex.Message}");
            }
        }

        private void SendDashboardUpload()
        {
            var content = MzdashContent;
            if (content == null || content.Length == 0) return;
            if (!_connection.IsConnected) return;

            // Pick the upload session from the wheel's device-init burst.
            // Picker prefers 0x04 if device-initiated; otherwise first of
            // 0x05..0x0a. Caller (RunBackgroundDashboardUpload) already
            // waited for at least one to open.
            byte uploadSess = ChooseUploadSession();
            _uploadSession = uploadSess;

            // Skip-if-unchanged: if the wheel already reported this dashboard
            // as loaded (via session 0x09 state) and the MD5 matches, don't
            // re-upload. Saves ~1 s of handshake per reconnect.
            if (CanSkipUpload(content))
            {
                MozaLog.Debug(
                    $"[Moza] Dashboard \"{MzdashName}\" already loaded on wheel (hash match) — skipping upload");
                return;
            }

            string dashboardName = !string.IsNullOrEmpty(MzdashName) ? MzdashName : "dashboard";
            uint token = DashboardUploader.PickToken();
            long tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Probe-based wire-format selection. Older wheels (VGS, GS V2P,
            // CS V2.1, etc.) accept Legacy2025_11; newer wheels (W17 CS Pro /
            // W18 KS Pro / W13 FSR V2 on 2026-04+ firmware) silently drop
            // Legacy and only ack New2026_04. Identity probes (`wheel-sw-version`
            // = `RS21-W18-MC SW`) carry no build/version field, so we can't
            // pick from a string match. Try the user-configured format first
            // (default New2026_04 — most wheels in the wild now), and on
            // sub-msg 1 ack timeout, fall back to the other format.
            bool fellBack = false;
            DashboardUploader.UploadPayload upload =
                DashboardUploader.BuildUpload(content, dashboardName, token, tsMs, _policy.UploadWireFormat);

            MozaLog.Debug(
                $"[Moza] Uploading dashboard \"{dashboardName}\" via session 0x{uploadSess:X2} " +
                $"(wire={_policy.UploadWireFormat}): " +
                $"raw={upload.UncompressedSize}B md5={upload.Md5Hex} token=0x{token:X8}");

            _uploadSubMsg1Response.Reset();
            _uploadSubMsg2Response.Reset();
            _uploadEndReceived.Reset();
            _uploadInboundMsgCount = 0;

            // Sub-msg 1: path registration.
            int seq1 = _uploadOutboundSeq + 1;
            var subMsg1Frames = TierDefinitionBuilder.ChunkMessage(
                upload.SubMsg1PathRegistration, uploadSess, ref seq1);
            foreach (var frame in subMsg1Frames)
            {
                if (!_enabled || !_connection.IsConnected) return;
                _connection.Send(frame);
            }
            _uploadOutboundSeq = seq1;

            // Wait for device's path echo (capture shows ~6 chunks, arrives within ~200ms).
            if (!_uploadSubMsg1Response.Wait(2000))
            {
                // Probe fallback: flip wire format and retry sub-msg 1 once.
                // Only runs when policy.AutoFallbackUploadWireFormat is true
                // (era=Auto). When the user picked a specific era we honour it
                // strictly so the logged wire format reflects their choice and
                // a wedged upload is diagnosable instead of always converging
                // on the fallback.
                if (!_policy.AutoFallbackUploadWireFormat)
                {
                    MozaLog.Warn(
                        $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout with " +
                        $"wire={_policy.UploadWireFormat} — fallback disabled (era pinned by user)");
                }
                else
                {
                    var fallback = _policy.UploadWireFormat == FileTransferWireFormat.New2026_04
                        ? FileTransferWireFormat.Legacy2025_11
                        : FileTransferWireFormat.New2026_04;
                    MozaLog.Warn(
                        $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout with " +
                        $"wire={_policy.UploadWireFormat} — retrying with wire={fallback}");

                    _policy.UploadWireFormat = fallback;
                    fellBack = true;
                    upload = DashboardUploader.BuildUpload(content, dashboardName, token, tsMs, _policy.UploadWireFormat);

                    _uploadSubMsg1Response.Reset();
                    _uploadSubMsg2Response.Reset();
                    _uploadInboundMsgCount = 0;

                    seq1 = _uploadOutboundSeq + 1;
                    subMsg1Frames = TierDefinitionBuilder.ChunkMessage(
                        upload.SubMsg1PathRegistration, uploadSess, ref seq1);
                    foreach (var frame in subMsg1Frames)
                    {
                        if (!_enabled || !_connection.IsConnected) return;
                        _connection.Send(frame);
                    }
                    _uploadOutboundSeq = seq1;

                    if (!_uploadSubMsg1Response.Wait(2000))
                        MozaLog.Warn(
                            $"[Moza] Session 0x{uploadSess:X2} sub-msg 1 ack timeout on fallback " +
                            $"wire={_policy.UploadWireFormat} — wheel may not be in upload-ready state");
                    else
                        MozaLog.Debug(
                            $"[Moza] Wire format auto-detected: wheel accepts {_policy.UploadWireFormat} " +
                            "(cached for this session)");
                }
            }
            // Suppress unused warning when fallback didn't fire.
            _ = fellBack;

            // Sub-msg 2: file content push. May be split across multiple sub-msgs
            // for new-firmware uploads when the body exceeds 0xFFFF bytes (TODO:
            // true multi-sub-msg chunking; today this is single-element for both
            // formats — see FileTransferBuilder.BuildFileContentChunked).
            _uploadInboundMsgCount = 0; // reset so next threshold triggers sub-msg 2 event
            int seq2 = _uploadOutboundSeq + 1;
            for (int chunkIdx = 0; chunkIdx < upload.SubMsg2Chunks.Count; chunkIdx++)
            {
                var subMsg2 = upload.SubMsg2Chunks[chunkIdx];
                var subMsg2Frames = TierDefinitionBuilder.ChunkMessage(subMsg2, uploadSess, ref seq2);
                foreach (var frame in subMsg2Frames)
                {
                    if (!_enabled || !_connection.IsConnected) return;
                    _connection.Send(frame);
                }
            }
            _uploadOutboundSeq = seq2;

            if (!_uploadSubMsg2Response.Wait(3000))
                MozaLog.Warn($"[Moza] Session 0x{uploadSess:X2} sub-msg 2 response timeout");

            // End marker on the upload session.
            SendSessionEnd(uploadSess, (ushort)_uploadOutboundSeq);

            if (_uploadEndReceived.Wait(1000))
                MozaLog.Debug($"[Moza] Dashboard upload complete (session 0x{uploadSess:X2} closed by device)");
            else
                MozaLog.Debug("[Moza] Dashboard upload finished; device did not echo end marker within 1s");

            // Wheel's 2025-11 firmware fires a post-upload state refresh on
            // the upload session (updated directory listing) and session 0x09
            // (updated configJson state blob including the newly-uploaded
            // dashboard). Continue pumping so OnMessageDuringPreamble can ack
            // + consume those chunks before the preamble phase ends and the
            // handler detaches.
            int preRefreshCount = _uploadInboundMsgCount;
            Thread.Sleep(500);
            int refreshChunks = _uploadInboundMsgCount - preRefreshCount;
            if (refreshChunks > 0)
                MozaLog.Debug(
                    $"[Moza] Session 0x{uploadSess:X2} post-upload state refresh: {refreshChunks} chunks");
        }

        /// <summary>
        /// Compare the active mzdash MD5 against the wheel's reported hash from
        /// its last session 0x09 state blob. Wheel stores hash as ASCII-hex of
        /// ASCII-hex of MD5 (observed: `33 63 31 64 ...` = ASCII of
        /// "3c1d..."). Returns true when the wheel already has this exact
        /// dashboard loaded in enableManager.
        /// </summary>
        private bool CanSkipUpload(byte[] content)
        {
            var state = _configJson.LastState;
            if (state == null || state.EnabledDashboards.Count == 0) return false;
            byte[] md5 = FileTransferBuilder.ComputeMd5(content);
            string md5Hex = FileTransferBuilder.Md5Hex(md5);
            string wireHash = AsciiHexOfAsciiHex(md5Hex);
            foreach (var entry in state.EnabledDashboards)
            {
                if (string.Equals(entry.Hash, wireHash, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string AsciiHexOfAsciiHex(string ascii)
        {
            var sb = new System.Text.StringBuilder(ascii.Length * 2);
            foreach (var c in ascii) sb.Append(((byte)c).ToString("x2"));
            return sb.ToString();
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
                if (!_enabled || !_connection.IsConnected) return;
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

        private int _session0aOutboundSeq;

        /// <summary>
        /// Send a host→wheel JSON RPC call on session 0x0a and optionally wait
        /// for the wheel's reply. Wire format matches configJson: 9-byte
        /// envelope ([flag=0x00][comp_size:u32 LE][uncomp_size:u32 LE]) wrapping
        /// a zlib stream of `{"<method>()": arg, "id": N}`. Reply has shape
        /// `{"id": N, "result": ...}` and is decoded by HandleRpcReply.
        /// Returns the decoded reply bytes on success, null on timeout.
        /// </summary>
        public byte[]? SendRpcCall(string method, object arg, int timeoutMs = 2000)
        {
            if (!_connection.IsConnected) return null;
            if (Volatile.Read(ref _disposed) != 0) return null;
            int id;
            var waiter = new ManualResetEventSlim(false);
            lock (_rpcLock)
            {
                id = _rpcNextId++;
                _rpcWaiters[id] = waiter;
            }
            byte[] envelope = BuildRpcCallEnvelope(method, arg, id);
            int seq = _session0aOutboundSeq + 1;
            var frames = TierDefinitionBuilder.ChunkMessage(envelope, 0x0a, ref seq);
            foreach (var frame in frames)
            {
                if (!_enabled || !_connection.IsConnected) { CleanupRpcWaiter(id); return null; }
                _connection.Send(frame);
            }
            _session0aOutboundSeq = seq;
            bool acked = waiter.Wait(timeoutMs);
            byte[]? reply = null;
            lock (_rpcLock)
            {
                _rpcReplies.TryGetValue(id, out reply);
                _rpcReplies.Remove(id);
                _rpcWaiters.Remove(id);
            }
            waiter.Dispose();
            return acked ? reply : null;
        }

        private void CleanupRpcWaiter(int id)
        {
            lock (_rpcLock)
            {
                if (_rpcWaiters.TryGetValue(id, out var w))
                {
                    _rpcWaiters.Remove(id);
                    try { w.Dispose(); } catch { }
                }
            }
        }

        private static byte[] BuildRpcCallEnvelope(string method, object arg, int id)
        {
            var root = new Newtonsoft.Json.Linq.JObject();
            root[$"{method}()"] = Newtonsoft.Json.Linq.JToken.FromObject(arg);
            root["id"] = id;
            string json = root.ToString(Newtonsoft.Json.Formatting.None);
            byte[] uncompressed = System.Text.Encoding.UTF8.GetBytes(json);
            byte[] compressed = ZlibCompress(uncompressed);
            var env = new byte[9 + compressed.Length];
            env[0] = 0x00;
            uint c = (uint)compressed.Length;
            env[1] = (byte)(c & 0xFF); env[2] = (byte)((c >> 8) & 0xFF);
            env[3] = (byte)((c >> 16) & 0xFF); env[4] = (byte)((c >> 24) & 0xFF);
            uint u = (uint)uncompressed.Length;
            env[5] = (byte)(u & 0xFF); env[6] = (byte)((u >> 8) & 0xFF);
            env[7] = (byte)((u >> 16) & 0xFF); env[8] = (byte)((u >> 24) & 0xFF);
            Array.Copy(compressed, 0, env, 9, compressed.Length);
            return env;
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var output = new System.IO.MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new System.IO.Compression.DeflateStream(
                output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            uint adler = (b << 16) | a;
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        /// <summary>
        /// Decode a session 0x0a reply and route to the waiter by `id`. Method
        /// name is NOT inspected — replies route solely on integer id, which
        /// accommodates:
        /// - standard replies `{"id": N, "result": ...}`
        /// - method-keyed replies `{"<method>()": <value>, "id": N}`
        /// - empty-method replies `{"()": "", "id": N}` (observed on reset, 2026-04-21)
        /// Caller's `SendRpcCall` assigns any integer id; wheel echoes it back.
        /// </summary>
        private void HandleRpcReply(byte[] uncompressed)
        {
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(uncompressed);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var idTok = obj["id"];
                if (idTok == null) return;
                int id = (int)idTok;
                lock (_rpcLock)
                {
                    _rpcReplies[id] = uncompressed;
                    if (_rpcWaiters.TryGetValue(id, out var waiter))
                        waiter.Set();
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] RPC reply parse failed: {ex.Message}");
            }
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
            // Top-of-handler DIAG: snapshot every C3/71/7C session-data frame
            // for sessions 0x09/0x0a so we can prove whether the handler is
            // actually reached for chunks that the WIRE log shows arriving.
            // Placed BEFORE the _enabled / data.Length / data[0] / data[1]
            // gates so we can see whether one of those gates is dropping the
            // chunk too.
            if (data != null && data.Length >= 8 && data[0] == 0xC3 && data[1] == 0x71
                && data[2] == 0x7C && data[3] == 0x00
                && (data[4] == 0x09 || data[4] == 0x0A))
            {
                int seq = data[6] | (data[7] << 8);
                MozaLog.Info(
                    $"[Moza] DIAG: OnMessageDuringPreamble TOP for sess=0x{data[4]:X2} " +
                    $"type=0x{data[5]:X2} seq={seq} dataLen={data.Length} " +
                    $"_enabled={_enabled} _preambleComplete={_preambleComplete}");
            }

            if (!_enabled)
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
                    if (session >= 0x04 && session <= 0x0b)
                    {
                        lock (_ftCandidateSessions) _ftCandidateSessions.Add(session);
                        _uploadSessionOpened.Set();
                    }
                    // Route device-init through dispatcher. If a consumer
                    // (e.g. DashboardDownloader) has claimed this session, it
                    // gets the notification exclusively. Otherwise fall through
                    // to legacy upload-session tracking.
                    _dispatcher.DispatchOpen(session, openSeq);
                    // Legacy: update _uploadSession for sessions not yet
                    // claimed by a dispatcher consumer.
                    if (session >= 0x04 && session <= 0x0b
                        && session != 0x09 && session != 0x0a
                        && _dispatcher.GetOwner(session) == null)
                    {
                        _uploadSession = session;
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

                    // Diagnostic: trace every type=0x01 chunk arrival for the
                    // configJson sessions. Pairs with the WIRE log in
                    // MozaSerialConnection so we can see whether a chunk
                    // logged at the read layer reaches the dispatch layer
                    // (and which branch swallows it).
                    if (session == 0x09 || session == 0x0a)
                    {
                        MozaLog.Info(
                            $"[Moza] DIAG: OnMessageDuringPreamble entered for sess=0x{session:X2} " +
                            $"seq={seq} payload={chunkPayload.Length}B " +
                            $"_enabled={_enabled} _preambleComplete={_preambleComplete}");
                    }

                    // Dispatcher-owned sessions: route exclusively through
                    // dispatcher and ack. Skip all legacy if-chains below.
                    var owner = _dispatcher.GetOwner(session);
                    if (owner != null)
                    {
                        // Diagnostic: surface unexpected ownership of
                        // configJson sessions. v1 pipeline does NOT claim
                        // 0x09/0x0a — anything taking them out of the legacy
                        // path means dashboard-state decode is dead.
                        if (session == 0x09 || session == 0x0a)
                        {
                            MozaLog.Info(
                                $"[Moza] DIAG: session 0x{session:X2} chunk seq={seq} routed " +
                                $"via dispatcher to {owner.GetType().Name} " +
                                $"(legacy configJson handler bypassed)");
                        }
                        SendSessionAck(session, (ushort)seq);
                        _dispatcher.DispatchData(session, seq, chunkPayload);
                        return;
                    }

                    // Ack on the telemetry session
                    if (session == FlagByte)
                    {
                        if (seq > _sessionAckSeq)
                            _sessionAckSeq = seq;
                        SendSessionAck(FlagByte, (ushort)_sessionAckSeq);

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

                    // Ack on the management session (upload handshake)
                    if (session == _mgmtPort && _mgmtPort != 0)
                    {
                        if (seq > _mgmtAckSeq)
                            _mgmtAckSeq = seq;
                        SendSessionAck(_mgmtPort, (ushort)_mgmtAckSeq);
                        _mgmtResponseEvent.Set();
                    }

                    // File-transfer session: ack + count responses. Capture shows
                    // device sends sub-msg 1 echo (6 chunks) then sub-msg 2 ack
                    // (6 chunks) — simplest heuristic is to wait for a quiet period
                    // after some chunks, but sub-msg events fire once enough chunks
                    // arrive to assume the device replied. Session number is
                    // dynamic (see ChooseUploadSession).
                    if (session == _uploadSession)
                    {
                        SendSessionAck(session, (ushort)seq);
                        _uploadInboundSeq = seq;
                        _uploadInboundMsgCount++;
                        // After ~5 chunks on the upload session from the device,
                        // assume a sub-msg reply has fully arrived (capture shows
                        // 6 chunks per response). SendDashboardUpload resets the
                        // counter to 0 between sub-msg 1 and sub-msg 2, so both
                        // thresholds are 5.
                        if (_uploadInboundMsgCount >= 5 && !_uploadSubMsg1Response.IsSet)
                            _uploadSubMsg1Response.Set();
                        else if (_uploadInboundMsgCount >= 5 && !_uploadSubMsg2Response.IsSet)
                            _uploadSubMsg2Response.Set();

                        // 2025-11 firmware also pushes a zlib-compressed directory
                        // listing on the upload session (initial + post-upload
                        // refresh). Reassemble + decompress so the plugin can
                        // confirm the upload landed in the wheel's FS. Same
                        // 9-byte envelope as session 0x09 configJson state.
                        _uploadInbox.AddChunk(chunkPayload);
                        byte[]? dirBlob = _uploadInbox.TryDecompress();
                        if (dirBlob != null)
                        {
                            _uploadInbox.Clear();
                            _uploadDirListingRefreshed = true;
                            try
                            {
                                string json = System.Text.Encoding.UTF8.GetString(dirBlob);
                                MozaLog.Debug(
                                    $"[Moza] Session 0x{session:X2} dir listing: {dirBlob.Length} bytes, " +
                                    $"children≈{CountOccurrences(json, "\"name\"")}");
                            }
                            catch (Exception ex)
                            {
                                MozaLog.Debug(
                                    $"[Moza] Session 0x{session:X2} dir listing decode: {ex.Message}");
                            }
                        }
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
                        // Diagnostic: confirm legacy configJson branch entered.
                        // Pairs with the upstream "DIAG: OnMessageDuringPreamble entered"
                        // log so we know chunks are actually reaching here vs being
                        // dropped between subscribers. INFO so it's not filtered out.
                        MozaLog.Info(
                            $"[Moza] DIAG: legacy configJson branch entered for sess=0x{session:X2} " +
                            $"seq={seq} chunkLen={chunkPayload.Length}");
                        SendSessionAck(session, (ushort)seq);
                        if (session == 0x09) _session09InboundSeq = seq;
                        try
                        {
                            MozaLog.Debug(
                                $"[Moza] session 0x{session:X2} inbound chunk: seq={seq} payload={chunkPayload.Length}B " +
                                $"first8={BitConverter.ToString(chunkPayload, 0, Math.Min(8, chunkPayload.Length))}");
                        }
                        catch { }
                        var state = _configJson.OnChunk(chunkPayload);
                        if (state != null)
                        {
                            MaybeSendConfigJsonReply(state, session);
                            MaybeTriggerDashboardDownload(state);
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
                        _session0aInbox.AddChunk(chunkPayload);
                        byte[]? replyBlob = _session0aInbox.TryDecompress();
                        if (replyBlob != null)
                        {
                            _session0aInbox.Clear();
                            HandleRpcReply(replyBlob);
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
                        // Strip CRC from tail (last 4 bytes) before handing to parser
                        if (chunkPayload.Length >= 4)
                        {
                            byte[] net = new byte[chunkPayload.Length - 4];
                            Array.Copy(chunkPayload, 0, net, 0, net.Length);
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

                    // Buffer the chunk payload (strip CRC) for channel catalog parsing.
                    // Wheel may push the catalog on either the telemetry session
                    // (FlagByte) or the mgmt session (0x01) depending on firmware
                    // — V0 URL-subscription firmware (CSP post-2026-04) sends URL
                    // entries on 0x01 while V2-compact firmware uses 0x02. Collect
                    // from both during preamble so ParseWheelChannelCatalog sees
                    // entries regardless of which session the wheel uses.
                    // Always accumulate catalog data — no gate. The tick
                    // handler detects changes after 2s of quiet and re-issues
                    // the tier-def automatically.
                    bool isCatalogSession = session == FlagByte || session == 0x01;
                    if (isCatalogSession && data.Length > 12)
                    {
                        byte[] raw = new byte[data.Length - 8];
                        Array.Copy(data, 8, raw, 0, raw.Length);
                        if (raw.Length >= 5)
                        {
                            int netLen = raw.Length - 4;
                            lock (_incomingSessionBuffer)
                            {
                                for (int k = 0; k < netLen; k++)
                                    _incomingSessionBuffer.Add(raw[k]);
                            }
                            _channelBufferLastActivityMs = Environment.TickCount;
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
                    if (session == _uploadSession) _uploadEndReceived.Set();
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

        /// <summary>
        /// Parse the wheel's channel catalog from the buffered incoming 7c:00 session data.
        /// The wheel sends tag 0x04 entries with channel URLs during the preamble.
        /// </summary>
        private static bool CatalogEquals(
            System.Collections.Generic.IReadOnlyList<string>? a,
            System.Collections.Generic.IReadOnlyList<string>? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], System.StringComparison.Ordinal))
                    return false;
            return true;
        }

        /// <summary>
        /// Scan <see cref="_incomingSessionBuffer"/> for a complete catalog:
        /// ≥3 plausible 0x04 URL entries followed by a 0x06 end-marker.
        /// Caller must hold <c>lock (_incomingSessionBuffer)</c>.
        /// </summary>
        private bool HasCompleteCatalog()
        {
            int cnt = _incomingSessionBuffer.Count;
            if (cnt <= 30) return false;

            int urlCount = 0;
            for (int ci = 0; ci + 5 <= cnt; ci++)
            {
                byte b = _incomingSessionBuffer[ci];
                if (b == 0x04)
                {
                    uint sz = (uint)(_incomingSessionBuffer[ci + 1]
                        | (_incomingSessionBuffer[ci + 2] << 8)
                        | (_incomingSessionBuffer[ci + 3] << 16)
                        | (_incomingSessionBuffer[ci + 4] << 24));
                    if (sz >= 1 && sz < 200 && ci + 5 + (int)sz <= cnt)
                    {
                        urlCount++;
                        ci += 4 + (int)sz;
                    }
                }
                else if (b == 0x06 && urlCount >= 3)
                {
                    return true;
                }
            }
            return false;
        }

        private void ParseWheelChannelCatalog()
        {
            byte[] buffer;
            lock (_incomingSessionBuffer)
            {
                if (_incomingSessionBuffer.Count == 0) return;
                buffer = _incomingSessionBuffer.ToArray();
            }

            // Pre-scan: any tag=0x04 records present? Buffer fills with end-
            // marker noise (06 04 ... val) post-renegotiate; parsing every tick
            // is wasted work. Skip + suppress logging unless URL records exist.
            bool hasUrlRecord = false;
            for (int b = 0; b + 5 < buffer.Length; b++)
            {
                if (buffer[b] == 0x04)
                {
                    uint sz = (uint)(buffer[b + 1] | (buffer[b + 2] << 8)
                                   | (buffer[b + 3] << 16) | (buffer[b + 4] << 24));
                    if (sz >= 1 && sz < 200 && b + 5 + (int)sz <= buffer.Length)
                    {
                        hasUrlRecord = true;
                        break;
                    }
                }
            }
            if (!hasUrlRecord) return;

            // Diagnostic: hex-dump first 128 bytes of catalog buffer
            int dumpLen = Math.Min(buffer.Length, 128);
            var hex = new System.Text.StringBuilder(dumpLen * 3);
            for (int d = 0; d < dumpLen; d++)
            {
                if (d > 0) hex.Append('-');
                hex.Append(buffer[d].ToString("X2"));
            }
            MozaLog.Debug($"[Moza] Catalog buffer dump ({buffer.Length} bytes): {hex}");

            // Scan-forward for `04`-tag URL records. Each record encodes its
            // canonical wheel-firmware idx in the byte at offset i+5 (1-based).
            // Wheel re-indexes URLs per dashboard — same URL gets different idx
            // before/after a dash switch (verified in moza-wire 161929: idx 4 =
            // Gear under Core, idx 4 = TyreTempFrontRight under Grids). Plugin
            // MUST honor the wheel's idx, not parse-order positional.
            //
            // Catalog stored as List<string?> indexed by idx-1; nulls fill
            // unannounced gaps. Merge writes URLs at canonical positions.
            var parsed = new System.Collections.Generic.Dictionary<int, string>();
            int i = 0;
            int diagFullUrl = 0, diagPrefixUrl = 0, diagAbbrUrl = 0;
            int diagBackRef = 0, diagBackRefFail = 0;
            int diagSizeReject = 0, diagPlausReject = 0;
            var existingCatalog = _wheelChannelCatalog;
            while (i + 6 < buffer.Length)
            {
                byte tag = buffer[i];
                if (tag != 0x04) { i++; continue; }
                uint param = (uint)(buffer[i + 1] | (buffer[i + 2] << 8) |
                             (buffer[i + 3] << 16) | (buffer[i + 4] << 24));
                if (param < 1 || param >= 200 || i + 5 + (int)param > buffer.Length)
                {
                    diagSizeReject++;
                    i++; continue;
                }
                int idx = buffer[i + 5];  // wheel-firmware-canonical idx (1-based)
                int urlLen = (int)param - 1;
                int urlStart = i + 6;

                if (urlLen == 0)
                {
                    // Backref: payload is just the idx byte. Resolve URL from
                    // the existing catalog at this idx; record the same idx
                    // in our parse map so merge preserves the binding.
                    if (existingCatalog != null && idx >= 1 && idx <= existingCatalog.Count
                        && !string.IsNullOrEmpty(existingCatalog[idx - 1]))
                    {
                        parsed[idx] = existingCatalog[idx - 1];
                        diagBackRef++;
                    }
                    else
                    {
                        diagBackRefFail++;
                    }
                    i += 5 + (int)param;
                    continue;
                }

                bool plausible = urlLen >= 3
                    && ((buffer[urlStart] == (byte)'v'
                         && buffer[urlStart + 1] == (byte)'1'
                         && buffer[urlStart + 2] == (byte)'/')
                        || buffer[urlStart] == 0x01
                        || (buffer[urlStart] == 0x5C
                            && urlLen >= 4
                            && buffer[urlStart + 1] == 0x31));
                if (!plausible) { diagPlausReject++; i++; continue; }
                string url;
                if (buffer[urlStart] == 0x01)
                {
                    url = "v1/gameData/" + System.Text.Encoding.ASCII.GetString(
                        buffer, urlStart + 1, urlLen - 1);
                    diagPrefixUrl++;
                }
                else if (buffer[urlStart] == 0x5C && buffer[urlStart + 1] == 0x31)
                {
                    string suffix = System.Text.Encoding.ASCII.GetString(
                        buffer, urlStart + 2, urlLen - 2);
                    suffix = suffix
                        .Replace("\\t", "TyreTemp")
                        .Replace("\\P", "TyrePressure")
                        .Replace("{FL}", "FrontLeft")
                        .Replace("{FR}", "FrontRight")
                        .Replace("{RL}", "RearLeft")
                        .Replace("{RR}", "RearRight");
                    url = "v1/gameData/" + suffix;
                    diagAbbrUrl++;
                }
                else
                {
                    url = System.Text.Encoding.ASCII.GetString(buffer, urlStart, urlLen);
                    diagFullUrl++;
                }
                if (idx >= 1) parsed[idx] = url;
                i += 5 + (int)param;
            }
            MozaLog.Debug(
                $"[Moza] Catalog parse stats: full={diagFullUrl} prefix={diagPrefixUrl} " +
                $"abbr={diagAbbrUrl} backref={diagBackRef} backrefFail={diagBackRefFail} " +
                $"sizeReject={diagSizeReject} plausReject={diagPlausReject} " +
                $"distinct-idx={parsed.Count}");

            if (parsed.Count > 0)
            {
                // Build/extend the idx-positional catalog. Latest wheel
                // announcement wins per idx (dashboard switches re-index URLs).
                var prior = _wheelChannelCatalog;
                int maxIdx = parsed.Keys.Max();
                int newSize = Math.Max(maxIdx, prior?.Count ?? 0);
                var merged = new System.Collections.Generic.List<string>(newSize);
                for (int k = 0; k < newSize; k++)
                {
                    string? entry = (prior != null && k < prior.Count) ? prior[k] : null;
                    if (parsed.TryGetValue(k + 1, out var u)) entry = u;
                    merged.Add(entry ?? "");
                }
                bool changed = prior == null
                    || prior.Count != merged.Count
                    || !prior.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase);
                if (changed)
                {
                    _wheelChannelCatalog = merged;
                    var diff = new System.Text.StringBuilder();
                    foreach (var kv in parsed.OrderBy(kv => kv.Key))
                    {
                        bool wasDifferent = prior == null
                            || kv.Key - 1 >= prior.Count
                            || !string.Equals(prior[kv.Key - 1], kv.Value, StringComparison.OrdinalIgnoreCase);
                        if (wasDifferent) diff.Append($" [{kv.Key}]={kv.Value}");
                    }
                    MozaLog.Debug(
                        $"[Moza] Wheel channel catalog updated (size {prior?.Count ?? 0}→{merged.Count}):{diff}");
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
            if (!_enabled || !_connection.IsConnected)
                return;

            var tiers = _tiers;
            // No-profile bootstrap: when builtins/mzdash are unavailable the
            // sender starts with `_tiers == null`. Rather than bail (which
            // strands preamble forever and prevents catalog reception), keep
            // the preamble ticking so the wheel-side catalog frames get
            // buffered + parsed. After preamble completes, MaybeSwapProfile-
            // ForCatalog synthesises a WheelCatalog profile from what the
            // wheel advertised, populating `_tiers` for subsequent ticks.
            bool noProfile = tiers == null || tiers.Length == 0;

            try
            {
                // Preamble phase: ~1 second of session ack processing + heartbeats.
                // No telemetry, enable, or channel config until preamble completes.
                if (!_preambleComplete)
                {
                    _tickCounter++;

                    int slowInterval = Math.Max(1, 1000 / _baseTickMs);
                    if (_tickCounter % slowInterval == 0)
                        SendHeartbeat();

                    if (_tickCounter >= _preambleTickTarget)
                    {
                        _preambleComplete = true;

                        ParseWheelChannelCatalog();
                        ApplySubscription(force: false);

                        _tickCounter = 0;
                        _slowCounter = 0;
                    }
                    return;
                }

                // Continuous catalog absorption. Wheel pushes URL records in
                // batches with ~1.2s gaps; parse every time the buffer grows
                // and merge non-destructively so URLs are never dropped.
                {
                    int curLen;
                    lock (_incomingSessionBuffer) curLen = _incomingSessionBuffer.Count;
                    if (curLen > _lastCatalogParseLen)
                    {
                        ParseWheelChannelCatalog();
                        _lastCatalogParseLen = curLen;
                        if (curLen > 4096)
                        {
                            lock (_incomingSessionBuffer) _incomingSessionBuffer.Clear();
                            _lastCatalogParseLen = 0;
                        }
                    }
                }

                _autoTest?.Tick(_baseTickMs);

                // Re-read _tiers: MaybeSwapProfileForCatalog may have rebuilt.
                tiers = _tiers;
                if (tiers == null || tiers.Length == 0)
                    return;

                // Post-renegotiation diagnostic: log first few ticks
                if (_postRenegDiagTicks > 0)
                {
                    bool useV0Diag = _policy.Encoding == TierDefEncoding.V0Url;
                    MozaLog.Debug(
                        $"[Moza] TICK DIAG: tiers={tiers.Length} " +
                        $"testMode={TestMode} gameRunning={_gameRunning} " +
                        $"useV0={useV0Diag} tickCounter={_tickCounter} " +
                        $"profile={_profile?.Name ?? "null"} " +
                        $"catalog={_wheelChannelCatalog?.Count ?? -1} " +
                        $"framesSent={_framesSent}");
                    _postRenegDiagTicks--;
                }

                if (_gameStartHandshakePending)
                {
                    _gameStartHandshakePending = false;
                    SendGameStartHandshake();
                }

                // Active phase: telemetry + enable + periodic streams
                // Muted during dashboard switch transition: Profile has already
                // changed but the new tier-def hasn't been sent yet. Sending
                // value frames with the new data layout under the old flag bytes
                // gives the wheel garbage it can't decode.
                if (_dashSwitchMuted)
                    goto postValueFrames;

                GameDataSnapshot snapshot = TestMode
                    ? default
                    : GameDataSnapshot.FromStatusData(_latestGameData);

                // PitHouse capture wireshark/csp/start-game-change-dash.pcapng
                // (CSP / Type02 firmware) shows host outbound telemetry value
                // frames use the bit-packed 7d:23 group=0x43 path (1689 frames
                // observed). Session 0x02 FF records are reserved for property
                // pushes (kind=14 baseline brightness, kind=15 setting,
                // kind=10 standby) — verified by histogramming all FF kinds in
                // capture. Earlier comment claiming Type02 uses V0 FF for
                // telemetry was wrong. V0 FF is only for true V0 URL
                // subscription (Era2024 / TierDefEncoding.V0Url).
                bool useV0Values = _policy.Encoding == TierDefEncoding.V0Url;
                if (useV0Values)
                {
                    // V0 / Type02 firmware: emit per-channel value frames only
                    // while a game is actively running (or TestMode for the
                    // diagnostic pattern). PitHouse stays silent at idle on
                    // session 0x02; bursting V0 frames there collides with
                    // property-push records (brightness/standby on kinds 1, 10,
                    // 14, etc.) per bridge capture 2026-04-29.
                    if (TestMode || _gameRunning)
                        SendV0ValueFrames(snapshot);
                }
                else
                {
                    // Legacy V2 path: always send. BuildTestFrame vs
                    // BuildFrameFromSnapshot differentiates test/live within
                    // the loop.
                    byte subFlagBase = _activeSubscription?.FlagBase ?? 0;
                    for (int i = 0; i < tiers.Length; i++)
                    {
                        var tier = tiers[i];
                        if (_tickCounter % tier.TickInterval != 0)
                            continue;

                        // Match flag byte to the tier-def we last sent: each
                        // tier-def claims `flagBase + tierIdx` (BuildTier-
                        // DefinitionMessage line 289). Wheel routes value
                        // frames by flag byte → registered tier.
                        byte flagByte = (byte)(subFlagBase + i);
                        byte[] frame = TestMode
                            ? tier.Builder.BuildTestFrame(flagByte)
                            : tier.Builder.BuildFrameFromSnapshot(snapshot, flagByte);

                        // Diagnostic: log first emission of each tier after test mode starts
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

                postValueFrames:

                // Gate FFB-enable + sequence-counter on gameRunning. PitHouse capture
                // 2026-04-29 (R5 base, idle Nebula) shows zero `0x41/0x17 fdde` and
                // `0x2D/0x13 f531` frames during idle — those streams only fire while
                // a game is actively driving telemetry. Sending them at idle wastes
                // bandwidth and was the largest plugin-vs-PitHouse drift source.
                if (TestMode || _gameRunning)
                {
                    _connection.SendStream(StreamKind.Enable, _cachedEnableFrame);
                    if (SendSequenceCounter)
                        _connection.SendStream(StreamKind.Sequence, BuildSequenceCounterFrame());
                }

                // Peripheral output polls (handbrake + pedals). PitHouse polls these
                // at fixed cadence; mirror with sub-tick gating relative to the base
                // tick rate (default 30 ms = ~33 Hz). Each modulo target picks an
                // emit interval that approximates PitHouse's measured rate.
                //   tick % 3 != 0 = ~22 Hz (PitHouse 22 Hz handbrake-presence)
                //   tick % 3 == 0 = ~11 Hz (PitHouse 10 Hz handbrake-output)
                //   tick % 5 == 0 = ~6.6 Hz (PitHouse 7 Hz pedal-output × 3)
                if (_tickCounter % 3 != 0)
                    _connection.Send(_handbrakePresenceFrame);
                if (_tickCounter % 3 == 0)
                    _connection.Send(_handbrakeOutputFrame);
                if (_tickCounter % 5 == 0)
                {
                    _connection.Send(_pedalThrottleOutFrame);
                    _connection.Send(_pedalBrakeOutFrame);
                    _connection.Send(_pedalClutchOutFrame);
                }

                // LED state poll — group 1 frequent (~18 Hz target → tick%2==0
                // gives ~16.5 Hz on 33 Hz base), group 2 occasional (~1.7 Hz
                // target → tick%20==0 gives ~1.65 Hz).
                if (_tickCounter % 2 == 0)
                    _connection.Send(_ledStatePollGroup1);
                if (_tickCounter % 20 == 0)
                    _connection.Send(_ledStatePollGroup2);

                // Retransmit unacked session-data chunks. PitHouse re-emits each
                // chunk at ~1.4 Hz (50× over 37 s capture) until acked. Plugin
                // mirrors that with a 200 ms minimum gap between retransmits per
                // chunk and a 100-attempt safety cap to bound queue growth on a
                // permanently silent wheel.
                foreach (var chunk in _retransmitter.DueRetransmits(intervalMs: 200, maxRetries: 100))
                {
                    if (!_enabled || !_connection.IsConnected) break;
                    _connection.Send(chunk);
                }

                if (_tierDefBlindFrames != null
                    && _tierDefBlindSentRounds < TierDefBlindMaxRounds
                    && (Environment.TickCount - _tierDefBlindLastTickCount) >= TierDefBlindIntervalMs)
                {
                    _tierDefBlindSentRounds++;
                    _tierDefBlindLastTickCount = Environment.TickCount;
                    for (int i = 0; i < _tierDefBlindFrames.Length; i++)
                    {
                        if (!_enabled || !_connection.IsConnected) break;
                        _connection.Send(_tierDefBlindFrames[i]);
                    }
                    MozaLog.Debug(
                        $"[Moza] Blind retransmit round {_tierDefBlindSentRounds}/{TierDefBlindMaxRounds} " +
                        $"({_tierDefBlindFrames.Length} chunks)");
                    if (_tierDefBlindSentRounds >= TierDefBlindMaxRounds)
                        _tierDefBlindFrames = null;
                }

                _tickCounter++;

                // PitHouse-mirror widget polls: one frame per outer tick
                // (33Hz). Cycle of 80 slots = each frame fires ~0.4/s,
                // matching capture cadence ~0.2/s within tolerable range.
                if (_tickCounter % 10 == 0)
                    SendWidgetStatePoll();

                int slow = Math.Max(1, 1000 / _baseTickMs);
                if (_slowCounter++ % slow == 0)
                {
                    // SendHeartbeat() emits group-0 length-0 presence pings to
                    // each detected device. PitHouse capture (2026-04-29) shows
                    // none of these on the wire — PitHouse uses 0x43-keepalives
                    // (SendDashKeepalive below) for the same purpose. Skipping
                    // here removes ~4 frames/s of plugin-only noise. Hot-swap
                    // detection still works via PollStatus's wheel-model probe.
                    SendDashKeepalive();
                    // Plugin's per-tick mode poll was ~3/s vs PitHouse 0.7/s (2026-04-29
                    // diff). Move to slow path (1 Hz) and only when telemetry-mode setting
                    // is enabled.
                    if (SendTelemetryMode)
                        _connection.SendStream(StreamKind.Mode, _cachedModeFrame);
                    // Throttle display-config to every other slow tick (~0.5 Hz). PitHouse
                    // emits the (7c27, 7c27, 7c23) trio at <1 Hz; plugin previously fired at
                    // 1 Hz which made 7c23 ~7× too frequent vs PitHouse baseline.
                    if ((_slowCounter & 1) == 1)
                        SendDisplayConfig();
                    else if (_slowCounter % 4 == 0)
                        Send28xPoll();
                    SendStatusPush();
                    SendSession09Keepalive();
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Telemetry send error: {ex.Message}");
            }
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
            var catalog = _wheelChannelCatalog;

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
                    if (!_enabled || !_connection.IsConnected) return;
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
            if (!_preambleComplete) return;
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
            if (!_preambleComplete) return;
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
            // Pithouse's fc:00 frames are purely reactive session acks — there is no
            // separate "active-phase status sender." The ack_seq tracks the highest
            // sequence received on this session. Sending periodically with the current
            // ack_seq is harmless (just re-acks the same point if no new data arrived).
            SendSessionAck(FlagByte, (ushort)_sessionAckSeq);
        }

        public void Dispose()
        {
            // Idempotent: SimHub may invoke Dispose more than once during plugin
            // reload; double-dispose on ManualResetEventSlim throws.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            try { _ackReceived.Dispose(); } catch { }
            try { _mgmtResponseEvent.Dispose(); } catch { }
            try { _uploadSessionOpened.Dispose(); } catch { }
            try { _uploadSubMsg1Response.Dispose(); } catch { }
            try { _uploadSubMsg2Response.Dispose(); } catch { }
            try { _uploadEndReceived.Dispose(); } catch { }
        }

        private class TierState
        {
            public TelemetryFrameBuilder Builder = null!;
            public int TickInterval;
        }
    }
}
