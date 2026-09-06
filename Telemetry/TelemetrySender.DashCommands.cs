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
            => _propertyPushQueue.SendU32(kind, value);

        /// <summary>Push a u64-valued property (e.g. standby in milliseconds).</summary>
                public void SendSessionPropertyU64(uint kind, ulong value)
            => _propertyPushQueue.SendU64(kind, value);

        /// <summary>Push a single-byte-valued property (e.g. VGS display-rotation mode).</summary>
                public void SendSessionPropertyU8(uint kind, byte value)
            => _propertyPushQueue.SendU8(kind, value);

        /// <summary>
        /// Send a session-data chunk via <see cref="_connection"/> and register
        /// it with the retransmit queue so it gets re-emitted until acked. For
        /// non-chunk frames the retransmitter Track() is a no-op (ignored by
        /// shape check), so this is safe to call broadly.
        /// </summary>
        private void SendAndTrackChunk(byte[] frame)
        {
            NoteOutboundChunk(frame);
            _connection.Send(frame);
            _retransmitter.Track(frame);
        }

        /// <summary>Count a host-sent <c>7c:00 type=01</c> session data chunk
        /// against its session. Feeds the Diagnostics "Session traffic" table
        /// (its Out column read 0 for every session until this existed) and,
        /// more importantly, gives <see cref="Lifecycle.DisplayWatchdog"/>'s
        /// Context C a denominator: "no inbound on this lane" is only evidence
        /// of a dead binding once we have actually driven the lane.
        ///
        /// Covers the two telemetry chunk wrappers (this and
        /// <see cref="SendRawFrame"/>), which between them carry every tier-def,
        /// string-value and property-push chunk on the mgmt / tier-def lanes.
        /// The uploader / downloader / RPC lanes send straight through
        /// <c>ConnectionRef.Send</c> and are not counted.</summary>
        private void NoteOutboundChunk(byte[] frame)
        {
            // 7E [N] 43 <dev> 7C 00 [session] [type=01] [seq lo] [seq hi] …
            if (frame != null && frame.Length >= 10
                && frame[0] == MozaProtocol.MessageStart
                && frame[4] == 0x7C && frame[5] == 0x00 && frame[7] == 0x01)
            {
                BumpSessionCount(frame[6], outbound: true);
            }
        }

        /// <summary>Config-session variant: tracked regardless of target
        /// device so CM2 config sends get retransmit protection too (the
        /// broad wheel-only gate in Track stays for value-frame paths).</summary>
        private void SendAndTrackConfigChunk(byte[] frame)
        {
            _connection.Send(frame);
            _retransmitter.Track(frame, anyDevice: true);
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
                    "[AZOM] SendDashDisplayBrightness: skipping non-explicit 0 push " +
                    "(use allowZero=true for deliberate display-off)");
                return;
            }
            SendSessionPropertyU32(
                global::MozaPlugin.Telemetry.Sessions.SessionPropertyPushBuilder.KindDashBrightness,
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
        /// <returns><c>true</c> if a kind=4 frame was actually emitted on
        /// the wire (so callers know a Stop+Start cycle is now needed and
        /// the wheel's sess=0x09 timeout has been re-armed). <c>false</c>
        /// when emission was suppressed — disconnected, non-Active state,
        /// or still inside the post-emit cooldown window — in which case
        /// the wheel state has not changed and no follow-up restart is
        /// required.</returns>
        public bool SendDashboardSwitch(uint slotIndex, bool anchorSlotRoundTrip = true)
        {
            if (!_connection.IsConnected) return false;
            // Block kind=4 emission during the post-emit silence window or
            // any non-Active state. Sending kind=4 mid-restart races with
            // the wheel's session re-handshake — observed 2026-05-09: a
            // user's rapid double-click during the silence wait leaked a
            // kind=4 onto the wire BEFORE Start re-opened sessions, putting
            // the wheel into a state where it pushed corrupt backref-style
            // catalog records the parser couldn't decode.
            if (_state != TelemetryState.Active || IsInSilenceCooldown)
            {
                MozaLog.Debug(
                    $"[AZOM] SendDashboardSwitch slot={slotIndex} suppressed: " +
                    $"state={_state} cooldown={IsInSilenceCooldown}. " +
                    "User must wait for restart cycle to complete.");
                return false;
            }

            byte[] body = global::MozaPlugin.Telemetry.Sessions.SessionPropertyPushBuilder
                .BuildDashboardSwitchBody(slotIndex);
            _propertyPushQueue.SendBody(body);
            // Arm the UI cooldown (IsInSilenceCooldown) and the
            // SendDashboardSwitch self-gate above. The wheel's sess=0x09
            // binding-state timeout begins when it receives the kind=4,
            // so we also need to keep the UI from initiating another
            // switch until the wheel's window closes. The StartInner
            // silence sleep is armed separately by Stop() (see
            // SilenceGate.MarkStopped) — that handles the host-side reopen
            // protocol; this field handles UI affordances and double-
            // click suppression on SendDashboardSwitch itself.
            _silenceGate.MarkSwitchEmitted(System.DateTime.UtcNow.Ticks);
            // Record the slot we just bound so callers can detect
            // redundant subsequent emits (catalog probe + profile-apply
            // racing to the same slot is the common case).
            _slotTracker.NoteHostEmittedKind4((int)slotIndex);
            // Anchor the display watchdog's slot round-trip window for every
            // kind=4 source (resync probe, SwitchToProfile, convergence nudge)
            // — EXCEPT a convergence nudge to a slot the wheel already reports
            // it's on (anchorSlotRoundTrip=false). Re-stamping the window and
            // clearing the not-engaged debounce on an already-bound slot is
            // pointless churn, and it slows the watchdog's reaction if the
            // wheel later drifts off-slot. The slot-tracker note above still
            // fires so redundant-emit detection stays accurate.
            if (anchorSlotRoundTrip)
                _watchdog.NoteHostEmittedKind4((int)slotIndex);
            MozaLog.Debug(
                $"[AZOM] Sent dashboard-switch FF-record: slot={slotIndex} " +
                $"on FF session 0x{ResolveFfSession():X2} (mirror of tier-def session)");
            return true;
        }

        /// <summary>True while the post-emit silence enforcement gate is
        /// active (a kind=4 dashboard-switch frame went out on the wire
        /// within the last silence window and the wheel's sess=0x09
        /// binding-state timeout is still running). UI consumers should
        /// reflect this in their dashboard-switch affordance (disable
        /// dropdown / Start Test button) so the user can't trigger races
        /// against the in-flight Stop+Start.</summary>
        public bool IsInSilenceCooldown => _silenceGate.IsInSilenceCooldown;

        /// <summary>True only when the telemetry pipeline has completed
        /// its preamble and is delivering value frames. Callers gating
        /// dashboard-apply on channel readiness check this before
        /// emitting a kind=4 — if false, the wheel hasn't yet bound to
        /// the channel catalog and the switch would be silently lost.</summary>
        public bool IsActive => _state == TelemetryState.Active;

        /// <summary>
        /// Read-only flattened pipeline status — see <see cref="PipelinePhase"/>.
        /// Derived from <see cref="_state"/>, <see cref="_recovery"/>,
        /// <see cref="_hotSwitch"/>, and <see cref="_silenceGate"/>; no separate
        /// state is maintained. Callers should prefer this over stitching
        /// their own predicate from <c>IsActive</c> / <c>IsInSilenceCooldown</c>
        /// / <c>HotSwitchBurstPending</c> so all consumers agree on the same
        /// flattened status.
        /// </summary>
        public PipelinePhase Phase
        {
            get
            {
                // Park beats everything — recovery rate-limit exhausted /
                // sess=0x09 retry exhausted both land here, and the pipeline
                // won't auto-recover until the user toggles telemetry or hot-
                // swaps the wheel.
                if (_recovery.IsParked) return PipelinePhase.Parked;

                // Recovery debounce window — a watchdog has queued a Stop+Start;
                // the actual state may still be Active for a few ticks until
                // the worker thread lands.
                if (_recovery.IsRecoveryInFlight) return PipelinePhase.Recovery;

                // Steady-state sub-cases.
                if (_state == TelemetryState.Active)
                {
                    return _hotSwitch.IsBurstPending
                        ? PipelinePhase.HotSwitchBurst
                        : PipelinePhase.Active;
                }
                if (_state == TelemetryState.Starting
                    || _state == TelemetryState.Preamble)
                    return PipelinePhase.Starting;

                // Idle — distinguish "in silence wait" from "fully idle"
                // so UI can show "waiting for wheel to reconnect" vs.
                // "telemetry disabled".
                if (_silenceGate.IsInSilenceCooldown) return PipelinePhase.SilenceWait;
                return PipelinePhase.Idle;
            }
        }

        // ===== Internal accessors for DisplayWatchdog =====
        internal bool StateIsIdle => _state == TelemetryState.Idle;
        /// <summary>
        /// True while a <see cref="Start"/> is queued or executing — including the
        /// pre-open silence-gate wait, during which <see cref="_state"/> is deliberately
        /// still <c>Idle</c> (StartInner Stop()s before sleeping). Callers that poll
        /// "is it Idle? then (re)Start it" MUST also check this: a periodic reconcile
        /// (PollStatus → EnsureCm2Pipeline, ~5 s) would otherwise supersede an in-progress
        /// start every poll, re-stamping the ~11 s silence gate each time so it never
        /// elapses — livelocking the CM2 cold-start (CS-Pro + bus CM2: sender stuck Idle,
        /// CM2 screen dark while its LEDs work). _startCts is non-null for the whole
        /// Start()/StartInner span and nulled in its finally.
        /// </summary>
        internal bool StartInProgress
        {
            get { lock (_startCtsLock) return _startCts != null; }
        }
        internal bool StateIsActive => _state == TelemetryState.Active;
        internal bool ConnectionIsConnected => _connection.IsConnected;
        // Live connection reference for collaborators — never capture this:
        // Rebind() replaces _connection (CM2 standalone repoint).
        internal MozaSerialConnection ConnectionRef => _connection;
        internal Sessions.SessionInfo SessionsGetOrCreate(byte session) => _sessions.GetOrCreate(session);
        internal bool ConfigJsonHasLastState => _configJson.LastState != null;
        internal long ConfigJsonLastForwardGapUtcTicks => _configJson.LastForwardGapUtcTicks;
        internal int Session09InboundSeq => _session09InboundSeq;
        internal int CatalogCount => _catalogParser?.Count ?? 0;
        internal bool HasActiveSubscription => _activeSubscription != null;
        internal void SendRawFrame(byte[] frame)
        {
            NoteOutboundChunk(frame);
            _connection.Send(frame);
        }
    }
}
