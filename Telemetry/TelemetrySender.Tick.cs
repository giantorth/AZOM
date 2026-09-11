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
                // from a clean budget. Keep the cross-cycle FLAP history, though,
                // so a connection that keeps dropping+reconnecting+restarting
                // eventually parks instead of looping forever.
                try { _recovery.Reset(clearFlapHistory: false); } catch { }
            }

            if (_state == TelemetryState.Idle || !currentlyConnected)
                return;

            try
            {
                // Device display-log pull. Deliberately ABOVE the Preamble
                // branch and the no-tiers early return below: the log is
                // independent of whether telemetry is transmitting, so it must
                // run whenever the session layer is up — during preamble, and
                // on a wheel that has no bound dashboard (no tiers) at all.
                TickEmitDeviceLogPoll();

                // Dashboard-upload progress on the wheel's RPM bar. Sits with
                // the device-log pull ABOVE the Preamble branch and the
                // no-tiers early return below: the connect-time upload is
                // dispatched before this timer even starts and can run through
                // the whole preamble, and a wheel with no bound dashboard
                // uploads too. Internally paced and idempotent, so both
                // senders ticking it is harmless.
                Devices.Led.UploadProgressLedBar.Tick(MozaPlugin.Instance);

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
                // last synthesis; the steady-state cost is an allocation-free
                // catalog content hash.
                MaybeSwapProfileForCatalog();

                // Re-read _tiers: MaybeSwapProfileForCatalog may have rebuilt
                // them above (synthesised from wheel-advertised catalog when
                // no profile was loaded at Start time).
                var tiers = _tiers;
                if (tiers == null || tiers.Length == 0)
                    return;

                TickFireGameStartHandshake();
                // Value/string frames pause while a dashboard upload is in
                // flight: the wheel processes upload rounds at a few hundred
                // B/s (ground truth: 26-round upload, 7-40 s per 4 KB round)
                // and 30 ms value frames compete for the same serial + CPU
                // budget. PitHouse's own tick traffic during an upload is only
                // the 1 Hz display-config/keepalive class, which keeps running
                // below (polls, retransmits, slow path).
                bool uploadInFlight = _uploader?.IsUploadInFlight ?? false;
                if (!uploadInFlight)
                {
                    TickEmitValueFrames(tiers);
                    TickEmitStringValues();
                }

                TickEmitSequence();
                // Parity polls keep the wheel engaged during idle. Empirically
                // verified: turning them off entirely caused the dashboard to
                // freeze on last-value within ~5 min. ~1 Hz cadence is enough
                // at ~12% of PitHouse's full ~7-22 Hz wire cost.
                TickEmitPeripheralPolls();
                TickEmitLedStatePolls();
                TickEmitRetransmits();
                _tierDefEmitter.TickEmitTierDefBlindRetransmits();
                // A base-bridged dash whose type is still unknown (could be a CM1
                // group-0x35 device that never advertises a tier-def catalog) must NOT
                // trip the no-catalog engagement watchdog — it would burn restarts and
                // spam tier-def opens while the CM1 discriminator decides. See
                // MozaPlugin.TickCm1Discriminator.
                // Also held off while an upload is in flight: value frames are
                // paused (above) and the wheel goes content-quiet while writing
                // the bundle, so an engagement verdict here would restart the
                // pipeline mid-transfer. The upload's own per-round timeouts
                // (60 s) bound how long this suppression can last.
                if (!SuppressDisplayWatchdog && !uploadInFlight)
                    _watchdog.TickDisplayWatchdog();
                TickGrowSubscriptionIfCatalogStable();
                TickPostSwitchCatalogConvergence();

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
                    MozaLog.Warn($"[AZOM] Telemetry send error: {ex.GetType().Name}: {ex}");
                else
                    MozaLog.Warn(
                        $"[AZOM] Telemetry send error #{_consecutiveTickFailures}: " +
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
                            "[AZOM] Preamble extended: waiting for catalog (count=0). " +
                            $"Cap {extendedCap} ticks ({extendedCap * _baseTickMs} ms).");
                    }
                    return;
                }

                // Cold start only: even when the base advertised intrinsic
                // channels (merged Count>0), those may live on a DIFFERENT
                // session than the one the tier-def rides on. The wheel binds a
                // tier-def by the catalog idx of its OWN session, so emitting
                // before THAT session has a real catalog makes the wheel reject
                // the tier-def and close the session (verified: cold R5 base —
                // real 4-ch catalog only on sess=0x02, sess=0x01 degenerate
                // idx=0/empty — closed sess=0x01 after our tier-def). Hold the
                // transition until the tier-def's session has a real catalog
                // (≥1 valid URL record + a valid END u32), capped so a
                // screenless / never-ready wheel still proceeds. Warm and
                // hot-switch reloads skip this entirely
                // (_coldStartWheelGatePending=false → zero latency); on a warm
                // wheel the tier-def session's catalog is already real so the
                // gate passes on the first check.
                // Gate on the TIER-DEF session only (mgmt — where the tier-def
                // rides per ResolveTierDefSession). The old "either session" check
                // passed on a flag-only (0x02) cold catalog, then the emitter
                // forced the tier-def onto the degenerate mgmt (0x01) and the wheel
                // rejected it. StartInner's pre-init catalog wait now re-cycles the
                // sessions so the wheel re-advertises on mgmt before we get here,
                // so on a healthy cold start this gate passes on the first check
                // (no dead latency); the cap only bites a screenless / never-ready
                // wheel or one that wouldn't re-advertise after the re-requests.
                byte gateMgmt = _mgmtPort != 0 ? _mgmtPort : (byte)0x01;
                // Dynamic session follow: gate on whichever session the tier-def will
                // actually ride. ResolveTierDefSession() follows the catalog to the flag
                // session when the wheel committed it there (Form B), so a flag-only
                // catalog now PASSES the gate and we proceed to Active on 0x02 instead
                // of re-provoking/giving up (which wedged the cold start until a manual
                // telemetry toggle). Falls back to mgmt only when neither session has a
                // real catalog yet (true cold start / screenless).
                bool tierDefSessionCatalogReady =
                    _catalogParser.HasRealCatalogOnSession(ResolveTierDefSession());
                if (_coldStartWheelGatePending && !tierDefSessionCatalogReady)
                {
                    // Radar/track-map dashboards advertise a huge catalog (~223 ch,
                    // ~19 s to stream), so the preamble hold must wait far longer than
                    // the ordinary 6 s or it cuts the catalog off and wedges.
                    int catalogWaitMs =
                        (MozaPlugin.Instance?.Settings?.EnableRadarTrackMapChannels ?? false)
                            ? 30000 : PreambleSess01CatalogWaitMaxMs;
                    int catalogCap = Math.Max(_preambleTickTarget,
                        catalogWaitMs / Math.Max(1, _baseTickMs));
                    if (_tickCounter < catalogCap)
                    {
                        if (!_coldStartGateLogged)
                        {
                            _coldStartGateLogged = true;
                            MozaLog.Info(
                                $"[AZOM] Cold-start preamble hold: waiting for a real catalog " +
                                $"(valid URL + END u32) on tier-def sess 0x{gateMgmt:X2} " +
                                $"before first tier-def. Cap {catalogCap} ticks " +
                                $"({PreambleSess01CatalogWaitMaxMs} ms).");
                        }
                        return;
                    }
                    // The gate timed out. Distinguish the WRONG-SESSION WEDGE from a
                    // genuinely screenless / never-ready wheel:
                    //   • WEDGE — the wheel advertised a real catalog+END on the FLAG
                    //     session but NOT the tier-def session. Emitting the tier-def
                    //     now carries END=0 against the wheel's real END: the wheel
                    //     either CLOSEs the session (REJECT) or silently ignores it (no
                    //     bind, no close → stuck Active + blank, no recovery trigger).
                    //     Following the catalog to the flag session doesn't bind either
                    //     (its advertisement is malformed in this state). The ONLY thing
                    //     that binds is the wheel re-advertising on the tier-def session,
                    //     which a full cold re-provoke (Stop → ~11 s sess=0x09 settle →
                    //     Start, re-rolling the wheel's Form-A/B choice) forces. Re-provoke
                    //     and RETRY, bounded, instead of emitting a doomed tier-def.
                    //   • SCREENLESS / never-ready — no real catalog on the flag session
                    //     either → proceed to Active (nothing to bind / re-provoke).
                    byte flagSess = ResolveFfSession();
                    bool catalogOnFlagSession = flagSess != gateMgmt
                        && _catalogParser.HasRealCatalogOnSession(flagSess);
                    if (catalogOnFlagSession && _coldStartReprovokeAttempts < MaxColdStartReprovokes)
                    {
                        _coldStartReprovokeAttempts++;
                        MozaLog.Warn(
                            $"[AZOM] Cold-start catalog WEDGE: real catalog on flag sess 0x{flagSess:X2} " +
                            $"but not tier-def sess 0x{gateMgmt:X2}. Emitting END=0 here won't bind " +
                            $"(and 0x{flagSess:X2} is malformed) — forcing a cold re-provoke so the wheel " +
                            $"re-advertises on 0x{gateMgmt:X2} (attempt {_coldStartReprovokeAttempts}/{MaxColdStartReprovokes}).");
                        System.Threading.Interlocked.Exchange(ref _forceColdReprovoke, 1);
                        _coldStartWheelGatePending = false;   // re-armed by the re-provoked Start
                        RestartForSwitch();
                        return;   // do NOT proceed to Active / do NOT emit the doomed tier-def
                    }
                    MozaLog.Warn(
                        $"[AZOM] Cold-start catalog wait exceeded {PreambleSess01CatalogWaitMaxMs} ms " +
                        $"without a real catalog on tier-def session 0x{gateMgmt:X2} " +
                        $"({(catalogOnFlagSession ? $"re-provoke budget exhausted after {_coldStartReprovokeAttempts} attempts" : "screenless or slow wheel")}) " +
                        $"— proceeding to Active anyway (watchdog will recover if binding fails). " +
                        $"parser: {_catalogParser.DescribeSession(gateMgmt)} {_catalogParser.DescribeSession(flagSess)}");
                    // One-shot: don't re-hold if preamble is somehow re-entered.
                    _coldStartWheelGatePending = false;

                    // Stale-profile guard. Without a real catalog this session, a
                    // synthesised profile still loaded is from a PRIOR session/dashboard
                    // (the persistent wire survives reload) or older code — emitting it
                    // streams wrong-channel values and, for a pre-fix all-ri-at-pkg-30
                    // tiering, floods the link (the off-the-rails regression). Drop it so
                    // value frames hold until the wheel re-advertises a catalog and
                    // MaybeSwapProfileForCatalog rebuilds a profile that matches. A
                    // user-loaded mzdash profile (Name != CatalogProfileName) is left intact.
                    if (_profile != null && _profile.Name == CatalogProfileName)
                    {
                        MozaLog.Info(
                            "[AZOM] Cold-start gave up without a real catalog — dropping the stale " +
                            "synthesised profile so value frames hold until the wheel re-advertises " +
                            "(prevents emitting a prior/degenerate tiering).");
                        Profile = null;
                    }
                }

                TransitionTo(TelemetryState.Active, "preamble countdown elapsed");
                // Anchor for the DisplayWatchdog's engagement grace
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

        /// <summary>Cold-start only: ceiling on the preamble→Active hold while
        /// waiting for the tier-def session to carry a REAL catalog (valid URL
        /// record + valid END u32). On wheels whose cold-start catalog lands on
        /// the flag session (e.g. this R5/CS-Pro base advertises on sess=0x02
        /// while tier-def rides sess=0x01), StartInner's pre-init catalog wait
        /// now re-cycles the sessions so the wheel re-advertises on the tier-def
        /// session before reaching this hold — so on a healthy cold start this
        /// passes immediately. The cap only bounds the residual wait for a
        /// screenless / never-ready wheel, or one that won't re-advertise on the
        /// tier-def session after StartInner's re-requests (the DisplayWatchdog
        /// reject-recovery is the last-resort backstop there).
        /// Only consulted while _coldStartWheelGatePending is true.</summary>
        private const int PreambleSess01CatalogWaitMaxMs = 6_000;

        /// <summary>Continuous catalog absorption. Wheel pushes URL records
        /// in batches with ~1.2s gaps; parse every time the buffer grows and
        /// merge non-destructively so URLs are never dropped.</summary>
        private void TickAbsorbCatalogIfChanged()
        {
            int curLen = _catalogParser.BufferLength;
            // Also parse when chunks are parked behind a hole: in that state the
            // buffer does NOT grow, so the growth test alone would never fire and
            // the reassembler's stall escape could never run.
            if (curLen > _catalogParser.LastParsedBufferLen || _catalogParser.HasPendingChunks)
            {
                // TryParse internally trims bytes up to the last committed
                // END marker, so in normal operation buffers stay bounded
                // to in-flight content (typically a few hundred bytes per
                // dashboard switch). The hard-limit wipe below only fires
                // when the wheel has gone N kB without an END marker the
                // parser could trim against (e.g. wheel keeps re-sending
                // back-refs without bounding them with a new END value).
                _catalogParser.TryParse();
                const int HardLimitBytes = 65536;
                if (_catalogParser.MaxSessionBufferLength > HardLimitBytes)
                {
                    _catalogParser.ClearOverflowingSessions(HardLimitBytes);
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
        // Serial-budget governor. The wheel link is 115200 8N1 (~11520 B/s usable);
        // value frames must never exceed it or the write queue backs up, acks stall,
        // and the session goes off the rails (observed: a stale/degenerate profile or
        // a full-grid track-map ran the link to 161% of ceiling). Reserve headroom for
        // the LED/poll/string/retransmit/tier-def traffic and cap value frames here.
        // When a rolling 1 s total would exceed the budget, the lowest-priority
        // (slowest, highest package_level) tiers are shed for the rest of that second.
        // The emit loop runs fastest-tier-first, so the near-radar fast tier and
        // normal fast channels are always sent; the radar overflow and track-map
        // (the big, optional, variable load) shed first under pressure.
        // The link is 115200 8N1 (~11520 B/s). ~9 kB/s for value frames leaves
        // ~2.5 kB/s for the LED/parity-poll, string, retransmit and tier-def traffic
        // that shares it — most of the link is usable, this isn't a tight cap. It's a
        // backstop: the radar slot cap + dynamic per-grid emission below keep a normal
        // radar dashboard's value frames well under this, so it only engages on a
        // pathological channel set, shedding the slow/big tiers first.
        private const int ValueFrameBudgetBytesPerSec = 9000;
        // A tier whose package level is at/above this is sheddable under budget
        // pressure (radar overflow 132, track-map 500, 2000-ms slow channels). The
        // radar fast tier (66) and normal fast channels (30) are always sent.
        private const int SheddableTierMinPackageLevel = 120;
        private int _vfBudgetWindowStartMs;
        private int _vfBudgetBytesThisWindow;
        private int _vfSheddedFramesThisWindow;
        private int _vfSheddingLastLogMs;

        // Dynamic radar/track-map emission. The wheel advertises a huge fixed slot
        // array (ri0..ri183 + Location_N) but only the cars actually on track exist;
        // an empty slot's tier is pure wire waste. Each tick we track the highest
        // live car slot and skip any radar/track-map sub-tier whose lowest slot is
        // above it — so emission scales with the grid (a 1v1 sends a couple of slots,
        // a full grid sends them all). High-water with a hold so a car momentarily
        // dropping to (0,0) for a frame doesn't blink its tier; releases when the grid
        // genuinely shrinks (session/track change). The wheel masks vacated slots via
        // OpponentCount, so skipping leaves no ghost car.
        private int _radarActiveSlotHighWater = -1;
        private int _radarActiveSlotHoldStartMs;
        private const int RadarActiveSlotHoldMs = 3000;

        // Highest live car slot this frame (max CarLocations index with a non-zero
        // position), smoothed: rises instantly when a car appears at a higher slot,
        // and only falls RadarActiveSlotHoldMs after the grid genuinely shrinks
        // (session/track change, cars retiring). Returns -1 when there is no per-car
        // data — harmless, as such profiles carry no slot tiers to gate.
        private int ComputeRadarActiveSlotHighWater(in GameDataSnapshot snapshot, int nowMs)
        {
            var locs = snapshot.CarLocations;
            int raw = -1;
            if (locs != null)
            {
                for (int k = 0; k < locs.Length; k++)
                    if (locs[k].X != 0f || locs[k].Z != 0f) raw = k;
            }
            if (raw >= _radarActiveSlotHighWater)
            {
                _radarActiveSlotHighWater = raw;
                _radarActiveSlotHoldStartMs = nowMs;
            }
            else if (unchecked(nowMs - _radarActiveSlotHoldStartMs) > RadarActiveSlotHoldMs
                     || unchecked(nowMs - _radarActiveSlotHoldStartMs) < 0)
            {
                _radarActiveSlotHighWater = raw;   // grid shrank for real — release
                _radarActiveSlotHoldStartMs = nowMs;
            }
            return _radarActiveSlotHighWater;
        }

        private void TickEmitValueFrames(TierState[] tiers)
        {
            // Only build the per-car track-map/radar arrays when an active tier
            // actually has a Location/Radar channel. With those channels disabled
            // (the shipped default) this skips GameDataSnapshot's reflection chain
            // and per-opponent allocation/loop — dead work that scaled with the
            // game's car count (e.g. open-world traffic). Cheap to recompute: a
            // few bool reads over the 1-4 tiers.
            bool needCarPositions = false;
            for (int t = 0; t < tiers.Length; t++)
            {
                if (tiers[t].Builder?.NeedsCarPositions == true) { needCarPositions = true; break; }
            }
            GameDataSnapshot snapshot = TestMode
                ? default
                : GameDataSnapshot.FromStatusData(_latestGameData, needCarPositions);

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
            {
                // Periodic reminder so the user sees in SimHub.txt that
                // value frames are being suppressed BECAUSE of the per-
                // overlay toggle, not a plugin malfunction. The transition
                // log fires once on the disable event but is easily missed
                // if the user goes back to investigate minutes later;
                // 30-second cadence keeps the log honest without spamming.
                int nowTickMs = Environment.TickCount;
                if (nowTickMs - _profileTelemetryDisabledLastReminderTickMs >= SuppressedReminderMs)
                {
                    _profileTelemetryDisabledLastReminderTickMs = nowTickMs;
                    MozaLog.Info(
                        "[AZOM] Wheel telemetry is disabled for the active SimHub overlay " +
                        "(value-frame emission suppressed). Toggle telemetry on for this overlay " +
                        "to resume dashboard updates.");
                }
                return;
            }

            byte subFlagBase = _activeSubscription?.FlagBase ?? 0;

            // Governor window roll: reset the per-second value-frame byte tally and
            // emit a throttled note if any tiers were shed in the window just closed.
            int vfNowMs = Environment.TickCount;
            if (_vfBudgetWindowStartMs == 0
                || unchecked(vfNowMs - _vfBudgetWindowStartMs) >= 1000
                || unchecked(vfNowMs - _vfBudgetWindowStartMs) < 0)
            {
                if (_vfSheddedFramesThisWindow > 0
                    && unchecked(vfNowMs - _vfSheddingLastLogMs) >= 5000)
                {
                    MozaLog.Debug(
                        $"[AZOM] value-frame governor: shed {_vfSheddedFramesThisWindow} low-priority " +
                        $"tier frame(s) last window to stay under {ValueFrameBudgetBytesPerSec} B/s " +
                        $"(link ~11520 B/s). Fast/near-radar channels unaffected.");
                    _vfSheddingLastLogMs = vfNowMs;
                }
                _vfBudgetWindowStartMs = vfNowMs;
                _vfBudgetBytesThisWindow = 0;
                _vfSheddedFramesThisWindow = 0;
            }

            // Dynamic per-grid radar slot ceiling: the highest live car slot this
            // frame, held briefly so a one-frame dropout doesn't blink a tier and
            // released when the grid shrinks. Radar/track-map tiers whose lowest slot
            // is above this carry only absent cars and are skipped below.
            int radarActiveSlot = ComputeRadarActiveSlotHighWater(in snapshot, vfNowMs);

            for (int i = 0; i < tiers.Length; i++)
            {
                var tier = tiers[i];
                // Skip a radar/track-map sub-tier when no live car reaches its slot
                // range (MinRadarSlot == 0 => normal or slot-0 tier, always emitted).
                if (tier.MinRadarSlot > 0 && tier.MinRadarSlot > radarActiveSlot)
                    continue;
                // Phase each tier by its index within its emit window so tiers
                // sharing a package_level don't all fire on the same tick. A
                // track-map dashboard splits its 63 location_t channels into ~11
                // sub-tiers all at pkg=500 (TickInterval=16); without this offset
                // they burst together — ~11 value frames back-to-back in one
                // 33 ms tick (~200 fps on the wire), overrunning the 115200-baud
                // ceiling and starving the LED + dashboard write pipeline.
                // Phasing spreads them one-per-tick (≤33 fps, the telemetry wire
                // norm); each tier's rate is unchanged (still once per
                // TickInterval), only its phase shifts.
                if (_tickCounter % tier.TickInterval != i % tier.TickInterval)
                    continue;

                // Serial-budget governor: once the rolling per-second value-frame
                // budget is exhausted, shed low-priority (slow/big) tiers — radar
                // overflow + track-map — for the rest of the window so no profile can
                // overrun the link. Fast tiers (radar fast, normal) are never shed;
                // the loop is fastest-first so they consume budget before the
                // sheddable ones are reached.
                var prof = tier.Builder.Profile;
                if (prof != null && prof.PackageLevel >= SheddableTierMinPackageLevel)
                {
                    int estBytes = prof.TotalBytes + 15;   // tier data + vf header + framing
                    if (_vfBudgetBytesThisWindow + estBytes > ValueFrameBudgetBytesPerSec)
                    {
                        _vfSheddedFramesThisWindow++;
                        continue;
                    }
                }

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
                        $"[AZOM] TIER-EMIT t[{i}] flag=0x{flagByte:X2} " +
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
                    SendStreamSlot((int)StreamKind.TierDash0 + i, frame);
                else
                    _connection.Send(frame);

                // Count toward the per-second governor budget (essential tiers count
                // too, so they reserve their share and the sheddable tiers shed once
                // the link's value-frame budget is spent).
                _vfBudgetBytesThisWindow += frame.Length;

                if (i == 0)
                {
                    _framesSent++;
                }
            }
        }

        /// <summary>Out-of-band string-channel value push, type=0x05. Strings
        /// (Telemetry.json compression=string — TrackId, CarModel,
        /// SessionTypeName, etc.) cannot be bit-packed into the value frame;
        /// they ride a separate sub-msg on the tier-def/catalog session
        /// (ResolveTierDefSession() — 0x01 or the FlagByte session, whichever
        /// the wheel advertised its catalog on, matching PitHouse). Cadence:
        /// emit immediately on value change with a 15-second keepalive floor
        /// for unchanged channels — matches the 14.76 s mean cadence observed
        /// in PitHouse capture bridge-20260514-204307.jsonl. Format and
        /// discovery in docs/protocol/sessions/session-0x01-channel-protocol.md.
        ///
        /// The resolved session's seq lock is acquired around each emit, the
        /// same lock SendTierDefinition and the value-frame/FF paths take on
        /// that session, so string chunks reserve a contiguous seq range and
        /// cannot interleave into another chunk train. Emission is on the tick
        /// handler (single-entry via _tickInProgress).</summary>
        private void TickEmitStringValues()
        {
            if (!TestMode && (!_gameRunning || !_profileTelemetryEnabled)) return;
            // String sources change on the order of minutes (TrackId, CarModel…);
            // resolving every channel's property + ToString per 30 ms tick was
            // pure churn. Poll at ~4 Hz — a change is picked up within ≤250 ms,
            // far inside the 15 s keepalive floor.
            if (_tickCounter % StringPollTickInterval != 0) return;
            EmitStringChannels(force: false);
        }

        private const int StringPollTickInterval = 8; // ~4 Hz at the 30 ms base tick
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

            // Strings ride the SAME session as the tier-def/catalog, NOT a
            // hardcoded 0x01. PitHouse puts type-0x05 string pushes on the
            // tier-def session (W13/FSR2: catalog+tier-def+strings on 0x02,
            // FF-records on 0x01). When the wheel advertises its catalog on the
            // FlagByte session, ResolveTierDefSession() returns it and the
            // tier-def follows — strings must follow too, or the wheel never
            // binds them (same failure SendTierDefinition documents: emit on
            // the wrong session → wheel acks then closes it). On wheels whose
            // catalog is on 0x01 (e.g. CS Pro) this resolves to 0x01 and
            // behaviour is unchanged. Mirrors SendTierDefinition's session/seq
            // selection so chunks share the per-session seq + lock.
            byte session = ResolveTierDefSession();
            bool onFlagByte = session == FlagByte && FlagByte != 0;
            object seqLock = onFlagByte ? _session02SeqLock : _session01SeqLock;
            lock (seqLock)
            {
                int seq = onFlagByte
                    ? Math.Max(2, _session02OutboundSeq)
                    : Math.Max(2, _session01OutboundSeq);
                var frames = Frames.TierDefinitionBuilder.ChunkMessage(
                    msg, session, ref seq, deviceId: _targetDeviceId);
                // SendAndTrackChunk instead of Send: strings ride the tier-def
                // session just like tier-def and FF-record property pushes, so a
                // lost string chunk gets retransmitted until acked instead of
                // waiting for the 15 s keepalive to re-send a fresh value. Most
                // strings are acked-and-dropped on the first send; the
                // protection only fires when one actually gets lost.
                foreach (var f in frames)
                    SendAndTrackChunk(f);
                if (onFlagByte) _session02OutboundSeq = seq;
                else _session01OutboundSeq = seq;
            }
        }

        /// <summary>Diagnostic: emit every string channel in the current
        /// profile right now, bypassing the change-detect + keepalive gate.
        /// Used by the auto-test harness (PhaseStringBurst) to produce a
        /// clearly-labelled wire-trace window even when no game is running
        /// and steady-state cadence is silent. Updates dedup state so the
        /// subsequent tick doesn't immediately re-emit.</summary>
        public void ForceStringEmitAll() => EmitStringChannels(force: true);

        /// <summary>Sequence-counter, gated on gameRunning because PitHouse only
        /// emits it while a game is actively driving telemetry — bursting it at
        /// idle is the largest plugin-vs-PitHouse drift source observed in
        /// 2026-04-29 captures.</summary>
        private void TickEmitSequence()
        {
            if (!TestMode && (!_gameRunning || !_profileTelemetryEnabled)) return;
            // No static FD DE "enable" here: wheels need none (CS Pro capture:
            // zero FD DE on the wire), and the CM2's live bitmask is owned by
            // MozaDashLedDeviceManager — a static zero mask streamed at tick
            // rate kills LEDs on whatever device it hits.
            if (SendSequenceCounter)
                SendStreamSlot((int)StreamKind.Sequence, _frames.BuildSequenceCounterFrame());
        }

        /// <summary>Peripheral output polls (handbrake + pedals) at ~1 Hz,
        /// staggered across ticks so the writes don't pile into a single
        /// 4ms-paced burst.</summary>
        private void TickEmitPeripheralPolls()
        {
            int slow = Math.Max(8, 1000 / _baseTickMs); // ~1Hz cycle (33 ticks @ 30ms base)
            int phase = _tickCounter % slow;
            if (phase == 0)             _connection.Send(Frames.TelemetryFrameCache.HandbrakePresenceFrame);
            else if (phase == slow / 5) _connection.Send(Frames.TelemetryFrameCache.HandbrakeOutputFrame);
            else if (phase == 2 * slow / 5)
            {
                _connection.Send(Frames.TelemetryFrameCache.PedalThrottleOutFrame);
                _connection.Send(Frames.TelemetryFrameCache.PedalBrakeOutFrame);
                _connection.Send(Frames.TelemetryFrameCache.PedalClutchOutFrame);
            }
        }

        /// <summary>LED state polls. Group 1 ~1 Hz, group 2 ~0.2 Hz. Load-bearing
        /// for per-group telemetry engagement on the GS V2 Pro — see the field-
        /// block comment near <c>TelemetryFrameCache.LedStatePollGroup1</c>.</summary>
        private void TickEmitLedStatePolls()
        {
            int slow = Math.Max(8, 1000 / _baseTickMs);
            if (_tickCounter % slow == 3 * slow / 5)
                _connection.Send(Frames.TelemetryFrameCache.LedStatePollGroup1);
            if (_tickCounter % (slow * 5) == 4 * slow / 5)
                _connection.Send(Frames.TelemetryFrameCache.LedStatePollGroup2);
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

        /// <summary>Widget-state poll cycle: one frame per ~1 Hz slow tick,
        /// rotating through the 63 live probes in SendOneWidgetPoll, so each
        /// individual probe recurs every ~63 s. PitHouse's own capture cadence
        /// is ~0.2/s per probe — we stay well under it.</summary>
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
                SendStreamSlot((int)StreamKind.Mode, _frames.ModeFrame);
            if ((_slowCounter & 1) == 1)
                SendDisplayConfig();
            else if (_slowCounter % 8 == 0)
                Send28xPoll();
            SendSession09Keepalive();
        }

        // ── Device display-log pull (FF kind=14 request / kind=15 receipt) ──
        // The wheel display runs a Linux MOZADash app with its own logger.
        // kind=14 asks for up to N lines; the device answers with a b2h kind=14
        // zlib'd UTF-16BE line list; kind=15 tells it how many we consumed, and
        // it drops that many. See docs/protocol/sessions/session-0x02-ff-init.md
        // § Device log pull.

        /// <summary>Lines requested per pull. PitHouse always asks for 100 —
        /// 1468/1468 requests across bridge-20260731-064830.jsonl.</summary>
        private const uint DeviceLogLinesPerPull = 100;

        /// <summary>Milliseconds between steady-state pulls.</summary>
        private const int DeviceLogPollIntervalMs = 60_000;

        // Next pull due, in UTC ticks. 0 = due now — so the first pull of a
        // connection goes out on the first tick after the sessions open rather
        // than a minute later. That first pull is the valuable one: it drains
        // the backlog the display accumulated while nothing was asking, which
        // is where crash backtraces live (the reference capture returned lines
        // dated 8 and 19 days before the session).
        private long _deviceLogNextPullUtcTicks;
        private int _pendingLogReceipt;
        // Session the last payload arrived on; the receipt goes back there.
        private int _deviceLogAckSession;
        private bool _deviceLogDecodeFailLogged;
        private bool _deviceLogFirstPullLogged;
        private int _deviceLogRequestsSent;
        private int _deviceLogPayloadsReceived;
        private int _deviceLogLastRequestSession;

        /// <summary>Device-log pull counters for the Diagnostics tab. Answers
        /// "no log lines showed up — did we ever ask, and did anything come
        /// back?" without needing a wire trace.</summary>
        internal string DeviceLogPullStatus
        {
            get
            {
                int sent = System.Threading.Interlocked.CompareExchange(ref _deviceLogRequestsSent, 0, 0);
                int got = System.Threading.Interlocked.CompareExchange(ref _deviceLogPayloadsReceived, 0, 0);
                int sess = System.Threading.Interlocked.CompareExchange(ref _deviceLogLastRequestSession, 0, 0);
                return $"{DeviceLogSourceName}: requests={sent} payloads={got} " +
                       $"sess=0x{sess:X2} state={_state}";
            }
        }
    }
}
