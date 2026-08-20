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

namespace MozaPlugin.Telemetry
{
    public partial class TelemetrySender
    {

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                MozaLog.Warn("[AZOM] Start() ignored — sender disposed");
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
                    "[AZOM] Start() skipped — sender already Active with live connection " +
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
                MozaLog.Debug("[AZOM] Start() cancelling prior in-progress start");
                try { prior.Cancel(); } catch (ObjectDisposedException) { }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!_startSemaphore.Wait(10000))
            {
                MozaLog.Warn("[AZOM] Start() could not acquire start lock after 10s");
                return;
            }
            if (prior != null)
                MozaLog.Debug($"[AZOM] Start() superseded in-progress start (waited {sw.ElapsedMilliseconds}ms)");

            // Re-check disposal after the (potentially long) wait — Dispose
            // may have arrived while we were queued.
            if (Volatile.Read(ref _disposed) != 0)
            {
                MozaLog.Warn("[AZOM] Start() ignored — sender disposed during wait");
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
                MozaLog.Debug("[AZOM] StartInner cancelled by supersession / disposal");
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
            // A wrong-session-wedge recovery (set by the cold-start gate) must take the
            // full COLD re-provoke path — wide session close + the catalog-on-tier-def-
            // session wait gate + the session re-cycle — even though it's a warm restart
            // (the wheel stopped, so isFirstStartInProcess is false). This is what re-rolls
            // the wheel's Form-A/B choice onto the tier-def session. The silence gate below
            // still keys off isFirstStartInProcess (a warm restart honors the ~11 s settle —
            // which is exactly the sess=0x09 timeout that makes the re-advertise land clean).
            bool forceColdReprovoke = System.Threading.Interlocked.Exchange(ref _forceColdReprovoke, 0) == 1;
            bool coldReprovoke = isFirstStartInProcess || forceColdReprovoke;
            // Genuine new start (not a wedge retry) → fresh re-provoke budget.
            if (!forceColdReprovoke) _coldStartReprovokeAttempts = 0;
            Stop();

            // Enforce minimum host silence since the last Stop() completion.
            // The wheel maintains an internal ~10-14 s timeout on its
            // sess=0x09 dashboard-binding state — during that window the
            // wheel ignores host re-opens. Verified 2026-05-08 wire
            // trace: failing cycles at 8.4 s of silence, working at
            // 13.9 s. This gate is the host-side enforcement.
            //
            // Cold-start in a fresh SimHub process skips the gate: there is
            // no prior in-process Stop to settle from. NOTE stale sessions
            // from a PREVIOUS process do NOT reliably time out on their own
            // (CS-Pro bundle 2026-07-21: sess=0x02 from a killed instance was
            // still alive and acking at cold start) — that case is detected
            // reactively in ProbeAndOpenSessions, whose acked-close settle
            // enforces the equivalent silence before the fresh opens.
            int waitMs = _silenceGate.RemainingStopReopenWaitMs(preStopTicks);
            if (!isFirstStartInProcess)
            {
                long elapsedMs = (System.DateTime.UtcNow.Ticks - preStopTicks)
                    / System.TimeSpan.TicksPerMillisecond;
                if (waitMs > 0)
                {
                    MozaLog.Debug(
                        $"[AZOM] Start: enforcing {waitMs}ms silence " +
                        $"(elapsed since last Stop: {elapsedMs}ms; min: {Lifecycle.SilenceGate.StopReopenSilenceMs}ms) " +
                        "so wheel session state can settle before reopen");
                    // Slice the sleep so a supersession (a new Start() cancelling
                    // our token) releases the start semaphore within one slice
                    // instead of pinning it for the full ~11 s — otherwise the
                    // superseding Start hits "could not acquire start lock after
                    // 10s" and telemetry stays wedged. Gate on the token only:
                    // StartInner's own Stop() above already left _state == Idle
                    // by design, so a state check here would bail immediately.
                    const int SilenceSliceMs = 100;
                    int slept = 0;
                    while (slept < waitMs)
                    {
                        if (cancel.IsCancellationRequested) return;
                        int slice = System.Math.Min(SilenceSliceMs, waitMs - slept);
                        try { System.Threading.Thread.Sleep(slice); } catch { }
                        slept += slice;
                    }
                }
            }
            else
            {
                MozaLog.Debug(
                    "[AZOM] Start: first start in this SimHub process — " +
                    "skipping silence gate (no prior Stop to settle from)");
            }

            InitTickStateAndTransitionToStarting();
            // Arm the sess=0x09-readiness preamble gate (see _coldStartWheelGatePending
            // docs) for a true cold start OR a forced wrong-session-wedge re-provoke.
            // Must be set AFTER the init method above, which clears it. Plain warm/
            // game-switch reloads leave it false → no added preamble latency.
            _coldStartWheelGatePending = coldReprovoke;
            _frames.BuildCachedFrames();

            // Subscribe early so we catch fc:00 acks during port probing AND preamble
            _connection.MessageReceived += _inboundDispatcher.OnMessageDuringPreamble;

            // Probe for available ports and open sessions. May run on a
            // background thread (dispatched by StartTelemetryIfReady) so the
            // serial read thread stays free to deliver fc:00 ack responses.
            //
            // On coldReprovoke the wide session close below makes the wheel
            // re-advertise its catalog from a clean slate, so the catalog parser must
            // reset to match. Without this the fresh advertisement UNIONs onto the
            // stale catalog — CommitLiveSet treats it as a same-arm-count continuation
            // batch (a re-provoke is not a dashboard switch, so the arm count doesn't
            // bump), and _catalog accumulates idxs across re-provoke cycles. The
            // emitted tier-def then balloons every cycle (END marker 77→154→…→497,
            // channel idx/compression garbled — observed on R5 when 4314db0's wedge
            // recovery re-provokes), and the wheel can never bind it → telemetry
            // "runs" but no channel ever updates. Warm game-switch reloads
            // (coldReprovoke=false) keep the catalog for the persistent-wire fast
            // path. (_catalogParser.Reset() was previously dead code.)
            if (coldReprovoke)
                _catalogParser.Reset();

            // Pass through coldReprovoke so cold starts AND wrong-session-wedge
            // re-provokes get the wide session close (0x01..0x0a) that flushes stale
            // wheel-side session state and makes the wheel re-advertise its catalog
            // from a clean slate. Plain game-switch reloads skip the wide close to
            // preserve the configJson handshake on the persistent wire.
            _sessionLife.ProbeAndOpenSessions(coldReprovoke, cancel);
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
            _sessionLife.SendSessionOpen(0x03, 0x03);

            _tierDefEmitter.WaitForChannelCatalogQuiet(quietMs: 200, timeoutMs: 2000);
            _catalogParser.TryParse();

            // CRITICAL ordering: wait for a REAL catalog (on either session)
            // before the FF-init handshake, so the handshake goes on the correct
            // (mirror) FF session the FIRST time. The cold-start bug: the catalog
            // reveals which session is the tier-def session (Form A=0x01,
            // Form B=0x02), and the FF-init must ride the OPPOSITE session. If we
            // send the init before the catalog arrives, it lands on the
            // pre-catalog default (0x02) and we re-send on 0x01 once the catalog
            // shows Form B — but the wheel, having received a premature 0x02 init
            // then a 0x01 init, never commits the binding (dash renders, no
            // data). Proof: a telemetry off→on toggle PRESERVES the catalog, so
            // its StartInner sends the init straight to 0x01 once and the wheel
            // renders fine. WaitForChannelCatalogQuiet only waits for QUIET (which
            // fires during the pre-catalog silence, before the catalog burst), so
            // it is NOT sufficient — we explicitly wait for HasRealCatalogOnSession.
            // Bounded so a screenless / never-cataloging wheel still proceeds.
            {
                // Base wait for an ordinary (small) catalog. A radar/track-map
                // dashboard advertises a HUGE catalog (observed 223 channels: 184 ri +
                // Location*) that streams at ~12 idx/s, so it needs ~19 s to arrive —
                // far past the base budget. Cutting it short re-cycles mid-burst and
                // restarts from idx 0, so it never completes (the cold-start wedge: in
                // one run only 60/223 idx arrived before the 5 s cut). So when radar
                // channels are enabled, allow a much larger cap AND keep waiting as
                // long as new chunks are still arriving (progress-based) — only give
                // up once it's STALLED (no new chunk for CatalogStallMs past the base),
                // which is the genuinely-truncated case worth a re-cycle.
                const int CatalogWaitBaseMs = 5000;
                int CatalogWaitMaxMs =
                    (MozaPlugin.Instance?.Settings?.EnableRadarTrackMapChannels ?? false)
                        ? 30000 : CatalogWaitBaseMs;
                const int CatalogStallMs = 3000;
                const int CatalogWaitSliceMs = 100;
                // The cold-start catalog can arrive with dropped/truncated
                // chunks under Wine: mid-burst session-data frames are lost
                // (observed on sess=0x01, seq 07→0c — the URL records 08-0b
                // never reach the read buffer), leaving a partial URL record +
                // END. The read thread cumulative-acks that END as delivered,
                // so the wheel never resends; the dash renders but shows no live
                // data and NEVER self-recovers — only a telemetry off/on toggle
                // (a full session re-cycle) fixes it. So when the wheel pushed an
                // END marker but we assembled ZERO valid channel URLs, treat the
                // catalog as garbage and RE-REQUEST: wipe the polluted buffer and
                // close+reopen the catalog sessions so the wheel re-pushes the
                // full catalog. Bounded; a wheel that pushed no END at all
                // (screenless / no dash bound) has nothing to re-request and just
                // proceeds.
                // The big radar/track-map catalog (223 ch, ri channels LAST at idx
                // 72+) drops below the Wine USB-CDC layer mid-burst, intermittently
                // and at a VARYING point (observed 60/86/110/244 ch across sessions —
                // probabilistic, not deterministic). The wheel does not self-resend
                // the dropped tail. A manual telemetry off/on recovers it because the
                // re-cycle re-pushes and sometimes lands a clean burst — so AUTOMATE
                // that: with radar enabled, retry the re-cycle many more times so a
                // clean catalog is caught without user intervention. Each attempt
                // exits early the instant a real catalog commits.
                int MaxCatalogRequests =
                    (MozaPlugin.Instance?.Settings?.EnableRadarTrackMapChannels ?? false)
                        ? 6 : 3;
                byte mgmt = _mgmtPort != 0 ? _mgmtPort : (byte)0x01;
                byte flag = FlagByte;
                int totalWaited = 0;
                bool haveCatalog = false;
                for (int attempt = 1; attempt <= MaxCatalogRequests; attempt++)
                {
                    int waited = 0;
                    while (waited < CatalogWaitMaxMs)
                    {
                        _catalogParser.TryParse();
                        // Readiness is gated on the TIER-DEF session ONLY (mgmt —
                        // the session the tier-def actually rides per
                        // ResolveTierDefSession, regardless of where the cold-start
                        // catalog landed). Accepting the catalog on the flag
                        // session here was the cold-start wedge: the gate passed on
                        // a flag-only (0x02) catalog, the emitter then forced the
                        // tier-def onto the degenerate mgmt (0x01) with END=0, the
                        // wheel rejected it and closed 0x01, and the DisplayWatchdog
                        // had to do a full ~14 s pipeline restart (which collapses
                        // the pipeline onto 0x01-only when 0x02 then fails to ack,
                        // breaking FF/kind=4 dashboard switches). Catch the
                        // wrong-session catalog up front and re-cycle below instead.
                        if (_catalogParser.HasRealCatalogOnSession(mgmt))
                        {
                            haveCatalog = true;
                            break;
                        }
                        if (flag != 0 && flag != mgmt
                            && _catalogParser.HasRealCatalogOnSession(flag))
                        {
                            // Dynamic session follow (2026-06-25): the wheel committed
                            // its catalog on the flag session (Form B — this W17 on a
                            // CS-Pro base routes its catalog+END to 0x02 and NEVER
                            // mirrors it to mgmt). ResolveTierDefSession() now follows
                            // the catalog there and ResolveFfSession() mirrors FF/kind=4
                            // to mgmt, so the binding is consistent on 0x02. ACCEPT it
                            // and proceed — do NOT re-cycle to force it back to mgmt.
                            // The force path could never move the catalog, so the cold
                            // start wedged and only a manual telemetry off/on (a warm
                            // re-cycle that re-lands the catalog) recovered it.
                            haveCatalog = true;
                            break;
                        }
                        if (cancel.IsCancellationRequested
                            || _state == TelemetryState.Idle || !_connection.IsConnected) return;
                        // Progress-based: a large radar catalog is still streaming, so
                        // keep waiting past the base budget as long as chunks keep
                        // arriving. Only stop early (to re-cycle) once we're past the
                        // base budget AND no new chunk has landed for CatalogStallMs —
                        // i.e. genuinely stalled/truncated, not merely slow.
                        int sinceChunkMs = unchecked(Environment.TickCount - _catalogParser.LastActivityMs);
                        if (waited >= CatalogWaitBaseMs && sinceChunkMs > CatalogStallMs)
                            break;
                        try { System.Threading.Thread.Sleep(CatalogWaitSliceMs); } catch { }
                        waited += CatalogWaitSliceMs;
                    }
                    totalWaited += waited;
                    if (haveCatalog) break;

                    // Not ready on the tier-def session. Two re-requestable cases:
                    //  • wrong-session: a full catalog landed on the flag session
                    //    only (this R5/CS-Pro base's cold-start behavior).
                    //  • incomplete burst: an END was seen but 0 valid URLs
                    //    assembled anywhere (dropped/truncated chunks under Wine).
                    // Both are fixed by wiping the buffer and re-cycling the catalog
                    // sessions so the wheel re-advertises. "No catalog at all" (no
                    // END ever — screenless / nothing bound) has nothing to ask for.
                    // No REAL catalog committed on either session (a complete
                    // catalog-on-flag was already followed + accepted above). What's
                    // left is an INCOMPLETE advertisement: a partial/truncated burst
                    // (dropped chunks under Wine — the large Radar catalog is the worst
                    // case) or URLs with no committed END. The wheel won't self-resend,
                    // so re-cycle the sessions to re-provoke a full re-advertisement.
                    // This is the re-provoke the old force path provided; removing it
                    // (when the swap path was added) was the cold-start wedge — a
                    // partial catalog had nothing kicking it to retry, so the pipeline
                    // never engaged and the display hung. Bounded by MaxCatalogRequests
                    // so a genuinely screenless / never-advertising wheel still proceeds.
                    bool sawAnyCatalog =
                        _catalogParser.GetEndMarkerForSession(mgmt) != 0
                        || (flag != 0 && _catalogParser.GetEndMarkerForSession(flag) != 0)
                        || _catalogParser.LastWheelEndMarker != 0
                        || (_catalogParser.LiveCatalog?.Count ?? 0) > 0
                        || (_catalogParser.Catalog?.Count ?? 0) > 0;
                    if (!sawAnyCatalog || attempt == MaxCatalogRequests)
                        break;

                    MozaLog.Warn(
                        $"[AZOM] No real catalog committed on 0x{mgmt:X2}/0x{flag:X2} after {waited}ms " +
                        $"(incomplete/partial advertisement); re-requesting via " +
                        $"session re-cycle (attempt {attempt}/{MaxCatalogRequests - 1}). " +
                        $"parser: {_catalogParser.DescribeSession(mgmt)} {_catalogParser.DescribeSession(flag)}");
                    // Re-provoke the catalog by re-cycling ONLY the catalog sessions
                    // (0x01 mgmt + 0x02 telemetry). Do NOT touch 0x03 — that's the
                    // tile-server / display-content session, and closing+reopening it
                    // on every retry reboots the wheel display (the radar-cold-start
                    // instability: a big catalog needs several re-provokes, and each
                    // 0x03 cycle flickered/rebooted the screen). 0x03 stays open from
                    // the initial cold-start open; the catalog doesn't ride it.
                    _catalogParser.ClearBuffer();
                    try { _sessionLife.TryCloseSession(0x01, 300); } catch { }
                    try { _sessionLife.TryCloseSession(0x02, 300); } catch { }
                    try { System.Threading.Thread.Sleep(250); } catch { }
                    if (cancel.IsCancellationRequested
                        || _state == TelemetryState.Idle || !_connection.IsConnected) return;
                    _sessionLife.TryOpenSession(0x01, 500);
                    if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                    _sessionLife.TryOpenSession(0x02, 500);
                }
                MozaLog.Debug(
                    $"[AZOM] Pre-init catalog wait: {totalWaited}ms — tier-def session resolved to " +
                    $"0x{ResolveTierDefSession():X2}, FF session 0x{ResolveFfSession():X2}" +
                    $"{(haveCatalog ? "" : " (no usable catalog — proceeding)")}.");
            }
            MaybeSwapProfileForCatalog();

            // FF-init handshake (kind=2 nonce + kind=7 slot-index) on the FF
            // session (mirror of the tier-def session, now resolved from the
            // catalog above). Required: without it the wheel ignores
            // dashboard-switch FF records and never commits the dashboard.
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
            _frames.ResetSequenceCounter();
            _slowCounter = 0;
            _displayConfigPage = 0;
            // ClearBuffer keeps the resolved _catalog so cross-switch backrefs
            // resolve (wheel uses size=1 backref records post-switch). Drops
            // in-progress reassembly buffer + per-session seq dedup.
            _catalogParser.ClearBuffer();
            // Same boundary, generation side: the wheel's END counter restarts low
            // on each session open, so the already-committed marker values from the
            // previous epoch would make CommitLiveSet mistake the new advertisement
            // for re-affirmation and drop it — freezing LiveCatalog (and with it the
            // synthesised profile and the channel-mapping grid) on a stale, possibly
            // incomplete generation. Cold starts additionally hit Reset() in
            // StartInner, which is a superset of this.
            _catalogParser.BeginCatalogEpoch();
            ResetDeviceLogPull();
            _nextFlagBase = 0;
            _activeSubscription = null;
            _sessionAckSeq = 0;
            _sessionLife.ClearContigAck();
            _dashboardDownloadTriggered = false;
            ConfigSessionLatch = 0;
            _preambleTickTarget = Math.Max(1, 1000 / _baseTickMs);
            // Default the cold-start preamble gate off; StartInner re-arms it
            // for true cold starts immediately after this call.
            _coldStartWheelGatePending = false;
            _coldStartGateLogged = false;
            // Fresh cycle: init handshake not yet sent (so the FF-session re-send
            // guard in ApplySubscription re-evaluates against this connection).
            _initHandshakeSession = 0;
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
            // 0x09 only. A 2026-08-16 experiment primed 0x0A first (the
            // session PitHouse's whole exchange rides, with live
            // reconciliation semantics) — but when THIS host primes both, the
            // wheel double-pushes and the 0x0A stream never yielded a
            // parseable full state; latching onto it starved the state
            // pipeline (Files list never populated). Migrating to 0x0A needs
            // its own investigation of PitHouse's full connect handshake —
            // see docs/protocol/dashboard-upload/config-rpc-session-09.md.
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
            var timer = new Timer(intervalMs) { AutoReset = true };
            timer.Elapsed += OnTimerElapsed;
            _sendTimer = timer;
            // A Stop() landing between StartInner's last gate and the assignment
            // above ran its Exchange against null — reclaim the orphan here.
            if (_state == TelemetryState.Idle)
            {
                var orphan = Interlocked.Exchange(ref _sendTimer, null);
                if (orphan != null) { orphan.Elapsed -= OnTimerElapsed; orphan.Dispose(); }
                return;
            }
            try { timer.Start(); }
            catch (ObjectDisposedException) { return; }   // lost to a concurrent Stop()
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
            // A CM2 / standalone-dashboard sender (target 0x12/0x14) ALWAYS opened host
            // sessions on its device, so it must ALWAYS close them — the wheel-centric
            // ShouldDriveDashboard() skip does NOT apply to it (post-decoupling that
            // predicate is false for a screenless/no-wheel rig whose CM2 is driven by
            // this very sender; gating on it left the CM2's 0x01/0x02/0x03 dangling).
            // Only the WHEEL-screen main sender (target 0x17) honors the screenless
            // skip: there Start() never opened a session and the closes would emit
            // stray dev=0x17 session frames to a wheel that doesn't speak the layer.
            bool cm2TargetSender = _targetDeviceId == MozaProtocol.DeviceMain
                                   || _targetDeviceId == MozaProtocol.DeviceDash;
            if (!cm2TargetSender && MozaPlugin.Instance?.ShouldDriveDashboard() == false) return;
            try { _sessionLife.SendSessionClose(0x01); } catch { }
            try { _sessionLife.SendSessionClose(0x02); } catch { }
            try { _sessionLife.SendSessionClose(0x03); } catch { }
            try { System.Threading.Thread.Sleep(100); } catch { }
        }

        // Top frames of the call chain into Stop(), for diagnosing which path
        // (watchdog / detection / switch) triggered a cooldown. Best-effort.
        private static string DescribeStopCaller()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false); // skip this + Stop
                var frames = st.GetFrames();
                if (frames == null) return "unknown";
                var sb = new System.Text.StringBuilder();
                int shown = 0;
                foreach (var f in frames)
                {
                    var m = f.GetMethod();
                    if (m == null) continue;
                    if (shown > 0) sb.Append(" ← ");
                    sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
                    if (++shown >= 4) break;
                }
                return sb.Length > 0 ? sb.ToString() : "unknown";
            }
            catch { return "unknown"; }
        }

        public void Stop()
        {
            // Was anything actually open on the wire? Sessions are only opened once a
            // Start gets past its silence wait into Starting/Preamble/Active; an Idle
            // sender has nothing open. Captured BEFORE the transition below so the gate
            // stamp at the end can skip a no-op close.
            bool hadLiveSessions = _state != TelemetryState.Idle;
            // Capture the call chain that led here — Stop()→Idle is the cooldown
            // path; several watchdogs and the device-detection/reconnect logic can
            // all reach it, and the bare "Stop()" reason didn't say which. The
            // top frames name the culprit in the diagnostics bundle.
            TransitionTo(TelemetryState.Idle, "Stop() ← " + DescribeStopCaller());
            _connection.MessageReceived -= _inboundDispatcher.OnMessageDuringPreamble;
            // Stop() is reachable concurrently (read thread, recovery workers,
            // poll timer, UI) — Exchange picks a single winner for the teardown.
            var timer = Interlocked.Exchange(ref _sendTimer, null);
            if (timer != null)
            {
                timer.Stop();
                timer.Elapsed -= OnTimerElapsed;
                timer.Dispose();
            }

            // Drop anything already queued or sitting in the OS write buffer —
            // otherwise frames keep flowing to the wheel for ~1.4 s after stop
            // (16 KB WriteBufferSize at 115200 baud). When two pipelines share this
            // connection, clear only THIS sender's slot lane so stopping/restarting
            // one doesn't blank the co-resident pipeline's frames.
            if (SharesConnection)
                _connection.ClearStreamSlots(StreamSlotBase, StreamBlockSize);
            else
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

            _sessionLife.ResetAckEvent();
            try { _mgmtResponseEvent.Reset(); } catch (ObjectDisposedException) { }
            // Disarm convergence — a torn-down pipeline shouldn't keep
            // firing kind=4 nudges into a session being closed.
            _postSwitchConvergence.Disarm();
            try { _uploader?.Reset(); } catch { }
            _sessions.Reset();
            _dispatcher.Reset();
            ResetDeviceLogPull();
            _session09InboundSeq = 0;
            // Reset the outbound seq counters under their guarding locks so a
            // tick still mid-burst can't clobber the reset (separate, non-nested
            // leaf locks — no ordering deadlock; each section is a field write).
            lock (_session09SeqLock)
            {
                _session09OutboundSeq = 0;
                _session09SeqSeeded = false;
                _session09SeedOpenSeq = -1;
            }
            // Re-arm so the next sess=0x09 device-init re-confirms the
            // canonical dashboard list to the wheel.
            _session09ReplySent = false;
            _watchdog.Reset();
            lock (_session02SeqLock) { _session02OutboundSeq = 0; }
            lock (_session01SeqLock) { _session01OutboundSeq = 0; }
            // Reset 0x0a seq for symmetry — fresh Start re-opens 0x0a from
            // zero wheel-side. Prevents stale-seq retransmits re-emitting
            // into a new session. See docs/protocol/sessions/chunk-format.md.
            _rpc.OutboundSeq = 0;
            _tierDefEmitter.Reset();
            _autoResolutionDone = false;
            _retransmitter.Clear();
            // Bump the HotSwitchCoordinator arm count so the catalog parser
            // treats the NEXT post-Start commit as a fresh-session boundary
            // (REPLACE _liveCatalog) instead of UNIONing the new wheel
            // session's catalog with the prior session's stale entries.
            // Critical on disconnect/reconnect: wheel display takes ~20 s
            // to boot after USB re-attach, so the first catalog batch from
            // the freshly-booted display lands long after Stop+Start with
            // no other signal we can use to detect "this is a different
            // wheel session." The arm-count bump is the explicit boundary.
            _hotSwitch.NotifySessionBoundary();
            // Take the seq lock so this Clear can't race a mid-flight
            // enumeration of the property-push dict. Pre-fix the unsynchronised
            // narrow window made the race invisible, but the new
            // _session02SeqLock holds the dict-touching code longer and
            // expanded the window enough to surface ConcurrentModification
            // exceptions during Stop+Start cycles under load.
            _propertyPushQueue.Clear();
            lock (_subscriptionResponseChunks) _subscriptionResponseChunks.Clear();
            Interlocked.Exchange(ref _subscriptionResponseDeadlineTicks, 0);
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

            // Arm the StartInner silence gate — but ONLY when this Stop actually
            // closed live sessions. The ~11 s gate exists so the wheel's sess=0x09
            // device-init can settle after we tear down a real session; a Stop() from
            // Idle (StartInner's own pre-open Stop on a fresh cold start, or a start
            // superseded before it opened anything) closed nothing on the wire, so
            // there is no interlock to wait out. Stamping unconditionally armed the
            // gate on a healthy cold start: one superseded no-op start left a "prior
            // Stop", so every subsequent start then honoured the full gate — and on a
            // shared bus the ~5 s EnsureCm2Pipeline reconcile kept superseding the
            // waiting start, re-stamping the gate so it never elapsed (CS-Pro + bus
            // CM2 livelock: _cm2Sender stuck Idle/frames=0, CM2 screen dark). A real
            // Active/Preamble→Stop (recovery, dashboard-switch restart, End()) still
            // stamps and still honours the gate.
            if (hadLiveSessions)
                _silenceGate.MarkStopped(System.DateTime.UtcNow.Ticks);
        }
    }
}
