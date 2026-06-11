using System;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// Session open/close state machine for <see cref="TelemetrySender"/>:
    /// the cold-start/warm session-close sweep + 0x01/0x02 opens with the
    /// slow-bring-up extended wait (ProbeAndOpenSessions), the open/close
    /// ack waits, the session-control frame builders (open/close/ack/prime/end),
    /// the fc:00 ack latch the inbound dispatcher signals, and the gap-aware
    /// contiguous-ack tracking used during catalog binding. Reads the
    /// connection live through <see cref="TelemetrySender.ConnectionRef"/>
    /// (the sender's connection is replaced by Rebind, so it must never be
    /// captured) and the target device id through
    /// <see cref="TelemetrySender.TargetDeviceId"/>.
    /// </summary>
    internal sealed class SessionLifecycle
    {
        private readonly TelemetrySender _sender;

        internal SessionLifecycle(TelemetrySender sender)
        {
            _sender = sender;
        }

        // Port probing state. _lastAckedSeq=-1 signals "ack present but seq unknown"
        // (5-byte fc:00 form). See docs/protocol/sessions/chunk-format.md.
        // Written directly by Inbound.TelemetryInboundDispatcher on the serial
        // read thread; volatile, never lock these.
        internal volatile byte _lastAckedSession;
        internal volatile int _lastAckedSeq = -1;
        private readonly ManualResetEventSlim _ackReceived = new ManualResetEventSlim(false);
        internal ManualResetEventSlim AckReceived => _ackReceived;

        // Wheel session-layer readiness latch. Set by TelemetryInboundDispatcher
        // when the wheel pushes its first spontaneous sess=0x09 device-init
        // (type=0x81) — the most reliable "I'm alive and ready for session-level
        // traffic" signal the wheel emits during a slow hot-attach boot
        // (~20s after wheel-telemetry-mode reads first succeed). Consumed by
        // ProbeAndOpenSessions to retry sess=0x01/0x02 opens that timed out
        // while the wheel was still booting.
        internal volatile bool _wheelReadyObserved;

        // Gap-aware catalog ack: highest CONTIGUOUS inbound seq per catalog
        // session (mgmt/telem) during binding. A dropped catalog chunk under
        // Wine leaves a seq gap (observed 07→0c on sess=0x01); acking the
        // post-gap seq tells the wheel we received chunks we didn't, so it never
        // resends and the catalog stays truncated → dash wedged. Instead we
        // re-ack the last contiguous seq across a gap. Pre-Active only;
        // steady-state telemetry keeps specific-seq acks. Reset per session open
        // (ResetContigAck) and per StartInner.
        private readonly object _contigAckLock = new object();
        private readonly System.Collections.Generic.Dictionary<byte, int> _contigAckSeqBySession
            = new System.Collections.Generic.Dictionary<byte, int>();

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
        /// boundaries via <see cref="Watchdog.DisplayWatchdog.Reset"/>
        /// so a subsequent reconnect re-arms detection from a clean slate.</summary>
        internal void ResetWheelReadyObserved() => _wheelReadyObserved = false;

        /// <summary>Gap-aware ack-seq for a catalog-bearing session during the
        /// binding phase. Advances on contiguous seqs, acks retransmits of
        /// already-seen seqs specifically, and on a gap (a dropped catalog
        /// chunk) re-acks the last CONTIGUOUS seq instead of acking past the
        /// hole — so we never tell the wheel we received chunks we didn't, and
        /// it gets a chance to resend. Once Active (steady-state telemetry) it
        /// acks the seq verbatim, preserving the existing specific-seq behaviour
        /// (a dropped keepalive must not stall the ack). Reset per session open.</summary>
        internal ushort GapAwareCatalogAckSeq(byte session, int seq)
        {
            if (_sender.StateIsActive)
                return (ushort)seq;
            lock (_contigAckLock)
            {
                int contig = _contigAckSeqBySession.TryGetValue(session, out var c) ? c : -1;
                if (contig < 0 || seq == contig + 1)
                {
                    _contigAckSeqBySession[session] = seq;   // first frame, or in-order
                    return (ushort)seq;
                }
                if (seq <= contig)
                    return (ushort)seq;                      // retransmit of an acked seq
                // seq > contig + 1: chunk(s) between contig and seq dropped on RX.
                // Dup-ack the last contiguous seq; do NOT advance past the hole.
                return (ushort)contig;
            }
        }

        /// <summary>Drop the gap-aware contiguous-ack baseline for a session so a
        /// fresh open (new seq generation) starts clean. Called from
        /// SendSessionOpen and the StartInner reset.</summary>
        internal void ResetContigAck(byte session)
        {
            lock (_contigAckLock) _contigAckSeqBySession.Remove(session);
        }

        /// <summary>StartInner reset: drop every session's contiguous-ack baseline.</summary>
        internal void ClearContigAck()
        {
            lock (_contigAckLock) _contigAckSeqBySession.Clear();
        }

        /// <summary>Stop() teardown: reset the ack latch so a stale signal can't
        /// satisfy the next Start's open wait.</summary>
        internal void ResetAckEvent()
        {
            try { _ackReceived.Reset(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Sender Dispose(): release the ack event.</summary>
        internal void DisposeAckEvent()
        {
            try { _ackReceived.Dispose(); } catch { }
        }

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
        internal void ProbeAndOpenSessions(bool isColdStart, CancellationToken cancel)
        {
            if (!_sender.ConnectionRef.IsConnected)
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
                $"[AZOM] Closing any stale {(isColdStart ? "cold-start" : "host")} sessions " +
                $"(0x01..0x{lastClosePort:X2}{(isColdStart ? " — wide" : "")})...");
            for (byte port = 1; port <= lastClosePort; port++)
            {
                if (cancel.IsCancellationRequested
                    || _sender.StateIsIdle || !_sender.ConnectionRef.IsConnected) return;
                bool acked = TryCloseSession(port, CloseAckTimeoutMs);
                MozaLog.Debug(
                    $"[AZOM] SessionClose 0x{port:X2} {(acked ? "acked" : "no ack within " + CloseAckTimeoutMs + "ms")}");
            }

            byte mgmtPort = TryOpenSession(MgmtSession, OpenAckTimeoutMs);
            if (_sender.StateIsIdle || !_sender.ConnectionRef.IsConnected) return;
            byte telemetryPort = TryOpenSession(TelemSession, OpenAckTimeoutMs);

            // Slow-bring-up hardware (CS-Pro on Universal Hub): wheel takes
            // 12-15 s to first ack a session-control frame after device-lock.
            // If both opens stayed silent within the 500 ms budget, block here
            // for the engagement signal before letting the rest of cold-start
            // (hub enum, session 0x09 prime, tier-def emission, etc.) blast
            // state into a wheel that hasn't woken yet. Health wheels never
            // hit this branch; for CS-Pro the wheel eventually fc:00-acks
            // sess=0x02 (~14 s on the 0.9.3-dev capture) and we proceed from
            // a known-awake state. Pairs with DisplayWatchdog's 20 s
            // sess=0x01 grace — the watchdog catches the rarer case where the
            // wheel acks something but never engages sess=0x01 specifically.
            if (mgmtPort == 0 && telemetryPort == 0 && _sender.ConnectionRef.IsConnected)
            {
                const int ExtendedAckWaitMs = 20_000;
                const int ExtendedAckSliceMs = 100;
                MozaLog.Info(
                    $"[AZOM] Both sess=0x{MgmtSession:X2}/0x{TelemSession:X2} opens silent within " +
                    $"{OpenAckTimeoutMs}ms — waiting up to {ExtendedAckWaitMs}ms for slow-bring-up " +
                    "wheel (CS-Pro on Universal Hub takes ~14 s)");
                bool gotLateAck = false;
                _wheelReadyObserved = false;
                try { _ackReceived.Reset(); }
                catch (ObjectDisposedException) { return; }
                // Slice the wait so a SUPERSESSION (a new Start() cancelling our
                // token) or an external Stop()/disconnect releases the start
                // semaphore within one slice instead of pinning it for the full
                // 20 s. A single blocking Wait here observed neither `cancel` nor
                // `_state`, so a Stop→Start inside the window (e.g. the user
                // toggling telemetry-enable mid-bring-up) parked this StartInner
                // the whole 20 s holding the semaphore; the re-Start then hit
                // "could not acquire start lock after 10s" and telemetry never
                // came up — KS+CM2 cold start, diagnostics bundle 2026-06-08.
                int extWaited = 0;
                while (extWaited < ExtendedAckWaitMs)
                {
                    if (cancel.IsCancellationRequested
                        || _sender.StateIsIdle || !_sender.ConnectionRef.IsConnected) return;
                    try
                    {
                        if (_ackReceived.Wait(ExtendedAckSliceMs, cancel))
                        {
                            gotLateAck = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    extWaited += ExtendedAckSliceMs;
                }
                if (_sender.StateIsIdle || !_sender.ConnectionRef.IsConnected) return;
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
                        "[AZOM] Wheel session-layer ready observed (sess=0x09 device-init) — " +
                        $"retrying sess=0x{MgmtSession:X2}/0x{TelemSession:X2} opens with " +
                        $"{WheelReadyRetryMs}ms budget");
                    if (_sender.ConnectionRef.IsConnected)
                        mgmtPort = TryOpenSession(MgmtSession, WheelReadyRetryMs);
                    if (_sender.StateIsIdle || !_sender.ConnectionRef.IsConnected) return;
                    if (_sender.ConnectionRef.IsConnected)
                        telemetryPort = TryOpenSession(TelemSession, WheelReadyRetryMs);
                }
                else if (gotLateAck)
                {
                    MozaLog.Info(
                        $"[AZOM] Late ack on sess=0x{ackedSession:X2} after extended wait " +
                        "— wheel is alive, proceeding with cold-start");
                    if (ackedSession == MgmtSession) mgmtPort = MgmtSession;
                    else if (ackedSession == TelemSession) telemetryPort = TelemSession;
                    // Retry the still-unacked side with a 1 s budget — wheel
                    // should ack promptly now that it's awake. CS-Pro famously
                    // never acks sess=0x01, so the mgmt retry will time out
                    // and we fall through to the MgmtSession default below.
                    if (mgmtPort == 0 && _sender.ConnectionRef.IsConnected)
                        mgmtPort = TryOpenSession(MgmtSession, 1_000);
                    if (telemetryPort == 0 && _sender.ConnectionRef.IsConnected)
                        telemetryPort = TryOpenSession(TelemSession, 1_000);
                }
                else
                {
                    MozaLog.Warn(
                        $"[AZOM] No ack within {ExtendedAckWaitMs}ms extended wait — " +
                        "proceeding with defaults; session watchdog will retry post-Active. " +
                        "If this recurs after a SimHub restart, the cold-start wide close " +
                        "(0x01..0x0a above) should already have cleared any stale wheel " +
                        "session state — escalate via wire trace if the wedge persists.");
                }
            }

            _sender._mgmtPort = mgmtPort;

            // Session-open frames use seq=port. Data chunks must start
            // AFTER the open seq. PitHouse bridge capture shows first
            // session 0x02 data at seq=4 (not 2). Initialize outbound
            // seq counters so SendSessionPropertyBody (Math.Max(2, seq))
            // and SendTierDefinition (Math.Max(2, seq+1)) produce
            // correct first-use values.
            // Under the seq lock for consistency with the tick-thread writers.
            lock (_sender._session02SeqLock) { _sender._session02OutboundSeq = TelemSession + 1; } // port=2 → first data seq=3

            if (telemetryPort != 0)
            {
                _sender.FlagByte = telemetryPort;
                MozaLog.Debug(
                    $"[AZOM] Sessions opened: mgmt=0x{mgmtPort:X2} telem=0x{telemetryPort:X2}");
            }
            else if (mgmtPort != 0)
            {
                _sender.FlagByte = mgmtPort;
                MozaLog.Warn(
                    $"[AZOM] Telem session 0x{TelemSession:X2} did not ack, using mgmt 0x{mgmtPort:X2} for telemetry");
            }
            else
            {
                // No acks — proceed anyway using PitHouse defaults. Real wheels
                // may silently accept data on 0x02 even without an explicit ack.
                _sender.FlagByte = TelemSession;
                MozaLog.Warn(
                    "[AZOM] No session acks received, proceeding with defaults mgmt=0x01 telem=0x02");
                _sender._mgmtPort = MgmtSession;
            }
        }

        /// <summary>
        /// Send a SESSION_OPEN for the given session byte and wait up to
        /// <paramref name="timeoutMs"/> for a matching fc:00 ack. Returns the
        /// session byte on success, 0 on timeout.
        ///
        /// Single-attempt: an earlier revision added a 3× retry loop with
        /// Thread.Sleep between attempts, but every Reset()/Wait() on
        /// <see cref="_ackReceived"/> opened a window where <see cref="TelemetrySender.Dispose"/>
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
                            $"[AZOM] OpenSession 0x{session:X2}: ack_seq={gotAckSeq} " +
                            $"(expected {session}); accepting (firmware may use own port counter)");
                    }
                    return session;
                }

                // Stale ack (different session) — discard and keep waiting.
                MozaLog.Debug(
                    $"[AZOM] OpenSession 0x{session:X2}: ignoring stale ack for 0x{_lastAckedSession:X2}");
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
        /// Send a type=0x00 end-marker on the given session. Used to reclaim sessions
        /// left open after a previous SimHub crash/kill, where End() did not run.
        /// If the session is already closed, the wheel silently ignores this frame.
        /// </summary>
        internal void SendSessionClose(byte session)
        {
            // Length byte is the payload count (cmd + data, not incl. group/dev/cksum).
            // Payload is 6 bytes: 7C 00 <session> 00 <ack_lo> <ack_hi>. Must match
            // len=6 — a shorter frame with len=6 caused the wheel/sim to over-read
            // and corrupt the next frame in the stream, breaking the read loop.
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                // Target the active dashboard device (wheel 0x17 / CM2 0x14|0x12),
                // matching SendSessionOpen — closing on the wheel left a CM2's
                // stale sessions open and spammed the wheel with rejected cmds.
                MozaProtocol.TelemetrySendGroup, _sender.TargetDeviceId,
                0x7C, 0x00,
                session, 0x00,          // type=0x00 (end marker)
                0x00, 0x00,             // ack_seq = 0 (LE)
                0x00                    // checksum placeholder
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            _sender.ConnectionRef.Send(frame);
        }

        internal void SendSessionOpen(byte session, byte port)
        {
            // Fresh seq generation incoming — clear the gap-aware contiguous
            // ack baseline so the re-opened session re-bases cleanly.
            ResetContigAck(session);
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, _sender.TargetDeviceId,
                0x7C, 0x00,
                session, 0x81,          // session byte + type (channel open)
                port, 0x00,             // seq = port (LE)
                port, 0x00,             // session_id = port (LE)
                0xFD, 0x02,             // receive_window = 765 (LE)
                0x00                    // checksum placeholder
            };
            frame[14] = MozaProtocol.CalculateWireChecksum(frame);
            _sender.ConnectionRef.Send(frame);
        }

        internal void SendSessionAck(byte session, ushort ackSeq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x05,
                MozaProtocol.TelemetrySendGroup, _sender.TargetDeviceId,
                0xFC, 0x00,
                session,
                (byte)(ackSeq & 0xFF),
                (byte)(ackSeq >> 8),
                0x00
            };
            frame[9] = MozaProtocol.CalculateWireChecksum(frame);
            // Priority lane: acks must not get buried behind tier-def bursts in
            // the one-shot FIFO. Wheel times out sessions whose acks lag past
            // ~1 s and silently drops them (observed root cause of issue #43
            // "telemetry dies after dashboard switch"): during the switch, the
            // ~1300-frame tier-def burst stalled sess=0x02 acks behind 4ms-paced
            // FIFO, wheel concluded sess=0x02 was dead, telemetry never recovered
            // until full handshake reset. PH wire traces show sess=0x02 ack-lag
            // stays ≤ 870 ms even under heavy h2b load.
            _sender.ConnectionRef.SendPriority(frame);
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
                MozaProtocol.TelemetrySendGroup, _sender.TargetDeviceId,
                0x7C, 0x00,
                session, 0x01,                  // type=0x01 (data chunk)
                (byte)(seq & 0xFF),
                (byte)(seq >> 8),
                0x00, 0x00,                     // ack_seq = 0
                0x00, 0x00,                     // 2 bytes of empty data (matches Pithouse)
                0x00                            // checksum placeholder
            };
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            _sender.ConnectionRef.Send(frame);
        }

        internal void SendSessionEnd(byte session, ushort seq)
        {
            var end = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, _sender.TargetDeviceId,
                0x7C, 0x00,
                session, 0x00,
                (byte)(seq & 0xFF), (byte)((seq >> 8) & 0xFF),
                0x00
            };
            end[end.Length - 1] = MozaProtocol.CalculateWireChecksum(end);
            _sender.ConnectionRef.Send(end);
        }
    }
}
