using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Standalone driver for the FSR V1 group-0x42 display push (dev 0x17). Runs on
    /// its OWN timer, independent of the tier-def <see cref="TelemetrySender"/>, so an
    /// FSR1 wheel screen and a CM2 dash (driven by a separate tier-def sender on the
    /// same or a different connection) can run concurrently. Shares the wheelbase
    /// <see cref="MozaSerialConnection"/> but uses only the wheel-lane stream slots
    /// (0..8), disjoint from a co-resident bus-CM2 pipeline at slot-base 18.
    ///
    /// Reuses <see cref="Fsr1DisplayEmitter"/> / <see cref="Fsr1DashboardCatalog"/> and
    /// the plugin's per-field mappings, active-index, pending-select, and Param-6
    /// follow (all on <see cref="MozaPlugin"/>). See docs/protocol/devices/wheel-0x17.md
    /// § Group 0x42.
    /// </summary>
    internal sealed class Fsr1DisplayDriver : IDisposable
    {
        private const double TickIntervalMs = 35.0;   // ~28.6 Hz, matches capture
        // FSR1 occupies the wheel lane: TierDash0..6 (records) + Enable (keepalive).
        private const int LaneSlotCount = 9;          // slots 0..8
        // _lastStreamedIndex sentinel meaning "currently in full-set fallback mode".
        private const int FloodSentinel = -2;

        private readonly MozaSerialConnection _connection;
        private readonly Func<string, double> _resolve;

        private System.Timers.Timer? _timer;
        private int _tickInProgress;
        private volatile bool _running;
        private bool _lastConnected;

        private volatile StatusDataBase? _latestGameData;
        private volatile bool _gameRunning;
        private int _tickCounter;
        private int _lastStreamedIndex = -1;

        public Fsr1DisplayDriver(MozaSerialConnection connection, Func<string, double> resolve)
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
            _lastStreamedIndex = -1;
            _lastConnected = _connection.IsConnected;
            SendDeclarationSweep();
            _timer = new System.Timers.Timer(TickIntervalMs) { AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
            MozaLog.Info("[AZOM] FSR V1 display driver started (group-0x42 → 0x17)");
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
            // Clear only the FSR1 wheel-lane slots — leave any co-resident CM2 lane.
            try { _connection.ClearStreamSlots(0, LaneSlotCount); } catch { }
        }

        public void Dispose() => Stop();

        private void SendDeclarationSweep()
        {
            try
            {
                foreach (var frame in Fsr1DisplayEmitter.DeclarationSweep)
                    _connection.Send(frame);
            }
            catch { }
        }

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _tickInProgress, 1, 0) != 0) return;
            try { Tick(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] FSR1 driver tick error: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _tickInProgress, 0); }
        }

        private void Tick()
        {
            bool connected = _connection.IsConnected;
            if (!connected) { _lastConnected = false; return; }
            if (!_lastConnected)
            {
                // Reconnect: re-send the declaration sweep so the wheel re-accepts records.
                _lastConnected = true;
                _lastStreamedIndex = -1;
                SendDeclarationSweep();
            }

            var plugin = MozaPlugin.Instance;
            int oneHzEvery = Math.Max(1, (int)Math.Round(1000.0 / TickIntervalMs));
            bool testMode = plugin?.DashboardTestPatternActive ?? false;
            bool probe = plugin?.Fsr1ProbeActive ?? false;

            // Telemetry disabled by the user: keepalive only (keeps the wheel engaged).
            // The test pattern / byte probe override this so the screen renders with no game.
            if (!(plugin?.ActiveTelemetryEnabled ?? false) && !testMode && !probe)
            {
                if (_tickCounter % oneHzEvery == 0)
                    _connection.SendStream(StreamKind.Enable, Fsr1DisplayEmitter.Keepalive43);
                _tickCounter++;
                return;
            }

            var data = _latestGameData;
            bool engineRunning = testMode || (data?.Rpms ?? 0.0) > 1.0;
            var resolve = _resolve;
            long testNowMs = testMode ? DashboardTestPattern.NowMs() : 0;

            // Host-initiated dashboard switch: emit the group-0x32/0x81 select command.
            int pending = plugin?.TakePendingFsr1Select() ?? -1;
            if (pending >= 0)
                _connection.Send(Fsr1DisplayEmitter.BuildSelect(pending));

            // Resolve a field's value, applying the user override's Scale/Bias gain and
            // the resolved encoding's output ceiling (so an overridden byte width re-clamps).
            long ValueFor(Fsr1Dashboard dash, Fsr1FieldDef f, Fsr1FieldMapping? m, long outMax)
            {
                if (f.Kind == Fsr1FieldKind.EngineFlag)
                    return engineRunning ? Fsr1DisplayEmitter.EngineFlagValue : 0;

                // Test pattern: sweep each field across its (resolved) output range.
                if (testMode)
                    return Clamp((long)Math.Round(
                        DashboardTestPattern.Sweep(f.FieldId, outMax, testNowMs)),
                        0, outMax);

                string prop = m?.Property ?? f.DefaultProperty;
                if (string.IsNullOrEmpty(prop)) return 0;

                double raw = resolve != null ? resolve(prop) : 0.0;
                raw = raw * (m?.Scale ?? 1.0) + (m?.Bias ?? 0.0);
                if (f.Kind == Fsr1FieldKind.Direct)
                    // Send the scaled value's digits as an integer — truncate, don't round.
                    // Precision is carried by Scale (shift the wanted decimals into the integer,
                    // e.g. ×100); the display is assumed to apply the inverse scale on its side.
                    return Clamp((long)raw, 0, outMax);

                double inMin = m != null ? m.InMin : f.DefaultInMin;
                double inMax = m != null ? m.InMax : f.DefaultInMax;
                double span = inMax - inMin;
                double t = span > 0 ? (raw - inMin) / span : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                return Clamp((long)Math.Round(t * outMax), 0, outMax);
            }

            // Per-field effective (offsets, encoding, value) honouring per-profile boundary /
            // endianness overrides — the emitter packs each field at its resolved span.
            (int[]? offsets, Fsr1Encoding enc, long value) LayoutValueFor(Fsr1Dashboard dash, Fsr1FieldDef f)
            {
                var m = plugin?.GetFsr1FieldMapping(dash.Key, f.FieldId);
                var (offsets, enc) = Fsr1DashboardCatalog.ResolveLayout(f, m, dash.PayloadLen);
                long outMax = Fsr1DashboardCatalog.OutputMaxFor(enc, f.FullScale);
                return (offsets, enc, ValueFor(dash, f, m, outMax));
            }

            // Probe modes (mutually exclusive, both override value computation):
            //  • Field-span probe (row editor open): light exactly the edited field's CURRENT
            //    byte span on its record type, zeroing every other record — the user watches
            //    that box move/grow as the boundary steppers change the span.
            //  • Byte-stepper probe (toolbar ◀/▶): a single data byte held at 0xFF; stepping
            //    the offset reveals which box each byte feeds (boundary = where the lit box
            //    changes, width = consecutive offsets lighting the same box).
            const int ProbeValue = 0xFF;
            var fieldProbe = probe ? plugin?.Fsr1FieldProbeTarget() : null;
            (byte probeType, int probeOff) = (probe && fieldProbe == null)
                ? (plugin?.Fsr1ProbeTarget() ?? ((byte)0, -1)) : ((byte)0, -1);

            byte[] RecordFor(Fsr1Dashboard dash)
            {
                if (fieldProbe is { } fp)
                    return dash.RecordType == fp.type
                        ? Fsr1DisplayEmitter.BuildProbeSpanRecord(dash, fp.startOff, fp.endOff, ProbeValue)
                        : Fsr1DisplayEmitter.BuildProbeRecord(dash, -1, 0); // other records: all-zero
                if (probe)
                    return Fsr1DisplayEmitter.BuildProbeRecord(
                        dash, dash.RecordType == probeType ? probeOff : -1, ProbeValue);
                // Compose catalog + synthetic split fields so net-new fields are packed too.
                var fields = Fsr1FieldComposer.FieldsFor(plugin, dash);
                return Fsr1DisplayEmitter.BuildRecord(dash, fields, f => LayoutValueFor(dash, f));
            }

            // Build the live numeric-viz record for one dash from its REAL telemetry values
            // (independent of probe/test — the panel shows actual data, not the probe pattern).
            Fsr1VizRecord BuildVizRecord(Fsr1Dashboard dash)
            {
                var fields = Fsr1FieldComposer.FieldsFor(plugin, dash);
                var frame = Fsr1DisplayEmitter.BuildRecord(dash, fields, f => LayoutValueFor(dash, f));
                var vfields = new List<Fsr1VizField>(fields.Count);
                foreach (var f in fields)
                {
                    var (offsets, enc, value) = LayoutValueFor(dash, f);
                    if (offsets == null || offsets.Length == 0) continue;
                    int start = offsets[0], end = offsets[offsets.Length - 1];
                    var bytes = new byte[end - start + 1];
                    for (int o = start; o <= end; o++)
                    {
                        int idx = 4 + o;
                        bytes[o - start] = (idx >= 0 && idx < frame.Length) ? frame[idx] : (byte)0;
                    }
                    bool synth = Fsr1FieldComposer.IsSynthetic(plugin, dash.Key, f.FieldId);
                    vfields.Add(new Fsr1VizField(f.Label, start, end, enc.ToString(), value, bytes, synth));
                }
                vfields.Sort((a, b) => a.Start.CompareTo(b.Start));
                return new Fsr1VizRecord(dash.RecordType, dash.Label, dash.PayloadLen, vfields.ToArray());
            }

            // PitHouse streams exactly ONE record type — the one for the wheel's
            // currently-displayed page — not all of them. Match that: when the active
            // index (tracked from the Param-6 0x0E log / g32-81 ack) maps to a known
            // record type, stream only that type at full rate. Fall back to the whole
            // live set ONLY while the index is genuinely unknown/unmapped, so the
            // screen is never dead on a page we haven't decoded. See wheel-0x17.md.
            int activeIdx = plugin?.GetActiveFsr1Index() ?? 0;
            var active = Fsr1DashboardCatalog.ByIndex(activeIdx);
            var live = Fsr1DashboardCatalog.LiveDashboards;

            if (active.Length > 0)
            {
                if (_lastStreamedIndex != activeIdx)
                {
                    _lastStreamedIndex = activeIdx;
                    // Drop any leftover records from the previous page / fallback so
                    // only the active type(s) keep retransmitting, then re-declare each
                    // (PitHouse re-declares on every switch before streaming). Most
                    // pages map to one type; the GT-style page streams two (11+12).
                    _connection.ClearStreamSlots(0, live.Length);
                    foreach (var dash in active)
                        _connection.Send(Fsr1DisplayEmitter.BuildDeclaration(dash));
                }
                for (int slot = 0; slot < active.Length; slot++)
                {
                    var dash = active[slot];
                    _connection.SendStream(
                        (StreamKind)((int)StreamKind.TierDash0 + slot),
                        RecordFor(dash));
                }
            }
            else
            {
                if (_lastStreamedIndex != FloodSentinel)
                {
                    _lastStreamedIndex = FloodSentinel;
                    MozaLog.Debug(
                        $"[AZOM] FSR1 active index {activeIdx} not in the decoded " +
                        "index→type map — streaming the full live set as a fallback " +
                        "until the wheel reports a known page.");
                }
                for (int slot = 0; slot < live.Length; slot++)
                {
                    var dash = live[slot];
                    if (dash.Fields.Length == 0) continue;
                    _connection.SendStream(
                        (StreamKind)((int)StreamKind.TierDash0 + slot),
                        RecordFor(dash));
                }
            }

            // Live numeric viz: publish a snapshot of the active record set's real telemetry
            // values for the channel-mapping panel's byte strip (only while it asks for it).
            if (plugin != null && plugin.Fsr1VizActive)
            {
                var vizSet = active.Length > 0 ? active : live;
                var records = new Fsr1VizRecord[vizSet.Length];
                for (int i = 0; i < vizSet.Length; i++)
                    records[i] = BuildVizRecord(vizSet[i]);
                plugin.SetFsr1VizSnapshot(new Fsr1VizSnapshot(records));
            }

            if (_tickCounter % oneHzEvery == 0)
                _connection.SendStream(StreamKind.Enable, Fsr1DisplayEmitter.Keepalive43);

            _tickCounter++;
        }

        private static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
