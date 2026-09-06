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
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin
{
    public partial class MozaPlugin
    {

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (IsShuttingDown) return;
            // Stamp the game-data feed for the auto-standby reconcile (see
            // StandbyCoordinator.Apply). Done first so a stale-instance early-out below
            // doesn't make the feed look quiet.
            _standby?.NoteDataUpdate(data.GameRunning);
            // AZOM.MaxTorque is a per-game-session peak: clear it on the game-start
            // edge, the same false→true edge TelemetrySender.SetGameRunning uses.
            // The rolling graph ring is deliberately NOT cleared — its own peak
            // feeds the graph's autoscale fallback, and blanking a 2-minute trace
            // mid-session reads as a glitch.
            if (data.GameRunning && !_lastGameRunningForTorquePeak)
                _data?.ResetLiveTorquePeak();
            _lastGameRunningForTorquePeak = data.GameRunning;
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

            // Per-zone brightness (SimHub's "Brightness limiter and balance" sliders).
            // Same data-thread + cfg-cache discipline as the master above, one register
            // per zone so moving one balance slider writes only that zone.
            int zoneRpmBri  = WheelLedBrightnessRpm;
            int zoneBtnBri  = WheelLedBrightnessButtons;
            int zoneKnobBri = WheelLedBrightnessKnob;
            if (zoneRpmBri != _zoneLedBrightnessApplied0
                || zoneBtnBri != _zoneLedBrightnessApplied1
                || zoneKnobBri != _zoneLedBrightnessApplied3)
            {
                _zoneLedBrightnessApplied0 = zoneRpmBri;
                _zoneLedBrightnessApplied1 = zoneBtnBri;
                _zoneLedBrightnessApplied3 = zoneKnobBri;
                _hardwareApplier?.ApplyWheelLedZoneBrightness(zoneRpmBri, zoneBtnBri, zoneKnobBri);
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
            _standby?.Apply();
        }
    }
}
