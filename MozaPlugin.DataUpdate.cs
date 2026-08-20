using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Hardware;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.UI.UpdateCheck;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    public partial class MozaPlugin
    {

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (IsShuttingDown) return;
            // Stamp the game-data feed for the auto-standby reconcile (see
            // ApplyAutoStandby). Done first so a stale-instance early-out below
            // doesn't make the feed look quiet.
            Interlocked.Exchange(ref _autoStandbyLastDataUpdateTicks, DateTime.UtcNow.Ticks);
            _autoStandbyLastGameRunning = data.GameRunning;
            // Feed the truck-sim stalk controller the current game context so it can
            // gate keyboard output to a running ETS2/ATS session.
            try { _stalksController?.SetGameContext(pluginManager.GameName, data.GameRunning); } catch { }
            // Keep the process responsive in the background (EcoQoS opt-out + 1 ms timer)
            // the moment a game is active. Idempotent; the PollStatus backstop handles
            // release if DataUpdate goes quiet on game exit.
            ApplyResponsivenessState();
            // Persistent-wire reload guard. On a SimHub plugin reload, End()
            // keeps the telemetry sender alive in s_persistentTelemetrySender
            // (the next Init reuses it) but nulls the reloaded instance's
            // _telemetrySender. If SimHub then keeps driving DataUpdate on a
            // stale instance, _telemetrySender is null and the game-data feed
            // silently stops — the persistent sender keeps emitting its last
            // snapshot forever, freezing the dashboard on stale data while the
            // wire/binding look healthy (observed 2026-06-06, W13). Route the
            // feed to the persistent sender so it always reaches the emitter.
            var sender = _telemetrySender ?? s_persistentTelemetrySender;
            if (_telemetrySender == null && sender != null && !_warnedStaleDataFeed)
            {
                _warnedStaleDataFeed = true;
                MozaLog.Warn("[AZOM] DataUpdate fired with _telemetrySender=null — routing game " +
                             "data to the persistent sender (stale post-reload instance).");
            }
            sender?.UpdateGameData(data.NewData);
            sender?.SetGameRunning(data.GameRunning);
            _fsr1Driver?.UpdateGameData(data.NewData);
            _fsr1Driver?.SetGameRunning(data.GameRunning);
            _cm2Sender?.UpdateGameData(data.NewData);
            _cm2Sender?.SetGameRunning(data.GameRunning);
            _cm1Driver?.UpdateGameData(data.NewData);
            _cm1Driver?.SetGameRunning(data.GameRunning);
            _gearshift?.Tick(data);

            // Push SimHub's shared/master LED brightness to the wheel firmware group
            // brightness (rpm/buttons/knobs) when the user moves the slider. The wheel
            // LED driver publishes the settled value into WheelLedMasterBrightness off
            // the LED thread; apply it here (change-gated) so the firmware write runs on
            // the data thread and shares HardwareApplier's per-wheel cfg cache with the
            // connect/profile brightness path (no fight, no redundant flash). NEW-protocol
            // wheels only — ES/ESX (old-protocol) route through TickEsMasterBrightness on
            // the steady poll timer, since this data thread goes quiet at idle (#113).
            int masterLedBri = WheelLedMasterBrightness;
            if (masterLedBri != _masterLedBrightnessApplied)
            {
                _masterLedBrightnessApplied = masterLedBri;
                if (masterLedBri >= 0)
                    _hardwareApplier?.ApplyMasterWheelLedBrightness(masterLedBri);
            }

            // Hand the latest RPM, MaxRpm + engine-on flag to the AB9 engine-vib
            // worker. GameRunning stays true while paused or in menu, so we'd
            // keep streaming buzz frames the whole time the user is in the
            // pause menu without this gate. GamePaused / GameInMenu collapse
            // the stream to silent-keepalive within one tick of the user
            // pressing Esc / returning to the menu. MaxRpm drives the worker's
            // rpm/redline intensity scaling — games that don't report it fall
            // back to flat (unscaled) amplitude.
            double rpm = data.NewData?.Rpms ?? 0.0;
            double maxRpm = data.NewData?.MaxRpm ?? 0.0;
            bool engineOn = data.GameRunning && !data.GamePaused && !data.GameInMenu;
            _ab9Worker?.PostFrame(rpm, maxRpm, engineOn);

            // Wheelbase LFE worker: just feed liveness (running & not paused/in-menu).
            // All per-channel values (RPM, ABSActive, Gear, …) come from the
            // channels' own formulas, evaluated live via the property resolver.
            _baseLfeWorker?.PostFrame(engineOn);

            // Control Mapper variant-provider bridge: drive wheel-change detection
            // each tick when registered; otherwise retry registration up to the
            // tick budget (ControlMapperPlugin may not be loaded at Init time).
            if (_controlMapperBridge != null)
            {
                if (_controlMapperBridge.IsRegistered)
                {
                    _controlMapperBridge.Poll();
                }
                else if (_controlMapperRetryTicks > 0 && !_controlMapperBridge.IsGivenUp)
                {
                    _controlMapperRetryTicks--;
                    if (_controlMapperBridge.TryRegister(pluginManager))
                        _controlMapperRetryTicks = 0;
                    else if (_controlMapperRetryTicks == 0 && !_controlMapperBridge.IsGivenUp)
                        MozaLog.Warn(
                            "[AZOM] ControlMapper bridge: ControlMapperPlugin never became available — " +
                            "giving up retry. Variant integration disabled this session.");
                }
            }

            // Slice F: DataUpdate hook re-enabled.
            // Fan-out fresh telemetry to every mBooster's effect worker.
            // Lock-free fast path: when no mBoosters are registered (the
            // common case for users without the device), skip the entire
            // snapshot build + LockedDict traversal. HasControllers reads a
            // volatile int updated only on Refresh().
            if (_mboosterRegistry != null && _mboosterRegistry.HasControllers)
            {
                var nd = data.NewData;
                double brake01 = (nd?.Brake ?? 0.0) / 100.0;
                if (brake01 < 0) brake01 = 0; if (brake01 > 1) brake01 = 1;
                double throttle01 = (nd?.Throttle ?? 0.0) / 100.0;
                if (throttle01 < 0) throttle01 = 0; if (throttle01 > 1) throttle01 = 1;
                // ABSActive/TCActive are SimHub's loosely-typed properties —
                // games supply bool / int / sbyte / byte / short / long
                // depending on backend. Pattern-match the common shapes to
                // skip Convert.ToInt32's InvariantCulture lookup and the
                // try/catch on the hot path (DataUpdate runs at SimHub's
                // data rate, ~60Hz+). Unknown types fall through to false —
                // same observable behaviour as the catch-and-default that
                // lived here previously.
                object? rawAbs = nd?.ABSActive;
                bool absActive = rawAbs switch
                {
                    bool b   => b,
                    int i    => i != 0,
                    byte by  => by != 0,
                    sbyte sb => sb != 0,
                    short sh => sh != 0,
                    long lo  => lo != 0,
                    _ => false,
                };
                object? rawTc = nd?.TCActive;
                bool tcActive = rawTc switch
                {
                    bool b   => b,
                    int i    => i != 0,
                    byte by  => by != 0,
                    sbyte sb => sb != 0,
                    short sh => sh != 0,
                    long lo  => lo != 0,
                    _ => false,
                };
                double vehicleMs = (nd?.SpeedKmh ?? 0.0) / 3.6;
                double avgWheelMs = 0.0;
                double idleRpm = 800.0;
                // No generic suspension-travel telemetry exists in SimHub;
                // AccelerationHeave (vertical G) is the closest proxy for
                // road-surface roughness. Nullable — 0 for games that don't
                // report it, same fail-soft style as the rest of this block.
                double suspensionHeaveG = nd?.AccelerationHeave ?? 0.0;
                // Longitudinal chassis acceleration, in G — SimHub's
                // StatusDataBase.AccelerationSurge (= AccelerationX), same
                // family/convention as AccelerationHeave above. Positive =
                // accelerating, negative = braking/decelerating. Drives the
                // G-Force (Inertial Pedal Feel) effect — see
                // MBoosterEffectWorker.UpdateGForceRequest. Nullable — 0 for
                // games that don't report it.
                double longitudinalG = nd?.AccelerationSurge ?? 0.0;
                // Brake Fade's temperature signal — peak across all 4
                // corners (any one wheel overheating should trigger the
                // warning, not just the average). BrakesTemperatureMax is
                // nullable — 0 for games that don't report it. Normalized
                // to Celsius: TemperatureUnit is a per-game display hint
                // (Celsius/Fahrenheit), same "unit gotcha" the protocol
                // note warns about for speed fields elsewhere in this
                // method — fail-soft substring match rather than an exact
                // string comparison, since the real set of values SimHub's
                // game plugins actually write isn't documented anywhere.
                double brakeTempRaw = nd?.BrakesTemperatureMax ?? 0.0;
                string tempUnit = nd?.TemperatureUnit ?? "";
                double brakeTempC = tempUnit.IndexOf("F", StringComparison.OrdinalIgnoreCase) >= 0
                    ? (brakeTempRaw - 32.0) * 5.0 / 9.0
                    : brakeTempRaw;
                // Gear-change edge for the mBooster's Gear Shift effect —
                // same string-latch + warm-up-guard pattern as
                // CheckGearshiftEvent (wheelbase) / CheckAb9GearshiftEvent,
                // but with its own independent latch and no debounce here
                // (each mBooster device applies its own debounce/neutral
                // settings in MBoosterEffectWorker.UpdateGearShiftRequest).
                string? gearForMBooster = nd?.Gear;
                if (!string.IsNullOrEmpty(gearForMBooster))
                {
                    if (_lastMBoosterGearString == null)
                    {
                        _lastMBoosterGearString = gearForMBooster; // warm-up: don't fire on the first observed value
                    }
                    else if (gearForMBooster != _lastMBoosterGearString)
                    {
                        _lastMBoosterGearString = gearForMBooster;
                        _mboosterShiftSeq++; // monotonic — the worker samples this on its own timer; a bool edge would be dropped when DataUpdate outruns it
                    }
                }
                // Level (not edge): true whenever the current gear is Neutral,
                // so the worker reads valid neutral-ness even if it samples a
                // tick or two after _mboosterShiftSeq advanced.
                bool gearIsNeutral = gearForMBooster == "N" || gearForMBooster == "0";
                var snap = new MBoosterTelemetrySnapshot(
                    gameRunning: data.GameRunning,
                    rpm: rpm,
                    maxRpm: maxRpm,
                    idleRpm: idleRpm,
                    brake: brake01,
                    throttle: throttle01,
                    absActive: absActive,
                    tcActive: tcActive,
                    vehicleSpeedMs: vehicleMs,
                    avgWheelSpeedMs: avgWheelMs,
                    suspensionHeaveG: suspensionHeaveG,
                    longitudinalG: longitudinalG,
                    brakeTempC: brakeTempC,
                    gearShiftSeq: _mboosterShiftSeq,
                    gearIsNeutral: gearIsNeutral);
                _mboosterRegistry.OnDataUpdate(snap);
            }

            // Auto-standby: wake the base the instant a game starts; standby is
            // deferred to the idle-timeout reconcile below.
            ApplyAutoStandby();
        }

        /// <summary>
        /// Mark the user as actively using the plugin (e.g. interacting with the
        /// settings UI). Bumps the auto-standby activity clock so the wheel does
        /// not power down mid-configuration. Cheap and safe to call regardless of
        /// whether auto-standby is enabled.
        /// </summary>
        internal void NotifyUserActivity()
        {
            Interlocked.Exchange(ref _autoStandbyLastActivityTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Called when the user turns auto-standby off. If auto-standby had put
        /// the base to sleep, wake it — disabling the feature should never leave
        /// the wheel powered down. No-op if we didn't cause the standby.
        /// </summary>
        internal void CancelAutoStandby()
        {
            bool weStandbyed = _autoStandbyApplied == 1;
            _autoStandbyApplied = -1;
            if (!weStandbyed || !DetectionState.BaseDetected) return;
            if (_data != null) _data.WorkMode = 0;
            WriteIfBaseConnected("main-set-work-mode", 0);
            MozaLog.Info("[AZOM] Auto-standby disabled — waking base");
        }

        /// <summary>
        /// Opt-in auto-standby (see the field block above). With
        /// <see cref="MozaPluginSettings.AutoStandbyWhenNoGame"/> enabled, send
        /// <c>main-set-work-mode</c>=1 (standby) once the wheel has been idle for
        /// <see cref="MozaPluginSettings.AutoStandbyTimeoutMinutes"/> with no game
        /// and no activity, and =0 (active) the moment a game runs or the user
        /// interacts. Idempotent — writes only when the desired value changes —
        /// so it is safe to call every DataUpdate tick and from PollStatus.
        /// Never standbys on the first reconcile (lazy activity baseline), so the
        /// plugin never boots the wheel straight into standby. No-op without a
        /// detected base.
        /// </summary>
        internal void ApplyAutoStandby()
        {
            if (IsShuttingDown) return;
            var settings = _settings;
            if (settings == null || !settings.AutoStandbyWhenNoGame) { _autoStandbyApplied = -1; return; }
            if (!DetectionState.BaseDetected) { _autoStandbyApplied = -1; return; }

            long now = DateTime.UtcNow.Ticks;
            // Lazy baseline: the first reconcile after startup/reload seeds the
            // activity clock so we can never standby immediately ("never start in
            // standby") — the first write below is therefore always wake=0.
            if (Interlocked.Read(ref _autoStandbyLastActivityTicks) == 0)
                Interlocked.Exchange(ref _autoStandbyLastActivityTicks, now);

            // A game is "active" only with a fresh data feed AND GameRunning —
            // DataUpdate goes quiet when no game runs, so a stale feed means no
            // game even if the last GameRunning we saw was true.
            bool gameActive = IsGameActive;

            if (gameActive)
                Interlocked.Exchange(ref _autoStandbyLastActivityTicks, now); // running game keeps it awake
            else
                MaybeStampHidActivity(now); // physical wheel/pedal/button use keeps it awake

            long idleMs = (now - Interlocked.Read(ref _autoStandbyLastActivityTicks)) / TimeSpan.TicksPerMillisecond;
            int timeoutMin = settings.AutoStandbyTimeoutMinutes;
            if (timeoutMin < 1) timeoutMin = 1;
            long timeoutMs = (long)timeoutMin * 60_000L;

            int desired = (!gameActive && idleMs >= timeoutMs) ? 1 : 0; // 1 = standby, 0 = active
            if (_autoStandbyApplied == desired) return; // write only on change

            _autoStandbyApplied = desired;
            if (_data != null) _data.WorkMode = desired; // keep the UI toggle in sync
            WriteIfBaseConnected("main-set-work-mode", desired);
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

            if (!_asHidBaselined)
            {
                _asSteer = steer; _asThrottle = thr; _asBrake = brk; _asClutch = clu;
                _asHandbrake = hb; _asLeftPaddle = lp; _asRightPaddle = rp; _asButtonHash = btnHash;
                _asHidBaselined = true;
                return;
            }

            const int Dead = 3; // percent units — above sensor jitter, below deliberate movement
            bool active = false;
            // Rebaseline per axis only when it moves past the deadband, so slow
            // deliberate movement still registers (each Dead% of travel) while
            // resting jitter never does.
            if (steer >= 0 && (_asSteer < 0 || Math.Abs(steer - _asSteer) >= Dead)) { _asSteer = steer; active = true; }
            if (Math.Abs(thr - _asThrottle) >= Dead) { _asThrottle = thr; active = true; }
            if (Math.Abs(brk - _asBrake) >= Dead) { _asBrake = brk; active = true; }
            if (Math.Abs(clu - _asClutch) >= Dead) { _asClutch = clu; active = true; }
            if (Math.Abs(hb - _asHandbrake) >= Dead) { _asHandbrake = hb; active = true; }
            if (Math.Abs(lp - _asLeftPaddle) >= Dead) { _asLeftPaddle = lp; active = true; }
            if (Math.Abs(rp - _asRightPaddle) >= Dead) { _asRightPaddle = rp; active = true; }
            if (btnHash != _asButtonHash) { _asButtonHash = btnHash; active = true; }

            if (active) Interlocked.Exchange(ref _autoStandbyLastActivityTicks, nowTicks);
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
