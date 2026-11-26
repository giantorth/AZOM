using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// source, storage per game and per vehicle (both per source), and
    /// calibration learned and refined from every clean standing start.
    ///
    /// A game's first calibration seeds both the game and the vehicle it was
    /// made in. A vehicle with no calibration of its own inherits the game's;
    /// its first automatic sample within <see cref="InheritAgreementPct"/> of
    /// that value keeps refining from the game's samples, anything further off
    /// discards them and starts the vehicle fresh.
    /// </summary>
    public partial class MozaPlugin
    {
        // Automatic samples are only trusted from a slow release: detection
        // can't see delay in the game's own speed output, which a fast release
        // turns into a low reading.
        // Pedal fraction per second: full travel in 0.6 s.
        private const double MaxAutoReleaseRatePerSec = 1.0 / 0.6;

        // How long BitePointCalState.Captured / TooFast is shown after a launch.
        private const double ResultShownSec = 5.0;

        // Clutch travel (%) within which an automatic sample agrees with an
        // inherited game calibration.
        private const int InheritAgreementPct = 5;

        private const string VehicleKeySeparator = "|car:";

        // Data thread only.
        private readonly MBoosterBitePointDetector _bitePointAutoDetector = new MBoosterBitePointDetector();
        private string? _bitePointAutoKey;
        // Set from any thread when the clutch signal changes: the data thread restarts detection.
        private int _bitePointAutoRestart;
        // Stopwatch ticks until which the last launch's result is reported; 0 = none. Data thread only.
        private long _bitePointResultUntil;
        // BitePointCalState as of the last data tick.
        private volatile int _bitePointCalState;
        // BitePointConditions as of the last data tick.
        private volatile int _bitePointConditions;
        // Last standing start measured for the current game and vehicle; null = none yet.
        private volatile BitePointLaunch? _bitePointLastLaunch;
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
        private readonly object _bitePointHidLock = new object();
        // Effective source last applied by SyncBitePointSource, as int; -1 = none yet.
        private int _bitePointAppliedSource = -1;
        // Handed to the mBooster workers so a HID clutch is read live each motor tick.
        private Func<double>? _bitePointLiveClutch;

        /// <summary>The calibration in use as the UI shows it.</summary>
        internal readonly struct BitePointInfo
        {
            public readonly int Pct;
            public readonly int SampleCount;
            /// <summary>The vehicle has none of its own; this is the game's.</summary>
            public readonly bool Inherited;
            /// <summary>The current vehicle's own calibration.</summary>
            public readonly bool OwnVehicle;

            public BitePointInfo(int pct, int sampleCount, bool inherited, bool ownVehicle)
            {
                Pct = pct;
                SampleCount = sampleCount;
                Inherited = inherited;
                OwnVehicle = ownVehicle;
            }

            public bool IsCalibrated => Pct >= 0;

            /// <summary>"Car", "Game", or "" when uncalibrated (AZOM.ClutchBitePointScope).</summary>
            public string Scope => !IsCalibrated ? "" : OwnVehicle ? "Car" : "Game";
        }

        /// <summary>A measured standing start.</summary>
        internal sealed class BitePointLaunch
        {
            public readonly int Pct;
            /// <summary>Clutch release rate at the bite, % of travel per second.</summary>
            public readonly int ReleaseRatePct;
            /// <summary>Released too fast to count.</summary>
            public readonly bool Skipped;

            public BitePointLaunch(int pct, int releaseRatePct, bool skipped)
            {
                Pct = pct;
                ReleaseRatePct = releaseRatePct;
                Skipped = skipped;
            }
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

        /// <summary>Whether a standing start can be measured now. Any thread.</summary>
        internal BitePointCalState BitePointCalibrationState => (BitePointCalState)_bitePointCalState;

        /// <summary>True when every one of <paramref name="conditions"/> was met on the last data tick. Any thread.</summary>
        internal bool BitePointConditionsMet(BitePointConditions conditions) =>
            ((BitePointConditions)_bitePointConditions & conditions) == conditions;

        /// <summary>Last standing start measured for the current game and vehicle, or null. Any thread.</summary>
        internal BitePointLaunch? BitePointLastLaunch => _bitePointLastLaunch;

        // Drop a measurement in progress: it read the old clutch signal.
        private void RestartBitePointDetection() => Interlocked.Exchange(ref _bitePointAutoRestart, 1);

        /// <summary>Load the saved Prediction travel times and save each new one.
        /// Init and Clear all settings.</summary>
        internal void LoadBitePointTravel()
        {
            MBoosterBitePointTravel.Load(_settings?.MBoosterBitePointTravelTimes);
            MBoosterBitePointTravel.Changed = () =>
            {
                if (_settings == null) return;
                _settings.MBoosterBitePointTravelTimes = MBoosterBitePointTravel.Snapshot();
                PersistSettings();
            };
        }

        /// <summary>The clutch source picked in the ShakeIt editor; see <see cref="BitePointSource"/>.</summary>
        internal MBoosterBitePointSource ChosenBitePointSource
        {
            get => _settings?.MBoosterBitePointSource ?? MBoosterBitePointSource.Game;
            set
            {
                if (_settings == null || _settings.MBoosterBitePointSource == value) return;
                _settings.MBoosterBitePointSource = value;
                SyncBitePointSource();
                PersistSettings();
            }
        }

        /// <summary>The plugin tab drives a connected, motorized Clutch pedal, so the
        /// clutch is read from that pedal whatever <see cref="ChosenBitePointSource"/> says.</summary>
        internal bool BitePointSourceIsMBoosterClutch =>
            MBoosterRoutedToPluginTab
            && _mboosterRegistry?.TryGetEffectPedal(MBoosterRole.Clutch, out _, out _) == true;

        /// <summary>Where the trigger and calibration read the clutch. Any thread.</summary>
        internal MBoosterBitePointSource BitePointSource =>
            BitePointSourceIsMBoosterClutch ? MBoosterBitePointSource.MBoosterPedal : ChosenBitePointSource;

        // On a change of BitePointSource: restart detection, re-point the HID reader. Any thread.
        private void SyncBitePointSource()
        {
            int source = (int)BitePointSource;
            if (Volatile.Read(ref _bitePointAppliedSource) == source) return;
            int was = Interlocked.Exchange(ref _bitePointAppliedSource, source);
            if (was == source) return;
            if (was >= 0) RestartBitePointDetection();
            _currentClutchBitePointPct = ClutchBitePointInfo().Pct;
            ApplyBitePointHidReader();
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
                if (BitePointSource == MBoosterBitePointSource.Formula) RestartBitePointDetection();
                PersistSettings();
            }
        }

        /// <summary>The axis read for <see cref="MBoosterBitePointSource.HidAxis"/>; changing the
        /// axis (not just its travel) restarts detection.</summary>
        internal HidAxisSelection? BitePointHidAxis
        {
            get => _settings?.MBoosterBitePointHidAxis;
            set
            {
                if (_settings == null) return;
                var old = _settings.MBoosterBitePointHidAxis;
                _settings.MBoosterBitePointHidAxis = value;
                if (BitePointSource == MBoosterBitePointSource.HidAxis && !(value?.SameAxis(old) ?? old == null))
                    RestartBitePointDetection();
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

        /// <summary>The bite point value for the calibration UI.</summary>
        internal static string BitePointValueText(in BitePointInfo info) =>
            info.IsCalibrated ? info.Pct + "%" : "—";

        /// <summary>Calibration indicator text: a standing start being measured or its result,
        /// else whether the vehicle or the game is calibrated and from how many samples.</summary>
        internal static string BitePointIndicatorText(in BitePointInfo info, BitePointCalState state, BitePointLaunch? launch)
        {
            if (state == BitePointCalState.Armed) return Strings.BiteCal_Ready;
            if (state == BitePointCalState.Releasing) return Strings.BiteCal_Measuring;
            if (state == BitePointCalState.TooFast) return Strings.BiteCal_TooFast;
            if (state == BitePointCalState.Captured && launch != null) return string.Format(Strings.BiteCal_Captured, launch.Pct);
            if (!info.IsCalibrated) return Strings.BiteCal_NotCalibrated;
            return string.Format(info.OwnVehicle ? Strings.BiteCal_CarCalibrated : Strings.BiteCal_GameCalibrated,
                info.SampleCount);
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
            if (map == null || gameKey == null) return new BitePointInfo(-1, 0, false, false);
            string? vehicleKey = VehicleKey(gameKey, _currentBitePointVehicle);
            if (vehicleKey != null && map.TryGetValue(vehicleKey, out var own) && own != null)
                return new BitePointInfo(own.Pct, own.Samples?.Count ?? 0, false, true);
            if (map.TryGetValue(gameKey, out var game) && game != null)
                return new BitePointInfo(game.Pct, game.Samples?.Count ?? 0, vehicleKey != null, false);
            return new BitePointInfo(-1, 0, false, false);
        }

        /// <summary>
        /// Clear the current vehicle's own calibration (it falls back to the game's).
        /// With no vehicle known, clears the game's.
        /// </summary>
        internal void ResetClutchBitePoint()
        {
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

        // A standing-start sample. See the class summary for how a vehicle inherits.
        private void StoreAutoSample(string key, BitePointSample sample)
        {
            string gameKey = GameKeyOf(key);
            int pct = sample.Pct;
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
            MozaLog.Info($"[AZOM/mBooster] Clutch bite point sample for {key}: {pct}% at {sample.ReleaseRatePerSec * 100:F0}%/s{note}, now {ClutchBitePointInfo().Pct}%");
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

        /// <summary>Data thread: read the clutch source, refresh the current value, learn from standing starts.</summary>
        private void UpdateClutchBitePoint(PluginManager pluginManager, ref GameData data)
        {
            SyncBitePointSource();
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
            UpdateAutoBiteFeel(gameKey, data.GameRunning && !data.GamePaused, hasClutch ? clutch01 : double.NaN);
            string? gear = nd?.Gear;
            bool inGear = !string.IsNullOrEmpty(gear) && gear != "N" && gear != "0";
            bool inGame = key != null && data.GameRunning && !data.GamePaused && nd != null;
            bool ready = inGame && hasClutch && inGear;
            double speedKmh = nd?.SpeedKmh ?? double.NaN;
            _bitePointConditions = (int)(
                (inGame ? BitePointConditions.InGame : 0)
                | (hasClutch ? BitePointConditions.HasClutch : 0)
                | (inGear ? BitePointConditions.InGear : 0)
                | (speedKmh < MBoosterBitePointDetector.StandstillKmh ? BitePointConditions.Stopped : 0)
                | (hasClutch && clutch01 >= MBoosterBitePointDetector.ClutchInLevel ? BitePointConditions.ClutchIn : 0));

            if (Interlocked.Exchange(ref _bitePointAutoRestart, 0) != 0
                || !string.Equals(key, _bitePointAutoKey, StringComparison.OrdinalIgnoreCase))
            {
                _bitePointAutoDetector.Reset();
                _bitePointAutoKey = key;
                _bitePointResultUntil = 0;
                _bitePointLastLaunch = null;
            }
            if (_bitePointAutoDetector.Update(ready, speedKmh, clutch01, out var sample) && key != null)
            {
                bool skipped = sample.ReleaseRatePerSec > MaxAutoReleaseRatePerSec;
                _bitePointLastLaunch = new BitePointLaunch(sample.Pct, (int)Math.Round(sample.ReleaseRatePerSec * 100), skipped);
                _bitePointResultUntil = Stopwatch.GetTimestamp() + (long)(ResultShownSec * Stopwatch.Frequency);
                if (!skipped) StoreAutoSample(key, sample);
                else MozaLog.Debug($"[AZOM/mBooster] Bite point sample {sample.Pct}% skipped: released at {sample.ReleaseRatePerSec * 100:F0}%/s");
            }

            var phase = _bitePointAutoDetector.Current;
            BitePointCalState state;
            if (ready && phase != MBoosterBitePointDetector.Phase.Waiting)
            {
                // The next attempt replaces the last result.
                _bitePointResultUntil = 0;
                state = phase == MBoosterBitePointDetector.Phase.Armed ? BitePointCalState.Armed : BitePointCalState.Releasing;
            }
            else if (_bitePointResultUntil != 0 && Stopwatch.GetTimestamp() < _bitePointResultUntil
                     && _bitePointLastLaunch is { } last)
                state = last.Skipped ? BitePointCalState.TooFast : BitePointCalState.Captured;
            else
                state = ready ? BitePointCalState.Waiting : BitePointCalState.NotReady;
            _bitePointCalState = (int)state;
        }
    }
}
