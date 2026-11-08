using System;
using System.Collections.Generic;
using System.Threading;
using GameReaderCommon;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using SimHub.Plugins;

namespace MozaPlugin
{
    /// <summary>
    /// Clutch bite point for the Clutch Bite Point effect: the clutch position
    /// source, storage per game and per vehicle (both per source), user-started
    /// calibration runs, and automatic learning/refinement from every clean
    /// standing start.
    ///
    /// A game's first calibration seeds both the game and the vehicle it was
    /// made in. A vehicle with no calibration of its own inherits the game's;
    /// its first automatic sample within <see cref="InheritAgreementPct"/> of
    /// that value keeps refining from the game's samples, anything further off
    /// discards them and starts the vehicle fresh.
    /// </summary>
    public partial class MozaPlugin
    {
        // Automatic samples are only trusted from a slow release: the bite is
        // detected from a lagging speed signal, so a fast release reads low.
        // Pedal fraction per second (1.0 = full travel in one second).
        private const double MaxAutoReleaseRatePerSec = 1.0;

        // Clutch travel (%) within which an automatic sample agrees with an
        // inherited game calibration.
        private const int InheritAgreementPct = 5;

        private const string VehicleKeySeparator = "|car:";

        private readonly MBoosterBitePointCalibrator _bitePointCalibrator = new MBoosterBitePointCalibrator();
        // Data thread only.
        private readonly MBoosterBitePointDetector _bitePointAutoDetector = new MBoosterBitePointDetector();
        private string? _bitePointAutoKey;
        private string? _bitePointVehicleGame;
        private Func<string?, double>? _bitePointFormulaResolver;
        // Serializes writers; readers use the copy-on-write dictionary snapshot.
        private readonly object _bitePointLock = new object();
        // Current vehicle (CarId, else CarModel) in the current game; null when unknown.
        private volatile string? _currentBitePointVehicle;
        // Current bite point for the current source, refreshed every data tick; -1 when none.
        private volatile int _currentClutchBitePointPct = -1;
        // Game/Formula source position (0..1, NaN = none) from the last data tick,
        // as double bits: a plain double can tear on the x86 build.
        private long _bitePointSourceClutchBits = BitConverter.DoubleToInt64Bits(double.NaN);
        // HidAxis source reader; created on first use, disposed at shutdown.
        private HidAxisReader? _bitePointHidReader;
        private bool _bitePointHidApplied;
        private readonly object _bitePointHidLock = new object();
        // Handed to the mBooster workers so a HID clutch is read live each motor tick.
        private Func<double>? _bitePointLiveClutch;

        /// <summary>The calibration in use as the UI shows it.</summary>
        internal readonly struct BitePointInfo
        {
            public readonly int Pct;
            public readonly int SampleCount;
            /// <summary>The vehicle has none of its own; this is the game's.</summary>
            public readonly bool Inherited;

            public BitePointInfo(int pct, int sampleCount, bool inherited)
            {
                Pct = pct;
                SampleCount = sampleCount;
                Inherited = inherited;
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

        /// <summary>The bite point in use (0..100) for the current game, vehicle and source, -1 when uncalibrated.</summary>
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
                _currentClutchBitePointPct = ClutchBitePointInfo().Pct;
                ApplyBitePointHidReader();
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

        /// <summary>The axis read for <see cref="MBoosterBitePointSource.HidAxis"/>; changing the
        /// axis (not just its travel) stops any run.</summary>
        internal HidAxisSelection? BitePointHidAxis
        {
            get => _settings?.MBoosterBitePointHidAxis;
            set
            {
                if (_settings == null) return;
                var old = _settings.MBoosterBitePointHidAxis;
                _settings.MBoosterBitePointHidAxis = value;
                if (BitePointSource == MBoosterBitePointSource.HidAxis && !(value?.SameAxis(old) ?? old == null))
                    _bitePointCalibrator.Stop();
                ApplyBitePointHidReader();
                PersistSettings();
            }
        }

        /// <summary>The chosen HID axis' raw value, for the picker's readout.</summary>
        internal bool TryGetBitePointHidRaw(out int raw)
        {
            raw = 0;
            HidAxisReader? reader;
            lock (_bitePointHidLock) reader = _bitePointHidReader;
            return reader != null && BitePointHidAxis != null && reader.TryGetRaw(out raw);
        }

        internal bool IsBitePointHidRangeCapturing
        {
            get
            {
                lock (_bitePointHidLock) return _bitePointHidReader?.IsCapturing ?? false;
            }
        }

        /// <summary>Start recording the axis' travel: press and release the pedal fully, then end it.</summary>
        internal void BeginBitePointHidRangeCapture()
        {
            lock (_bitePointHidLock) _bitePointHidReader?.BeginRangeCapture();
        }

        /// <summary>Store the recorded travel; the pedal is taken to be released now.</summary>
        /// <returns>False when the axis barely moved, leaving the travel unchanged.</returns>
        internal bool EndBitePointHidRangeCapture()
        {
            HidAxisReader? reader;
            lock (_bitePointHidLock) reader = _bitePointHidReader;
            var sel = BitePointHidAxis;
            if (reader == null || sel == null || !reader.EndRangeCapture(out int released, out int pressed)) return false;
            BitePointHidAxis = new HidAxisSelection
            {
                VendorId = sel.VendorId,
                ProductId = sel.ProductId,
                DeviceName = sel.DeviceName,
                DevicePath = sel.DevicePath,
                Usage = sel.Usage,
                LogicalMin = sel.LogicalMin,
                LogicalMax = sel.LogicalMax,
                RawReleased = released,
                RawPressed = pressed,
            };
            MozaLog.Info($"[AZOM] Clutch HID axis travel set: released {released}, pressed {pressed}");
            return true;
        }

        /// <summary>Stop recording without changing the stored travel.</summary>
        internal void CancelBitePointHidRangeCapture()
        {
            lock (_bitePointHidLock) _bitePointHidReader?.EndRangeCapture(out _, out _);
        }

        // Point the reader at the chosen axis while the HidAxis source is in use.
        private void ApplyBitePointHidReader()
        {
            var sel = BitePointSource == MBoosterBitePointSource.HidAxis ? BitePointHidAxis : null;
            lock (_bitePointHidLock)
            {
                if (sel == null)
                {
                    _bitePointHidReader?.SetTarget(null);
                    return;
                }
                (_bitePointHidReader ??= new HidAxisReader()).SetTarget(sel);
            }
        }

        /// <summary>Shutdown: stop the HID axis thread.</summary>
        internal void DisposeBitePointHidReader()
        {
            HidAxisReader? reader;
            lock (_bitePointHidLock)
            {
                reader = _bitePointHidReader;
                _bitePointHidReader = null;
            }
            reader?.Dispose();
        }

        /// <summary>Game/Formula source position from the last data tick (0..1), NaN when none.</summary>
        internal double BitePointSourceClutch01 =>
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _bitePointSourceClutchBits));

        /// <summary>The configured source's clutch position (0..1). Any thread.</summary>
        /// <returns>False when the source has no value.</returns>
        internal bool TryGetBitePointClutchPosition(out double position01)
        {
            var source = BitePointSource;
            if (source == MBoosterBitePointSource.MBoosterPedal)
                return TryGetMBoosterClutchPosition(out position01);
            if (source == MBoosterBitePointSource.HidAxis)
            {
                position01 = double.NaN;
                var sel = BitePointHidAxis;
                if (sel == null || !TryGetBitePointHidRaw(out int raw)) return false;
                position01 = sel.Normalize(raw);
                return !double.IsNaN(position01);
            }
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
                    state = !info.IsCalibrated ? Strings.BiteCal_Idle
                        : info.Inherited ? Strings.BiteCal_GameValue
                        : string.Format(Strings.BiteCal_Samples, info.SampleCount);
                    break;
            }
            return info.IsCalibrated ? info.Pct + "% · " + state : state;
        }

        // Settings key for a game under the current source. The mBooster pedal
        // keeps the bare game code; the others are prefixed.
        private string? GameKey(string? game)
        {
            if (string.IsNullOrEmpty(game)) return null;
            return BitePointSource switch
            {
                MBoosterBitePointSource.Game => "game:" + game,
                MBoosterBitePointSource.Formula => "formula:" + game,
                MBoosterBitePointSource.HidAxis => "hid:" + game,
                _ => game,
            };
        }

        private static string? VehicleKey(string? gameKey, string? vehicle) =>
            gameKey == null || string.IsNullOrEmpty(vehicle) ? null : gameKey + VehicleKeySeparator + vehicle;

        private static string GameKeyOf(string key)
        {
            int i = key.IndexOf(VehicleKeySeparator, StringComparison.Ordinal);
            return i < 0 ? key : key.Substring(0, i);
        }

        /// <summary>The calibration in use for the current game and vehicle under the current source.</summary>
        internal BitePointInfo ClutchBitePointInfo()
        {
            var map = _settings?.MBoosterClutchBitePointCalibrations;
            string? gameKey = GameKey(CurrentGameName);
            if (map == null || gameKey == null) return new BitePointInfo(-1, 0, false);
            string? vehicleKey = VehicleKey(gameKey, _currentBitePointVehicle);
            if (vehicleKey != null && map.TryGetValue(vehicleKey, out var own) && own != null)
                return new BitePointInfo(own.Pct, own.Samples?.Count ?? 0, false);
            if (map.TryGetValue(gameKey, out var game) && game != null)
                return new BitePointInfo(game.Pct, game.Samples?.Count ?? 0, vehicleKey != null);
            return new BitePointInfo(-1, 0, false);
        }

        /// <summary>Start a calibration run for the current vehicle, or cancel the one in progress.</summary>
        internal void ToggleBitePointCalibration() => _bitePointCalibrator.Toggle();

        /// <summary>
        /// Clear the current vehicle's own calibration (it falls back to the game's)
        /// and stop any run. With no vehicle known, clears the game's.
        /// </summary>
        internal void ResetClutchBitePoint()
        {
            _bitePointCalibrator.Stop();
            string? gameKey = GameKey(CurrentGameName);
            string? key = VehicleKey(gameKey, _currentBitePointVehicle) ?? gameKey;
            if (key == null) return;
            UpdateCalibrations(map => map.Remove(key));
        }

        /// <summary>Change the calibrations on a private copy, published only when
        /// <paramref name="change"/> returns true.</summary>
        private void UpdateCalibrations(Func<Dictionary<string, MBoosterBitePointCalibration>, bool> change)
        {
            if (_settings == null) return;
            lock (_bitePointLock)
            {
                var copy = new Dictionary<string, MBoosterBitePointCalibration>(
                    _settings.MBoosterClutchBitePointCalibrations, StringComparer.OrdinalIgnoreCase);
                if (!change(copy)) return;
                _settings.MBoosterClutchBitePointCalibrations = copy;
            }
            _currentClutchBitePointPct = ClutchBitePointInfo().Pct;
            PersistSettings();
        }

        private static MBoosterBitePointCalibration SingleSample(int pct)
        {
            var cal = new MBoosterBitePointCalibration();
            cal.AddSample(pct);
            return cal;
        }

        // A user-started run: the key's own calibration restarts from this sample,
        // and a game with no calibration yet takes it too.
        private void StoreRunSample(string key, int pct)
        {
            string gameKey = GameKeyOf(key);
            UpdateCalibrations(map =>
            {
                map[key] = SingleSample(pct);
                if (!map.ContainsKey(gameKey)) map[gameKey] = SingleSample(pct);
                return true;
            });
            MozaLog.Info($"[AZOM/mBooster] Clutch bite point for {key} calibrated at {pct}%");
        }

        // An automatic sample. See the class summary for how a vehicle inherits.
        private void StoreAutoSample(string key, int pct)
        {
            string gameKey = GameKeyOf(key);
            string note = "";
            UpdateCalibrations(map =>
            {
                if (map.TryGetValue(key, out var own) && own != null)
                {
                    var next = own.Clone();
                    next.AddSample(pct);
                    map[key] = next;
                    return true;
                }
                if (!map.TryGetValue(gameKey, out var game) || game == null)
                {
                    // The game's first calibration seeds the game and this vehicle.
                    map[gameKey] = SingleSample(pct);
                    if (key != gameKey) map[key] = SingleSample(pct);
                    note = " (first for the game)";
                    return true;
                }
                if (key == gameKey)
                {
                    var next = game.Clone();
                    next.AddSample(pct);
                    map[key] = next;
                    return true;
                }
                if (Math.Abs(pct - game.Pct) <= InheritAgreementPct)
                {
                    var next = game.Clone();
                    next.AddSample(pct);
                    map[key] = next;
                    note = $" (agrees with game {game.Pct}%)";
                }
                else
                {
                    map[key] = SingleSample(pct);
                    note = $" (game {game.Pct}% discarded)";
                }
                return true;
            });
            MozaLog.Info($"[AZOM/mBooster] Clutch bite point sample for {key}: {pct}%{note}, now {ClutchBitePointInfo().Pct}%");
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

        // Data thread: the current vehicle, kept while the game stays the same.
        private void TrackBitePointVehicle(string? game, StatusDataBase? nd)
        {
            if (!string.Equals(game, _bitePointVehicleGame, StringComparison.OrdinalIgnoreCase))
            {
                _bitePointVehicleGame = game;
                _currentBitePointVehicle = null;
            }
            string? vehicle = nd?.CarId;
            if (string.IsNullOrEmpty(vehicle)) vehicle = nd?.CarModel;
            if (!string.IsNullOrEmpty(vehicle)) _currentBitePointVehicle = vehicle;
        }

        /// <summary>The worker's live clutch reader for the current source; null = use the snapshot value.</summary>
        internal Func<double>? BitePointLiveClutchReader =>
            BitePointSource == MBoosterBitePointSource.HidAxis
                ? _bitePointLiveClutch ??= () => TryGetBitePointClutchPosition(out double p) ? p : double.NaN
                : null;

        /// <summary>Data thread: read the clutch source, refresh the current value, observe standing starts.</summary>
        private void UpdateClutchBitePoint(PluginManager pluginManager, ref GameData data)
        {
            if (!_bitePointHidApplied)
            {
                _bitePointHidApplied = true;
                ApplyBitePointHidReader();
            }
            Interlocked.Exchange(ref _bitePointSourceClutchBits, BitConverter.DoubleToInt64Bits(ReadBitePointSource(ref data)));

            string? game;
            try { game = pluginManager.GameName; }
            catch { game = null; }
            var nd = data.NewData;
            TrackBitePointVehicle(game, nd);
            string? gameKey = GameKey(game);
            string? key = VehicleKey(gameKey, _currentBitePointVehicle) ?? gameKey;
            _currentClutchBitePointPct = ClutchBitePointInfo().Pct;

            bool hasClutch = TryGetBitePointClutchPosition(out double clutch01);
            string? gear = nd?.Gear;
            bool inGear = !string.IsNullOrEmpty(gear) && gear != "N" && gear != "0";
            bool ready = data.GameRunning && !data.GamePaused && nd != null && hasClutch && inGear;
            double speedKmh = nd?.SpeedKmh ?? double.NaN;

            if (_bitePointCalibrator.Update(key, ready, speedKmh, clutch01, out string runKey, out var runSample))
                StoreRunSample(runKey, runSample.Pct);

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
            StoreAutoSample(key, sample.Pct);
        }
    }
}
