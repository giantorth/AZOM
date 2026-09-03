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
    public partial class TelemetrySender
    {
        internal void RaiseDashboardPipelineParked()
        {
            try { DashboardPipelineParked?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private DisplayWatchdog _watchdog = null!;
        internal DisplayWatchdog Watchdog => _watchdog;
        internal bool HotSwitchBurstPending => _hotSwitch.IsBurstPending;

        // LEDs are throttled while this is true; a chunk landing within this window
        // counts as the catalog being actively (re)advertised.
        private const int CatalogActiveWindowMs = 1200;

        /// <summary>
        /// True only while the wheel is ACTIVELY (re)advertising its channel catalog —
        /// a catalog chunk landed within the last <see cref="CatalogActiveWindowMs"/>.
        /// The half-duplex 115200 link is contended in that window (our tier-def burst
        /// + the wheel's inbound catalog chunks), so collaborators throttle NON-ESSENTIAL
        /// h2b traffic (the ~60 Hz LED stream) to avoid dropping inbound catalog chunks.
        ///
        /// SELF-CLEARING (falls false ~1.2 s after chunks stop). The prior
        /// phase/_coldStartWheelGatePending version was wrong in both directions: it
        /// MISSED the initial advertisement (which arrives while the pipeline is still
        /// Idle) and STUCK ON after boundComplete because _coldStartWheelGatePending
        /// never cleared — the LED throttle never turned off (observed 2026-07-04).
        /// </summary>
        internal bool WheelInCatalogNegotiation
        {
            get
            {
                int last = _catalogParser.LastActivityMs;
                if (last == 0) return false;
                int since = unchecked(Environment.TickCount - last);
                return since >= 0 && since < CatalogActiveWindowMs;
            }
        }

        private readonly Display.WheelSlotTracker _slotTracker;
        private readonly PropertyPushQueue _propertyPushQueue;
        private readonly Frames.TierDefinitionEmitter _tierDefEmitter;
        internal Frames.TierDefinitionEmitter TierDefEmitter => _tierDefEmitter;
        private readonly Lifecycle.TelemetryInboundDispatcher _inboundDispatcher;
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
        internal global::MozaPlugin.Telemetry.Sessions.SessionRetransmitter Retransmitter => _retransmitter;
        internal void SendAndTrackChunkInternal(byte[] frame) => SendAndTrackChunk(frame);
        internal MultiStreamProfile? ProfileRef => _profile;

        /// <summary>The session the tier-def is emitted on: whichever carries
        /// the wheel's real catalog+END. PitHouse "Form A" = mgmt (0x01),
        /// "Form B" = FlagByte (0x02); same wheel can use either by where it
        /// pushes its catalog, so we follow it. Falls back to the era policy's
        /// session before any catalog has arrived. Single source of truth shared
        /// by the tier-def emitter AND the FF/property-push session pick so they
        /// stay mirrored.</summary>
        internal byte ResolveTierDefSession()
        {
            byte mgmt = _mgmtPort != 0 ? _mgmtPort : (byte)0x01;
            // Prefer mgmt (0x01) whenever it carries the wheel's real catalog+END.
            // A Form-A wheel (CS-Pro) binds the subscription there, and after a
            // power cycle the cold-start catalog-on-flag is coaxed back onto mgmt
            // by the StartInner re-request loop, so mgmt is usually ready by the
            // time we emit.
            if (_catalogParser.HasRealCatalogOnSession(mgmt)) return mgmt;
            // Dynamic session follow (re-enabled 2026-06-25). Some wheels (this
            // W17 on a CS-Pro base) route their real catalog+END to the flag
            // session (0x02) and NEVER mirror it onto mgmt — the re-request loop
            // can't coax it over. Pinning the tier-def to mgmt then emits END=0
            // there while the wheel's END lives on 0x02; the wheel acks, then
            // CLOSEs 0x01, and DisplayWatchdog spins a futile restart loop
            // forever (catalog-on-wrong-session wedge, diag bundle 2026-06-25).
            // Follow the catalog to wherever its END actually lives: the emitter
            // echoes GetEndMarkerForSession(thisSession) so the END matches, and
            // ResolveFfSession mirrors FF/kind=4 onto the opposite session, so
            // binding stays consistent on whichever we pick. Only follows once
            // mgmt has demonstrably failed to carry the catalog, so the Form-A
            // coax-to-0x01 path above is unaffected.
            byte flag = FlagByte != 0 ? FlagByte : (byte)0x02;
            if (flag != mgmt && _catalogParser.HasRealCatalogOnSession(flag))
                return flag;
            // True cold start — neither session has committed a catalog yet.
            // Fall back to mgmt, matching the PitHouse Form-A default.
            return mgmt;
        }

        /// <summary>The session FF-init / dashboard-switch (kind=4) / property
        /// pushes ride: the OPPOSITE of the tier-def session. PitHouse Form A
        /// puts tier-def on 0x01 and the FF records on 0x02; Form B mirrors both
        /// (tier-def 0x02, FF 0x01). The wheel only acks the FF-init
        /// (kind=10/16) and commits the tier-def to the display when the FF
        /// records arrive on the expected (mirror) session — sending them on the
        /// wrong session is "switch is visual but the dash shows no data."</summary>
        internal byte ResolveFfSession()
        {
            byte mgmt = _mgmtPort != 0 ? _mgmtPort : (byte)0x01;
            byte flag = FlagByte != 0 ? FlagByte : (byte)0x02;
            return ResolveTierDefSession() == flag ? mgmt : flag;
        }
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
            Interlocked.Exchange(ref _subscriptionResponseDeadlineTicks, deadlineTicks);
        }

        // ── Inbound-dispatcher accessors ─────────────────────────────────
        internal SessionDispatcher Dispatcher => _dispatcher;
        internal SessionRegistry Sessions => _sessions;
        internal WheelUploadCoordinator Uploader => _uploader;
        internal ConfigJsonClient ConfigJson => _configJson;
        internal RpcCallChannel Rpc => _rpc;
        internal SessionDataReassembler Session0aInbox => _session0aInbox;
        internal TileServerStateParser TileServerParser => _tileServerParser;
        internal ManualResetEventSlim AckReceived => _sessionLife.AckReceived;
        internal ManualResetEventSlim MgmtResponseEvent => _mgmtResponseEvent;

        // Wheel-ready latch + ack latch live in Sessions/SessionLifecycle.cs.
        internal void MarkWheelReadyObserved() => _sessionLife.MarkWheelReadyObserved();
        internal void ResetWheelReadyObserved() => _sessionLife.ResetWheelReadyObserved();
        /// <summary>True once the wheel has device-inited sess=0x09 (type=0x81)
        /// this Start cycle — the wheel's own "session layer engaged" signal.
        /// Never true across the stale-session wedge (the stale instance only
        /// emits keepalives); reset per Start via <see cref="Lifecycle.DisplayWatchdog.Reset"/>.</summary>
        internal bool WheelReadyObserved => _sessionLife._wheelReadyObserved;
        // Written on tick/start threads, read per inbound chunk on the serial
        // read thread — Interlocked (x86 64-bit).
        internal long SubscriptionResponseDeadlineTicksField
        {
            get => Interlocked.Read(ref _subscriptionResponseDeadlineTicks);
            set => Interlocked.Exchange(ref _subscriptionResponseDeadlineTicks, value);
        }
        internal System.Collections.Generic.List<byte[]> SubscriptionResponseChunksList => _subscriptionResponseChunks;
        internal System.Collections.Generic.Dictionary<byte, int> TileServerHighestSeqMap => _tileServerHighestSeq;
        internal int IncrementCatalogCrcRejects() => Interlocked.Increment(ref _catalogCrcRejects);
        internal int IncrementTileServerCrcRejects() => Interlocked.Increment(ref _tileServerCrcRejects);
        internal void SendSessionAckInternal(byte session, ushort ackSeq) => _sessionLife.SendSessionAck(session, ackSeq);
        internal ushort GapAwareCatalogAckSeq(byte session, int seq) => _sessionLife.GapAwareCatalogAckSeq(session, seq);
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
            // Same convergence cycle as host-initiated switches — the wheel
            // pushes its post-switch catalog identically whether the trigger
            // came from us or from a wheel-side button, and we want the same
            // "ensure host catalog matches wheel catalog" post-condition.
            ArmPostSwitchConvergence(slot);
            try { WheelInitiatedSwitch?.Invoke(slot); }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] WheelInitiatedSwitch handler threw: {ex.Message}");
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
        // FF session the init handshake (kind=2/7) was last sent on. The
        // handshake fires early in StartInner BEFORE the wheel's catalog has
        // arrived, so on a Form-B wheel it goes on the pre-catalog FF default
        // (0x02) while the catalog later flips the FF session to 0x01. The wheel
        // acked the init on the old session and won't accept kind=4 / commit the
        // tier-def on the new session, so ApplySubscription re-sends the init on
        // the corrected FF session. 0 = not sent this cycle.
        private byte _initHandshakeSession;

        internal void SendSessionInitHandshake()
        {
            if (_state == TelemetryState.Idle || !_connection.IsConnected) return;

            byte[] init2 = global::MozaPlugin.Telemetry.Sessions.SessionPropertyPushBuilder
                .BuildSessionInitField2Body();
            _propertyPushQueue.SendBody(init2);

            byte[] init7 = global::MozaPlugin.Telemetry.Sessions.SessionPropertyPushBuilder
                .BuildSessionInitField7Body();
            _propertyPushQueue.SendBody(init7);

            // kind=8 / kind=11 deliberately not emitted — see method-level
            // comment above and docs/protocol/sessions/session-0x02-ff-init.md
            // for the required body-decode work before re-attempting.

            // Non-VGS display wheels: force the firmware display-rotation
            // property off. V0 value frames misrouted at a non-V0 wheel (bad
            // manual era pick) collide with property kind=5 and latch rotation
            // on in wheel flash; these wheels have no UI anywhere to clear it.
            var model = WheelModelInfoProvider?.Invoke();
            if (model != null && model.HasDisplay == true && !model.SupportsDisplayRotation)
            {
                SendDashDisplayRotation(0);
                MozaLog.Debug("[AZOM] Forced display rotation off (non-VGS display wheel)");
            }

            _initHandshakeSession = ResolveFfSession();
            MozaLog.Debug(
                $"[AZOM] Sent init handshake (kind=2 nonce + kind=7 enum=3) on FF session " +
                $"0x{_initHandshakeSession:X2} (mirror of tier-def session).");
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
                    $"[AZOM] SwitchToProfile slot={slotIndex}: HOT path — " +
                    $"{Lifecycle.HotSwitchCoordinator.MinEmissions}-" +
                    $"{Lifecycle.HotSwitchCoordinator.MaxEmissions} tier-def emissions queued " +
                    $"~{Lifecycle.HotSwitchCoordinator.EmissionSpacingMs}ms apart (adaptive on bind state)");
                // Also arm the convergence watcher so we keep verifying the
                // catalog converges post-burst even if no chunks were
                // missed. Cheap when everything is healthy — the first
                // sample matches the next two and we disarm in ~6 s.
                ArmPostSwitchConvergence((int)slotIndex);
            }
            else
            {
                MozaLog.Info(
                    $"[AZOM] SwitchToProfile slot={slotIndex}: STOP+START path " +
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
                    MozaLog.Error($"[AZOM] RestartForSwitch failed: {ex.Message}");
                }
            });
        }

        /// <summary>Convenience: push display standby timeout in minutes (converts to ms).</summary>
        public void SendDashDisplayStandbyMinutes(int minutes)
        {
            if (minutes < 1) minutes = 1;
            ulong ms = (ulong)minutes * 60_000UL;
            SendSessionPropertyU64(
                global::MozaPlugin.Telemetry.Sessions.SessionPropertyPushBuilder.KindDashStandbyMs,
                ms);
        }

        /// <summary>
        /// Convenience: push the display-rotation mode (0=off, 1=smooth,
        /// 2=immediate). The wheel senses its own angle with an internal IMU and
        /// counter-rotates the dashboard; this only selects how. Sent once per
        /// change (not periodic), matching PitHouse. Values outside 0..2 are
        /// clamped. VGS: profile-driven — see the gate in
        /// <c>HardwareApplier.ApplyWheelToHardware</c> and the VGS-only UI in
        /// <c>DashboardManagementControl</c>. All other display wheels: forced
        /// to 0 at session init and detection (no UI).
        /// </summary>
        public void SendDashDisplayRotation(int mode)
        {
            if (mode < 0) mode = 0;
            if (mode > 2) mode = 2;
            SendSessionPropertyU8(
                global::MozaPlugin.Telemetry.Sessions.SessionPropertyPushBuilder.KindDashDisplayRotation,
                (byte)mode);
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
    }
}
