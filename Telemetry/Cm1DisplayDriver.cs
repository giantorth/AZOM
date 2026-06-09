using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Standalone driver for the CM1 base-bridged dash (group-0x35 keyed value stream,
    /// dev 0x14, responses 0x41). Runs on its OWN timer, independent of the tier-def
    /// <see cref="TelemetrySender"/>, so it drives the CM1 concurrently with a wheel screen
    /// (FSR1 driver or a tier-def display wheel) sharing the wheelbase
    /// <see cref="MozaSerialConnection"/>. Uses the dash-lane stream slots (18..28),
    /// disjoint from the wheel lane (0..8).
    ///
    /// The CM1 has no tier-def catalog; the field set is the flat
    /// <see cref="Cm1DashboardCatalog"/>. Each tick streams the full set as group-0x35
    /// records (one slot per 10-record chunk, latest-wins) plus a ~1 Hz session ping;
    /// handles host-initiated dashboard selects and follows wheel-side switches via the
    /// <c>Table 7, Param 6 Written: N</c> log (parsed in MozaPlugin). See
    /// docs/protocol/devices/ (CM1 group-0x35).
    /// </summary>
    internal sealed class Cm1DisplayDriver : IDisposable
    {
        private const double TickIntervalMs = 50.0;       // ~20 Hz per field (matches ~80 Hz frame cadence)
        private const int ChunkSlotBase = 18;             // dash lane: slots 18..28
        private const int LaneSlotCount = 11;             // slots 18..28 cleared on stop
        private const int PingSlot = 28;                  // session ping rides the top of the lane

        private readonly MozaSerialConnection _connection;
        private readonly Func<string, double> _resolve;

        private System.Timers.Timer? _timer;
        private int _tickInProgress;
        private volatile bool _running;
        private bool _lastConnected;
        private int _tickCounter;

        private volatile StatusDataBase? _latestGameData;
        private volatile bool _gameRunning;

        public Cm1DisplayDriver(MozaSerialConnection connection, Func<string, double> resolve)
        {
            _connection = connection;
            _resolve = resolve;
        }

        public bool IsRunning => _running;
        public void UpdateGameData(StatusDataBase? data) => _latestGameData = data;
        public void SetGameRunning(bool running) => _gameRunning = running;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _tickCounter = 0;
            _lastConnected = _connection.IsConnected;
            SendHandshake();
            _timer = new System.Timers.Timer(TickIntervalMs) { AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
            MozaLog.Info("[AZOM] CM1 display driver started (group-0x35 → 0x14)");
        }

        public void Stop()
        {
            if (!_running && _timer == null) return;
            _running = false;
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Elapsed -= OnTick;
                _timer.Dispose();
                _timer = null;
            }
            // Clear only the dash-lane slots — leave any co-resident wheel lane (0..8).
            try { _connection.ClearStreamSlots(ChunkSlotBase, LaneSlotCount); } catch { }
        }

        public void Dispose() => Stop();

        private void SendHandshake()
        {
            try
            {
                _connection.Send(Cm1DisplayEmitter.PresenceProbe);
                _connection.Send(Cm1DisplayEmitter.SessionPing);
            }
            catch { }
        }

        private void OnTick(object sender, ElapsedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _tickInProgress, 1, 0) != 0) return;
            try { Tick(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM1 driver tick error: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _tickInProgress, 0); }
        }

        private void Tick()
        {
            bool connected = _connection.IsConnected;
            if (!connected) { _lastConnected = false; return; }
            if (!_lastConnected) { _lastConnected = true; SendHandshake(); }

            var plugin = MozaPlugin.Instance;
            int oneHzEvery = Math.Max(1, (int)Math.Round(1000.0 / TickIntervalMs));

            // ~1 Hz session ping keeps the dash engaged (latest-wins on its own slot).
            if (_tickCounter % oneHzEvery == 0)
                _connection.SendStream((StreamKind)PingSlot, Cm1DisplayEmitter.SessionPing);

            // Telemetry disabled by the user: ping only, no value stream. The test
            // pattern overrides this so the dash can be verified with no game running.
            bool testMode = plugin?.DashboardTestPatternActive ?? false;
            if (!(plugin?.ActiveTelemetryEnabled ?? false) && !testMode) { _tickCounter++; return; }

            // Host-initiated dashboard switch (group-0x32/0x81 → dev 0x14).
            int pending = plugin?.TakePendingCm1Select() ?? -1;
            if (pending >= 0)
                _connection.Send(Cm1DisplayEmitter.BuildSelect(pending));

            var resolve = _resolve;
            long testNowMs = testMode ? DashboardTestPattern.NowMs() : 0;
            float ValueFor(Cm1FieldDef f)
            {
                var m = plugin?.GetCm1FieldMapping(f.FieldId);
                string prop = m?.Property ?? f.DefaultProperty;
                // Test pattern: sweep each field over an inferred natural range
                // (the CM1 catalog carries no per-field range). Constants stay put.
                if (testMode)
                {
                    if (f.Constant.HasValue) return (float)f.Constant.Value;
                    double max = DashboardTestPattern.NaturalMax(f.FieldId, prop);
                    return (float)(DashboardTestPattern.Sweep(f.FieldId, max, testNowMs) * f.Scale);
                }
                if (string.IsNullOrEmpty(prop))
                    return f.Constant.HasValue ? (float)f.Constant.Value : 0f;
                double raw = resolve != null ? resolve(prop) : 0.0;
                return (float)(raw * f.Scale);
            }

            // Stream the full flat field set as group-0x35 records, 10 per frame, one
            // stream slot per chunk (latest-wins → only ever drops a stale copy of the
            // same chunk, never a different field).
            var fields = Cm1DashboardCatalog.Fields;
            int per = Cm1DisplayEmitter.RecordsPerFrame;
            int chunk = 0;
            var recs = new List<Cm1DisplayEmitter.Record>(per);
            for (int i = 0; i < fields.Length; i++)
            {
                recs.Add(new Cm1DisplayEmitter.Record(fields[i].Key, ValueFor(fields[i])));
                if (recs.Count == per || i == fields.Length - 1)
                {
                    int slot = ChunkSlotBase + chunk;
                    if (slot < PingSlot)
                        _connection.SendStream((StreamKind)slot,
                            Cm1DisplayEmitter.BuildValueFrame(Cm1DisplayEmitter.GroupPrimary, recs));
                    recs = new List<Cm1DisplayEmitter.Record>(per);
                    chunk++;
                }
            }

            _tickCounter++;
        }
    }
}
