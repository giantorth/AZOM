using System;
using System.Collections.Generic;
using System.Diagnostics;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin
{
    /// <summary>
    /// Automatic bite point: a clutch pedal's Pedal Feel curve drawn from
    /// <see cref="MBoosterAutoBiteCurve"/>'s template, peaking at the calibrated
    /// bite point.
    ///
    /// The calibration is stored in the clutch source's units; the curve's X is
    /// the pedal's own travel as the graph's live dot reads it (raw HID, before
    /// Max Threshold). The mBooster pedal source converts directly; every other
    /// source goes through a per-game <see cref="MBoosterBitePointPedalMap"/>
    /// learned while source and pedal both hold steady. Until the map covers the
    /// bite point, the stored curve stays.
    ///
    /// The data thread keeps the unit on the right curve as the game, vehicle
    /// or calibration changes, writing only while the clutch is released.
    /// </summary>
    public partial class MozaPlugin
    {
        // A map sample needs both positions within this range (%) over the
        // window: 10 %/s, so the telemetry's lag costs well under 1 %.
        private const double MapSteadyRangePct = 2.0;
        private const double MapSteadyWindowSec = 0.2;
        private const int MapHistorySize = 64;
        private static readonly TimeSpan MapSaveInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AutoBiteCheckInterval = TimeSpan.FromMilliseconds(250);
        // A queued push still not on the unit after this is queued again.
        private static readonly TimeSpan AutoBiteRequeueAfter = TimeSpan.FromSeconds(2);
        // Clutch travel (%) below which the pedal counts as released.
        private const double AutoBiteRestPct = 5.0;

        // Data thread only.
        private readonly long[] _mapHistTicks = new long[MapHistorySize];
        private readonly double[] _mapHistSource = new double[MapHistorySize];
        private readonly double[] _mapHistPedal = new double[MapHistorySize];
        private int _mapHistHead;
        private int _mapHistCount;
        private string? _mapHistKey;
        private bool _mapDirty;
        private DateTime _mapSavedAt;
        private DateTime _autoBiteCheckedAt;
        private DateTime _autoBiteQueuedAt;

        // Bite point in clutch pedal travel (0..100), refreshed every data tick; -1 when unknown.
        private volatile int _clutchBitePedalPct = -1;

        /// <summary>The current bite point in clutch pedal travel (0..100), -1 when unknown.</summary>
        internal int ClutchBitePedalPct => _clutchBitePedalPct;

        /// <summary>
        /// The automatic bite point curve for this pedal when it applies:
        /// Automatic bite point on, an active clutch, and the bite point known in
        /// pedal travel.
        /// </summary>
        internal bool TryAutoBiteFeelCurve(IMBoosterPedalConfig cfg, MBoosterDeviceController c, int axis,
            out float[] y, out float[] x, out float endY)
        {
            y = x = Array.Empty<float>();
            endY = -1;
            var p = cfg.AutoBiteFeel;
            int bite = _clutchBitePedalPct;
            if (p == null || !p.Enabled || bite < 0) return false;
            if (c.AxisRole(axis) != MBoosterRole.Clutch || !c.IsAxisMotorized(axis)) return false;
            MBoosterAutoBiteCurve.Build(bite, p.WidthPct, p.BeforePct, p.AfterPct, out y, out x, out endY);
            return true;
        }

        /// <summary>The feel curve a pedal gets: the automatic one when it applies,
        /// else the stored one. Deadzone and Max Force are the stored values
        /// either way.</summary>
        /// <returns>True for the automatic curve.</returns>
        internal bool ResolveMBoosterFeelCurve(IMBoosterPedalConfig cfg, MBoosterDeviceController c, int axis,
            out double deadzoneKg, out double maxForceKg, out float[]? y, out float[]? x, out float endY)
        {
            deadzoneKg = cfg.DeadzoneKg >= 0 ? cfg.DeadzoneKg : 0;
            maxForceKg = cfg.MaxForceKg >= 0 ? cfg.MaxForceKg : 200;
            if (TryAutoBiteFeelCurve(cfg, c, axis, out var ay, out var ax, out float aEnd))
            {
                y = ay;
                x = ax;
                endY = aEnd;
                return true;
            }
            y = cfg.InputCurveY;
            x = cfg.InputCurveX;
            endY = cfg.InputCurveEndY;
            return false;
        }

        /// <summary>Push a pedal's resolved feel curve (see
        /// <see cref="ResolveMBoosterFeelCurve"/>) to <paramref name="dev"/>.</summary>
        /// <returns>False when <paramref name="guard"/> skipped it.</returns>
        internal bool PushResolvedFeelCurve(IMBoosterPedalConfig cfg, MBoosterDeviceController c, int axis, byte dev,
            bool allX = false, MBoosterFeelPushGuard guard = MBoosterFeelPushGuard.None)
        {
            bool auto = ResolveMBoosterFeelCurve(cfg, c, axis, out double dz, out double mf, out var y, out var x, out float endY);
            if (!FeelPushAllowed(guard, auto, cfg, c, dev)) return false;
            c.PushFeelCurveResync(dz, mf, y, x, endY, dev, allX);
            return true;
        }

        private static bool FeelPushAllowed(MBoosterFeelPushGuard guard, bool auto, IMBoosterPedalConfig cfg,
            MBoosterDeviceController c, byte dev) =>
            guard == MBoosterFeelPushGuard.None || auto || MozaMBoosterRegistry.IsFeelCurveSet(cfg)
            || (guard == MBoosterFeelPushGuard.IfSetOrSent && c.AnyFeelCurvePushed(dev));

        // Data thread, from UpdateClutchBitePoint: learn the map, refresh the bite
        // point in pedal travel, keep the unit's curve current.
        private void UpdateAutoBiteFeel(string? gameKey, bool driving, double source01)
        {
            var registry = _mboosterRegistry;
            if (registry == null || !registry.TryGetEffectPedal(MBoosterRole.Clutch, out var found, out int axis))
            {
                _clutchBitePedalPct = -1;
                _mapHistCount = 0;
                return;
            }
            var c = found!;
            var raw = c.LastAxisRawPercentPreThreshold;
            double pedalPct = axis < raw.Length ? raw[axis] : 0;

            if (BitePointSource != MBoosterBitePointSource.MBoosterPedal && gameKey != null && driving && !double.IsNaN(source01))
                LearnBitePedalMap(gameKey, source01 * 100.0, pedalPct);
            else
                _mapHistCount = 0;

            _clutchBitePedalPct = BiteInPedalTravel(c, axis, gameKey);
            KeepAutoBiteFeelPushed(c, axis, pedalPct);
        }

        private void LearnBitePedalMap(string gameKey, double sourcePct, double pedalPct)
        {
            if (!string.Equals(gameKey, _mapHistKey, StringComparison.OrdinalIgnoreCase))
            {
                _mapHistKey = gameKey;
                _mapHistCount = 0;
            }
            long now = Stopwatch.GetTimestamp();
            _mapHistTicks[_mapHistHead] = now;
            _mapHistSource[_mapHistHead] = sourcePct;
            _mapHistPedal[_mapHistHead] = pedalPct;
            _mapHistHead = (_mapHistHead + 1) % MapHistorySize;
            if (_mapHistCount < MapHistorySize) _mapHistCount++;
            if (!MapSamplesSteady(now) || _settings == null) return;

            var maps = _settings.MBoosterBitePointPedalMaps;
            if (maps == null || !maps.TryGetValue(gameKey, out var map) || map == null)
            {
                map = new MBoosterBitePointPedalMap();
                lock (_bitePointLock)
                {
                    var copy = maps == null
                        ? new Dictionary<string, MBoosterBitePointPedalMap>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, MBoosterBitePointPedalMap>(maps, StringComparer.OrdinalIgnoreCase);
                    copy[gameKey] = map;
                    _settings.MBoosterBitePointPedalMaps = copy;
                }
            }
            if (map.Learn(sourcePct, pedalPct)) _mapDirty = true;
            var utcNow = DateTime.UtcNow;
            if (_mapDirty && utcNow - _mapSavedAt >= MapSaveInterval)
            {
                _mapDirty = false;
                _mapSavedAt = utcNow;
                PersistSettings();
            }
        }

        // Both positions held within MapSteadyRangePct over the whole window.
        private bool MapSamplesSteady(long now)
        {
            long window = (long)(MapSteadyWindowSec * Stopwatch.Frequency);
            double sMin = double.MaxValue, sMax = double.MinValue, pMin = double.MaxValue, pMax = double.MinValue;
            for (int i = 1; i <= _mapHistCount; i++)
            {
                int idx = (_mapHistHead - i + MapHistorySize) % MapHistorySize;
                if (now - _mapHistTicks[idx] > window)
                    return sMax - sMin <= MapSteadyRangePct && pMax - pMin <= MapSteadyRangePct;
                sMin = Math.Min(sMin, _mapHistSource[idx]);
                sMax = Math.Max(sMax, _mapHistSource[idx]);
                pMin = Math.Min(pMin, _mapHistPedal[idx]);
                pMax = Math.Max(pMax, _mapHistPedal[idx]);
            }
            // History doesn't reach back a whole window yet.
            return false;
        }

        private int BiteInPedalTravel(MBoosterDeviceController c, int axis, string? gameKey)
        {
            int bite = _currentClutchBitePointPct;
            if (bite < 0) return -1;
            if (BitePointSource == MBoosterBitePointSource.MBoosterPedal)
            {
                // The source is the shaped position: undo the Max Threshold gain,
                // which only a USB lane's HID reading carries.
                var cfg = MozaMBoosterRegistry.PeekPedalConfig(c.CurrentSettings, axis, c.SoleConnectedAxis());
                double gain = c.IsRouted || cfg == null ? 1.0 : MozaMBoosterRegistry.MaxThresholdGain(cfg, c.IsAxisMotorized(axis));
                return (int)Math.Round(bite / gain, MidpointRounding.AwayFromZero);
            }
            var maps = _settings?.MBoosterBitePointPedalMaps;
            if (gameKey == null || maps == null || !maps.TryGetValue(gameKey, out var map) || map == null) return -1;
            return map.TryMap(bite, out double p) ? (int)Math.Round(p, MidpointRounding.AwayFromZero) : -1;
        }

        // Push the resolved curve when it differs from what the unit was last
        // sent — the automatic one, or the stored one once the bite point is
        // gone — while the clutch is released. Turning the option off is the
        // UI's to push.
        private void KeepAutoBiteFeelPushed(MBoosterDeviceController c, int axis, double pedalPct)
        {
            var now = DateTime.UtcNow;
            if (now - _autoBiteCheckedAt < AutoBiteCheckInterval) return;
            _autoBiteCheckedAt = now;
            if (now - _autoBiteQueuedAt < AutoBiteRequeueAfter) return;
            if (!IsMBoosterSettingsResolved(c)) return;
            var cfg = MozaMBoosterRegistry.PeekPedalConfig(c.CurrentSettings, axis, c.SoleConnectedAxis());
            if (cfg?.AutoBiteFeel?.Enabled != true) return;
            if (!c.OwnsSingletonRegisters(axis) || !c.TryCalibDeviceForAxis(axis, out byte dev)) return;
            bool auto = ResolveMBoosterFeelCurve(cfg, c, axis, out double dz, out double mf, out var y, out var x, out float endY);
            if (!FeelPushAllowed(MBoosterFeelPushGuard.IfSetOrSent, auto, cfg, c, dev)) return;
            if (c.IsFeelCurvePushed(dev, dz, mf, y, x, endY) || pedalPct >= AutoBiteRestPct) return;

            _autoBiteQueuedAt = now;
            MozaLog.Info(auto
                ? $"[AZOM/mBooster] Clutch feel curve: bite point at {_clutchBitePedalPct}% travel"
                : "[AZOM/mBooster] Clutch feel curve: no bite point, stored curve");
            // Resolved again when it runs, after the coalescing delay; skipped if
            // the clutch has been pressed since.
            c.QueueCalibWrite($"{dev:x2}:feel-curve", () =>
            {
                var rawNow = c.LastAxisRawPercentPreThreshold;
                if (axis < rawNow.Length && rawNow[axis] >= AutoBiteRestPct) return;
                var cfgNow = MozaMBoosterRegistry.PeekPedalConfig(c.CurrentSettings, axis, c.SoleConnectedAxis());
                if (cfgNow?.AutoBiteFeel?.Enabled == true)
                    PushResolvedFeelCurve(cfgNow, c, axis, dev, guard: MBoosterFeelPushGuard.IfSetOrSent);
            });
        }
    }
}
