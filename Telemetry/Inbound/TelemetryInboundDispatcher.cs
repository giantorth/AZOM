using System;
using System.Threading;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;

namespace MozaPlugin.Telemetry.Inbound
{
    /// <summary>
    /// Routes inbound 0xC3 / device 0x71 chunks: fc:00 acks, 7c:00 device-init
    /// (type=0x81), session-data (type=0x01), end marker (type=0x00), and 0x87
    /// display-detect. Per-session dispatch lives in the type=0x01 branch.
    /// </summary>
    internal sealed class TelemetryInboundDispatcher
    {
        private readonly TelemetrySender _sender;

        public TelemetryInboundDispatcher(TelemetrySender sender)
        {
            _sender = sender;
        }

        public void OnMessageDuringPreamble(byte[] data)
        {
            if (_sender.StateIsIdle) return;
            // data layout: [group, device, cmdPayload...]
            if (data.Length < 4) return;
            // Only process 0xC3 (response to 0x43) from the active target.
            // Standard wheel target = 0x71 (nibble-swapped 0x17). Standalone
            // dashboards (CM2) answer on their bridge/main 0x21 (nibble-swapped
            // 0x12); accept the broader set of swapped device ids when the
            // sender is in standalone-dashboard mode so the wheel-side and
            // dash-side answer paths both reach the dispatcher.
            if (data[0] != 0xC3) return;
            bool targetMatches = data[1] == _sender.TargetDeviceIdSwapped;
            // When two pipelines share one connection (e.g. wheel + bus-CM2), each
            // sender must consume ONLY its own device's replies — no fan-in, or both
            // would process the same frame. The broad standalone fan-in below is for
            // the single-pipeline case only.
            if (!targetMatches && !_sender.StrictInboundFilter && _sender.IsStandaloneDashboardTarget)
            {
                // TODO(cm2): narrow this fan-in once CM2 wire-traces nail down
                // exactly which dev-ids CM2 answers on for each command family.
                targetMatches =
                    data[1] == MozaProtocol.SwapNibbles(MozaProtocol.DeviceMain)
                    || data[1] == MozaProtocol.SwapNibbles(MozaProtocol.DeviceDash)
                    || data[1] == MozaProtocol.SwapNibbles(MozaProtocol.DeviceWheel);
            }
            if (!targetMatches) return;

            byte cmd1 = data[2];
            byte cmd2 = data[3];

            // fc:00 ack — session open accepted; 7-byte form carries seq for retransmit drain.
            if (cmd1 == 0xFC && cmd2 == 0x00 && data.Length >= 5)
            {
                HandleFc00Ack(data);
                return;
            }

            // 7c:00 data chunks.
            if (cmd1 == 0x7C && cmd2 == 0x00 && data.Length >= 8)
            {
                byte session = data[4];
                byte type = data[5];

                if (type == 0x81)
                {
                    HandleDeviceInit(data, session);
                    return;
                }
                if (type == 0x01)
                {
                    HandleSessionData(data, session);
                }
                if (type == 0x00)
                {
                    HandleSessionEnd(data, session);
                }
                return;
            }

            // Display sub-device identity response: 0x87 model name (response to 0x07 query).
            if (data[2] == 0x87 && data.Length >= 5 && data[3] == 0x01)
            {
                int nameLen = 0;
                for (int k = 4; k < data.Length && data[k] != 0; k++) nameLen++;
                if (nameLen > 0)
                {
                    string name = System.Text.Encoding.ASCII.GetString(data, 4, nameLen);
                    _sender.SetDisplayDetected(name);
                    MozaLog.Debug($"[AZOM] Display sub-device detected: \"{name}\"");
                }
            }
        }

        private void HandleFc00Ack(byte[] data)
        {
            _sender._lastAckedSession = data[4];
            if (data.Length >= 7)
            {
                int ackSeq = data[5] | (data[6] << 8);
                _sender._lastAckedSeq = ackSeq;
                _sender.Retransmitter.Ack(data[4], ackSeq);
                // Route ack to session owner (downloader, uploader, etc.).
                _sender.Dispatcher.DispatchAck(data[4], ackSeq);
            }
            else
            {
                // 5-byte fc:00 (no seq). Signal "ack present but seq unknown" so
                // TryOpenSession's seq verification falls through to session-match-only.
                _sender._lastAckedSeq = -1;
            }
            // Mgmt session engagement signal: a wheel fc:00 ack on sess=MgmtPort
            // proves the session is open even if the wheel never pushes data on
            // it. See DisplayWatchdog (liveness feeder) for why this matters on
            // slow-bring-up wheels (CS-Pro / Universal Hub).
            if (data[4] == _sender.MgmtPort && _sender.MgmtPort != 0)
                _sender.Watchdog.NoteSession01Engaged();
            _sender.AckReceived.Set();
        }

        private void HandleDeviceInit(byte[] data, byte session)
        {
            int openSeq = data.Length >= 8 ? data[6] | (data[7] << 8) : 0;
            var info = _sender.Sessions.GetOrCreate(session);
            info.DeviceInitiated = true;
            info.Port = (byte)(openSeq & 0xFF);
            _sender.SendSessionAckInternal(session, (ushort)openSeq);
            // Route device-init through dispatcher first so we check ownership
            // before redirecting the uploader's ActiveSession (don't let
            // NoteDeviceInit re-point to a session DashboardDownloader has claimed).
            _sender.Dispatcher.DispatchOpen(session, openSeq);
            if (session >= 0x04 && session <= 0x0b
                && _sender.Dispatcher.GetOwner(session) == null)
            {
                _sender.Uploader.NoteDeviceInit(session);
            }
            // sess=0x09 device-init is the wheel's first reliable
            // "session-layer ready" signal during a slow hot-attach boot —
            // wakes ProbeAndOpenSessions's 20 s extended wait so the host can
            // retry the sess=0x01/0x02 opens that timed out while the wheel
            // was still booting. Constrained to 0x09 because in every wire
            // trace the wheel opens 0x09 first; broadening this would risk
            // false-positives from upload / RPC sessions later in the
            // pipeline.
            if (session == 0x09)
                _sender.MarkWheelReadyObserved();
        }

        private void HandleSessionData(byte[] data, byte session)
        {
            int seq = data[6] | (data[7] << 8);
            byte[] chunkPayload = new byte[data.Length - 8];
            Array.Copy(data, 8, chunkPayload, 0, chunkPayload.Length);

            _sender.BumpSessionCount(session, outbound: false);

            // Dispatcher-owned sessions: route exclusively and ack.
            var owner = _sender.Dispatcher.GetOwner(session);
            if (owner != null)
            {
                _sender.SendSessionAckInternal(session, (ushort)seq);
                _sender.Dispatcher.DispatchData(session, seq, chunkPayload);
                return;
            }

            // Ack on telemetry session — send the SPECIFIC seq (running max would
            // silently drop retransmits of older seqs).
            if (session == _sender.FlagByte)
            {
                if (seq > _sender._sessionAckSeq)
                    _sender._sessionAckSeq = seq;
                _sender.SendSessionAckInternal(
                    _sender.FlagByte, _sender.GapAwareCatalogAckSeq(_sender.FlagByte, seq));

                // First inbound on sess=FlagByte = engagement gate.
                _sender.Watchdog.NoteSession02FirstInbound();
                // Wheel-reported dashboard slot tracker.
                _sender.SlotTracker.TryAbsorbType04Slot(chunkPayload);

                // Capture wheel's post-subscription response (5 s window).
                if (_sender.SubscriptionResponseDeadlineTicksField != 0
                    && System.Diagnostics.Stopwatch.GetTimestamp() < _sender.SubscriptionResponseDeadlineTicksField
                    && chunkPayload.Length > 0)
                {
                    var list = _sender.SubscriptionResponseChunksList;
                    lock (list)
                    {
                        if (list.Count < 32) list.Add(chunkPayload);
                    }
                }
            }

            // Ack on mgmt session. Specific-seq ack (NOT running max) — otherwise
            // retransmits of older seqs never get cleared.
            if (session == _sender.MgmtPort && _sender.MgmtPort != 0)
            {
                if (seq > _sender._mgmtAckSeq)
                    _sender._mgmtAckSeq = seq;
                _sender.SendSessionAckInternal(
                    _sender.MgmtPort, _sender.GapAwareCatalogAckSeq(_sender.MgmtPort, seq));
                _sender.MgmtResponseEvent.Set();
                // Mgmt session engagement signal — data flow on sess=MgmtPort
                // is the strongest possible proof the session is alive.
                _sender.Watchdog.NoteSession01Engaged();
                // Wheel-reported dashboard slot tracker. CS-family wheels echo
                // the type-04 slot record on sess=FlagByte (0x02); W13/FSR2
                // reports it on the mgmt session (0x01) instead. Listen on both
                // so WheelReportedSlot converges and the slot round-trip can
                // succeed — without this the W13's report is never absorbed,
                // WheelReportedSlot stays -1, and the DisplayWatchdog
                // force-restarts a healthy display after every kind=4. The
                // strict padding/bound validation rejects the mgmt session's
                // 0x06 acks and 0x04 catalog-URL records.
                _sender.SlotTracker.TryAbsorbType04Slot(chunkPayload);
            }

            // File-transfer candidate sessions (0x04..0x08): ack ALL, forward to
            // uploader. Wheel device-inits multiple FT sessions and pushes data
            // on whichever it chose; gating on ActiveSession alone meant chunks
            // on the other session were never acked.
            if (session >= 0x04 && session <= 0x08)
            {
                _sender.SendSessionAckInternal(session, (ushort)seq);
                _sender.Uploader.NoteInboundChunk(session, seq, chunkPayload);
            }

            // configJson state push. Older firmware: 0x09. KS Pro / 2026-04+: 0x0a.
            if (session == 0x09 || session == 0x0a)
            {
                if (session == 0x09) _sender._session09InboundSeq = seq;
                try
                {
                    MozaLog.Debug(
                        $"[AZOM] session 0x{session:X2} inbound chunk: seq={seq} payload={chunkPayload.Length}B " +
                        $"first8={BitConverter.ToString(chunkPayload, 0, Math.Min(8, chunkPayload.Length))}");
                }
                catch { }

                // Cumulative-ACK + buffer-preserving gap handling. Process chunk
                // first, then ack the reassembler's HighWaterSeq (highest contiguous-
                // received seq). On forward gap HighWaterSeq stays at pre-gap value,
                // so wheel sees the missing-chunk ack window and auto-retransmits.
                _sender.Watchdog.NoteConfigJsonChunkArrived();
                var result = _sender.ConfigJson.OnChunk(seq, chunkPayload, $"sess=0x{session:X2}");

                // Pick ACK value:
                //  - StateReady: chunk completed the burst; HighWaterSeq is now -1
                //    but we DID receive seq contiguously — ack `seq`.
                //  - Buffered/GapDetected: ack HighWaterSeq (stays at pre-gap on gap).
                int ackSeq = (result == ConfigJsonClient.ChunkResult.StateReady)
                    ? seq
                    : _sender.ConfigJson.HighWaterSeq;
                if (ackSeq >= 0)
                    _sender.SendSessionAckInternal(session, (ushort)ackSeq);

                if (result == ConfigJsonClient.ChunkResult.StateReady)
                {
                    _sender.Watchdog.ResetConfigJsonGapTracking();
                    var state = _sender.ConfigJson.LastState;
                    if (state != null)
                    {
                        _sender.MaybeSendConfigJsonReplyInternal(state, session);
                        _sender.MaybeTriggerDashboardDownloadInternal(state);
                        // The wheel's catalog burst on sess=0x02 (type-04 slot
                        // record) lands ~180 ms before the sess=0x09 state
                        // burst — so WheelSlotTracker may have buffered a
                        // wheel-initiated slot change that couldn't validate
                        // against the (then-empty) configJsonList. Replay it
                        // now that the list is available; without this,
                        // post-hot-swap slot changes the wheel makes on its
                        // own (e.g., auto-loading its persisted last-used
                        // dashboard) silently drop and the host keeps emitting
                        // tier-defs for the prior slot's channel catalog.
                        _sender.SlotTracker.ReplayPendingSwitchIfReady();
                    }
                }
                else if (result == ConfigJsonClient.ChunkResult.GapDetected)
                {
                    _sender.Watchdog.HandleConfigJsonGap(session, seq);
                }
            }

            // Session 0x0a: RPC reply channel. Ack already sent above (shared
            // handler with configJson); don't double-ack.
            if (session == 0x0a)
            {
                if (_sender.Session0aInbox.AddChunk(seq, chunkPayload, "sess=0x0a"))
                {
                    byte[]? replyBlob = _sender.Session0aInbox.TryDecompress();
                    if (replyBlob != null)
                    {
                        _sender.Session0aInbox.Clear();
                        _sender.Rpc.HandleReply(replyBlob);
                    }
                }
            }

            // Tile-server state push. Older firmware: 0x03. KS Pro / 2026-04+: 0x0b.
            if (session == 0x03 || session == 0x0b)
            {
                _sender.SendSessionAckInternal(session, (ushort)seq);
                // Strip + validate CRC32 trailer before parsing — corrupted chunk
                // can hit-spot a spurious sentinel.
                if (chunkPayload.Length >= 4)
                {
                    int netLen = chunkPayload.Length - 4;
                    uint wireCrc = (uint)(chunkPayload[netLen]
                                        | (chunkPayload[netLen + 1] << 8)
                                        | (chunkPayload[netLen + 2] << 16)
                                        | (chunkPayload[netLen + 3] << 24));
                    uint calcCrc = TierDefinitionBuilder.Crc32(chunkPayload, 0, netLen);
                    if (calcCrc != wireCrc)
                    {
                        int n = _sender.IncrementTileServerCrcRejects();
                        if (n <= 5 || n % 50 == 0)
                            MozaLog.Debug(
                                $"[AZOM] Tile-server chunk CRC mismatch sess=0x{session:X2} " +
                                $"seq={seq}: calc=0x{calcCrc:X8} wire=0x{wireCrc:X8} (rejects={n})");
                    }
                    else
                    {
                        // Seq dedup: skip retransmits so parser buffer doesn't
                        // accumulate duplicate copies.
                        bool isNew;
                        var seqMap = _sender.TileServerHighestSeqMap;
                        lock (seqMap)
                        {
                            isNew = !seqMap.TryGetValue(session, out int prev) || seq > prev;
                            if (isNew) seqMap[session] = seq;
                        }
                        if (isNew)
                        {
                            byte[] net = new byte[netLen];
                            Array.Copy(chunkPayload, 0, net, 0, netLen);
                            var tile = _sender.TileServerParser.OnChunk(net);
                            if (tile != null)
                            {
                                try
                                {
                                    MozaLog.Debug(
                                        $"[AZOM] Tile-server state received on session 0x{session:X2}: " +
                                        $"root='{tile.Root}' version={tile.Version} games={tile.Games.Count} " +
                                        $"any_populated={tile.AnyPopulated}");
                                }
                                catch { }
                            }
                        }
                    }
                }
            }

            // Channel catalog buffering. V0 URL-subscription firmware (CSP post-
            // 2026-04) sends URL entries on 0x01; V2-compact uses 0x02. Listen
            // on both during preamble. CRC validation prevents corrupted chunks
            // from mangling diagnostic catalog URLs.
            bool isCatalogSession = session == _sender.FlagByte || session == 0x01;
            if (isCatalogSession && data.Length > 12 && chunkPayload.Length >= 4)
            {
                int netLen = chunkPayload.Length - 4;
                uint wireCrc = (uint)(chunkPayload[netLen]
                                    | (chunkPayload[netLen + 1] << 8)
                                    | (chunkPayload[netLen + 2] << 16)
                                    | (chunkPayload[netLen + 3] << 24));
                uint calcCrc = TierDefinitionBuilder.Crc32(chunkPayload, 0, netLen);
                if (calcCrc == wireCrc)
                {
                    // Seq-aware append: dedup retransmits per session.
                    _sender.CatalogParser.AppendChunkIfNew(session, seq, chunkPayload, 0, netLen);
                }
                else
                {
                    int n = _sender.IncrementCatalogCrcRejects();
                    if (n <= 5 || n % 50 == 0)
                    {
                        MozaLog.Debug(
                            $"[AZOM] Catalog chunk CRC mismatch sess=0x{session:X2} " +
                            $"seq={seq}: calc=0x{calcCrc:X8} wire=0x{wireCrc:X8} " +
                            $"(total rejects: {n})");
                    }
                }
            }
        }

        private void HandleSessionEnd(byte[] data, byte session)
        {
            int closeSeq = data.Length >= 8 ? data[6] | (data[7] << 8) : 0;
            // Diagnostic visibility for wheel-initiated CLOSE. Tracks repeat
            // rate per session and warns on "close storms" so the silent
            // firmware-rejection failure mode surfaces in logs without a
            // wire trace. Does not alter recovery behaviour — that stays
            // with the engagement watchdogs below.
            _sender.Watchdog.NoteWheelInitiatedClose(session);
            // Ack the wheel's CLOSE so it stops retransmitting it. A wheel-
            // initiated close is reliable-delivery: if we never ack, the wheel
            // re-sends the SAME close (same seq) ~1/sec indefinitely — this is
            // the "close storm" on sess=0x01 (one close, retransmitted for the
            // whole session because the plugin never acked it). Only the core
            // session-layer ports we open and keep (mgmt 0x01 / telem 0x02)
            // need this; dispatcher-owned upload sessions handle their own
            // close routing below.
            if (session == _sender.MgmtPort || session == _sender.FlagByte)
                _sender.SendSessionAckInternal(session, (ushort)closeSeq);
            // Dispatcher-owned sessions: route exclusively.
            if (_sender.Dispatcher.GetOwner(session) != null)
            {
                _sender.Dispatcher.DispatchClose(session, closeSeq);
                return;
            }
            // Legacy routing for non-dispatcher sessions.
            if (session == _sender.MgmtPort) _sender.MgmtResponseEvent.Set();
            _sender.Uploader.NoteEndMarker(session);
        }
    }
}
