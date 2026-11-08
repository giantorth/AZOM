using System;
using System.Collections.Generic;
using System.Threading;
using GameReaderCommon;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;
using SimHub.Plugins;

namespace MozaPlugin
{
    /// <summary>
    /// Per-game clutch bite point for the mBooster Clutch Bite Point effect: the clutch
    /// position source, storage (per game and source), user-started calibration
    /// runs, and automatic learning/refinement from every clean standing start.
    /// </summary>
    public partial class MozaPlugin
    {
        // Automatic samples are only trusted from a slow release: the bite is
        // detected from a lagging speed signal, so a fast release reads low.
        // Pedal fraction per second (1.0 = full travel in one second).
        private const double MaxAutoReleaseRatePerSec = 1.0;

        private readonly MBoosterBitePointCalibrator _bitePointCalibrator = new MBoosterBitePointCalibrator();
        // Data thread only.
        private readonly MBoosterBitePointDetector _bitePointAutoDetector = new MBoosterBitePointDetector();
        private string? _bitePointAutoKey;
        private Func<string?, double>? _bitePointFormulaResolver;
        // Serializes writers; readers use the copy-on-write dictionary snapshot.
        private readonly object _bitePointLock = new object();
        // Current game's bite point for the current source, refreshed every data tick; -1 when none.
        private volatile int _currentClutchBitePointPct = -1;
        // Game/Formula source position (0..1, NaN = none) from the last data tick,
        // as double bits: a plain double can tear on the x86 build.
        private long _bitePointSourceClutchBits = BitConverter.DoubleToInt64Bits(double.NaN);

        /// <summary>One game's calibration as the UI shows it.</summary>
        internal readonly struct BitePointInfo
        {
            public readonly int Pct;
            public readonly int SampleCount;
            public readonly bool Pinned;

            public BitePointInfo(int pct, int sampleCount, bool pinned)
            {
                Pct = pct;
                SampleCount = sampleCount;
                Pinned = pinned;
            }

            public bool IsCalibrated => Pct >= 0;
        }

        /// <summary>SimHub's current game code, or null.</summary>
        internal string? CurrentGameName
        {
            get
            {
                try { return _pluginManager?.GameName; }
                catch { return null; }
            }
        }

        /// <summary>The current game's bite point (0..100) for the current source, -1 when uncalibrated.</summary>
        internal int CurrentClutchBitePointPct => _currentClutchBitePointPct;

        internal BitePointCalState BitePointCalibrationState => _bitePointCalibrator.State;

        internal bool IsBitePointCalibrationRunning => _bitePointCalibrator.IsActive;

        internal bool BitePointAutoCalibrate
        {
            get => _settings?.MBoosterBitePointAutoCalibrate ?? true;
            set
            {
                if (_settings == null || _settings.MBoosterBitePointAutoCalibrate == value) return;
                _settings.MBoosterBitePointAutoCalibrate = value;
                PersistSettings();
            }
        }

        /// <summary>Where the trigger and calibration read the clutch. Changing it stops any run.</summary>
        internal MBoosterBitePointSource BitePointSource
        {
            get => _settings?.MBoosterBitePointSource ?? MBoosterBitePointSource.MBoosterPedal;
            set
            {
                if (_settings == null || _settings.MBoosterBitePointSource == value) return;
                _settings.MBoosterBitePointSource = value;
                _bitePointCalibrator.Stop();
                _currentClutchBitePointPct = ClutchBitePointInfo(CurrentGameName).Pct;
                PersistSettings();
            }
        }

        /// <summary>Stored formula for <see cref="MBoosterBitePointSource.Formula"/>.</summary>
        internal string BitePointSourceFormula
        {
            get => _settings?.MBoosterBitePointSourceFormula ?? "";
            set
            {
                value ??= "";
                if (_settings == null || _settings.MBoosterBitePointSourceFormula == value) return;
                _settings.MBoosterBitePointSourceFormula = value;
                if (BitePointSource == MBoosterBitePointSource.Formula) _bitePointCalibrator.Stop();
                PersistSettings();
            }
        }

        /// <summary>Game/Formula source position from the last data tick (0..1), NaN when none.</summary>
        internal double BitePointSourceClutch01 =>
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _bitePointSourceClutchBits));

        /// <summary>The configured source's clutch position (0..1). Any thread.</summary>
        /// <returns>False when the source has no value.</returns>
        internal bool TryGetBitePointClutchPosition(out double position01)
        {
            if (BitePointSource == MBoosterBitePointSource.MBoosterPedal)
                return TryGetMBoosterClutchPosition(out position01);
            position01 = BitePointSourceClutch01;
            return !double.IsNaN(position01);
        }

        /// <summary>Status line for the calibration UI: the bite point value, then the
        /// run's phase or what the value rests on.</summary>
        internal string BitePointStatusText(in BitePointInfo info)
        {
            string state;
            switch (_bitePointCalibrator.State)
            {
                case BitePointCalState.Waiting: state = Strings.BiteCal_Waiting; break;
                case BitePointCalState.Armed: state = Strings.BiteCal_Armed; break;
                case BitePointCalState.Releasing: state = Strings.BiteCal_Releasing; break;
                case BitePointCalState.Cancelled: state = Strings.BiteCal_Cancelled; break;
                default:
                    state = info.Pinned ? Strings.BiteCal_Pinned
                        : info.IsCalibrated ? string.Format(Strings.BiteCal_Samples, info.SampleCount)
                        : Strings.BiteCal_Idle;
                    break;
            }
            return info.IsCalibrated ? info.Pct + "% · " + state : state;
        }

        // Settings key for a game under the current source. The mBooster pedal
        // keeps the bare game code; the others are prefixed.
        private string? CalibrationKey(string? game)
        {
            if (string.IsNullOrEmpty(game)) return null;
            return BitePointSource switch
            {
                MBoosterBitePointSource.Game => "game:" + game,
                MBoosterBitePointSource.Formula => "formula:" + game,
                _ => game,
            };
        }

        /// <summary>A game's calibration under the current source.</summary>
        internal BitePointInfo ClutchBitePointInfo(string? game)
        {
            var map = _settings?.MBoosterClutchBitePointCalibrations;
            string? key = CalibrationKey(game);
            if (map == null || key == null || !map.TryGetValue(key, out var cal) || cal == null)
                return new BitePointInfo(-1, 0, false);
            return new BitePointInfo(cal.Pct, cal.Samples?.Count ?? 0, cal.PinnedPct.HasValue);
        }

        /// <summary>Hand-set a game's bite point; pins it against refinement.</summary>
        internal void PinClutchBitePoint(string game, int pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            UpdateCalibration(CalibrationKey(game), cur =>
            {
                if (cur?.PinnedPct == pct) return null;
                var next = cur?.Clone() ?? new MBoosterBitePointCalibration();
                next.PinnedPct = pct;
                return next;
            });
        }

        /// <summary>Start a calibration run for the current game, or cancel the one in progress.</summary>
        internal void ToggleBitePointCalibration() => _bitePointCalibrator.Toggle();

        /// <summary>Clear the current game's calibration (current source) and stop any run.</summary>
        internal void ResetClutchBitePoint()
        {
            _bitePointCalibrator.Stop();
            UpdateCalibration(CalibrationKey(CurrentGameName), cur => cur == null ? null : RemoveEntry);
        }

        // Marker for UpdateCalibration: drop the entry.
        private static readonly MBoosterBitePointCalibration RemoveEntry = new MBoosterBitePointCalibration();

        /// <param name="change">Returns the replacement entry, <see cref="RemoveEntry"/>, or null for no change.</param>
        private void UpdateCalibration(string? key, Func<MBoosterBitePointCalibration?, MBoosterBitePointCalibration?> change)
        {
            if (_settings == null || string.IsNullOrEmpty(key)) return;
            int pct;
            lock (_bitePointLock)
            {
                var map = _settings.MBoosterClutchBitePointCalibrations;
                map.TryGetValue(key!, out var cur);
                var next = change(cur);
                if (next == null) return;
                var copy = new Dictionary<string, MBoosterBitePointCalibration>(map, StringComparer.OrdinalIgnoreCase);
                if (ReferenceEquals(next, RemoveEntry)) copy.Remove(key!);
                else copy[key!] = next;
                _settings.MBoosterClutchBitePointCalibrations = copy;
                pct = ReferenceEquals(next, RemoveEntry) ? -1 : next.Pct;
            }
            if (string.Equals(key, CalibrationKey(CurrentGameName), StringComparison.OrdinalIgnoreCase))
                _currentClutchBitePointPct = pct;
            PersistSettings();
        }

        // Data thread: the Game/Formula source's position this tick (0..1), NaN when none.
        private double ReadBitePointSource(ref GameData data)
        {
            switch (BitePointSource)
            {
                case MBoosterBitePointSource.Game:
                    var nd = data.NewData;
                    if (!data.GameRunning || nd == null) return double.NaN;
                    return Math.Max(0, Math.Min(1, nd.Clutch / 100.0));
                case MBoosterBitePointSource.Formula:
                    string formula = BitePointSourceFormula;
                    if (string.IsNullOrWhiteSpace(formula)) return double.NaN;
                    _bitePointFormulaResolver ??= CreateHapticsFormulaResolver();
                    double v = _bitePointFormulaResolver(formula);
                    return double.IsNaN(v) ? double.NaN : Math.Max(0, Math.Min(1, v / 100.0));
                default:
                    return double.NaN;
            }
        }

        /// <summary>Data thread: read the clutch source, refresh the current value, observe standing starts.</summary>
        private void UpdateClutchBitePoint(PluginManager pluginManager, ref GameData data)
        {
            Interlocked.Exchange(ref _bitePointSourceClutchBits, BitConverter.DoubleToInt64Bits(ReadBitePointSource(ref data)));

            string? game;
            try { game = pluginManager.GameName; }
            catch { game = null; }
            string? key = CalibrationKey(game);
            _currentClutchBitePointPct = ClutchBitePointInfo(game).Pct;

            bool hasClutch = TryGetBitePointClutchPosition(out double clutch01);
            var nd = data.NewData;
            string? gear = nd?.Gear;
            bool inGear = !string.IsNullOrEmpty(gear) && gear != "N" && gear != "0";
            bool ready = data.GameRunning && !data.GamePaused && nd != null && hasClutch && inGear;
            double speedKmh = nd?.SpeedKmh ?? double.NaN;

            if (_bitePointCalibrator.Update(key, ready, speedKmh, clutch01, out string runKey, out var runSample))
            {
                // A user-started run replaces the history and any pin.
                UpdateCalibration(runKey, _ =>
                {
                    var next = new MBoosterBitePointCalibration();
                    next.AddSample(runSample.Pct);
                    return next;
                });
                MozaLog.Info($"[AZOM/mBooster] Clutch bite point for {runKey} calibrated at {runSample.Pct}%");
            }

            if (_bitePointCalibrator.IsActive || !BitePointAutoCalibrate || key == null)
            {
                _bitePointAutoDetector.Reset();
                _bitePointAutoKey = null;
                return;
            }
            if (!string.Equals(key, _bitePointAutoKey, StringComparison.OrdinalIgnoreCase))
            {
                _bitePointAutoDetector.Reset();
                _bitePointAutoKey = key;
            }
            if (!_bitePointAutoDetector.Update(ready, speedKmh, clutch01, out var sample)) return;
            if (sample.ReleaseRatePerSec > MaxAutoReleaseRatePerSec)
            {
                MozaLog.Debug($"[AZOM/mBooster] Bite point sample {sample.Pct}% skipped: released at {sample.ReleaseRatePerSec * 100:F0}%/s");
                return;
            }
            bool added = false;
            UpdateCalibration(key, cur =>
            {
                if (cur?.PinnedPct != null) return null;
                var next = cur?.Clone() ?? new MBoosterBitePointCalibration();
                next.AddSample(sample.Pct);
                added = true;
                return next;
            });
            if (added)
                MozaLog.Info($"[AZOM/mBooster] Clutch bite point sample for {key}: {sample.Pct}% (now {ClutchBitePointInfo(game).Pct}%)");
        }
    }
}
