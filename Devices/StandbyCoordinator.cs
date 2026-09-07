using System;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Opt-in auto-standby: puts the wheelbase into Work Mode standby after an
    /// idle period with no game and no physical input, and wakes it the moment
    /// either returns. Extracted from MozaPlugin.
    ///
    /// <para>Work Mode standby fully powers down the wheel/display, so it must
    /// only engage after a genuine idle period — never immediately, never on
    /// startup, and never while the user is using the wheel or the plugin UI.</para>
    /// </summary>
    internal sealed class StandbyCoordinator
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;
        private readonly MozaHidReader _hidReader;

        // DataUpdate stamps the feed timestamp + last GameRunning; a fresh feed
        // with GameRunning means a game is active (DataUpdate goes quiet when no
        // game runs, so a stale feed implies no game). _lastActivityTicks is the
        // last-activity clock — bumped by a running game, by physical HID input
        // (wheel/pedals/buttons past a deadband), and by UI interaction
        // (NotifyUserActivity). Standby engages only once now-lastActivity exceeds
        // the user's timeout AND no game is active. _applied caches the last value
        // written so the reconcile is idempotent (writes only on change).
        private long _lastDataUpdateTicks;
        private volatile bool _lastGameRunning;
        private long _lastActivityTicks;          // 0 until first reconcile (lazy baseline)
        private volatile int _applied = -1;       // -1 unknown / 0 active / 1 standby
        private const long FeedStaleMs = 3000;

        // HID-activity baseline (last sampled positions; change past the deadband
        // counts as physical use). _hidBaselined gates the first sample so it
        // seeds the baseline instead of registering as activity.
        private bool _hidBaselined;
        private int _steer = -1, _throttle, _brake, _clutch, _handbrake, _leftPaddle, _rightPaddle, _buttonHash;

        internal StandbyCoordinator(
            MozaPlugin plugin,
            MozaData data,
            DeviceDetectionState detectionState,
            MozaHidReader hidReader)
        {
            _plugin = plugin;
            _data = data;
            _detectionState = detectionState;
            _hidReader = hidReader;
        }

        /// <summary>
        /// True while a game is actively feeding telemetry: a fresh DataUpdate
        /// (within <see cref="FeedStaleMs"/>) AND GameRunning. DataUpdate goes
        /// quiet when no game runs, so a stale feed means no game even if the last
        /// GameRunning we saw was true. GameRunning stays true through
        /// menus/pauses, so this spans the whole session. The LED keepalive reads
        /// this to never let the wheel sleep mid-game.
        /// </summary>
        internal bool IsGameActive
        {
            get
            {
                long lastFeed = Interlocked.Read(ref _lastDataUpdateTicks);
                bool feedFresh = (DateTime.UtcNow.Ticks - lastFeed) <= FeedStaleMs * TimeSpan.TicksPerMillisecond;
                return feedFresh && _lastGameRunning;
            }
        }

        /// <summary>Stamp the game-data feed. Called first thing in DataUpdate so
        /// a stale-instance early-out doesn't make the feed look quiet.</summary>
        internal void NoteDataUpdate(bool gameRunning)
        {
            Interlocked.Exchange(ref _lastDataUpdateTicks, DateTime.UtcNow.Ticks);
            _lastGameRunning = gameRunning;
        }

        /// <summary>
        /// Mark the user as actively using the plugin (e.g. interacting with the
        /// settings UI). Bumps the auto-standby activity clock so the wheel does
        /// not power down mid-configuration. Cheap and safe to call regardless of
        /// whether auto-standby is enabled.
        /// </summary>
        internal void NotifyUserActivity()
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Called when the user turns auto-standby off. If auto-standby had put
        /// the base to sleep, wake it — disabling the feature should never leave
        /// the wheel powered down. No-op if we didn't cause the standby.
        /// </summary>
        internal void Cancel()
        {
            bool weStandbyed = _applied == 1;
            _applied = -1;
            if (!weStandbyed || !_detectionState.BaseDetected) return;
            if (_data != null) _data.WorkMode = 0;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-work-mode", 0);
            MozaLog.Info("[AZOM] Auto-standby disabled — waking base");
        }

        /// <summary>
        /// With <see cref="MozaPluginSettings.AutoStandbyWhenNoGame"/> enabled, send
        /// <c>main-set-work-mode</c>=1 (standby) once the wheel has been idle for
        /// <see cref="MozaPluginSettings.AutoStandbyTimeoutMinutes"/> with no game
        /// and no activity, and =0 (active) the moment a game runs or the user
        /// interacts. Idempotent — writes only when the desired value changes —
        /// so it is safe to call every DataUpdate tick and from PollStatus.
        /// Never standbys on the first reconcile (lazy activity baseline), so the
        /// plugin never boots the wheel straight into standby. No-op without a
        /// detected base.
        /// </summary>
        internal void Apply()
        {
            if (MozaPlugin.IsShuttingDown) return;
            var settings = _plugin.Settings;
            if (settings == null || !settings.AutoStandbyWhenNoGame) { _applied = -1; return; }
            if (!_detectionState.BaseDetected) { _applied = -1; return; }

            long now = DateTime.UtcNow.Ticks;
            // Lazy baseline: the first reconcile after startup/reload seeds the
            // activity clock so we can never standby immediately ("never start in
            // standby") — the first write below is therefore always wake=0.
            if (Interlocked.Read(ref _lastActivityTicks) == 0)
                Interlocked.Exchange(ref _lastActivityTicks, now);

            // A game is "active" only with a fresh data feed AND GameRunning —
            // DataUpdate goes quiet when no game runs, so a stale feed means no
            // game even if the last GameRunning we saw was true.
            bool gameActive = IsGameActive;

            if (gameActive)
                Interlocked.Exchange(ref _lastActivityTicks, now); // running game keeps it awake
            else
                MaybeStampHidActivity(now); // physical wheel/pedal/button use keeps it awake

            long idleMs = (now - Interlocked.Read(ref _lastActivityTicks)) / TimeSpan.TicksPerMillisecond;
            int timeoutMin = settings.AutoStandbyTimeoutMinutes;
            if (timeoutMin < 1) timeoutMin = 1;
            long timeoutMs = (long)timeoutMin * 60_000L;

            int desired = (!gameActive && idleMs >= timeoutMs) ? 1 : 0; // 1 = standby, 0 = active
            if (_applied == desired) return; // write only on change

            _applied = desired;
            if (_data != null) _data.WorkMode = desired; // keep the UI toggle in sync
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-work-mode", desired);
            MozaLog.Info($"[AZOM] Auto-standby: {(desired == 1 ? $"standby (idle {idleMs / 1000}s >= {timeoutMin}m)" : "wake (active)")}");
        }

        /// <summary>
        /// Bump the activity clock when physical input (steering, pedals,
        /// paddles, handbrake, or buttons) has changed past a small deadband
        /// since the last sample. The HID reader runs continuously on its own
        /// thread, so this works with no game and the settings pane closed. The
        /// first sample only seeds the baseline (never counts as activity).
        /// </summary>
        private void MaybeStampHidActivity(long nowTicks)
        {
            var data = _data;
            if (data == null || !data.IsHidConnected) return;

            double steerD = _hidReader?.GetSteeringPositionPercent() ?? -1.0;
            int steer = steerD < 0 ? -1 : (int)Math.Round(steerD);
            int thr = data.ThrottlePosition, brk = data.BrakePosition, clu = data.ClutchPosition;
            int hb = data.HandbrakePosition, lp = data.LeftPaddlePosition, rp = data.RightPaddlePosition;
            int btnHash = ComputeButtonActivityHash(data);

            if (!_hidBaselined)
            {
                _steer = steer; _throttle = thr; _brake = brk; _clutch = clu;
                _handbrake = hb; _leftPaddle = lp; _rightPaddle = rp; _buttonHash = btnHash;
                _hidBaselined = true;
                return;
            }

            const int Dead = 3; // percent units — above sensor jitter, below deliberate movement
            bool active = false;
            // Rebaseline per axis only when it moves past the deadband, so slow
            // deliberate movement still registers (each Dead% of travel) while
            // resting jitter never does.
            if (steer >= 0 && (_steer < 0 || Math.Abs(steer - _steer) >= Dead)) { _steer = steer; active = true; }
            if (Math.Abs(thr - _throttle) >= Dead) { _throttle = thr; active = true; }
            if (Math.Abs(brk - _brake) >= Dead) { _brake = brk; active = true; }
            if (Math.Abs(clu - _clutch) >= Dead) { _clutch = clu; active = true; }
            if (Math.Abs(hb - _handbrake) >= Dead) { _handbrake = hb; active = true; }
            if (Math.Abs(lp - _leftPaddle) >= Dead) { _leftPaddle = lp; active = true; }
            if (Math.Abs(rp - _rightPaddle) >= Dead) { _rightPaddle = rp; active = true; }
            if (btnHash != _buttonHash) { _buttonHash = btnHash; active = true; }

            if (active) Interlocked.Exchange(ref _lastActivityTicks, nowTicks);
        }

        private static int ComputeButtonActivityHash(MozaData data)
        {
            int h = data.HandbrakeButtonPressed ? 1 : 0;
            var b = data.ButtonStates;
            for (int i = 0; i < b.Length; i++)
                if (b[i]) h = (h * 31) + (i + 2);
            // Stalks live on their own button surface (see MozaData.StalksButtonStates)
            // but pressing them is still user activity — keep it counting toward standby.
            var s = data.StalksButtonStates;
            for (int i = 0; i < s.Length; i++)
                if (s[i]) h = (h * 31) + (i + 1000);
            return h;
        }
    }
}
