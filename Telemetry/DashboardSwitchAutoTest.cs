using System;
using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Single-switch auto test harness driven by <see cref="TelemetrySender"/>.
    ///
    /// Flow:
    ///   1. Wait for initial subscription to settle (SubscriptionGen ≥ 1).
    ///   2. Enable TestMode for <see cref="PreSwitchTestMs"/> — captures the
    ///      starting dashboard's wire-level test pattern as baseline.
    ///   3. Determine target slot: ALWAYS slot 1 (alphabetical second dash) when
    ///      starting on slot 0, else slot 0. Deterministic for wire-diff captures.
    ///   4. Resolve target profile via callback, then call SwitchToProfile
    ///      atomically (no race with a separate Profile-set + SendDashboardSwitch).
    ///   5. Wait for SubscriptionGen to bump (or timeout).
    ///   6. TestMode for <see cref="PostSwitchTestMs"/> on the new dashboard.
    ///   7. Finish.
    ///
    /// At every state transition we inject a phase-marker frame on the wire
    /// (grp=0x55 dev=0x55 cmd="MK" + phase id) via
    /// <see cref="TelemetrySender.SendPhaseMarker(byte)"/>. The wheel ignores it;
    /// the frame appears in the wire trace so post-mortem tooling can align
    /// runs by phase boundary.
    ///
    /// <see cref="Reset"/> re-arms the harness for another run without reconstruction.
    /// Triggered by <see cref="UI.MozaPluginSettings.EnableAutoTestOnConnect"/>.
    /// </summary>
    internal sealed class DashboardSwitchAutoTest
    {
        private enum State
        {
            Idle, PreSwitchTest, StringBurst, SwitchPending, WaitRenegotiate,
            PostSwitchTest, Done,
        }

        // Phase ids written to the wire trace via TelemetrySender.SendPhaseMarker.
        // Stable values so post-mortem tooling can search for them.
        private const byte PhaseEnterIdle           = 0x10;
        private const byte PhaseEnterPreSwitchTest  = 0x11;
        private const byte PhaseEnterSwitchPending  = 0x12;
        private const byte PhaseEnterWaitRenegotiate= 0x13;
        private const byte PhaseEnterPostSwitchTest = 0x14;
        // StringBurst sandwiches the PreSwitchTest and SwitchPending phases:
        // forces every profile.StringChannels entry onto the wire bypassing
        // change-detect + keepalive gates so the JSONL trace contains a
        // distinctly-bounded window where the operator can verify which
        // string channels reached the wheel.
        private const byte PhaseStringBurst         = 0x15;
        private const byte PhaseEnterDone           = 0x1F;

        // Hold time after the string burst is fired before transitioning to
        // SwitchPending. Long enough that any wheel-side type=0x06 acks for
        // the burst land inside the same window in the wire trace.
        private const int StringBurstHoldMs = 1000;

        // Resolves a dashboard name to its parsed MultiStreamProfile. Implemented
        // by the host (MozaPlugin) since profile parsing involves the cache + builtins.
        public delegate MultiStreamProfile? ProfileResolver(string dashName);
        // Returns the dashboard cache (nullable) for the fallback dash-list path.
        public delegate DashboardCache? DashCacheResolver();
        // Called when the auto-test settles on a target — used to persist the
        // user-visible TelemetryProfileName so the UI reflects what the auto-test did.
        public delegate void TargetChosenCallback(string dashName);

        private readonly TelemetrySender _telemetry;
        private readonly ProfileResolver _resolveProfile;
        private readonly DashCacheResolver _resolveDashCache;
        private readonly TargetChosenCallback? _onTargetChosen;

        private State _state = State.Idle;
        private int _elapsedMs;
        private int _targetSlot = -1;
        private int _startSlot = -1;
        private int _prevSubscriptionGen;
        private int _framesAtPhaseStart;
        private IReadOnlyList<string>? _dashList;

        private const int IdleTimeoutMs = 30000;
        private const int PreSwitchTestMs = 5000;
        private const int RenegotiateTimeoutMs = 10000;
        private const int PostSwitchTestMs = 5000;

        public DashboardSwitchAutoTest(
            TelemetrySender telemetry,
            ProfileResolver resolveProfile,
            DashCacheResolver resolveDashCache,
            TargetChosenCallback? onTargetChosen = null)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _resolveProfile = resolveProfile ?? throw new ArgumentNullException(nameof(resolveProfile));
            _resolveDashCache = resolveDashCache ?? throw new ArgumentNullException(nameof(resolveDashCache));
            _onTargetChosen = onTargetChosen;
        }

        // Re-arm the harness so the next Tick runs a fresh single-switch sequence
        // against the currently-active dashboard. Useful when a caller wants
        // multiple switches per session for repeated wire-trace captures.
        public void Reset()
        {
            _state = State.Idle;
            _elapsedMs = 0;
            _targetSlot = -1;
            _startSlot = -1;
            _prevSubscriptionGen = 0;
            _framesAtPhaseStart = 0;
            _dashList = null;
        }

        public bool IsDone => _state == State.Done;

        public void Tick(int tickMs)
        {
            _elapsedMs += tickMs;
            switch (_state)
            {
                case State.Idle: TickIdle(); break;
                case State.PreSwitchTest: TickPreSwitchTest(); break;
                case State.StringBurst: TickStringBurst(); break;
                case State.SwitchPending: TickSwitchPending(); break;
                case State.WaitRenegotiate: TickWaitRenegotiate(); break;
                case State.PostSwitchTest: TickPostSwitchTest(); break;
                case State.Done: break;
            }
        }

        private void TickIdle()
        {
            int gen = _telemetry.SubscriptionGen;
            if (gen == 0) return; // wait for first subscription

            // Test-mode-only operation: the harness's only job is to flip
            // TestMode on once the initial subscription has settled, then
            // sit in Done. Dashboard switching, string burst, and post-switch
            // phases are all suppressed so the wheel keeps streaming TestSignal
            // data on a single dashboard — used to investigate dashboard-update
            // breakdowns that appear without any switching action.
            string currentName = _telemetry.ActiveProfileName ?? "?";
            _telemetry.SendPhaseMarker(PhaseEnterPreSwitchTest);
            _telemetry.TestMode = true;
            MozaLog.Debug(
                $"[Moza] AUTO-TEST: TestMode-only enabled dash=\"{currentName}\" " +
                $"subGen={gen} — staying in TestMode, no switching");
            _state = State.Done;
        }

        private void TickPreSwitchTest()
        {
            if (_elapsedMs < PreSwitchTestMs) return;

            int frames = _telemetry.FramesSent - _framesAtPhaseStart;
            string startName = _startSlot >= 0 && _dashList != null && _startSlot < _dashList.Count
                ? _dashList[_startSlot] : "?";
            MozaLog.Debug(
                $"[Moza] AUTO-TEST: pre-switch test done dash=\"{startName}\" " +
                $"frames={frames} {(frames > 0 ? "PASS" : "FAIL")}");

            // Keep TestMode on through the string burst so the burst's string
            // values come from TestSignal (deterministic "STR-Name") rather
            // than empty SimHub property reads if no game is running.
            _elapsedMs = 0;
            _telemetry.SendPhaseMarker(PhaseStringBurst);
            _telemetry.ForceStringEmitAll();
            _state = State.StringBurst;
        }

        private void TickStringBurst()
        {
            if (_elapsedMs < StringBurstHoldMs) return;

            int strCount = _telemetry.Profile?.StringChannels.Count ?? 0;
            MozaLog.Debug(
                $"[Moza] AUTO-TEST: string burst done channels={strCount}");

            _telemetry.TestMode = false;
            _elapsedMs = 0;
            _telemetry.SendPhaseMarker(PhaseEnterSwitchPending);
            _state = State.SwitchPending;
        }

        private void TickSwitchPending()
        {
            if (_dashList == null) { _state = State.Done; return; }
            string targetName = _dashList[_targetSlot];

            // Resolve the target profile WITHOUT going through the plugin's
            // ApplyTelemetrySettings path — that would set Profile on the host
            // synchronously, queueing a tier-def for the new dash before the
            // wheel has accepted the slot change. SwitchToProfile threads the
            // profile through the renegotiate state machine atomically.
            var profile = _resolveProfile(targetName);
            _onTargetChosen?.Invoke(targetName);

            _prevSubscriptionGen = _telemetry.SubscriptionGen;
            // Marker BEFORE SwitchToProfile so the kind=4 emission is bracketed
            // by the marker → next-phase-marker pair in the wire trace.
            _telemetry.SendPhaseMarker(PhaseEnterWaitRenegotiate);
            _telemetry.SwitchToProfile((uint)_targetSlot, profile);
            _elapsedMs = 0;
            Transition(State.WaitRenegotiate,
                $"target=\"{targetName}\"(slot={_targetSlot}) " +
                $"resolvedProfile={(profile != null ? "yes" : "no")}");
        }

        private void TickWaitRenegotiate()
        {
            int gen = _telemetry.SubscriptionGen;
            if (gen != _prevSubscriptionGen)
            {
                _prevSubscriptionGen = gen;
                _framesAtPhaseStart = _telemetry.FramesSent;
                _telemetry.TestMode = true;
                _elapsedMs = 0;
                string targetName = _dashList?[_targetSlot] ?? "?";
                _telemetry.SendPhaseMarker(PhaseEnterPostSwitchTest);
                Transition(State.PostSwitchTest,
                    $"target=\"{targetName}\"(slot={_targetSlot}) " +
                    $"subGen={gen}");
                return;
            }

            if (_elapsedMs >= RenegotiateTimeoutMs)
            {
                MozaLog.Warn(
                    $"[Moza] AUTO-TEST: renegotiate TIMEOUT after {RenegotiateTimeoutMs}ms — " +
                    $"target slot={_targetSlot} subGen still {_prevSubscriptionGen}");
                Finish(persistTarget: false);
            }
        }

        private void TickPostSwitchTest()
        {
            if (_elapsedMs < PostSwitchTestMs) return;

            int frames = _telemetry.FramesSent - _framesAtPhaseStart;
            string targetName = _dashList?[_targetSlot] ?? "?";
            bool ok = frames > 0;
            MozaLog.Debug(
                $"[Moza] AUTO-TEST: post-switch test done dash=\"{targetName}\" " +
                $"frames={frames} {(ok ? "PASS" : "FAIL")}");

            Finish(persistTarget: true);
        }

        private void Finish(bool persistTarget)
        {
            _telemetry.TestMode = false;
            _telemetry.SendPhaseMarker(PhaseEnterDone);
            // AutoTestLastSlot persistence intentionally removed — deterministic
            // target slot makes alternation irrelevant for wire-diff captures.
            MozaLog.Debug("[Moza] AUTO-TEST: state=DONE");
            _state = State.Done;
        }

        private void Transition(State next, string detail)
        {
            _state = next;
            MozaLog.Debug($"[Moza] AUTO-TEST: state={next} {detail}");
        }
    }
}
