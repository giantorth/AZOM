using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.TestMode;
using MozaPlugin.Settings;

namespace MozaPlugin.Telemetry.Display
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
        // ~50 Hz. PitHouse streams each 0x42 record at ~20 ms during ACTIVE gameplay —
        // single page 02 @ ~21 ms (46.7 Hz) and GT dual page 11/12 each @ ~20 ms (≈50 Hz),
        // interleaved 11 12 11 12. Measured from usb-capture/fsr1 GT + game captures via
        // tools/fsr1-0x42-cadence.py. The old 35 ms matched the whole-capture AVERAGE
        // (~28/s), which is diluted by menu/idle gaps — not the live rate. Both active
        // records are still streamed per tick, so each refreshes at the full tick rate.
        private const double TickIntervalMs = 20.0;
        // FSR1 occupies the wheel lane: TierDash0..6 (records) + Enable (keepalive).
        private const int LaneSlotCount = 9;          // slots 0..8
        // _lastStreamedIndex sentinel meaning "currently in full-set fallback mode".
        private const int FloodSentinel = -2;

        private readonly MozaSerialConnection _connection;
        private readonly Func<string, double> _resolve;

        private System.Timers.Timer? _timer;
        private int _tickInProgress;
        private volatile bool _running;
        // Start/Stop race from PollStatus and the serial read thread (via
        // StartTelemetryIfReady); a check-then-act on _running let two Starts
        // each create a timer and orphan the first. Leaf lock, never on a tick.
        private readonly object _lifecycleLock = new object();
        private bool _lastConnected;

        private volatile StatusDataBase? _latestGameData;
        private volatile bool _gameRunning;
        private int _tickCounter;
        private int _lastStreamedIndex = -1;
        // Table-7 EEPROM write gate (see Tick): selects (Param 6) and display brightness
        // (Param 5) are both PERSISTED by the wheel, and back-to-back commits preceded
        // every observed param-store wedge (2026-08-06 crash bundles — two brightness
        // commits 700 ms apart, storage wedged ~35 s later). 1 s sits above that crash
        // pair with margin while staying at click cadence; the wheel's own combo-cycle
        // writes at ~0.5 s intervals without wedging, so 1 s host writes have headroom.
        // The gate only delays CONSECUTIVE writes — a lone click emits immediately.
        private const int ParamWriteMinIntervalMs = 1000;
        private int _lastParamWriteMs = Environment.TickCount - ParamWriteMinIntervalMs;

        // One-time-per-process guard for the catalog gapless self-check (runs on first Start).
        private static bool s_partitionsValidated;

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
            lock (_lifecycleLock)
            {
                if (_running) return;
                // Invariant self-check (once/process): every 0x42 catalog record must tile its
                // data range [5, PayloadLen-1] gaplessly — a gap is a dead byte on the wheel.
                // Warns if a future catalog edit breaks the partition; no effect in normal runs.
                if (!s_partitionsValidated)
                {
                    s_partitionsValidated = true;
                    try { Fsr1DashboardCatalog.ValidateDefaultPartitions(); } catch { }
                }
                _running = true;
                _tickCounter = 0;
                _lastStreamedIndex = -1;
                _lastConnected = _connection.IsConnected;
                SendDeclarationSweep();
                _timer = new System.Timers.Timer(TickIntervalMs) { AutoReset = true };
                _timer.Elapsed += OnTick;
                _timer.Start();
            }
            MozaLog.Info("[AZOM] FSR V1 display driver started (group-0x42 → 0x17)");
        }

        public void Stop()
        {
            lock (_lifecycleLock)
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
            // Resolved once per tick (profile + wheel-page lookup) instead of once per
            // field; the mapping dicts are copy-on-write snapshots.
            var fsr1Maps = plugin?.GetActiveFsr1Mappings();
            int oneHzEvery = Math.Max(1, (int)Math.Round(1000.0 / TickIntervalMs));
            bool testMode = plugin?.DashboardTestPatternActive ?? false;
            bool probe = plugin?.Fsr1Probe.Active ?? false;

            // What drives the wheel this tick: live telemetry, the test sweep, or the byte
            // probe. When none are active we normally just keepalive — but if the channel-
            // mapping panel's byte preview is open (Fsr1Probe.VizActive) we still flow through to
            // compute and publish a viz snapshot from live game data, so the preview stays
            // live while the user edits with telemetry-send toggled off. Wheel streaming is
            // skipped in that case (vizOnly), so nothing is actually pushed to the screen.
            bool streamLive = (plugin?.ActiveTelemetryEnabled ?? false) || testMode || probe;
            bool vizOnly = !streamLive && (plugin?.Fsr1Probe.VizActive ?? false);
            if (!streamLive && !vizOnly)
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

            // Host-initiated Table-7 param writes (dashboard select / display brightness):
            // only when actually driving the wheel — in viz-only mode pendings stay queued
            // so they fire once telemetry resumes. Both are wheel EEPROM writes, so at most
            // ONE goes out per gate window; the pending slots keep only the LATEST value,
            // so a spun cycle-dashboard encoder or a dragged brightness slider coalesces to
            // a single write instead of one per detent/step.
            if (streamLive && plugin != null
                && unchecked(Environment.TickCount - _lastParamWriteMs) >= ParamWriteMinIntervalMs)
            {
                int pending = plugin.TakePendingFsr1Select();
                if (pending >= 0)
                {
                    _connection.Send(Fsr1DisplayEmitter.BuildSelect(pending));
                    _lastParamWriteMs = Environment.TickCount;
                }
                else
                {
                    int bright = plugin.TakePendingFsr1Brightness();
                    if (bright >= 0)
                    {
                        foreach (var f in Fsr1DisplayEmitter.BuildBrightness(bright))
                            _connection.Send(f);
                        _lastParamWriteMs = Environment.TickCount;
                        MozaLog.Info($"[AZOM] FSR1 display brightness → {bright}%");
                    }
                }
            }

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
                // User override wins; otherwise the catalog default (tyre +300, lap time ×1000).
                raw = raw * (m?.Scale ?? f.DefaultScale) + (m?.Bias ?? f.DefaultBias);
                if (f.Kind == Fsr1FieldKind.SignedMagnitude)
                {
                    // 24-bit sign-magnitude: bit23 (data[9] bit7) = sign, the low **23 bits**
                    // (data[9] bits 0-6 + data[10-11]) = |value| in ms. Verified from a PitHouse
                    // gap capture where data[9] carried magnitude bits (0x07/0x08/0x99) and the
                    // range reached ±1640 s — so the field is NOT limited to ±65 s. Clamp |value|
                    // to 0x7FFFFF only to keep the sign bit isolated (caps at ~2.3 h, unreachable
                    // in practice). A negative delta (ahead) must not clamp to 0.
                    long sv = (long)raw;
                    long mag = System.Math.Abs(sv);
                    if (mag > 0x7FFFFF) mag = 0x7FFFFF;
                    long v = (sv < 0 ? 0x800000L : 0L) | mag;
                    // Diagnostic: shows what we actually resolve vs what SimHub displays for this
                    // property. If 'resolved' ≠ the SimHub inspector value, we're reading a different
                    // value than the UI shows (path/timing), not a wire-format problem.
                    //
                    // Steady-state instrument with no error condition — it fires once a second
                    // for EVERY gap field on EVERY streamed record (the catalog has 5, and the
                    // fallback path streams the whole live set), so left ungated it costs 1–5
                    // lines/s for the life of the session. Behind the verbose gate it stays
                    // available to whoever is actually debugging gap rendering.
                    if (MozaLog.WireDebugEnabled && _tickCounter % oneHzEvery == 0)
                        MozaLog.Debug($"[AZOM] FSR1 gap '{prop}' resolved={raw / 1000.0:F3}s -> wire {(v >> 16) & 0xFF:X2} {(v >> 8) & 0xFF:X2} {v & 0xFF:X2} (shows {(sv < 0 ? "-" : "")}{mag / 1000.0:F3}s)");
                    return v;
                }
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

            // A field's value given the geometry the partition resolved for it — the partition
            // owns offsets/encoding/bits; this applies the field's mapping + output ceiling.
            // Byte-aligned slots clamp to the byte-width cap; packed slots to their bit-width cap.
            long ValueForSlot(Fsr1Dashboard dash, Fsr1Slot slot)
            {
                var f = slot.Field;
                Fsr1FieldMapping? m = null;
                if (fsr1Maps != null && fsr1Maps.TryGetValue(dash.Key, out var inner)
                    && inner != null && inner.TryGetValue(f.FieldId, out var fm))
                    m = fm;
                long outMax = slot.IsByteAligned
                    ? Fsr1DashboardCatalog.OutputMaxFor(slot.Enc, f.FullScale)
                    : Fsr1DashboardCatalog.BitOutputMax(slot.BitWidth, f.FullScale);
                return ValueFor(dash, f, m, outMax);
            }

            // Probe modes (mutually exclusive, both override value computation):
            //  • Field-span probe (row editor open): light exactly the edited field's CURRENT
            //    byte span on its record type, zeroing every other record — the user watches
            //    that box move/grow as the boundary steppers change the span.
            //  • Byte-stepper probe (toolbar ◀/▶): a single data byte swept 0→255→0;
            //    stepping the offset reveals which box each byte feeds (boundary = where the
            //    animated box changes, width = consecutive offsets driving the same box).
            //    The value RAMPS rather than holding a constant 0xFF: a static byte renders a
            //    16-bit gauge's LOW byte at only 255/65535 ≈ 0.4% (invisible), so the box looks
            //    dead on every other step. A 0→255→0 triangle makes every byte's box visibly
            //    pulse, including low bytes (which sweep 0→255 — small but clearly moving).
            long probeRamp = DashboardTestPattern.NowMs() / 3 % 512;   // 0..511 over ~1.5 s
            int probeValue = (int)(probeRamp < 256 ? probeRamp : 511 - probeRamp); // triangle 0..255..0
            var fieldProbe = probe ? plugin?.Fsr1Probe.FieldProbeTarget() : null;
            (byte probeType, int probeOff) = (probe && fieldProbe == null)
                ? (plugin?.Fsr1Probe.Target() ?? ((byte)0, -1)) : ((byte)0, -1);

            byte[] RecordFor(Fsr1Dashboard dash)
            {
                if (fieldProbe is { } fp)
                {
                    if (dash.RecordType != fp.type)
                        return Fsr1DisplayEmitter.BuildProbeRecord(dash, -1, 0); // other records: all-zero
                    if (fp.packed)
                    {
                        // Overlay the ramp on just this field's bits over the live record, so the
                        // byte it shares with a neighbour keeps the neighbour's real value visible.
                        var live = Fsr1DashboardCatalog.ResolvePartition(dash);
                        return Fsr1DisplayEmitter.BuildBitProbeRecord(dash, live, slot => ValueForSlot(dash, slot),
                            fp.bitOffset, fp.bitWidth, probeValue, fp.msbFirst);
                    }
                    return Fsr1DisplayEmitter.BuildProbeSpanRecord(dash, fp.startOff, fp.endOff, probeValue);
                }
                if (probe)
                    return Fsr1DisplayEmitter.BuildProbeRecord(
                        dash, dash.RecordType == probeType ? probeOff : -1, probeValue);
                // Pack each catalog slot's value — never a gap/overlap on the wire.
                var partition = Fsr1DashboardCatalog.ResolvePartition(dash);
                return Fsr1DisplayEmitter.BuildRecord(dash, partition, slot => ValueForSlot(dash, slot));
            }

            // Build a record, isolating a malformed partition/field so one bad record can't abort
            // the whole tick — that used to swallow the exception at the Tick level and freeze ALL
            // emission (the wheel, gap included, goes blank until the page changes). Returns null
            // on error; the caller leaves that slot's last value in place and keeps streaming the rest.
            byte[]? SafeRecordFor(Fsr1Dashboard dash)
            {
                try { return RecordFor(dash); }
                catch (Exception ex)
                {
                    if (_tickCounter % oneHzEvery == 0)
                        MozaLog.Debug($"[AZOM] FSR1 record 0x{dash.RecordType:X2} skipped (build error): {ex.Message}");
                    return null;
                }
            }

            // Build the live numeric-viz record for one dash from its REAL telemetry values
            // (independent of probe/test — the panel shows actual data, not the probe pattern).
            Fsr1VizRecord BuildVizRecord(Fsr1Dashboard dash)
            {
                var partition = Fsr1DashboardCatalog.ResolvePartition(dash);
                var frame = Fsr1DisplayEmitter.BuildRecord(dash, partition, slot => ValueForSlot(dash, slot));
                var vfields = new List<Fsr1VizField>(partition.Count);
                foreach (var slot in partition)
                {
                    var f = slot.Field;
                    int start = slot.ByteStart, end = slot.ByteEnd;
                    var bytes = new byte[end - start + 1];
                    for (int o = start; o <= end; o++)
                    {
                        int idx = 4 + o;
                        bytes[o - start] = (idx >= 0 && idx < frame.Length) ? frame[idx] : (byte)0;
                    }
                    long value = ValueForSlot(dash, slot);
                    string encStr = slot.IsByteAligned ? slot.Enc.ToString() : $"{slot.BitWidth}b.{slot.BitOffset & 7}";
                    vfields.Add(new Fsr1VizField(f.Label, start, end, encStr, value, bytes,
                        slot.IsByteAligned ? -1 : slot.BitOffset, slot.IsByteAligned ? 0 : slot.BitWidth));
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

            // Push records to the wheel only when actually driving it. In viz-only mode we
            // skip all streaming (the panel just wants the computed values for its preview).
            if (streamLive)
            {
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
                    int streamSlot = 0;
                    foreach (var dash in active)
                    {
                        if (dash.IsBackground)
                        {
                            // Background tyre/status cache (type-0d): one-shot at ~0.5 Hz so the
                            // primary page keeps its full refresh. Matches PitHouse interleaving
                            // ~1 cache frame per 100 primary frames. Send() = non-retransmitting.
                            if (_tickCounter % (oneHzEvery * 2) == 0)
                            { var bg = SafeRecordFor(dash); if (bg != null) _connection.Send(bg); }
                            continue;
                        }
                        var rec = SafeRecordFor(dash);
                        if (rec != null)
                            _connection.SendStream(
                                (StreamKind)((int)StreamKind.TierDash0 + streamSlot), rec);
                        streamSlot++;
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
                        if (dash.IsBackground)
                        {
                            if (_tickCounter % (oneHzEvery * 2) == 0)
                            { var bg = SafeRecordFor(dash); if (bg != null) _connection.Send(bg); }
                            continue;
                        }
                        var rec = SafeRecordFor(dash);
                        if (rec != null)
                            _connection.SendStream(
                                (StreamKind)((int)StreamKind.TierDash0 + slot), rec);
                    }
                }
            }

            // Live numeric viz: publish a snapshot of the active record set's real telemetry
            // values for the channel-mapping panel's byte strip (only while it asks for it).
            if (plugin != null && plugin.Fsr1Probe.VizActive)
            {
                // UI-only preview — must never take down the streaming tick if one record's
                // partition is malformed. Isolate it so the wheel keeps updating regardless.
                try
                {
                    var vizSet = active.Length > 0 ? active : live;
                    var records = new Fsr1VizRecord[vizSet.Length];
                    for (int i = 0; i < vizSet.Length; i++)
                        records[i] = BuildVizRecord(vizSet[i]);
                    plugin.Fsr1Probe.SetVizSnapshot(new Fsr1VizSnapshot(records));
                }
                catch (Exception ex)
                {
                    if (_tickCounter % oneHzEvery == 0)
                        MozaLog.Debug($"[AZOM] FSR1 viz snapshot skipped (build error): {ex.Message}");
                }
            }

            if (_tickCounter % oneHzEvery == 0)
                _connection.SendStream(StreamKind.Enable, Fsr1DisplayEmitter.Keepalive43);

            _tickCounter++;
        }

        private static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);

    }
}
