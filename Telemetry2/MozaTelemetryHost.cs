using System;
using System.Collections.Generic;
using GameReaderCommon;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry2.Operations;
using MozaPlugin.Telemetry2.Wire;
using SessionDispatcher = MozaPlugin.Telemetry2.Sessions.SessionDispatcher;
using SessionEndpoint = MozaPlugin.Telemetry2.Sessions.SessionEndpoint;
using RetransmitPolicy = MozaPlugin.Telemetry2.Sessions.RetransmitPolicy;

namespace MozaPlugin.Telemetry2
{
    // Facade owning the new telemetry pipeline: SessionDispatcher + per-session
    // SessionEndpoints + all Operations + a single coordinator state machine.
    //
    // Public surface mirrors TelemetrySender (the IMozaTelemetry contract) so the
    // MozaPlugin dispatch shim can pick either implementation behind a setting flag.
    //
    // Coordinator state machine:
    //   Disconnected → Handshaking → WaitCatalog → Ready → (Switching → Ready loop)
    // Each transition is event-driven (handshake-sequence-emitted, catalog-received,
    // dashboard-switch-requested). The negotiator and frame-streamer are pure inside;
    // the host wires them to the serial transport.
    //
    // **Phase 5 status**: skeleton facade. Construction wires up the components, the
    // public methods route through them, but actual wire emission (pushing chunks
    // into MozaSerialConnection) is left to Phase 5b — the I/O integration cycle that
    // requires real-hardware testing. The host today is observable + testable: every
    // method records its effect on the negotiator / endpoints / ops, and tests can
    // verify state transitions without serial output.
    public sealed class MozaTelemetryHost : IMozaTelemetry
    {
        public enum HostState
        {
            Disconnected,
            Handshaking,
            WaitCatalog,
            Ready,
            Switching,
        }

        private readonly Action<byte[]>? _send;
        private readonly Action<int, byte[]>? _sendStream;
        private const int StreamSlotTierDash0 = 0;
        private const int StreamSlotEnable = 8;
        private const int StreamSlotSequence = 9;
        private const int StreamSlotMode = 10;
        private readonly SessionDispatcher _dispatcher = new SessionDispatcher();
        private readonly SessionEndpoint _ep01;     // tier-def TLV
        private readonly SessionEndpoint _ep02;     // FF records
        private readonly SessionEndpoint _ep03;     // tile-server
        private readonly TierDefNegotiator _negotiator = new TierDefNegotiator();
        private readonly WheelHandshakeOp _handshake = new WheelHandshakeOp();
        private readonly KeepaliveOp _keepalive = new KeepaliveOp();
        private readonly ConfigJsonOp _configJson = new ConfigJsonOp();
        private readonly TileServerOp _tileServer = new TileServerOp();
        private readonly DashboardUploadOp _upload = new DashboardUploadOp();
        private readonly DashboardDownloadOp _download = new DashboardDownloadOp();
        private readonly CatalogConsumer _catalogConsumer = new CatalogConsumer();
        private byte[]? _actionCatalogPayload;

        // State
        private readonly object _stateLock = new object();
        private HostState _state = HostState.Disconnected;
        private bool _gameRunning;
        private MultiStreamProfile? _profile;
        private byte[]? _mzdashContent;
        private string _mzdashName = "";
        private Func<string, double>? _propertyResolver;
        private Func<IReadOnlyList<string>, string, MultiStreamProfile>? _catalogProfileBuilder;
        private volatile bool _catalogProfileActive;
        private volatile bool _catalogRebuildPending;
        private IReadOnlyList<string>? _latestCatalogSnapshot;
        private int _framesSent;
        private StatusDataBase? _latestSnapshot;
        private FrameStreamerOp? _frameStreamer;

        // Dash-keepalive pings — group 0x43 N=1 data=0x00 to dev=0x14 (dash), 0x15, 0x17
        // (wheel). PitHouse sends ~1.1s cadence. Without these, wheel disengages from
        // telemetry mode after ~30s and command-channel polls stop responding, tripping
        // the plugin's watchdog (per Telemetry/TelemetrySender.SendDashKeepalive at line 3411).
        private long _lastKeepaliveTicks;
        private bool _keepaliveBaseline;
        private const long KeepaliveIntervalTicks = 1100 * System.TimeSpan.TicksPerMillisecond;

        private static byte[] BuildKeepaliveFrame(byte dev)
        {
            var frame = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x01,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup, dev,
                0x00, 0x00,
            };
            frame[5] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private static readonly byte[] DashKeepaliveDash = BuildKeepaliveFrame(0x14);
        private static readonly byte[] DashKeepalive15 = BuildKeepaliveFrame(0x15);
        private static readonly byte[] DashKeepaliveWheel = BuildKeepaliveFrame(0x17);

        // Group-0 presence pings to all device IDs (18..30). PitHouse SendHeartbeat sends
        // these to keep wheel "aware" of host. Frame format: [0x7E][0x00][0x00][dev][chk]
        // (5 bytes, N=0). Mirrors Telemetry/TelemetrySender.SendHeartbeat at line 3401.
        private static byte[] BuildPresencePing(byte dev)
        {
            var frame = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x00, 0x00, dev, 0x00,
            };
            frame[4] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }
        private static readonly byte[][] PresencePings = BuildAllPresencePings();
        private static byte[][] BuildAllPresencePings()
        {
            var pings = new byte[13][];
            for (int i = 0; i < 13; i++) pings[i] = BuildPresencePing((byte)(18 + i));
            return pings;
        }

        // 28x poll — group 0x40 cmd 0x28:00 + 0x28:01 to wheel. PitHouse fires every
        // ~4s; wheel echoes back as group 0xC0 cmd 0x28, which triggers MarkWheelResponse
        // in MozaPlugin.OnMessageReceived → clears _wheelPollMisses → keeps watchdog at 0.
        // Without these polls, wheel-watchdog resets after ~165s. Mirrors
        // Telemetry/TelemetrySender.Send28xPoll at line 3440.
        private static byte[] Build40Frame3(byte c1, byte c2, byte c3)
        {
            var f = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x03,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetryModeGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                c1, c2, c3, 0x00,
            };
            f[7] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }
        private static readonly byte[] Poll28x00 = Build40Frame3(0x28, 0x00, 0x00);
        private static readonly byte[] Poll28x01 = Build40Frame3(0x28, 0x01, 0x00);

        private long _last28xTicks;
        private bool _poll28xBaseline;
        // 28x poll keeps the wheel watchdog from biting (~165s timeout); fire well
        // within that. 8s gives ~20× headroom and halves the bandwidth this slice
        // costs (vs the previous 4s cadence).
        private const long Poll28xIntervalTicks = 8000 * System.TimeSpan.TicksPerMillisecond;

        // Channel-config burst — group 0x40 frames that enable channels on the wheel's
        // display pages. PitHouse emits this once at preamble after the tier-def. Without
        // it, the wheel never enables the (page, channel) slots referenced by widgets,
        // so kind=4 dashboard switches and value frames render nothing.
        // Mirrors Telemetry/TelemetrySender.SendChannelConfig at line 3208.
        //
        // Wire format:
        //   1E enables: `7e 05 40 17 1E [page] [channel] 00 00 [chk]` for pages 0/1/3 × ch 2..6
        //   28:00 query: `7e 03 40 17 28 00 00 [chk]`  (WheelGetCfg_GetMultiFunctionSwitch)
        //   28:01 query: `7e 03 40 17 28 01 00 [chk]`  (WheelGetCfg_GetMultiFunctionNum)
        //   09:00 query: `7e 02 40 17 09 00 [chk]`
        //   28:02 mode:  `7e 04 40 17 28 02 01 00 [chk]`  (telemetry channel mode = multi)
        private static byte[] BuildChannelEnableFrame(byte page, byte channel)
        {
            var f = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x05,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetryModeGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0x1E, page, channel, 0x00, 0x00, 0x00,
            };
            f[9] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        private static byte[] Build40Frame2(byte c1, byte c2)
        {
            var f = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x02,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetryModeGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                c1, c2, 0x00,
            };
            f[6] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        private static byte[] Build40Frame4(byte c1, byte c2, byte c3, byte c4)
        {
            var f = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x04,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetryModeGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                c1, c2, c3, c4, 0x00,
            };
            f[8] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        private static readonly byte[][] ChannelEnableFrames = BuildAllChannelEnableFrames();
        private static byte[][] BuildAllChannelEnableFrames()
        {
            var pages = new byte[] { 0, 1, 3 };
            var frames = new byte[pages.Length * 5][];
            int i = 0;
            foreach (byte page in pages)
                for (byte ch = 2; ch <= 6; ch++)
                    frames[i++] = BuildChannelEnableFrame(page, ch);
            return frames;
        }
        private static readonly byte[] Query0900 = Build40Frame2(0x09, 0x00);
        private static readonly byte[] Mode2802 = Build40Frame4(0x28, 0x02, 0x01, 0x00);

        private bool _channelConfigSent;

        // Session 0x09 priming. PitHouse cold-start sends two distinct frames to encourage
        // wheel to open session 0x09 (configJson channel):
        //
        // 1. SendSessionPrime — `7e 0A 43 17 7c 00 09 01 [seq:LE] 00 00 00 00 [chk]`
        //    Empty type=0x01 data chunk. Hint to wheel that we want this session.
        //    Mirrors Telemetry/TelemetrySender.SendSessionPrime at line 3005.
        //
        // 2. SendConfigJsonOpenRequest — `7e 0A 43 17 7c 1e 6c 80 [seq:LE] 09 00 fe 01 [chk]`
        //    Special "open request" with configJson magic (7c 1e 6c 80). Post-2026-04 CSP
        //    firmware requires this distinct magic; SessionPrime alone doesn't trigger
        //    device-init for session 0x09. Mirrors Telemetry/TelemetrySender at line 3161.
        //
        // Without these, wheel never opens 0x09 → configJson tab stays empty + Diagnostics
        // shows no session 0x09 traffic.
        private static byte[] BuildSessionPrime(byte session, ushort seq)
        {
            var frame = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x0A,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session, 0x01,
                (byte)(seq & 0xFF), (byte)((seq >> 8) & 0xFF),
                0x00, 0x00,
                0x00, 0x00,
                0x00,
            };
            frame[frame.Length - 1] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private static byte[] BuildConfigJsonOpenRequest(byte port, ushort seq)
        {
            var frame = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x0A,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0x7C, 0x1E, 0x6C, 0x80,
                (byte)(seq & 0xFF), (byte)((seq >> 8) & 0xFF),
                port, 0x00,
                0xFE, 0x01,
                0x00,
            };
            frame[frame.Length - 1] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        // Periodic session-09 keepalive (mirrors Telemetry/TelemetrySender.SendSession09Keepalive
        // at line 3146). After wheel opens 0x09, host pings periodically to keep configJson
        // session warm. Per-call seq increments.
        private ushort _session09KeepaliveSeq;
        private long _lastSession09KeepaliveTicks;
        private bool _session09KeepaliveBaseline;
        private const long Session09KeepaliveIntervalTicks = 2000 * System.TimeSpan.TicksPerMillisecond;

        // Session 0x03 keepalive — empty 4-byte zero data chunk. Per
        // docs/protocol/sessions/lifecycle.md: 0x03 is "open frames + 4-byte zero
        // keepalives only" on 2026-04+ firmware. Cadence inferred from PitHouse
        // captures (~3s between sess=03 traffic events).
        private long _lastSession03KeepaliveTicks;
        private bool _session03KeepaliveBaseline;
        private const long Session03KeepaliveIntervalTicks = 3000 * System.TimeSpan.TicksPerMillisecond;

        // ── Peripheral output polls (PitHouse parity) ─────────────────────────
        // PitHouse polls these continuously regardless of game state. Without them
        // the wheel/base may consider the host telemetry-only and degrade rendering
        // (verified gap from v1 vs v2 wire diff 2026-05-05). v1 emits these from
        // TelemetrySender main loop with modulo-tick gating; v2 uses interval timers
        // because Tick cadence varies with SimHub's game-data rate.
        //
        // Cadences from PitHouse capture 2026-04-29 (Telemetry/TelemetrySender.cs:343):
        //   handbrake-presence  0x5A/0x1B 00            ~22 Hz → 45ms
        //   handbrake-output    0x5D/0x1B 01 00 00      ~10 Hz → 100ms
        //   pedal-throttle-out  0x25/0x19 01 00 00      ~7 Hz → 143ms
        //   pedal-brake-out     0x25/0x19 02 00 00      ~7 Hz → 143ms
        //   pedal-clutch-out    0x25/0x19 03 00 00      ~7 Hz → 143ms
        //   LED-state group 1   0x40/0x17 1F 03 01 ...  ~18 Hz → 55ms
        //   LED-state group 2   0x40/0x17 1F 03 02 ...  ~1.7 Hz → 588ms
        private static byte[] BuildShortFrame(byte group, byte dev, byte[] payload)
        {
            var frame = new byte[payload.Length + 5];
            frame[0] = global::MozaPlugin.Protocol.MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = group;
            frame[3] = dev;
            System.Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = global::MozaPlugin.Protocol.MozaProtocol
                .CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }
        private static readonly byte[] PeriphHandbrakePresence = BuildShortFrame(0x5A, 0x1B, new byte[] { 0x00 });
        private static readonly byte[] PeriphHandbrakeOutput   = BuildShortFrame(0x5D, 0x1B, new byte[] { 0x01, 0x00, 0x00 });
        private static readonly byte[] PeriphPedalThrottleOut  = BuildShortFrame(0x25, 0x19, new byte[] { 0x01, 0x00, 0x00 });
        private static readonly byte[] PeriphPedalBrakeOut     = BuildShortFrame(0x25, 0x19, new byte[] { 0x02, 0x00, 0x00 });
        private static readonly byte[] PeriphPedalClutchOut    = BuildShortFrame(0x25, 0x19, new byte[] { 0x03, 0x00, 0x00 });
        private static readonly byte[] PeriphLedGroup1Poll     = BuildShortFrame(0x40, 0x17, new byte[] { 0x1F, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00 });
        private static readonly byte[] PeriphLedGroup2Poll     = BuildShortFrame(0x40, 0x17, new byte[] { 0x1F, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00 });

        private long _lastHandbrakePresenceTicks;
        private long _lastHandbrakeOutputTicks;
        private long _lastPedalOutTicks;
        private long _lastLedGroup1PollTicks;
        private long _lastLedGroup2PollTicks;
        // Parity-poll intervals at ~1 Hz. Wheel needs SOME poll activity to
        // stay engaged at idle (turning these off entirely freezes the
        // dashboard on last-value within ~5 min); ~1 Hz is enough at ~12% of
        // the cost of mirroring PitHouse's full ~7-22 Hz cadence.
        private const long PeriphHandbrakePresenceIntervalTicks = 1000L * System.TimeSpan.TicksPerMillisecond;
        private const long PeriphHandbrakeOutputIntervalTicks   = 1000L * System.TimeSpan.TicksPerMillisecond;
        private const long PeriphPedalOutIntervalTicks          = 1000L * System.TimeSpan.TicksPerMillisecond;
        private const long PeriphLedGroup1PollIntervalTicks     = 1000L * System.TimeSpan.TicksPerMillisecond;
        private const long PeriphLedGroup2PollIntervalTicks     = 5000L * System.TimeSpan.TicksPerMillisecond;

        // FFB-enable + sequence-counter periodic streams (v1 parity per
        // Telemetry/TelemetrySender.cs:2879-2884 + 3402-3410). Both fire every Tick
        // when TestMode || gameRunning. Without them, widgets stay static even with
        // correctly-shaped value frames — wheel needs these to keep displays bound.
        // Wire bytes:
        //   FFB-enable:    7e 06 41 17 fd de 00 00 00 00 [chk]   (group=BaseSendTelemetry=0x41)
        //   Sequence ctr:  7e 06 2d 13 f5 31 00 00 00 [seq] [chk]  (seq is LAST byte of payload)
        // NOTE: v1 puts the rolling seq byte at FRAME OFFSET 9 (= last byte of payload
        // before checksum). Earlier v2 implementation incorrectly put it at offset 6,
        // which made the wheel ignore these frames entirely.
        private static readonly byte[] FfbEnableFrame = BuildFrameWithBody(
            0x41, 0x17, new byte[] { 0xFD, 0xDE, 0x00, 0x00, 0x00, 0x00 });
        private byte _sequenceCounter;
        private byte[] BuildSequenceCounterFrame()
        {
            byte[] f = new byte[] {
                0x7E, 0x06, 0x2D, 0x13,
                0xF5, 0x31, 0x00, 0x00, 0x00, _sequenceCounter,
                0x00,
            };
            _sequenceCounter++;
            f[f.Length - 1] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }
        private static byte[] BuildFrameWithBody(byte grp, byte dev, byte[] body)
        {
            byte[] f = new byte[body.Length + 5];
            f[0] = 0x7E;
            f[1] = (byte)body.Length;
            f[2] = grp;
            f[3] = dev;
            System.Array.Copy(body, 0, f, 4, body.Length);
            f[f.Length - 1] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        // Telemetry-mode periodic re-emit (v1: TelemetrySender.cs:2962-2963 emits at ~1Hz).
        // Wire: 7e 04 40 17 28 02 01 00 [chk]  → "telemetry channel mode = multi"
        private static readonly byte[] ModeFramePeriodic = BuildFrameWithBody(
            0x40, 0x17, new byte[] { 0x28, 0x02, 0x01, 0x00 });
        private long _lastModePeriodicTicks;
        private bool _modePeriodicBaseline;
        private const long ModePeriodicIntervalTicks = 1000L * System.TimeSpan.TicksPerMillisecond;

        // Widget-state poll cycle (v1: TelemetrySender.SendOneWidgetPoll line 3519).
        // 80-slot rotating cycle of probe frames: static 0x40/0x17 polls, channel-1e
        // sweep, 1f00/1f01 sweep, group-0E discovery, 1F LED reads, 3F display variants.
        // Fires once per "outer tick" at ~30Hz / 10 = ~3Hz cumulative cadence; per-slot
        // is ~0.4 Hz. Pre-built once and rotated at each emission.
        private static readonly byte[][] WidgetPollFrames = BuildWidgetPollFrames();
        private int _widgetPollIndex;
        private long _lastWidgetPollTicks;
        private bool _widgetPollBaseline;
        private const long WidgetPollIntervalTicks = 1000L * System.TimeSpan.TicksPerMillisecond;

        private static byte[][] BuildWidgetPollFrames()
        {
            // Mirrors TelemetrySender.SendOneWidgetPoll layout. 80 slots total.
            // Most slots use group 0x40 dev 0x17 (TelemetryModeGroup); some use other devs.
            byte[] frame(byte g, byte d, byte[] body) => BuildFrameWithBody(g, d, body);
            const byte g40 = 0x40, d17 = 0x17;
            byte[][] f = new byte[80][];
            // 0..14 static
            f[0]  = frame(g40, d17, new byte[] { 0x1B, 0x00, 0xFF, 0x00, 0x00 });
            f[1]  = frame(g40, d17, new byte[] { 0x1B, 0x01, 0xFF, 0x00, 0x00 });
            f[2]  = frame(g40, d17, new byte[] { 0x1B, 0x03, 0xFF, 0x00, 0x00 });
            f[3]  = frame(g40, d17, new byte[] { 0x1C, 0x00, 0x00 });
            f[4]  = frame(g40, d17, new byte[] { 0x1C, 0x01, 0x00 });
            f[5]  = frame(g40, d17, new byte[] { 0x1C, 0x03, 0x00 });
            f[6]  = frame(g40, d17, new byte[] { 0x1D, 0x00, 0x00 });
            f[7]  = frame(g40, d17, new byte[] { 0x1D, 0x01, 0x00 });
            f[8]  = frame(g40, d17, new byte[] { 0x1D, 0x03, 0x00 });
            f[9]  = frame(g40, d17, new byte[] { 0x20, 0x00 });
            f[10] = frame(g40, d17, new byte[] { 0x21, 0x00, 0x00 });
            f[11] = frame(g40, d17, new byte[] { 0x27, 0x00, 0x00, 0x00, 0x00, 0x00 });
            f[12] = frame(g40, d17, new byte[] { 0x28, 0x00, 0x00 });
            f[13] = frame(g40, d17, new byte[] { 0x29, 0x00, 0x00 });
            f[14] = frame(g40, d17, new byte[] { 0x2A, 0x00, 0x00 });
            // 15..29 — 1e0X sweep: sub ∈ {0x00,0x01,0x03} × b4 ∈ {0x02..0x06}
            for (int s = 0; s < 15; s++) {
                byte sub = (byte)((s / 5) == 0 ? 0x00 : (s / 5) == 1 ? 0x01 : 0x03);
                byte b4 = (byte)(0x02 + (s % 5));
                f[15 + s] = frame(g40, d17, new byte[] { 0x1E, sub, b4, 0x00, 0x00 });
            }
            // 30..43 — 1f00 sweep
            for (int s = 0; s < 14; s++) {
                byte b5 = (byte)(0x02 + s);
                f[30 + s] = frame(g40, d17, new byte[] { 0x1F, 0x00, 0xFF, b5, 0x00, 0x00, 0x00 });
            }
            // 44..57 — 1f01 sweep
            for (int s = 0; s < 14; s++) {
                byte b5 = (byte)(0x02 + s);
                f[44 + s] = frame(g40, d17, new byte[] { 0x1F, 0x01, 0xFF, b5, 0x00, 0x00, 0x00 });
            }
            // 58..69 — grp 0x0E discovery probes (12 slots)
            for (int s = 0; s < 12; s++) {
                byte dev = (byte)(s / 3 switch { 0 => 0x12, 1 => 0x13, 2 => 0x17, _ => 0x19 });
                int sub = s % 3;
                byte cmd = (byte)(sub == 0 ? 0x00 : sub == 1 ? 0x01 :
                    (dev == 0x12 ? 0x03 : dev == 0x13 ? 0x07 :
                     dev == 0x17 ? 0x0F : 0x13));
                f[58 + s] = frame(0x0E, dev, new byte[] { cmd, 0x00, 0x00 });
            }
            // 70..73 — grp 0x1F dev 0x12 4f08-4f0b LED state reads
            for (int s = 0; s < 4; s++) {
                f[70 + s] = frame(0x1F, 0x12, new byte[] { 0x4F, (byte)(0x08 + s), 0x00 });
            }
            // 74..79 — grp 0x3F dev 0x17 display variants
            f[74] = frame(0x3F, 0x17, new byte[] { 0x19, 0x01, 0x00 });
            f[75] = frame(0x3F, 0x17, new byte[] { 0x19, 0x03, 0x00 });
            f[76] = frame(0x3F, 0x17, new byte[] { 0x1A, 0x01, 0x00 });
            f[77] = frame(0x3F, 0x17, new byte[] { 0x1A, 0x03, 0x00 });
            f[78] = frame(0x3F, 0x17, new byte[] { 0x1F, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00 });
            f[79] = frame(0x3F, 0x17, new byte[] { 0x21, 0x00, 0x00 });
            return f;
        }

        // Session 0x09 configJson() reply — once per cold-start. v1
        // (Telemetry/TelemetrySender.MaybeSendConfigJsonReply line 1830) sends
        // a {"configJson()": {dashboards:[...]}, "id":11} envelope back to the
        // wheel after receiving its initial state push. Without this reply the
        // wheel never finalises integration: no b2h kind=14 heartbeats, no
        // kind=4 echoes, dashboard rendering stays disabled. Using the wheel's
        // own ConfigJsonList as the dashboards array means we don't have to
        // know the canonical list at startup — wheel's view is the source of truth.
        private bool _configJson09ReplySent;

        // ConfigJson reassembly watchdog. If the wheel pushes sess=0x09 data but a
        // serial drop corrupts the zlib stream, the reassembler accumulates garbage
        // indefinitely and never produces a valid WheelDashboardState. Track when
        // chunks started arriving and whether parsing succeeded; if stuck for >3s
        // after last chunk, clear the buffer and re-prime to trigger a fresh push.
        private long _configJsonFirstChunkTicks;
        private long _configJsonLastChunkTicks;
        private int _configJsonRetries;
        private const int ConfigJsonMaxRetries = 3;
        // Per-round stale-timeout backoff. First round catches transient drops fast
        // (3s), later rounds widen so we don't thrash a slow-firmware wheel that's
        // genuinely working. Total budget ≈ 3+5+8 = 16s across 3 rounds.
        private static readonly long[] ConfigJsonStaleTimeoutTicksByRound = new long[]
        {
            3000 * System.TimeSpan.TicksPerMillisecond,
            5000 * System.TimeSpan.TicksPerMillisecond,
            8000 * System.TimeSpan.TicksPerMillisecond,
        };

        // Blind retransmit state for session 0x01 tier-def. The wheel never
        // FC-acks session 0x01, so we re-emit on a backoff schedule until
        // catalog activity arrives or the budget is exhausted. Schedule lives
        // in Protocol/RetryBackoff so V0 and V2 can't drift.
        private List<SessionChunk>? _retransmitChunks;
        private long _retransmitLastTicks;
        private int _retransmitRoundsRemaining;
        private static int RetransmitRounds
            => global::MozaPlugin.Protocol.RetryBackoff.TierDefBlindMs.Length;

        // Display-config frames (7c:27 + 7c:23). Tells the wheel which dashboard pages
        // are active for the current dashboard. PitHouse emits these in pairs per page
        // at ~500ms cadence, cycling through pages. Without them, the wheel's display
        // pipeline never confirms which pages should be lit, and value-frame data
        // arriving on raw 7c:43 transport is treated as out-of-context.
        //
        // Mirrors bridge-20260503-113616.jsonl PitHouse cold-start (per-page pairs):
        //   7c 27 0f 80 [b2:u16LE] [b4:u16LE] fe 01     (config page B)
        //   7c 23 46 80 [ab2:u16LE] [ab4:u16LE] fe 01   (activate page B)
        // where b2 = 5+2*page, b4 = 3+2*page, ab2 = 7+2*page, ab4 = 5+2*page.
        private byte[][]? _displayConfigFrames;
        private int _displayConfigPageCount;
        private long _lastDisplayConfigTicks;
        private long _lastFrameDiagTicks;
        private bool _displayConfigBaseline;
        private const long DisplayConfigIntervalTicks = 500 * System.TimeSpan.TicksPerMillisecond;

        // ── Port allocation + deferred sess-01 open (2026-04+ firmware fix) ───
        // Wheel uses a global monotonic port counter shared with the host. Per
        // docs/protocol/sessions/lifecycle.md: PitHouse allocates host ports
        // accounting for ports the wheel has claimed (own keepalive sessions,
        // configJson, etc.). Opening sess 01 with port=1 immediately at startup
        // collides with the wheel's pre-allocated port range — wheel responds by
        // emitting type=0x00 end-marker on sess 01 every second, tearing down our
        // session before any tier-def can render.
        //
        // Fix: open sess 02 first (port=2 is always safe), wait for wheel
        // activity (any b2h type=0x81 OR data frame), then open sess 01 with
        // port = max(known_ports) + 1. Mirrors PitHouse's observed +9.30s open
        // of sess 01 with port=9 in bridge-20260430-210453.jsonl after wheel
        // claimed ports 3, 5, 7, 8 for its own sessions.
        private readonly System.Collections.Generic.HashSet<ushort> _claimedPorts = new();
        private bool _sess01Opened;
        private bool _wheelActivitySeen;
        private long _sess02OpenedTicks;
        private long _catalogReceivedTicks;
        // Maximum time to wait for wheel activity before opening sess 01 anyway
        // (fallback in case the wheel is silent — older firmware doesn't always
        // send b2h type=0x81 on every cold-start).
        private const long Sess01OpenMaxWaitTicks = 2500L * System.TimeSpan.TicksPerMillisecond;
        // If wheel hasn't announced its channel catalog within this window after
        // sess=01 opens, fall back to profile-derived alphabetical indices.
        // Mirrors v1's AutoFallbackWireFormat at TelemetrySender.cs:1419.
        private const long CatalogFallbackTimeoutTicks = 5000L * System.TimeSpan.TicksPerMillisecond;
        // Settle delay after the last wheel-catalog update before draining the
        // negotiator. The wheel announces entries incrementally (~30ms apart over
        // ~500ms). Emitting a tier-def mid-drip uses a partial catalog (wrong
        // channel indices) and wastes the preamble's retransmit window.
        private const long CatalogSettleTicks = 500L * System.TimeSpan.TicksPerMillisecond;
        private long _lastCatalogUpdateTicks;
        // Minimum wait so the wheel has a chance to claim its first port.
        private const long Sess01OpenMinWaitTicks = 200L * System.TimeSpan.TicksPerMillisecond;

        // ── Renegotiate state machine (warm dashboard switch) ─────────────────
        // Models PitHouse's switch handshake observed in bridge-20260503-115840.jsonl:
        //   T+0:    h2b FF kind=4 (slot N) on session 02
        //   T+50ms: b2h kind=4 echo + fc-ack — wheel accepted switch
        //   T+150ms: h2b grp40/cmd27 display-config burst (early)
        //   T+350ms-700ms: b2h multi-chunk record on session 02 (wheel-side state push)
        //   T+~200ms quiescence after wheel finishes pushing state
        //   T+~1.1s: h2b grp40/cmd1e channel-config burst (15 frames) + cmd27 burst again
        //   T+~1.85s: h2b new tier-def on session 01
        // Key insight: PitHouse defers the new tier-def until *after* the wheel has
        // finished announcing its post-switch state. Synchronous re-emission inside
        // SendDashboardSwitch (the previous behavior) raced the wheel's transition
        // and was the suspected cause of switches being acked at the transport layer
        // but not rendered on the wheel UI.
        // Public for diagnostics + unit tests; corresponds 1:1 with private SwitchState.
        public enum SwitchPhase { Idle, AwaitingEcho, AwaitingQuiescence }
        public SwitchPhase CurrentSwitchPhase
        {
            get { lock (_stateLock) return (SwitchPhase)_switchState; }
        }
        private enum SwitchState { Idle, AwaitingEcho, AwaitingQuiescence }
        private SwitchState _switchState = SwitchState.Idle;
        private long _switchStateEnteredTicks;
        private long _lastB2hChunkTicks;
        private uint _pendingSwitchSlot;
        private MultiStreamProfile? _pendingSwitchProfile;
        private const long SwitchWatchdogTicks = 3000L * System.TimeSpan.TicksPerMillisecond;
        private const long SwitchEchoTimeoutTicks = 1500L * System.TimeSpan.TicksPerMillisecond;
        private const long SwitchQuiescenceTicks = 200L * System.TimeSpan.TicksPerMillisecond;
        private const long SwitchMinQuiescenceWaitTicks = 100L * System.TimeSpan.TicksPerMillisecond;
        private const long SwitchQuiescenceMaxTicks = 1500L * System.TimeSpan.TicksPerMillisecond;

        private static byte[] BuildDisplayConfigConfigFrame(int page)
        {
            byte b2 = (byte)(0x05 + 2 * page);
            byte b4 = (byte)(0x03 + 2 * page);
            var f = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x0A,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0x7C, 0x27, 0x0F, 0x80, b2, 0x00, b4, 0x00, 0xFE, 0x01, 0x00,
            };
            f[14] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        private static byte[] BuildDisplayConfigActivateFrame(int page)
        {
            byte ab2 = (byte)(0x07 + 2 * page);
            byte ab4 = (byte)(0x05 + 2 * page);
            var f = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x0A,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0x7C, 0x23, 0x46, 0x80, ab2, 0x00, ab4, 0x00, 0xFE, 0x01, 0x00,
            };
            f[14] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        // Third display-config frame per page: 7c:27 0F00 [z=0x06+2*page]. Old pipeline
        // (Telemetry/TelemetrySender.cs:3633) emits this after the activate frame.
        private static byte[] BuildDisplayConfigConfigFrame2(int page)
        {
            byte z = (byte)(0x06 + 2 * page);
            var f = new byte[] {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x06,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0x7C, 0x27, 0x0F, 0x00, z, 0x00, 0x00,
            };
            f[10] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            return f;
        }

        private void EnsureDisplayConfigFrames()
        {
            int pageCount = _profile?.PageCount ?? 1;
            if (pageCount < 1) pageCount = 1;
            if (_displayConfigFrames != null && _displayConfigPageCount == pageCount) return;
            var frames = new byte[pageCount * 3][];
            for (int p = 0; p < pageCount; p++)
            {
                frames[p * 3 + 0] = BuildDisplayConfigConfigFrame(p);
                frames[p * 3 + 1] = BuildDisplayConfigActivateFrame(p);
                frames[p * 3 + 2] = BuildDisplayConfigConfigFrame2(p);
            }
            _displayConfigFrames = frames;
            _displayConfigPageCount = pageCount;
        }

        private void EmitDisplayConfigBurst()
        {
            EnsureDisplayConfigFrames();
            if (_displayConfigFrames == null || _displayConfigPageCount <= 0) return;
            // PitHouse emits all pages back-to-back in one burst (3 frames per page).
            for (int p = 0; p < _displayConfigPageCount; p++)
            {
                _send?.Invoke(_displayConfigFrames[p * 3 + 0]);
                _send?.Invoke(_displayConfigFrames[p * 3 + 1]);
                _send?.Invoke(_displayConfigFrames[p * 3 + 2]);
            }
        }

        // Channel-config burst — pages 0/1/3 × channels 2..6 = 15 1E-enable frames,
        // followed by 28:00 / 28:01 query frames, 09:00, and the 28:02 mode frame.
        // Fires once after the first tier-def emission of a session (or after a profile
        // change reset). Without it, value frames produce no visible output.
        private void EmitChannelConfigBurst()
        {
            foreach (var f in ChannelEnableFrames)
                _send?.Invoke(f);
            _send?.Invoke(Poll28x00);
            _send?.Invoke(Poll28x01);
            _send?.Invoke(Query0900);
            _send?.Invoke(Mode2802);
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: channel-config burst — {ChannelEnableFrames.Length} 1E enables + 28/09 queries + mode frame");
        }

        public HostState State
        {
            get { lock (_stateLock) return _state; }
        }

        public bool Type02NConvention { get; set; } = true;

        public WheelDashboardState? LastWheelState => _configJson.LastState;

        public TierDefNegotiator Negotiator => _negotiator;

        // Tile-server state (parsed from b2h session 0x03 chunks). Mirrors the
        // surface old TelemetrySender.TileServerState exposes.
        public TileServerState? TileServerState => _tileServer.LastState;

        // Wheel channel catalog (parsed from b2h tag=0x04 records via CatalogConsumer).
        // Returns the same data the negotiator uses for tier-def channel idx resolution.
        public IReadOnlyList<string>? WheelChannelCatalog
        {
            get
            {
                var c = _catalogConsumer.CurrentCatalog;
                return c.Count > 0 ? c : null;
            }
        }

        // Subscription snapshot for the Diagnostics tab. Mirrors old TelemetrySender
        // SubscriptionDiagnostics with the byte arrays captured on the latest emission.
        public SubscriptionSnapshot ActiveSubscription
            => _negotiator.ActiveSubscription;

        // Latest tier-def emission bytes + timestamp for the Diagnostics tab "Subscription"
        // section. Captures the full message (preamble + section) and timestamp of last
        // EmitOnSession01 call. Mirrors TelemetrySender._lastSubscriptionDiag.
        private byte[]? _lastSubscriptionBytes;
        private DateTime _lastSubscriptionAt;
        public (byte[] Bytes, DateTime CapturedAt)? LastSubscriptionRaw
        {
            get
            {
                lock (_stateLock)
                {
                    if (_lastSubscriptionBytes == null) return null;
                    return (_lastSubscriptionBytes, _lastSubscriptionAt);
                }
            }
        }

        // Inbound chunks captured on session 0x02 during the 5s window after the most-
        // recent subscription emission. Wheel may push tag=0x0c token assignments etc.
        // there. Mirrors TelemetrySender._subscriptionResponseChunks surface.
        private readonly List<byte[]> _subscriptionResponseChunks = new List<byte[]>();
        private long _subscriptionResponseDeadlineTicks;
        public IReadOnlyList<byte[]> LastSubscriptionResponse
        {
            get { lock (_subscriptionResponseChunks) return _subscriptionResponseChunks.ToArray(); }
        }

        // Session traffic counters: per-session (inbound, outbound) chunk counts.
        // Mirrors old TelemetrySender.SessionCounts surface for the Diagnostics tab.
        private readonly Dictionary<byte, int> _inboundChunksBySession = new Dictionary<byte, int>();
        private readonly Dictionary<byte, int> _outboundChunksBySession = new Dictionary<byte, int>();
        public IReadOnlyDictionary<byte, (int In, int Out)> SessionCounts
        {
            get
            {
                lock (_stateLock)
                {
                    var d = new Dictionary<byte, (int In, int Out)>();
                    foreach (var k in _inboundChunksBySession.Keys)
                        d[k] = (_inboundChunksBySession.TryGetValue(k, out var i) ? i : 0,
                                _outboundChunksBySession.TryGetValue(k, out var o) ? o : 0);
                    foreach (var k in _outboundChunksBySession.Keys)
                        if (!d.ContainsKey(k))
                            d[k] = (0, _outboundChunksBySession[k]);
                    return d;
                }
            }
        }

        // Wheel catalog announcements (b2h tag=04 URL records via CatalogConsumer)
        // provide the URL→idx mapping for tier-def channel records. The mzdash
        // defines WHAT channels to subscribe; the wheel catalog defines WHERE
        // those channels sit in the wheel's index space.
        public void SetWheelCatalog(IReadOnlyList<string> urlsByIndex)
        {
            if (urlsByIndex == null || urlsByIndex.Count == 0) return;
            global::MozaPlugin.MozaLog.Debug(
                $"[Moza] Telemetry2 host: wheel catalog announcement ({urlsByIndex.Count} entries) — forwarding to negotiator");
            _negotiator.SetCatalog(urlsByIndex);
            long now = System.DateTime.UtcNow.Ticks;
            _lastCatalogUpdateTicks = now;
            lock (_stateLock)
            {
                _catalogReceivedTicks = now;
                if (_state == HostState.WaitCatalog) _state = HostState.Ready;
            }
            _latestCatalogSnapshot = urlsByIndex;
            _catalogRebuildPending = true;
        }

        private void RebuildProfileFromCatalog(IReadOnlyList<string> catalog)
        {
            var builder = _catalogProfileBuilder;
            if (builder == null) return;
            MultiStreamProfile? catalogProfile;
            try { catalogProfile = builder(catalog, "WheelCatalog"); }
            catch { return; }
            if (catalogProfile == null || catalogProfile.Tiers.Count == 0) return;
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: rebuilt profile from wheel catalog — " +
                $"{catalogProfile.Tiers.Count} tiers, " +
                $"{CountChannels(catalogProfile)} channels");
            _profile = catalogProfile;
            _profile.Tiers.Sort((a, b) => a.PackageLevel.CompareTo(b.PackageLevel));
            _catalogProfileActive = true;
            ApplyProfileToNegotiator(_profile);
            RebuildFrameStreamer();
        }

        private static int CountChannels(MultiStreamProfile p)
        {
            int n = 0;
            foreach (var t in p.Tiers) n += t.Channels.Count;
            return n;
        }

        public MozaTelemetryHost() : this(null, null) { }

        public MozaTelemetryHost(Action<byte[]>? send, Action<int, byte[]>? sendStream = null)
        {
            _send = send;
            _sendStream = sendStream;
            _ep01 = new SessionEndpoint(0x01, RetransmitPolicy.TierDefBlind);
            _ep02 = new SessionEndpoint(0x02, RetransmitPolicy.NoRetransmit);
            _ep03 = new SessionEndpoint(0x03, RetransmitPolicy.NoRetransmit);

            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("MozaPlugin.Data.ActionCatalog.bin"))
            {
                if (s != null)
                {
                    _actionCatalogPayload = new byte[s.Length];
                    s.Read(_actionCatalogPayload, 0, _actionCatalogPayload.Length);
                }
            }

            _configJson.WheelStateChanged += (_, state) => OnWheelStateChanged(state);
            _catalogConsumer.CatalogChanged += (_, catalog) => SetWheelCatalog(catalog);

            // ConfigJson on sess=0x09 (this wheel's firmware). Sess=0x0A carries
            // TLV records (tier-def echoes?) — left UNCLAIMED since feeding to
            // ConfigJsonClient corrupts its buffer. We still ack it via OnInboundChunk
            // generic ack path. Future work: add a sess=0x0A consumer that parses
            // the TLV format if it turns out to matter for wheel integration.
            _dispatcher.Claim(0x09, _configJson);

            // Tile-server: dual-session. Sess=0x03 (legacy) and sess=0x0B (KS Pro
            // mirror) both carry the same envelope format. TileServerStateParser
            // is tolerant (returns null on malformed blobs).
            _dispatcher.Claim(0x03, _tileServer);
            _dispatcher.Claim(0x0B, _tileServer);

            _dispatcher.Claim(0x04, _upload);
        }

        // ===== IMozaTelemetry =====

        public void Start()
        {
            lock (_stateLock)
            {
                if (_state != HostState.Disconnected) return;
                _state = HostState.Handshaking;
                _negotiator.Reset();
                _keepalive.Reset();
                // PitHouse cold-start (bridge-20260503-112940.jsonl): host opens session
                // 0x01 with seq=1 then 0x02 with seq=2. Tier-def follows on 0x01 from
                // seq=2 onward. Wheel responded with full catalog (URLs ABSActive..TCActive
                // and tyre temps) on b2h 0x01 within 15ms of the open. Starting from 0
                // (our previous behavior) gave seq=0 opens which the wheel ignored — no
                // catalog announcement, watchdog reset.
                _ep01.ResetSeq(1);
                _ep02.ResetSeq(1);
                _ep03.ResetSeq(1);
                _framesSent = 0;
                _lastKeepaliveTicks = 0;
                _keepaliveBaseline = false;
                _last28xTicks = 0;
                _poll28xBaseline = false;
                // Start keepalive seq AFTER the hardcoded open-request seq (0x000B = 11).
                // BuildConfigJsonOpenRequest uses seq=11; if keepalive resets to 1, the
                // wheel acks seq=11 (largest seen) and ignores subsequent seq=2,3,4...
                // because they're behind. Starting at 11 means the first keepalive
                // pre-increments to 12 — strictly increasing, wheel accepts.
                _session09KeepaliveSeq = 11;
                _lastSession09KeepaliveTicks = 0;
                _session09KeepaliveBaseline = false;
                _lastSession03KeepaliveTicks = 0;
                _session03KeepaliveBaseline = false;
                _configJson09ReplySent = false;
                _configJsonFirstChunkTicks = 0;
                _configJsonLastChunkTicks = 0;
                _configJsonRetries = 0;
                _lastCatalogUpdateTicks = 0;
                _lastHandbrakePresenceTicks = 0;
                _lastHandbrakeOutputTicks = 0;
                _lastPedalOutTicks = 0;
                _lastLedGroup1PollTicks = 0;
                _lastLedGroup2PollTicks = 0;
                _lastDisplayConfigTicks = 0;
                _displayConfigBaseline = false;
                _channelConfigSent = false;
                _claimedPorts.Clear();
                _sess01Opened = false;
                _wheelActivitySeen = false;
            }
            // Reclaim host-managed sessions (0x01..0x03) from any stale state left by a
            // prior SimHub session. v1's ProbeAndOpenSessions sends SendSessionClose on
            // each before opening, with a 100ms settle. Wheel silently ignores closes
            // for sessions that aren't currently open, so this is safe always.
            EmitSessionClose(0x01);
            EmitSessionClose(0x02);
            EmitSessionClose(0x03);
            System.Threading.Thread.Sleep(100);

            // Open all three host sessions immediately with low fixed ports, matching
            // v1 (TelemetrySender.cs:1055) and PitHouse (bridge-20260503-112940: port=1
            // for 0x01, port=2 for 0x02). The previous deferred-open strategy allocated
            // port=10 for sess=0x01 because it waited for wheel port claims — but
            // bridge-20260430-210453 shows PitHouse itself fails with port=9 and falls
            // back to port=1. Wheel firmware expects low ports for host sessions.
            EmitOpenOnSession(_ep01, 1);
            EmitOpenOnSession(_ep02, 2);

            // Emit the cold-start init sequence on session 0x02 (kind=2, 7, 11).
            // kind=8 (channel catalog) is dashboard-scoped and must be built dynamically;
            // the static ChannelCatalog.bin is from a different dashboard and corrupts state.
            int tz = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now.LocalDateTime).TotalSeconds;
            var initRecords = _handshake.BuildInitSequence(DateTimeOffset.UtcNow, tz,
                channelCatalogZlib: null,
                actionCatalogZlib: _actionCatalogPayload);
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: Start() emitting {initRecords.Count} init records on session 0x02");
            foreach (var rec in initRecords)
                EmitOnSession02(rec.ToBytes());

            EmitOpenOnSession(_ep03, 3);

            // Push the empty-state tile-server blob on sess=03 (host→wheel only). Mirrors
            // Telemetry/TelemetrySender.SendTileServerState which is called in v1's cold-
            // start (line 682). Verified empirically 2026-05-05: v1 first-launch widget
            // rendering works on the user's RS21-W17 (firmware era 5 / Type02), v2 first-
            // launch does not — and the only meaningful cold-start delta v2 vs v1 is this
            // missing push. The TileServerOp.cs comment "2026-04+ keepalive-only" was
            // documented but contradicted by working v1 behaviour on the same firmware.
            byte[] tileServerBlob = _tileServer.BuildOutboundState();
            EmitOnSession03(tileServerBlob);
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: tile-server empty-state push on sess=03 — {tileServerBlob.Length}B");

            // Prime session 0x09 (configJson channel). Two-step process: empty data chunk
            // first, then the configJson open-request with port-9-specific magic. Without
            // these, post-2026-04 CSP firmware never opens 0x09 → diagnostics tab empty.
            _send?.Invoke(BuildSessionPrime(0x09, 0x0001));
            _send?.Invoke(BuildConfigJsonOpenRequest(0x09, 0x000B));
            global::MozaPlugin.MozaLog.Info(
                "[Moza] Telemetry2 host: primed session 0x09 (configJson) + configJson open request");

            // Emit initial display-config burst (7c:27 + 7c:23 per page). Tells the wheel
            // which dashboard pages are active. PitHouse cold-start fires this immediately
            // after the init records — without it the wheel's display pipeline rejects
            // value-frame data even when tier-def channel idx values are correct.
            EmitDisplayConfigBurst();
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: display-config burst — {_displayConfigPageCount} page(s)");

            lock (_stateLock)
            {
                _sess01Opened = true;
                _sess02OpenedTicks = System.DateTime.UtcNow.Ticks;
                if (_state == HostState.Handshaking) _state = HostState.WaitCatalog;
            }
        }

        // Driven from Tick(). Opens sess=01 once one of the conditions holds:
        //   - We've seen any wheel b2h activity (type=0x81 OR type=0x01) AND have waited
        //     a minimum quiescence window (so multiple wheel opens can settle their port
        //     allocation before we choose ours).
        //   - The max-wait timeout has elapsed (wheel may be silent on this firmware
        //     era — fall back to AllocateHostPort with whatever's claimed so far).
        private void TickColdStartOpens(long nowTicks)
        {
            bool needSess01; long openedTicks; bool wheelSeen;
            lock (_stateLock)
            {
                needSess01 = !_sess01Opened && _state != HostState.Disconnected;
                openedTicks = _sess02OpenedTicks;
                wheelSeen = _wheelActivitySeen;
            }
            if (!needSess01) return;
            // _sess02OpenedTicks=0 means Start() hasn't completed yet (race with the
            // first Tick from DataUpdate). Skip this Tick — the timer is uninitialised.
            if (openedTicks == 0) return;

            long elapsed = nowTicks - openedTicks;
            bool minWaited = elapsed >= Sess01OpenMinWaitTicks;
            bool maxWaited = elapsed >= Sess01OpenMaxWaitTicks;
            if (!(maxWaited || (wheelSeen && minWaited))) return;

            ushort port = AllocateHostPort(_ep01.Session);
            EmitOpenOnSession(_ep01, port);
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: cold-start sess=01 opened (port={port}, " +
                $"reason={(maxWaited ? "max-wait" : "wheel-active")}, " +
                $"elapsed={elapsed / System.TimeSpan.TicksPerMillisecond}ms, " +
                $"claimed=[{string.Join(",", _claimedPorts)}])");

            lock (_stateLock) _sess01Opened = true;
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                _state = HostState.Disconnected;
                _negotiator.ResetAll();
                _frameStreamer = null;
                _latestSnapshot = null;
                _wheelConfigJsonList = null;
                _catalogReceivedTicks = 0;
                _configJsonFirstChunkTicks = 0;
                _configJsonLastChunkTicks = 0;
                _configJsonRetries = 0;
                _configJson.Reset();
                _catalogProfileActive = false;
                _catalogRebuildPending = false;
                _latestCatalogSnapshot = null;
                _retransmitChunks = null;
                _retransmitRoundsRemaining = 0;
                _retransmitLastTicks = 0;
                _displayConfigFrames = null;
                _displayConfigPageCount = 0;
                _channelConfigSent = false;
                _sess01Opened = false;
                _sequenceCounter = 0;
                _configJson09ReplySent = false;
            }
        }

        public void UpdateGameData(StatusDataBase? data)
        {
            _latestSnapshot = data;
        }

        public void SetGameRunning(bool running)
        {
            lock (_stateLock) _gameRunning = running;
        }

        public void SendDashboardSwitch(uint slotIndex)
        {
            // Backwards-compatible entry: kicks off the renegotiate state machine
            // without supplying a new profile. The state machine still uses the
            // currently-loaded _profile; callers that need to atomically swap the
            // profile alongside the switch should use SwitchToProfile.
            SwitchToProfile(slotIndex, null);
        }

        // Atomically: stage the new profile (if supplied), emit FF kind=4 on session 02,
        // and enter the renegotiate state machine. The pending profile is committed only
        // after the wheel has accepted the switch (via b2h kind=4 echo) AND its
        // post-switch state push has settled (200ms b2h quiescence). This deferral
        // matches PitHouse's observed switch timing — synchronous tier-def re-emission
        // races the wheel's internal transition and leaves it in a state where chunks
        // ack but rendering doesn't follow.
        public void SwitchToProfile(uint slotIndex, MultiStreamProfile? newProfile)
        {
            var rec = DashboardSwitchOp.Build(slotIndex);
            long nowTicks = System.DateTime.UtcNow.Ticks;
            lock (_stateLock)
            {
                _pendingSwitchSlot = slotIndex;
                _pendingSwitchProfile = newProfile;
                _switchState = SwitchState.AwaitingEcho;
                _switchStateEnteredTicks = nowTicks;
                if (_state == HostState.Ready) _state = HostState.Switching;
            }
            EmitOnSession02(rec.ToBytes());
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: dashboard switch FF kind=4 slot={slotIndex} " +
                $"on session 0x02 (state machine armed, pendingProfile={(newProfile?.Name ?? "<none>")})");
        }

        // Drive the renegotiate state machine. Called once per Tick at the top, before
        // the negotiator drains. Pure event-driven — no Thread.Sleep. Transitions:
        //   AwaitingEcho       → AwaitingQuiescence on b2h kind=4 echo (or 1.5s timeout)
        //   AwaitingQuiescence → Idle when b2h has been silent for 200ms (or 1.5s timeout)
        //   any                → Idle on watchdog timeout (3s)
        private void TickSwitchStateMachine(long nowTicks)
        {
            SwitchState s; long entered, lastB2h;
            MultiStreamProfile? pending;
            lock (_stateLock)
            {
                s = _switchState;
                entered = _switchStateEnteredTicks;
                lastB2h = _lastB2hChunkTicks;
                pending = _pendingSwitchProfile;
            }
            if (s == SwitchState.Idle) return;

            long inState = nowTicks - entered;
            if (inState >= SwitchWatchdogTicks)
            {
                global::MozaPlugin.MozaLog.Warn(
                    $"[Moza] Telemetry2 host: switch watchdog timeout at state={s} after " +
                    $"{inState / System.TimeSpan.TicksPerMillisecond}ms — resetting");
                ResetSwitchState();
                return;
            }

            if (s == SwitchState.AwaitingEcho)
            {
                if (inState >= SwitchEchoTimeoutTicks)
                {
                    global::MozaPlugin.MozaLog.Warn(
                        "[Moza] Telemetry2 host: switch echo timeout — proceeding without confirmation");
                    EnterAwaitingQuiescence(nowTicks);
                }
                return;
            }

            // AwaitingQuiescence: advance when b2h has been silent for QuiescenceTicks
            // AND we've been in this state at least MinQuiescenceWaitTicks. The minimum
            // wait prevents premature advance when the wheel hadn't started its
            // post-switch state push yet at the moment we entered the state.
            long sinceB2h = nowTicks - lastB2h;
            bool quiescent = sinceB2h >= SwitchQuiescenceTicks;
            bool minWaited = inState >= SwitchMinQuiescenceWaitTicks;
            bool maxedOut = inState >= SwitchQuiescenceMaxTicks;
            if ((quiescent && minWaited) || maxedOut)
            {
                global::MozaPlugin.MozaLog.Info(
                    $"[Moza] Telemetry2 host: switch quiescence reached " +
                    $"(inState={inState / System.TimeSpan.TicksPerMillisecond}ms, " +
                    $"sinceB2h={sinceB2h / System.TimeSpan.TicksPerMillisecond}ms, " +
                    $"reason={(maxedOut ? "timeout" : "silence")}) — applying pending switch");
                ApplyPendingSwitch(pending);
            }
        }

        // Echo received (or echo timeout). Fire the early display-config burst now —
        // mirrors PitHouse's grp40/cmd27 burst ~150ms after kind=4 echo. This keeps the
        // wheel display alive while the wheel pushes its post-switch state.
        private void EnterAwaitingQuiescence(long nowTicks)
        {
            EmitDisplayConfigBurst();
            lock (_stateLock)
            {
                _switchState = SwitchState.AwaitingQuiescence;
                _switchStateEnteredTicks = nowTicks;
                // Reset the quiescence baseline so the 200ms wait starts now, not
                // earlier (any inbound chunks during AwaitingEcho shouldn't count).
                _lastB2hChunkTicks = nowTicks;
            }
        }

        // Wheel's post-switch state push has settled. Commit the pending profile (if
        // any), invalidate channel-config + display-config caches so they re-fire for
        // the new dash, and queue a fresh tier-def via the negotiator. The next Tick
        // drains the negotiator → EmitOnSession01 → SubscriptionGen bumps.
        private void ApplyPendingSwitch(MultiStreamProfile? pending)
        {
            if (_catalogProfileActive)
            {
                // After a switch the wheel re-announces its catalog with the new
                // dashboard's channels. Allow the settle-then-rebuild flow to pick
                // up the new catalog and rebuild the profile.
                _catalogProfileActive = false;
                _catalogRebuildPending = true;
            }
            else if (pending != null)
            {
                _profile = pending;
                _profile.Tiers.Sort((a, b) => a.PackageLevel.CompareTo(b.PackageLevel));
                ApplyProfileToNegotiator(_profile!);
                RebuildFrameStreamer();
            }
            else if (_profile != null)
            {
                ApplyProfileToNegotiator(_profile);
            }
            _channelConfigSent = false;
            _displayConfigFrames = null;
            _displayConfigPageCount = 0;
            ResetSwitchState();
        }

        private void ResetSwitchState()
        {
            lock (_stateLock)
            {
                _switchState = SwitchState.Idle;
                _pendingSwitchProfile = null;
            }
        }

        // Scan payload for an FF kind=4 record and extract its slot field. Used by
        // OnInboundChunk to detect the wheel's b2h echo of our outbound switch. The
        // FF sentinel can appear anywhere in the chunk payload; FfRecord.ParseAt
        // validates structure + CRC so false positives are negligible.
        private static bool TryDetectKind4Echo(byte[] payload, out uint slotIndex)
        {
            slotIndex = 0;
            if (payload == null || payload.Length < 21) return false;
            for (int i = 0; i + 13 <= payload.Length; i++)
            {
                if (payload[i] != 0xFF) continue;
                var (rec, consumed, crcOk) = global::MozaPlugin.Telemetry2.Wire.FfRecord.ParseAt(payload, i);
                if (consumed == 0 || !crcOk) continue;
                if (rec.Kind == 4 && rec.Value.Length >= 4)
                {
                    slotIndex = (uint)(rec.Value[0]
                                     | (rec.Value[1] << 8)
                                     | (rec.Value[2] << 16)
                                     | (rec.Value[3] << 24));
                    return true;
                }
                // Skip past this FF record and keep scanning — payload may carry more.
                i += consumed - 1;
            }
            return false;
        }

        public MultiStreamProfile? Profile
        {
            get => _profile;
            set
            {
                if (value != null)
                {
                    // When the catalog-driven profile is active, the negotiator and
                    // frame streamer already use catalog channels (correct idx values).
                    // An mzdash-sourced profile would overwrite those with URLs that
                    // don't resolve against the wheel catalog. Store the mzdash profile
                    // for reference (ActiveProfileName, page count) but don't push it
                    // to the negotiator or frame streamer.
                    if (_catalogProfileActive)
                    {
                        global::MozaPlugin.MozaLog.Debug(
                            $"[Moza] Telemetry2 host: Profile set ignored for negotiator — " +
                            $"catalog-driven profile active (incoming={value.Name})");
                        _channelConfigSent = false;
                        _displayConfigFrames = null;
                        _displayConfigPageCount = 0;
                        return;
                    }
                    _profile = value;
                    _profile.Tiers.Sort((a, b) => a.PackageLevel.CompareTo(b.PackageLevel));
                    ApplyProfileToNegotiator(_profile);
                    RebuildFrameStreamer();
                    _channelConfigSent = false;
                    _displayConfigFrames = null;
                    _displayConfigPageCount = 0;
                }
                else
                {
                    _profile = null;
                    _frameStreamer = null;
                }
            }
        }

        public Func<string, double>? PropertyResolver
        {
            get => _propertyResolver;
            set
            {
                _propertyResolver = value;
                if (_profile != null) RebuildFrameStreamer();
            }
        }

        // Delegate that builds a MultiStreamProfile from the wheel's announced catalog
        // URLs. MozaPlugin wires this to DashboardProfileStore.BuildProfileFromCatalog
        // so the tier-def subscribes to channels the wheel actually knows about (correct
        // idx values) rather than mzdash channels that may not exist in the catalog.
        public Func<IReadOnlyList<string>, string, MultiStreamProfile>? CatalogProfileBuilder
        {
            get => _catalogProfileBuilder;
            set => _catalogProfileBuilder = value;
        }

        // Build the FrameStreamerOp from the full profile. The wheel catalog is used
        // only for URL→idx resolution at tier-def emission time (channels with URLs
        // not in the announced catalog resolve to idx=0 sentinel). Channels are NOT
        // filtered out — the wheel has its own copy of the user's mzdash file and
        // resolves URLs from there. Mirrors Telemetry/TierDefinitionBuilder.cs:272
        // where unknown URLs default to chIndex=0 without dropping the channel.
        private void RebuildFrameStreamer()
        {
            if (_profile == null) { _frameStreamer = null; return; }
            _frameStreamer = new FrameStreamerOp(_profile, _negotiator,
                _propertyResolver, Type02NConvention);
        }

        public byte[]? MzdashContent
        {
            get => _mzdashContent;
            set => _mzdashContent = value;
        }

        public string MzdashName
        {
            get => _mzdashName;
            set => _mzdashName = value ?? "";
        }

        public int FramesSent => _framesSent;

        public bool Enabled
        {
            get { lock (_stateLock) return _state != HostState.Disconnected; }
        }

        // Bumped by the renegotiate state machine when it reaches Settled (cold-start
        // handshake or warm dashboard switch). Volatile reads on the auto-test side.
        private int _subscriptionGen;
        public int SubscriptionGen => System.Threading.Volatile.Read(ref _subscriptionGen);

        public string? ActiveProfileName => _profile?.Name;

        public System.Collections.Generic.IReadOnlyList<string>? WheelReportedDashboards
            => _configJson.LastState?.ConfigJsonList;

        // TestMode = synthetic triangle-wave value frames. When true, Tick() emits
        // BuildTestFrames() instead of BuildFrames(snapshot). Used for live verification
        // of wheel-side rendering without a game.
        private volatile bool _testMode;
        public bool TestMode
        {
            get => _testMode;
            set
            {
                _testMode = value;
                global::MozaPlugin.MozaLog.Debug(
                    $"[Moza] Telemetry2 host: TestMode={value}");
            }
        }

        // v2 host doesn't enforce a host-silence gate the way v1 does (its
        // restart path is owned by the renegotiate state machine). Always
        // returns false — UI just won't disable controls when v2 is active.
        public bool IsInSilenceCooldown => false;

        // Wire-trace phase marker (see IMozaTelemetry). Frame:
        //   7e 03 55 55 4d 4b [phaseId] [chk]
        // grp=0x55 dev=0x55 unused by real wheel commands; frame lands in
        // SerialTrafficCapture so v1↔v2 wire-diff can align by phase boundary.
        public void SendPhaseMarker(byte phaseId)
        {
            byte[] f = new byte[] { 0x7e, 0x03, 0x55, 0x55, 0x4d, 0x4b, phaseId, 0x00 };
            f[7] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(f, f.Length - 1);
            _send?.Invoke(f);
            global::MozaPlugin.MozaLog.Debug(
                $"[Moza] Telemetry2 host: phase-marker phaseId=0x{phaseId:X2} ({phaseId})");
        }

        public void Dispose()
        {
            Stop();
            _dispatcher.Reset();
        }

        // ===== Internal coordination =====

        // Translate a plugin-side MultiStreamProfile (mzdash + Data/Telemetry.json) into
        // the negotiator's DashboardSpec form. The URL→idx catalog comes from the wheel's
        // b2h catalog announcement (via SetWheelCatalog → _negotiator.SetCatalog), NOT
        // from the profile. The mzdash defines WHAT channels to subscribe; the wheel
        // catalog defines WHERE those channels sit in the wheel's index space.
        private void ApplyProfileToNegotiator(MultiStreamProfile profile)
        {
            var sorted = new List<DashboardProfile>(profile.Tiers);
            sorted.Sort((a, b) => a.PackageLevel.CompareTo(b.PackageLevel));
            var subTiers = new List<TierDefNegotiator.SubTier>(sorted.Count);
            foreach (var tier in sorted)
            {
                var channels = new List<TierDefNegotiator.ChannelSpec>(tier.Channels.Count);
                foreach (var ch in tier.Channels)
                {
                    channels.Add(new TierDefNegotiator.ChannelSpec(
                        url: ch.Url ?? "",
                        compressionName: ch.Compression ?? "uint16_t",
                        bitWidth: ch.BitWidth));
                }
                subTiers.Add(new TierDefNegotiator.SubTier(channels));
            }

            string name = profile.Tiers.Count > 0 ? "(profile)" : "";
            _negotiator.SetActiveDashboard(new TierDefNegotiator.DashboardSpec(name, subTiers));
        }

        private void OnWheelStateChanged(WheelDashboardState state)
        {
            // Wheel reports a change to enabled dashboards / configJsonList.
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: WheelDashboardState received — TitleId={state.TitleId} " +
                $"enabled={state.EnabledDashboards.Count} disabled={state.DisabledDashboards.Count}");

            // Send the configJson() reply once per cold-start. v1 confirmed-working
            // step (Telemetry/TelemetrySender.MaybeSendConfigJsonReply). Without
            // this the wheel doesn't fully integrate: no kind=14 heartbeats from
            // wheel, no kind=4 echoes, dashboards don't render — the symptoms we've
            // been chasing for the past several iterations.
            SendConfigJson09ReplyOnce(state);

            // Record the wheel's dashboard list so we can resolve names → slots for
            // user-initiated switches. The initial dashboard switch is NOT fired here —
            // PitHouse 112940 sends zero kind=4 switches during cold-start. The wheel
            // already has a dashboard loaded; firing a switch during the first broadcast
            // cycle resets the negotiator and corrupts tier-def emission. User-initiated
            // switches go through SwitchToProfile which enters the state machine properly.
            if (state.ConfigJsonList != null && state.ConfigJsonList.Count > 0)
            {
                lock (_stateLock)
                {
                    _wheelConfigJsonList = state.ConfigJsonList;
                }
            }
        }

        // Send the host's configJson() reply on session 0x09 once per cold-start.
        // Idempotent — guarded by _configJson09ReplySent. Returns silently if state
        // has no ConfigJsonList. Mirrors v1 Telemetry/TelemetrySender.MaybeSendConfigJsonReply
        // (line 1830). Reply payload: {"configJson()": {dashboardRootDir, dashboards:[...],
        // ...}, "id": 11}, zlib-compressed and wrapped in the standard 9-byte sess=09
        // envelope (built by ConfigJsonClient.BuildConfigJsonReply). Chunked over session
        // 0x09 using the existing keepalive seq counter so subsequent keepalives don't
        // collide.
        private void SendConfigJson09ReplyOnce(WheelDashboardState state)
        {
            lock (_stateLock)
            {
                if (_configJson09ReplySent) return;
                _configJson09ReplySent = true;
            }
            var dashboards = state.ConfigJsonList;
            if (dashboards == null || dashboards.Count == 0) return;

            byte[] reply = global::MozaPlugin.Telemetry.ConfigJsonClient
                .BuildConfigJsonReply(dashboards);
            ushort startSeq = _session09KeepaliveSeq;
            var (chunks, nextSeq) = global::MozaPlugin.Telemetry2.Wire.SessionChunk
                .ChunkMessage(reply, 0x09, startSeq);
            int sent = 0;
            foreach (var chunk in chunks)
            {
                byte[] body = chunk.ToBodyBytes();
                byte[] frame = global::MozaPlugin.Telemetry2.Wire.MozaFrame.Wrap(body);
                _send?.Invoke(frame);
                sent++;
            }
            _session09KeepaliveSeq = nextSeq;
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: sent configJson() reply on sess=09 — " +
                $"{dashboards.Count} dashboards, {sent} chunks (seq {startSeq}..{nextSeq - 1})");
        }

        // Wheel's configJsonList (dashboard names from sess=0x09 state push). Used to
        // resolve dashboard name → slot index for user-initiated switches.
        private IReadOnlyList<string>? _wheelConfigJsonList;

        // Inbound entry from the MozaPlugin's frame router. Dispatches to the right
        // consumer based on session byte. Catalog announcements (sessions 0x01 + 0x02
        // tag=0x04 records) feed the CatalogConsumer; everything else routes through
        // the SessionDispatcher.
        public void OnInboundChunk(byte session, byte type, int seq, byte[] payload)
        {
            // Track inbound chunk count per session for diagnostics tab + b2h activity
            // timestamp for the switch state machine's quiescence detection.
            long nowTicks = System.DateTime.UtcNow.Ticks;
            lock (_stateLock)
            {
                _inboundChunksBySession.TryGetValue(session, out int n);
                _inboundChunksBySession[session] = n + 1;
                _lastB2hChunkTicks = nowTicks;
            }

            // Track wheel-claimed ports for the host's port allocator. Type=0x81 open
            // payload starts with [port_lo][port_hi][port_lo][port_hi][fd][02] — the
            // first u16 LE is the wheel's chosen port for the session. Adding it to
            // _claimedPorts ensures AllocateHostPort skips ports the wheel owns when
            // we open our own sessions later.
            if (type == 0x81 && payload != null && payload.Length >= 2)
            {
                ushort wheelPort = (ushort)(payload[0] | (payload[1] << 8));
                lock (_stateLock)
                {
                    _claimedPorts.Add(wheelPort);
                    _wheelActivitySeen = true;
                }
            }
            else if (type == 0x01 || type == 0x00)
            {
                lock (_stateLock) _wheelActivitySeen = true;
            }

            // Switch state machine: detect b2h FF kind=4 echo on session 02 while
            // AwaitingEcho. The wheel's echo proves it accepted the switch and is
            // about to push its post-switch state.
            if (session == 0x02 && type == 0x01 && payload != null && payload.Length >= 16
                && _switchState == SwitchState.AwaitingEcho)
            {
                if (TryDetectKind4Echo(payload, out uint echoSlot)
                    && echoSlot == _pendingSwitchSlot)
                {
                    global::MozaPlugin.MozaLog.Info(
                        $"[Moza] Telemetry2 host: switch echo received slot={echoSlot} " +
                        $"({(nowTicks - _switchStateEnteredTicks) / System.TimeSpan.TicksPerMillisecond}ms after emit)");
                    EnterAwaitingQuiescence(nowTicks);
                }
            }
            // Capture session 0x02 chunks within 5s window after last subscription emit.
            if (session == 0x02 && type == 0x01 && payload != null && payload.Length > 0)
            {
                long deadline;
                lock (_stateLock) deadline = _subscriptionResponseDeadlineTicks;
                if (DateTime.UtcNow.Ticks <= deadline)
                {
                    lock (_subscriptionResponseChunks)
                    {
                        if (_subscriptionResponseChunks.Count < 256)
                            _subscriptionResponseChunks.Add((byte[])payload.Clone());
                    }
                }
            }
            if (type == 0x01)
            {
                SendSessionAck(session, (ushort)seq);
                _catalogConsumer.OnData(session, seq, payload!);
                _dispatcher.DispatchData(session, seq, payload!);
                if (session == 0x09 && _configJson.LastState == null)
                {
                    lock (_stateLock)
                    {
                        if (_configJsonFirstChunkTicks == 0)
                            _configJsonFirstChunkTicks = nowTicks;
                        _configJsonLastChunkTicks = nowTicks;
                    }
                }
            }
            else if (type == 0x81)
            {
                SendSessionAck(session, (ushort)seq);
                _dispatcher.DispatchOpen(session, seq);
            }
            else if (type == 0x00)
            {
                _dispatcher.DispatchClose(session, seq);
            }
        }

        // Send type=0x00 end-marker on the given session. Reclaims a stale session
        // left open by a prior SimHub crash/kill. Wire format: 7E 06 43 17 7C 00 [ses]
        // 00 00 00 [chk]. If the session is already closed, the wheel silently ignores
        // this frame. Mirrors Telemetry/TelemetrySender.SendSessionClose at line 2942.
        private void SendSessionClose(byte session)
        {
            var frame = new byte[]
            {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x06,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session, 0x00,
                0x00, 0x00,
                0x00,
            };
            frame[frame.Length - 1] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            _send?.Invoke(frame);
        }

        // Emit fc:00 ack: 7E 05 43 17 FC 00 [ses] [ack_lo] [ack_hi] [chk]
        // Mirrors Telemetry/TelemetrySender.SendSessionAck (line 2978).
        private void SendSessionAck(byte session, ushort ackSeq)
        {
            var frame = new byte[]
            {
                global::MozaPlugin.Protocol.MozaProtocol.MessageStart, 0x05,
                global::MozaPlugin.Protocol.MozaProtocol.TelemetrySendGroup,
                global::MozaPlugin.Protocol.MozaProtocol.DeviceWheel,
                0xFC, 0x00,
                session,
                (byte)(ackSeq & 0xFF),
                (byte)(ackSeq >> 8),
                0x00,
            };
            frame[9] = global::MozaPlugin.Protocol.MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            _send?.Invoke(frame);
        }

        // Test-only helper: feed a wheel catalog directly to the negotiator. Production
        // consumers will pull this from the b2h session 0x02 catalog reassembler in Phase 5b.
        internal void SetWheelCatalogForTest(IReadOnlyList<string> urlsByIndex)
        {
            _negotiator.SetCatalog(urlsByIndex);
            lock (_stateLock)
            {
                _catalogReceivedTicks = System.DateTime.UtcNow.Ticks;
                if (_state == HostState.WaitCatalog) _state = HostState.Ready;
            }
        }

        // Drive the host's tick. Caller invokes once per SimHub data-update cycle
        // (or any monotonic timer). Pumps pending tier-def emissions through session
        // 0x01 + frame bursts on raw 7c:43 + heartbeat cadence on session 0x02.
        public void Tick(long nowTicks)
        {
            // Don't run Tick before Start. Pre-Start FrameStreamer increments FramesSent,
            // which trips MozaPlugin.StartTelemetryIfReady's FramesSent>0 gate and prevents
            // Start from ever firing — leaving the wheel without init records.
            lock (_stateLock)
            {
                if (_state == HostState.Disconnected) return;
            }

            // 0a. Drive the deferred sess-01 cold-start open. Sess=01 carries tier-def
            //     and value frames — opening it before the wheel claims its ports causes
            //     the wheel to tear it down on 2026-04+ firmware (b2h type=0x00 every ~1s).
            TickColdStartOpens(nowTicks);

            // 0b. Drive the renegotiate state machine. Transitions may queue a
            //     tier-def emission via the negotiator; the next step drains it.
            TickSwitchStateMachine(nowTicks);

            // 1. Drain any pending tier-def emission from the negotiator. Each new
            //    emission resets the retransmit window. If no fresh emission, fire
            //    the next blind-retransmit round if its 200ms interval has elapsed.
            //    Gated on sess=01 being opened — emitting tier-def chunks before the
            //    open frame produces orphan-seq chunks the wheel rejects. Heartbeat
            //    + dash-keepalive cadence (later steps) still flow during the wait.
            bool sess01Ready;
            long catalogRx;
            long openedAt;
            lock (_stateLock)
            {
                sess01Ready = _sess01Opened;
                catalogRx = _catalogReceivedTicks;
                openedAt = _sess02OpenedTicks;
            }

            // Catalog timeout fallback: if the wheel hasn't announced its catalog
            // after CatalogFallbackTimeoutTicks, fall back to profile-derived
            // alphabetical indices so the tier-def can still emit.
            if (sess01Ready && catalogRx == 0 && openedAt > 0
                && nowTicks - openedAt >= CatalogFallbackTimeoutTicks
                && _profile != null)
            {
                global::MozaPlugin.MozaLog.Info(
                    "[Moza] Telemetry2 host: catalog timeout — falling back to profile-derived alphabetical indices");
                var allUrls = new List<string>();
                foreach (var tier in _profile.Tiers)
                    foreach (var ch in tier.Channels)
                    {
                        string url = ch.Url ?? "";
                        if (!string.IsNullOrEmpty(url)
                            && allUrls.FindIndex(u => string.Equals(u, url, System.StringComparison.OrdinalIgnoreCase)) < 0)
                            allUrls.Add(url);
                    }
                allUrls.Sort(System.StringComparer.OrdinalIgnoreCase);
                _negotiator.SetCatalog(allUrls);
                lock (_stateLock) _catalogReceivedTicks = nowTicks;
            }

            // Gate tier-def emission behind catalog settle: if the wheel is still
            // drip-feeding catalog entries, wait for CatalogSettleTicks of silence so
            // the first emission uses the complete catalog (correct channel indices)
            // and gets its full retransmit window (preamble included).
            bool catalogSettled = _lastCatalogUpdateTicks == 0
                || nowTicks - _lastCatalogUpdateTicks >= CatalogSettleTicks;
            if (catalogSettled && _catalogRebuildPending && _latestCatalogSnapshot != null)
            {
                _catalogRebuildPending = false;
                RebuildProfileFromCatalog(_latestCatalogSnapshot);
            }
            // D6 diagnostic: log gate values on every tick until first emission
            if (_negotiator.ActiveSubscription.TierCount == 0)
            {
                global::MozaPlugin.MozaLog.Debug(
                    $"[Moza] Telemetry2 host: tier-def gate — sess01Ready={sess01Ready} " +
                    $"catalogSettled={catalogSettled} lastCatalogUpdate={_lastCatalogUpdateTicks} " +
                    $"catalogRx={catalogRx} pending={_negotiator.PendingEmission} " +
                    $"dashboard={(_negotiator.HasDashboard ? "set" : "null")} " +
                    $"catalogSize={_negotiator.CatalogSize}");
            }
            bool retransmitDone = _retransmitChunks == null;
            byte[]? tierBytes = (sess01Ready && catalogSettled && retransmitDone)
                ? _negotiator.NextEmission() : null;
            if (tierBytes != null)
            {
                EmitOnSession01(tierBytes);
                // Mark the just-emitted round 1's timestamp so the next retransmit
                // waits the full interval instead of firing immediately.
                _retransmitLastTicks = nowTicks;
                // Channel-config burst lives alongside tier-def emission — wheel needs
                // both before any value frames render. Old pipeline fires them back-to-back
                // in ApplyTelemetrySubscription (Telemetry/TelemetrySender.cs:1249-1250).
                if (!_channelConfigSent)
                {
                    EmitChannelConfigBurst();
                    _channelConfigSent = true;
                }
            }
            else if (sess01Ready) RebroadcastSession01(nowTicks);

            // 2. Heartbeat pair on session 0x02 if cadence elapsed.
            var hb = _keepalive.Tick(nowTicks);
            foreach (var rec in hb)
                EmitOnSession02(rec.ToBytes());

            // (Removed: periodic init-record re-emit. v1 works fine with init records
            // emitted ONCE at Start() — re-emitting at 1.2s/3s cadence flooded sess=02
            // with ~41 chunks/s of action-catalog re-broadcasts. The hypothesis that
            // re-emission was needed for wheel integration was wrong; the actual
            // missing piece was the configJson() reply on sess=09.)

            // 2b. Dash-keepalive pings (group 0x43 N=1 data=0 to dev=0x14/0x15/0x17).
            // ~1.1s cadence. First Tick sets the baseline so the cadence test isn't
            // skewed by an immediate at-start emission.
            if (!_keepaliveBaseline)
            {
                _lastKeepaliveTicks = nowTicks;
                _keepaliveBaseline = true;
            }
            else if (nowTicks - _lastKeepaliveTicks >= KeepaliveIntervalTicks)
            {
                _send?.Invoke(DashKeepaliveDash);
                _send?.Invoke(DashKeepalive15);
                _send?.Invoke(DashKeepaliveWheel);
                // Group-0 presence pings to all detected device IDs (18..30). PitHouse
                // emits these in addition to dash-keepalives. Without DetectedDeviceMask
                // tracking we ping all; wheel ignores irrelevant ones.
                foreach (var ping in PresencePings)
                    _send?.Invoke(ping);
                _lastKeepaliveTicks = nowTicks;
            }

            // 2c. 28x polls (group 0x40 cmd 0x28:00 + 0x28:01) every ~4s. Wheel echoes
            // back as group 0xC0 cmd 0x28; MozaPlugin.OnMessageReceived clears
            // _wheelPollMisses on receipt. Without these polls, the watchdog resets
            // after ~165s of no acks.
            if (!_poll28xBaseline)
            {
                _last28xTicks = nowTicks;
                _poll28xBaseline = true;
            }
            else if (nowTicks - _last28xTicks >= Poll28xIntervalTicks)
            {
                _send?.Invoke(Poll28x00);
                _send?.Invoke(Poll28x01);
                _last28xTicks = nowTicks;
            }

            // 2d. Session 0x09 keepalive (~2s cadence). Empty type=0x01 data chunk on
            // session 0x09 with incrementing seq; keeps configJson session warm.
            if (!_session09KeepaliveBaseline)
            {
                _lastSession09KeepaliveTicks = nowTicks;
                _session09KeepaliveBaseline = true;
            }
            else if (nowTicks - _lastSession09KeepaliveTicks >= Session09KeepaliveIntervalTicks)
            {
                _session09KeepaliveSeq++;
                _send?.Invoke(BuildSessionPrime(0x09, _session09KeepaliveSeq));
                _lastSession09KeepaliveTicks = nowTicks;
            }

            // 2d2. Session 0x03 keepalive (~3s cadence). Empty 4-byte zero data chunk
            // — pure presence signal per docs (sess=03 is reserved-keepalive on 2026-04+).
            // Without this the wheel may consider the channel one-sided and degrade.
            if (!_session03KeepaliveBaseline)
            {
                _lastSession03KeepaliveTicks = nowTicks;
                _session03KeepaliveBaseline = true;
            }
            else if (nowTicks - _lastSession03KeepaliveTicks >= Session03KeepaliveIntervalTicks)
            {
                // Empty 4-byte zero payload per docs/protocol/sessions/lifecycle.md.
                EmitOnSession03(new byte[] { 0x00, 0x00, 0x00, 0x00 });
                _lastSession03KeepaliveTicks = nowTicks;
            }

            // 2d3. ConfigJson reassembly watchdog. If sess=0x09 data arrived but the
            // reassembler never produced a valid state (dropped chunk corrupted zlib),
            // clear the buffer and re-prime the session to trigger a fresh push.
            // Per-round backoff so a slow-firmware wheel doesn't get re-primed
            // before its current attempt has a fair chance to complete.
            long staleTimeoutTicks = ConfigJsonStaleTimeoutTicksByRound[
                Math.Min(_configJsonRetries, ConfigJsonStaleTimeoutTicksByRound.Length - 1)];
            if (_configJson.LastState == null && _configJsonLastChunkTicks > 0
                && nowTicks - _configJsonLastChunkTicks >= staleTimeoutTicks
                && _configJsonRetries < ConfigJsonMaxRetries)
            {
                _configJsonRetries++;
                _configJsonFirstChunkTicks = 0;
                _configJsonLastChunkTicks = 0;
                _configJson.Reset();
                lock (_stateLock)
                {
                    _configJson09ReplySent = false;
                }
                _send?.Invoke(BuildSessionPrime(0x09, 0x0001));
                _send?.Invoke(BuildConfigJsonOpenRequest(0x09, 0x000B));
                global::MozaPlugin.MozaLog.Info(
                    $"[Moza] Telemetry2 host: configJson reassembly stale — retry {_configJsonRetries}/{ConfigJsonMaxRetries}, re-priming sess=0x09");
            }

            // 2e. Display-config burst (7c:27 + 7c:23 per page) at ~500ms cadence.
            // PitHouse cycles through all dashboard pages each burst. This is what tells
            // the wheel which pages on the active dashboard should receive value frames.
            if (!_displayConfigBaseline)
            {
                _lastDisplayConfigTicks = nowTicks;
                _displayConfigBaseline = true;
            }
            else if (nowTicks - _lastDisplayConfigTicks >= DisplayConfigIntervalTicks)
            {
                EmitDisplayConfigBurst();
                _lastDisplayConfigTicks = nowTicks;
            }

            // 2f. Peripheral output polls (handbrake, pedals, LED state) at
            // ~1 Hz. Required for wheel idle engagement — turning off
            // entirely freezes the dashboard on last-value within ~5 min.
            if (nowTicks - _lastHandbrakePresenceTicks >= PeriphHandbrakePresenceIntervalTicks)
            {
                _send?.Invoke(PeriphHandbrakePresence);
                _lastHandbrakePresenceTicks = nowTicks;
            }
            if (nowTicks - _lastHandbrakeOutputTicks >= PeriphHandbrakeOutputIntervalTicks)
            {
                _send?.Invoke(PeriphHandbrakeOutput);
                _lastHandbrakeOutputTicks = nowTicks;
            }
            if (nowTicks - _lastPedalOutTicks >= PeriphPedalOutIntervalTicks)
            {
                _send?.Invoke(PeriphPedalThrottleOut);
                _send?.Invoke(PeriphPedalBrakeOut);
                _send?.Invoke(PeriphPedalClutchOut);
                _lastPedalOutTicks = nowTicks;
            }
            if (nowTicks - _lastLedGroup1PollTicks >= PeriphLedGroup1PollIntervalTicks)
            {
                _send?.Invoke(PeriphLedGroup1Poll);
                _lastLedGroup1PollTicks = nowTicks;
            }
            if (nowTicks - _lastLedGroup2PollTicks >= PeriphLedGroup2PollIntervalTicks)
            {
                _send?.Invoke(PeriphLedGroup2Poll);
                _lastLedGroup2PollTicks = nowTicks;
            }

            // 2g. FFB-enable + sequence-counter periodic streams (v1 parity).
            // Fire every Tick when TestMode || gameRunning, matching v1's
            // TelemetrySender.cs:2879-2884 behavior. Without these the wheel does NOT
            // animate widgets even with correctly-shaped value frames — verified
            // empirically 2026-05-05 via wire-diff between v1 (working) and v2 (static
            // widgets despite correct value-frame wire shape).
            if (_testMode || _gameRunning)
            {
                if (_sendStream != null)
                {
                    _sendStream(StreamSlotEnable, FfbEnableFrame);
                    _sendStream(StreamSlotSequence, BuildSequenceCounterFrame());
                }
                else
                {
                    _send?.Invoke(FfbEnableFrame);
                    _send?.Invoke(BuildSequenceCounterFrame());
                }
            }

            // 2h. Telemetry-mode periodic re-emit (~1Hz). v1 emits this in the slow path
            // (TelemetrySender.cs:2962-2963) so the wheel keeps "telemetry channel mode
            // = multi" set across the session.
            if (!_modePeriodicBaseline)
            {
                _lastModePeriodicTicks = nowTicks;
                _modePeriodicBaseline = true;
            }
            else if (nowTicks - _lastModePeriodicTicks >= ModePeriodicIntervalTicks)
            {
                if (_sendStream != null)
                    _sendStream(StreamSlotMode, ModeFramePeriodic);
                else
                    _send?.Invoke(ModeFramePeriodic);
                _lastModePeriodicTicks = nowTicks;
            }

            // 2i. Widget-state poll cycle at ~1 Hz. Same engagement rationale as 2f.
            if (!_widgetPollBaseline)
            {
                _lastWidgetPollTicks = nowTicks;
                _widgetPollBaseline = true;
            }
            else if (nowTicks - _lastWidgetPollTicks >= WidgetPollIntervalTicks)
            {
                byte[] poll = WidgetPollFrames[_widgetPollIndex % WidgetPollFrames.Length];
                _widgetPollIndex++;
                _send?.Invoke(poll);
                _lastWidgetPollTicks = nowTicks;
            }

            // 3. Push value frames (one per tier) using the active subscription.
            // TestMode emits a triangle-wave sweep across each channel's value range so
            // the wheel-side rendering can be verified without a game running. Mirrors
            // Telemetry/TelemetrySender.cs:2807-2808 BuildTestFrame branch.
            int frameDiagState = 0; // 0=streamer null, 1=zero frames, 2=emitted
            int frameDiagCount = 0;
            if (_frameStreamer != null)
            {
                bool useTestPattern = _testMode || _latestSnapshot == null;
                var frames = useTestPattern
                    ? _frameStreamer.BuildTestFrames()
                    : _frameStreamer.BuildFrames(_latestSnapshot);
                frameDiagCount = frames.Count;
                if (frames.Count == 0) frameDiagState = 1;
                else
                {
                    frameDiagState = 2;
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var frame = frames[i];
                        if (_sendStream != null)
                            _sendStream(StreamSlotTierDash0 + i, frame);
                        else
                            _send?.Invoke(frame);
                        System.Threading.Interlocked.Increment(ref _framesSent);
                    }
                }
            }
            // Log once per second of Tick activity. Helps diagnose "no value frames" by
            // showing whether streamer is null, returning empty, or emitting.
            if ((nowTicks - _lastFrameDiagTicks) >= System.TimeSpan.TicksPerSecond)
            {
                _lastFrameDiagTicks = nowTicks;
                var sub = _negotiator.ActiveSubscription;
                string desc = frameDiagState switch
                {
                    0 => "streamer=null",
                    1 => $"streamer.BuildFrames returned 0 (testMode={_testMode}, snapshot={(_latestSnapshot==null?"null":"set")})",
                    _ => $"emitted {frameDiagCount} frames"
                };
                global::MozaPlugin.MozaLog.Debug(
                    $"[Moza] Telemetry2 host: frame-diag {desc}; sub.TierCount={sub.TierCount} flags=[{string.Join(",", sub.FlagBytes)}] totalFramesSent={_framesSent}");
            }

            // 4. Auto-advance state once the first emission goes out.
            lock (_stateLock)
            {
                if (_state == HostState.Switching && _negotiator.ActiveSubscription.TierCount > 0)
                    _state = HostState.Ready;
            }
        }

        // Chunk a logical message through SessionEndpoint(0x01), wrap each chunk in a
        // MOZA frame, and push to the wire. Used for tier-def TLV streams. Captures the
        // chunks for blind retransmit (session 0x01 isn't FC-acked; PitHouse fires 10×).
        private void EmitOnSession01(byte[] message)
        {
            var chunks = _ep01.SendMessage(message);
            // Bump SubscriptionGen at the first emission of each tier-def. Cold-start
            // and warm switches both flow through here; the renegotiate state machine
            // gates *when* the new tier-def gets queued, so this single bump-site is
            // sufficient for both. RebroadcastSession01 (blind retransmit) does NOT
            // bump — it's the same logical emission.
            int gen = System.Threading.Interlocked.Increment(ref _subscriptionGen);
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: tier-def emit on session 0x01 — {message.Length}B → " +
                $"{chunks.Count} chunk(s), SubscriptionGen={gen}");
            foreach (var chunk in chunks)
            {
                byte[] body = chunk.ToBodyBytes();
                byte[] frame = MozaFrame.Wrap(body);
                _send?.Invoke(frame);
            }
            lock (_stateLock)
            {
                _outboundChunksBySession.TryGetValue(0x01, out int n);
                _outboundChunksBySession[0x01] = n + chunks.Count;
                // Capture for Diagnostics tab.
                _lastSubscriptionBytes = (byte[])message.Clone();
                _lastSubscriptionAt = DateTime.Now;
                // Open 5s capture window for inbound s02 chunks.
                _subscriptionResponseDeadlineTicks = DateTime.UtcNow.Ticks + 5L * TimeSpan.TicksPerSecond;
            }
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            // Arm blind retransmit: 9 more rounds (round 1 was the immediate emit above).
            _retransmitChunks = new List<SessionChunk>(chunks);
            _retransmitRoundsRemaining = RetransmitRounds - 1;
            _retransmitLastTicks = DateTime.UtcNow.Ticks;
        }

        // Re-emit the captured chunks at the same seqs (no endpoint state mutation).
        // Early-exits as soon as the wheel announces fresh catalog data (signal that
        // it's absorbing the tier-def). Per-round backoff so a stuck round doesn't
        // hammer the link at fixed cadence.
        private void RebroadcastSession01(long nowTicks)
        {
            if (_retransmitChunks == null || _retransmitRoundsRemaining <= 0) return;
            // Round count we've already completed (not counting initial emit at line 1956).
            int roundsDone = (RetransmitRounds - 1) - _retransmitRoundsRemaining;
            // Early-exit: the wheel pushed fresh catalog data since our last emit.
            if (roundsDone > 0 && _lastCatalogUpdateTicks > _retransmitLastTicks)
            {
                global::MozaPlugin.MozaLog.Debug(
                    $"[Moza] V2 blind retransmit early-exit after {roundsDone} extra rounds (catalog activity detected)");
                _retransmitChunks = null;
                _retransmitRoundsRemaining = 0;
                return;
            }
            var schedule = global::MozaPlugin.Protocol.RetryBackoff.TierDefBlindMs;
            long gateTicks = schedule[Math.Min(roundsDone, schedule.Length - 1)] * System.TimeSpan.TicksPerMillisecond;
            if (nowTicks - _retransmitLastTicks < gateTicks)
                return;
            foreach (var chunk in _retransmitChunks)
            {
                byte[] body = chunk.ToBodyBytes();
                byte[] frame = MozaFrame.Wrap(body);
                _send?.Invoke(frame);
            }
            _retransmitLastTicks = nowTicks;
            _retransmitRoundsRemaining--;
            if (_retransmitRoundsRemaining <= 0) _retransmitChunks = null;
        }

        // Emit a type=0x81 (device-init / session-open) chunk on the given endpoint.
        //
        // PitHouse format (bridge-20260503-113616.jsonl session 0x01 first frame):
        //   chunk body = [7C 00] [ses] [81] [port:u16LE] [port:u16LE] [FD 02]   (10 bytes)
        //
        // The port is emitted TWICE (header + payload) plus a constant `FD 02` trailer.
        // No CRC32 trailer (unlike type=0x01 data chunks).
        //
        // Port semantics (per docs/protocol/sessions/lifecycle.md):
        //   - The "seq" field of an open frame IS the port number, not a sequence number.
        //     The data-frame seq counter starts at port+1 after open.
        //   - Port is a globally-unique identifier across host AND wheel sessions.
        //     For 2025-11 firmware port == session_byte everywhere; for 2026-04+ the
        //     host must allocate ports past the wheel-claimed range (PitHouse uses
        //     port=9 for sess=01 when wheel has claimed ports 3, 5, 7, 8).
        //   - `AllocateHostPort()` honours the global counter rule when called.
        // Emit a session-close (type=0x00) frame on the given session number.
        // Mirrors Telemetry/TelemetrySender.SendSessionClose. The wheel ignores closes
        // for sessions that aren't currently open, so this is always safe. Used at
        // cold-start to clean up stale wheel-side state from a prior connection.
        private void EmitSessionClose(byte session)
        {
            byte[] body = new byte[]
            {
                0x7C, 0x00,
                session, 0x00,         // type=0x00 (end marker / close)
                0x00, 0x00,            // ack_seq = 0 (LE)
            };
            byte[] frame = global::MozaPlugin.Telemetry2.Wire.MozaFrame.Wrap(body);
            global::MozaPlugin.MozaLog.Debug(
                $"[Moza] Telemetry2 host: close session 0x{session:X2} (cold-start cleanup)");
            _send?.Invoke(frame);
        }

        private void EmitOpenOnSession(SessionEndpoint ep, ushort port)
        {
            ep.ResetSeq((ushort)(port + 1));   // next data starts at port+1
            lock (_stateLock) _claimedPorts.Add(port);

            byte[] body = new byte[]
            {
                0x7C, 0x00,
                ep.Session, 0x81,
                (byte)(port & 0xFF), (byte)((port >> 8) & 0xFF),
                (byte)(port & 0xFF), (byte)((port >> 8) & 0xFF),
                0xFD, 0x02,
            };
            byte[] frame = MozaFrame.Wrap(body);
            global::MozaPlugin.MozaLog.Info(
                $"[Moza] Telemetry2 host: open session 0x{ep.Session:X2} (type=0x81 port={port})");
            _send?.Invoke(frame);
        }

        // Allocate the next host-side port using the documented global monotonic counter.
        // Returns max(claimed) + 1 — i.e. the smallest port number not already used by
        // either side. With no claimed ports, falls back to the session byte (preserves
        // the simple port==session behaviour observed in 2025-11 firmware traces).
        private ushort AllocateHostPort(byte sessionByte)
        {
            lock (_stateLock)
            {
                if (_claimedPorts.Count == 0) return sessionByte;
                ushort maxClaimed = 0;
                foreach (var p in _claimedPorts) if (p > maxClaimed) maxClaimed = p;
                return (ushort)(maxClaimed + 1);
            }
        }

        // Same for session 0x03 (tile-server).
        private void EmitOnSession03(byte[] message)
        {
            var chunks = _ep03.SendMessage(message);
            foreach (var chunk in chunks)
            {
                byte[] body = chunk.ToBodyBytes();
                byte[] frame = MozaFrame.Wrap(body);
                _send?.Invoke(frame);
            }
            lock (_stateLock)
            {
                _outboundChunksBySession.TryGetValue(0x03, out int n);
                _outboundChunksBySession[0x03] = n + chunks.Count;
            }
        }

        // Same for session 0x02 (FF records).
        private void EmitOnSession02(byte[] message)
        {
            var chunks = _ep02.SendMessage(message);
            foreach (var chunk in chunks)
            {
                byte[] body = chunk.ToBodyBytes();
                byte[] frame = MozaFrame.Wrap(body);
                _send?.Invoke(frame);
            }
            lock (_stateLock)
            {
                _outboundChunksBySession.TryGetValue(0x02, out int n);
                _outboundChunksBySession[0x02] = n + chunks.Count;
            }
        }
    }
}
