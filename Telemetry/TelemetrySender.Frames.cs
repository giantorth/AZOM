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
using MozaPlugin.Devices.Led;

namespace MozaPlugin.Telemetry
{
    public partial class TelemetrySender
    {

        /// <summary>True while a host enable declaration for this dirName is
        /// outstanding — the wheel accepts the list but often never pushes a
        /// confirming delta mid-session, so the UI would otherwise keep
        /// showing a freshly-uploaded dash as disabled until a reconnect.</summary>
        public bool IsEnableDeclared(string dirName) =>
            !string.IsNullOrEmpty(dirName) && _intentionalEnables.ContainsKey(dirName);

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
            _connection.Send(_frames.ModeFrame);
        }

        private byte[] BuildChannelEnableFrame(byte page, byte channelIndex)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 5,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x1E, page,
                channelIndex, 0x00, 0x00,
                0x00,
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
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
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 2,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2,
                0x00,
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private byte[] BuildGroup40Frame3(byte cmd1, byte cmd2, byte cmd3)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 3,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2, cmd3,
                0x00,
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        // Cached frame construction lives in Frames/TelemetryFrameCache.cs.

        // ── Periodic streams ────────────────────────────────────────────────

        public volatile int DetectedDeviceMask;

        private void SendHeartbeat()
        {
            int mask = DetectedDeviceMask;
            var heartbeats = _frames.HeartbeatFrames;
            for (int i = 0; i < heartbeats.Length; i++)
            {
                if (mask == 0 || (mask & (1 << i)) != 0)
                    _connection.Send(heartbeats[i]);
            }
        }

        private void SendDashKeepalive()
        {
            // TelemetryServer periodic connection ping (group 0x43, N=1, data=0x00).
            // Pithouse sends to 0x14 (dash), 0x15, and 0x17 (wheel) every ~1.1s.
            // Distinct from group 0x00 heartbeats and SerialStream fc:00 acks.
            // Unclear whether the wheel requires this for telemetry to flow, but
            // Pithouse sends it consistently (~15× per session).
            _connection.Send(Frames.TelemetryFrameCache.DashKeepaliveFrameDash);
            _connection.Send(Frames.TelemetryFrameCache.DashKeepaliveFrame15);
            _connection.Send(Frames.TelemetryFrameCache.DashKeepaliveFrameWheel);
            // CM2 standalone path: add a ping at 0x12 (CM2 bridge/main) so the
            // device keeps its connection state warm against the active target.
            if (IsStandaloneDashboardTarget && _targetDeviceId == MozaProtocol.DeviceMain)
                _connection.Send(Frames.TelemetryFrameCache.DashKeepaliveFrameMain);
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
            //   0..13   = 14 grp 0x40 dev 0x17 static polls
            //   14..28  = 15 grp 0x40 dev 0x17 1e0x scan (5×3)
            //   29..42  = 14 grp 0x40 dev 0x17 1f00 scan
            //   43..56  = 14 grp 0x40 dev 0x17 1f01 scan
            //   57..60  = 4 grp 0x1F dev 0x12 4f08-4f0b LED state reads
            //   61..62  = 2 grp 0x3F dev 0x17 1a01/1a03 display variants
            //
            // Slots are COMPACT — no null placeholders. An audit of 35 diagnostic
            // bundles across 7 wheel models (tools/poll-audit) put the surviving
            // slots at 84-90 % answered, so the cycle is a live conversation, not
            // filler, and a run of silent slots is a real gap in it. The dead
            // entries were dropped rather than nulled for that reason:
            //   · grp 0x0E dev 0x12/13/17/19 discovery probes (12 slots) — never
            //     emitted what the source said. `s / 3 switch { … }` parses as
            //     `s / (3 switch { … })` == s / 25 == 0, so every one addressed
            //     device 0x00 and the cmd ternary fell through to its 0x13 default:
            //     three malformed frames, 242/241/240 of them across the audit, and
            //     zero of the intended twelve. Not repaired — group 0x0E is the
            //     param-manager channel, poking it on the wheel provokes the Table-8
            //     read-fail storm, and PitHouse never sends 0e→0x17 (see the block
            //     comment in MozaPlugin.PollStatusCore). Repairing would START
            //     emitting traffic this plugin has never actually sent.
            //   · 0x40 dev 0x17 `29 00 00` — 82 sent, 0 answered, on every model.
            const int totalCycle = 63;
            int slot = idx % totalCycle;

            byte[]? frame = null;
            if (slot < 14)
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
                    // Byte-identical to the wheel-knob-signal-mode0 READ frame
                    // (MozaCommandDatabase sub-id { 42, 0 } + 1 payload byte), so the
                    // reply resolves to that command name — see the seed-once note in
                    // MozaData.StoreKnobSignalMode. Kept: the wheel answers it 86 % of
                    // the time and it is part of the engagement conversation.
                    13 => BuildGroup40Bytes(new byte[] { 0x2A, 0x00, 0x00 }),
                    _ => null,
                };
            }
            else if (slot < 29)
            {
                int s = slot - 14;
                byte sub = (byte)((s / 5) == 0 ? 0x00 : (s / 5) == 1 ? 0x01 : 0x03);
                byte b4 = (byte)(0x02 + (s % 5));
                frame = BuildGroup40Bytes(new byte[] { 0x1E, sub, b4, 0x00, 0x00 });
            }
            else if (slot < 43)
            {
                // 1F 00 FF XX — RPM-bar color reads (indices 2-15). Part of
                // the parity-poll keepalive set; on the GS V2 Pro the wheel
                // needs to see these requests landing periodically or the
                // RPM-LED group stops responding to telemetry-mode writes
                // (see field-block comment near _ledStatePollGroup1).
                byte b5 = (byte)(0x02 + (slot - 29));
                frame = BuildGroup40Bytes(new byte[] { 0x1F, 0x00, 0xFF, b5, 0x00, 0x00, 0x00 });
            }
            else if (slot < 57)
            {
                // 1F 01 FF XX — Button color reads (indices 2-15). Same
                // keepalive role as the RPM-bar reads above.
                byte b5 = (byte)(0x02 + (slot - 43));
                frame = BuildGroup40Bytes(new byte[] { 0x1F, 0x01, 0xFF, b5, 0x00, 0x00, 0x00 });
            }
            else if (slot < 61)
            {
                // grp 0x1F dev 0x12 cmd 4f08-4f0b — LED state reads
                byte cmd2 = (byte)(0x08 + (slot - 57));
                frame = BuildGenericFrame(0x1F, 0x12, new byte[] { 0x4F, cmd2, 0x00 });
            }
            else
            {
                // grp 0x3F dev 0x17 display variants. Only the buttons-bitmask
                // and knob-bitmask writes survive, both gated on
                // IsLiveAnywhere() — PitHouse-derived bridge captures only
                // emit these with active=0/window=0 (286/286 in
                // bridge-20260517-081336.jsonl) because PitHouse drives no
                // dynamic knob telemetry. Our plugin does; writing those bytes
                // on top of an active session briefly drops the firmware out
                // of "telemetry owns the LEDs" → revert to EEPROM defaults
                // until the next non-zero frame, which the user sees as a
                // ~87 s default-colour flash.
                //
                // Four more variants were here at one point but a sweep across
                // 55 bridge captures showed PitHouse never emits the exact
                // bytes the plugin was sending: (19 01 00) and
                // (19 03 00) are mis-sized button/knob colour-chunk writes
                // that the firmware would interpret as "set LED 0 to black"
                // per the colour-commands padding rule. (1F 00 FF 00 00 00 00)
                // writes to an idx=0xFF padding slot
                // and PitHouse never sends it. (21 00 00,
                // wheel-idle-timeout=0) IS something PitHouse sends, but
                // sporadically on user setting change, not periodically —
                // emitting it every cycle silently overrides whatever
                // idle-timeout the user set in the plugin UI. The legitimate
                // path (UI / saved-settings apply) writes wheel-idle-timeout
                // with the user's chosen value via
                // MozaWheelSettingsControl.cs:1180 and HardwareApplier.cs:181;
                // the widget-poll slot has no business reasserting 0 on top.
                int s = slot - 61;
                bool liveActive = Devices.Led.MozaLedDeviceManager.IsLiveAnywhere();
                frame = s switch
                {
                    0 => liveActive ? null : BuildGenericFrame(0x3F, 0x17, new byte[] { 0x1A, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
                    1 => liveActive ? null : BuildGenericFrame(0x3F, 0x17, new byte[] { 0x1A, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
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
            // The 7C:27/7C:23 cycle is documented for the wheel only (protocol/
            // channel-config/group-0x43-active-display-cycle.md) and the cached
            // frames address DeviceWheel — a CM2 sender on the shared bus must not
            // emit them, or its 7C:23 46 FT-activate lands on the wheel.
            if (_targetDeviceId != MozaProtocol.DeviceWheel) return;

            int pageCount = _profile?.PageCount ?? 1;
            if (pageCount < 1) pageCount = 1;
            var frames = _frames.GetDisplayConfigFrames(pageCount);

            int page = _displayConfigPage % pageCount;
            _displayConfigPage++;

            int baseIdx = page * 3;
            _connection.Send(frames[baseIdx + 0]);
            // 7C:23 dashboard-activate: tells the wheel which dashboard pages are
            // active. PitHouse sends one per page interleaved with 7C:27 at ~1 Hz —
            // but NEVER while a file transfer is in flight. The 7C:23 46 frame is
            // the same FT-ACT verb the upload uses to acquire its session; firing
            // it mid-upload re-targets the wheel's file-transfer state machine at
            // the display session and the upload's acks degrade to the degenerate
            // total=0 form (ground truth: PitHouse's during-upload cadence in
            // bridge-upload-groundtruth-20260816 is 7C:27-only; the 7C:23s stop
            // for the whole transfer). The 7C:27 pair keeps flowing.
            if (!(_uploader?.IsUploadInFlight ?? false))
                _connection.Send(frames[baseIdx + 1]);
            _connection.Send(frames[baseIdx + 2]);
        }
    }
}
