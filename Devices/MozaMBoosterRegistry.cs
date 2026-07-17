using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Multi-device owner for Moza mBooster Pedals (PID <c>0x0008</c>).
    /// Walks <see cref="MozaPortDiscovery"/> for every connected mBooster,
    /// spawns one <see cref="MBoosterDeviceController"/> per device, fans
    /// out game-data updates, and merges per-device HID positions into the
    /// shared <c>MozaData.{Throttle,Brake,Clutch}Position</c> fields based
    /// on each device's user-assigned role.
    ///
    /// Stable per-device identity is the USB device instance ID — survives
    /// reconnects so the profile's per-device settings (role, per-effect
    /// knobs) stick across replug.
    /// </summary>
    public sealed class MozaMBoosterRegistry : IDisposable
    {
        // identity → controller (one per physical mBooster)
        private readonly Dictionary<string, MBoosterDeviceController> _byIdentity =
            new Dictionary<string, MBoosterDeviceController>(StringComparer.OrdinalIgnoreCase);
        // Enumeration order (matches first-seen ordering) so role-merge
        // can deterministically resolve collisions (first-wins).
        private readonly List<MBoosterDeviceController> _order =
            new List<MBoosterDeviceController>();
        private readonly object _lock = new object();

        // Lock-free fast-path counter for the DataUpdate hot path. Updated
        // only on Refresh() add/drop, which happens on the reconnect timer
        // (5s cadence). Reading is a volatile int load — no lock, no alloc.
        private int _controllerCount;
        /// <summary>True iff at least one controller is registered. Lock-free; safe to call from DataUpdate.</summary>
        public bool HasControllers => Volatile.Read(ref _controllerCount) > 0;

        private readonly MozaData _data;
        private readonly Func<string, MBoosterDeviceSettings?> _settingsLookup;
        private readonly Func<bool> _isShuttingDown;
        private readonly Action<MBoosterDeviceController>? _onDeviceDetectedEdge;
        private readonly Func<string, double> _customEffectFormulaEvaluator;
        private readonly Action<string, string>? _onSerialResolved;

        // Collision logging — emit at most one warning per (role, identity-tail)
        // combo per session to avoid spam.
        private readonly HashSet<string> _collisionsLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True iff at least one mBooster is currently detected (UI gate).</summary>
        public bool AnyDetected
        {
            get
            {
                lock (_lock)
                    return _order.Any(c => c.Detected);
            }
        }

        /// <summary>Snapshot of all known controllers in enumeration order.</summary>
        public IReadOnlyList<MBoosterDeviceController> Devices
        {
            get
            {
                lock (_lock)
                    return new ReadOnlyCollection<MBoosterDeviceController>(_order.ToList());
            }
        }

        /// <summary>Fired (foreground thread NOT guaranteed) on detection rising edge.</summary>
        public event Action<MBoosterDeviceController>? DeviceDetected;
        /// <summary>Fired when a new device is added to the registry.</summary>
        public event Action<MBoosterDeviceController>? DeviceAdded;
        /// <summary>Fired when a device is removed (port disappeared from registry).</summary>
        public event Action<MBoosterDeviceController>? DeviceRemoved;

        public MozaMBoosterRegistry(
            MozaData data,
            Func<string, MBoosterDeviceSettings?> settingsLookup,
            Func<bool> isShuttingDown,
            Action<MBoosterDeviceController>? onDeviceDetectedEdge = null,
            Func<string, double>? customEffectFormulaEvaluator = null,
            Action<string, string>? onSerialResolved = null)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _settingsLookup = settingsLookup ?? throw new ArgumentNullException(nameof(settingsLookup));
            _isShuttingDown = isShuttingDown ?? (() => false);
            _onDeviceDetectedEdge = onDeviceDetectedEdge;
            _customEffectFormulaEvaluator = customEffectFormulaEvaluator ?? (_ => 0.0);
            _onSerialResolved = onSerialResolved;
        }

        /// <summary>
        /// Walk the port-discovery cache for mBooster PIDs; spawn a controller
        /// for any newly-attached device, drop any whose port has disappeared.
        /// Called from the plugin's 5 s reconnect timer alongside the AB9
        /// reconnect path. Idempotent — no-ops on a healthy steady state.
        /// </summary>
        public void Refresh()
        {
            if (_isShuttingDown()) return;

            var ports = MozaPortDiscovery.Instance.EnumerateMatching(MozaUsbIds.IsMBoosterPid);

            // Map current identities → port info for quick add/drop diff.
            var currentByIdentity = new Dictionary<string, MozaPortDiscovery.PortInfo>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ports.Count; i++)
            {
                var p = ports[i];
                // Identity falls back to "port:<COMx>" if the registry gave us
                // no instance ID — keeps the controller distinct from siblings
                // while making the limitation visible in the UI.
                string id = !string.IsNullOrEmpty(p.InstanceId)
                    ? p.InstanceId
                    : "port:" + p.PortName;
                currentByIdentity[id] = p;
            }

            List<MBoosterDeviceController>? added = null;
            List<MBoosterDeviceController>? removed = null;

            lock (_lock)
            {
                // Add new ones.
                foreach (var kvp in currentByIdentity)
                {
                    if (_byIdentity.ContainsKey(kvp.Key)) continue;
                    var c = new MBoosterDeviceController(
                        identity: kvp.Key,
                        portName: kvp.Value.PortName,
                        settingsLookup: () => _settingsLookup(kvp.Key),
                        isShuttingDown: _isShuttingDown,
                        customEffectFormulaEvaluator: _customEffectFormulaEvaluator,
                        containerId: kvp.Value.ContainerId);
                    // Wire rising-edge detection to the plugin-level handler
                    // (applies profile, reads calibration, etc.).
                    c.DetectedRisingEdge += () => OnControllerDetected(c);
                    // Forward the serial-interrogation result so the plugin can
                    // re-key per-device settings from the transport identity to
                    // the stable serial (settings follow the physical unit).
                    c.SerialResolved += (id, ser) =>
                    {
                        try { _onSerialResolved?.Invoke(id, ser); }
                        catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnSerialResolved: {ex.Message}"); }
                    };
                    _byIdentity[kvp.Key] = c;
                    _order.Add(c);
                    (added ??= new List<MBoosterDeviceController>()).Add(c);
                }

                // Drop stale ones.
                var toRemove = new List<string>();
                foreach (var kvp in _byIdentity)
                {
                    if (!currentByIdentity.ContainsKey(kvp.Key))
                        toRemove.Add(kvp.Key);
                }
                foreach (var key in toRemove)
                {
                    var c = _byIdentity[key];
                    _byIdentity.Remove(key);
                    _order.Remove(c);
                    (removed ??= new List<MBoosterDeviceController>()).Add(c);
                }

                // Publish the new count for the lock-free hot-path gate.
                Volatile.Write(ref _controllerCount, _order.Count);
            }

            // Connect newcomers + dispose removed (outside the lock — Connect
            // may block briefly and we don't want to hold the registry lock).
            if (added != null)
            {
                foreach (var c in added)
                {
                    MozaLog.Info($"[AZOM/mBooster] Discovered {MBoosterDeviceController.ShortIdentity(c.Identity)} on {c.PortName}");
                    try
                    {
                        c.TryConnect();
                        try { DeviceAdded?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceAdded handler: {ex.Message}"); }
                    }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] Connect failed for {c.Identity}: {ex.Message}"); }
                }
            }
            if (removed != null)
            {
                foreach (var c in removed)
                {
                    MozaLog.Info($"[AZOM/mBooster] Removed {MBoosterDeviceController.ShortIdentity(c.Identity)} (port gone from registry)");
                    try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Dispose: {ex.Message}"); }
                    try { DeviceRemoved?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceRemoved handler: {ex.Message}"); }
                }
            }

            // Reconnect any healthy-port-but-disconnected controllers (port
            // wedged, restored on next attempt). Snapshot under the lock, then
            // connect outside it: SerialPort.Open can block ~600ms under Wine
            // and must not stall other _lock holders (the telemetry/HID fan-out
            // takes _lock every tick).
            List<MBoosterDeviceController>? toReconnect = null;
            lock (_lock)
            {
                foreach (var c in _order)
                    if (!c.IsConnected)
                        (toReconnect ??= new List<MBoosterDeviceController>()).Add(c);
            }
            if (toReconnect != null)
            {
                foreach (var c in toReconnect)
                {
                    try { c.TryConnect(); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Reconnect: {ex.Message}"); }
                }
            }
        }

        private void OnControllerDetected(MBoosterDeviceController c)
        {
            try { _onDeviceDetectedEdge?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnDetectedEdge: {ex.Message}"); }
            try { DeviceDetected?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceDetected handler: {ex.Message}"); }
        }

        /// <summary>
        /// Fan-out the latest game telemetry to every controller's worker.
        /// Called from <c>MozaPlugin.DataUpdate</c> once per SimHub tick.
        /// </summary>
        public void OnDataUpdate(in MBoosterTelemetrySnapshot snap)
        {
            lock (_lock)
            {
                for (int i = 0; i < _order.Count; i++)
                    _order[i].PostTelemetry(snap);
            }
        }

        /// <summary>
        /// Update one device's HID position (called from <see cref="Protocol.MozaHidReader"/>
        /// when an mBooster HID axis changes). After saving the position on
        /// the controller, the merge step writes per-role values into
        /// <c>MozaData.{Throttle,Brake,Clutch}Position</c>. First-wins on
        /// role collision (logged once per (role, identity) combo).
        /// </summary>
        public void OnHidAxisUpdate(string identity, string containerId, int axisIndex, double pos01)
        {
            if (string.IsNullOrEmpty(identity)) return;
            if (axisIndex < 0) axisIndex = 0;
            if (axisIndex >= MBoosterDeviceController.MaxAxes) return;
            if (double.IsNaN(pos01)) pos01 = 0;
            if (pos01 < 0) pos01 = 0;
            if (pos01 > 1) pos01 = 1;

            MBoosterDeviceController? c;
            lock (_lock)
            {
                if (!_byIdentity.TryGetValue(identity, out c))
                {
                    // Container-ID rung: the HID + CDC interfaces of one physical
                    // device share a Container ID, so this pairs them even when
                    // Windows gives the two interfaces unrelated instance IDs (the
                    // common multi-lane case — see docs). No-ops when the ID is
                    // empty/absent (older drivers / Wine), falling through to the
                    // prefix + single-device rungs exactly as before.
                    c = FindByContainerIdLocked(containerId);
                    if (c == null) c = FindByInstancePrefixLocked(identity);
                    if (c == null && _byIdentity.Count == 1)
                    {
                        // Real-hardware logs show the HID and CDC interfaces of
                        // the same physical mBooster can get entirely unrelated
                        // Windows instance IDs (not just a differing trailing
                        // segment — see docs/protocol/devices/mbooster.md), so
                        // prefix matching can't always pair them. With exactly
                        // one mBooster registered there's no ambiguity: it must
                        // be this one.
                        using (var e = _byIdentity.GetEnumerator())
                        {
                            e.MoveNext();
                            c = e.Current.Value;
                        }
                        LogSingleDeviceFallbackOnceLocked(identity, c.Identity);
                    }
                    if (c == null) LogUnmatchedHidIdentityOnceLocked(identity);
                }
            }
            if (c == null) return;

            // Pedal Feel — host-side shaping of the raw HID position,
            // applied here so every downstream consumer (position bar,
            // MergePositions -> game telemetry, the effect worker's
            // brake-position fallback) sees the same shaped value. Does not
            // touch CurveY (still written to the device's own output-curve
            // command unchanged) — see docs/protocol/devices/mbooster.md
            // "Pedal Feel". Start/End of Travel (mm) is NOT shaped here —
            // it's a real hardware calibration write (mbooster-brake-travel-
            // start/end); the device's own firmware already clips/rescales
            // the raw signal before this HID read ever sees it.
            // Axis 0 (the master unit's pedal) carries the host-side Pedal Feel
            // shaping (deadzone / max force / input curve) exactly as the
            // single-axis path always has — those controls are calibrated
            // against the master pedal. Chained axes (1+) route raw for now;
            // per-axis Pedal Feel is a follow-up (Stage 3).
            // Per-axis Pedal Feel: shape EACH pedal's HID by ITS OWN config —
            // the master (axis 0) from the lane's flat fields, each chained pedal
            // from its per-pedal entry. Host-side only (deadzone / max force /
            // input curve); the wire calibration is applied separately in
            // ApplyMBoosterToHardware.
            var laneSettings = _settingsLookup(c.Identity);
            IMBoosterPedalConfig? cfg = laneSettings;
            if (axisIndex > 0)
            {
                var pedals = laneSettings?.Pedals;
                cfg = (pedals != null && pedals.TryGetValue(axisIndex, out var pp)) ? pp : null;
            }

            double posPct = pos01 * 100.0;
            if (cfg != null)
            {
                // Raw 0-100% HID travel isn't a fixed 0-200kg scale — it's
                // whatever this pedal's OWN Max Threshold calibration (Sim Input
                // Mapping) currently says 100% is (200kg fallback when unset).
                double fullScaleKg = cfg.MaxThresholdKg >= 0 ? cfg.MaxThresholdKg : 200.0;
                if (cfg.DeadzoneKg > 0 || cfg.MaxForceKg < fullScaleKg)
                    posPct = ApplyDeadzoneAndMaxForce(posPct, cfg.DeadzoneKg, cfg.MaxForceKg, fullScaleKg);
                // Store the pre-input-curve percent for EVERY axis so the UI's
                // live curve markers follow whichever pedal is selected (axis 0
                // also mirrored to LastRawPercentPreCurve for legacy callers).
                if (axisIndex < c.LastAxisRawPercentPreCurve.Length) c.LastAxisRawPercentPreCurve[axisIndex] = posPct;
                if (axisIndex == 0) c.LastRawPercentPreCurve = posPct;
                if (cfg.InputCurveY != null && cfg.InputCurveY.Length == 5)
                    posPct = EvaluateInputCurve(cfg.InputCurveY, posPct);
            }
            else
            {
                if (axisIndex < c.LastAxisRawPercentPreCurve.Length) c.LastAxisRawPercentPreCurve[axisIndex] = posPct;
                if (axisIndex == 0) c.LastRawPercentPreCurve = posPct;
            }

            double shaped01 = posPct / 100.0;
            if (axisIndex == 0) c.LastHidPosition = shaped01;

            c.LastAxisPositions[axisIndex] = shaped01;
            if (axisIndex + 1 > c.AxisCount) c.AxisCount = axisIndex + 1;

            // Merge step: re-compute the active positions across all devices
            // every time any one of them ticks. Cheap (N ≤ 3 typically).
            MergePositions();
        }

        /// <summary>
        /// Deadzone + Max Force, in kg of force — both host-side only.
        /// <paramref name="fullScaleKg"/> is the force at which raw 0-100%
        /// HID travel is CURRENTLY known to reach 100% — i.e. the device's
        /// own Max Threshold calibration (<c>MaxThresholdKg</c>/
        /// <c>EncodeThresholdKg</c>, Sim Input Mapping) when the user has
        /// set it from this plugin, or a 200kg fallback guess otherwise
        /// (the raw HID axis has no independent force calibration of its
        /// own to query). Combined into one kg-space remap rather than two
        /// independent percent-space steps:
        /// <list type="number">
        /// <item>Deadzone (0..40kg): force below this clamps to 0.</item>
        /// <item>Max Force (0..200kg, default 200 = off): the force at
        /// which the <em>input curve's</em> X-axis reaches 100% — lets a
        /// user who never presses past, say, 100kg (out of the device's
        /// real <paramref name="fullScaleKg"/>) use the curve's full
        /// 0-100% range instead of only ever reaching its midpoint. Values
        /// at or above <paramref name="fullScaleKg"/> are a no-op: the raw
        /// axis is already pegged at 100% by the device itself at that
        /// point, so there's no more resolution above it for software to
        /// require.</item>
        /// </list>
        /// Everything between the two rescales linearly. See
        /// docs/protocol/devices/mbooster.md "Pedal Feel".
        /// </summary>
        internal static double ApplyDeadzoneAndMaxForce(double xPercent, double deadzoneKg, double maxForceKg, double fullScaleKg)
        {
            if (fullScaleKg <= 0) fullScaleKg = 200.0;
            double loPercent = Math.Max(0, Math.Min(fullScaleKg, deadzoneKg)) / fullScaleKg * 100.0;
            double hiPercent = Math.Max(0, Math.Min(fullScaleKg, maxForceKg)) / fullScaleKg * 100.0;
            return ClipAndRescale(xPercent, loPercent, hiPercent);
        }

        /// <summary>
        /// Shared clip-and-rescale: positions at or below
        /// <paramref name="loPercent"/> clip to 0, positions at or above
        /// <paramref name="hiPercent"/> clip to 100, everything between
        /// rescales linearly to the full 0-100 range.
        /// </summary>
        private static double ClipAndRescale(double xPercent, double loPercent, double hiPercent)
        {
            xPercent = Math.Max(0, Math.Min(100, xPercent));
            loPercent = Math.Max(0, Math.Min(100, loPercent));
            hiPercent = Math.Max(0, Math.Min(100, hiPercent));

            double effective = Math.Max(0, xPercent - loPercent);
            double range = hiPercent - loPercent;
            if (range <= 0) return effective > 0 ? 100 : 0;

            double result = effective / range * 100.0;
            if (result < 0) return 0;
            if (result > 100) return 100;
            return result;
        }

        /// <summary>
        /// Evaluate a 5-point Pedal Feel curve at a given X (0..100),
        /// reproducing <see cref="MozaControls.MozaCurveEditor"/>'s
        /// Catmull-Rom rendering exactly (same 1/6-tangent formula, anchored
        /// at the origin) so the applied shaping matches what the user sees
        /// drawn. <paramref name="y"/> holds the 5 node Y-values for
        /// X=20,40,60,80,100; X=0 is an implicit (0,0) anchor. The control
        /// points this formula produces always fall between their segment's
        /// endpoints in X, so the segment's X(t) is monotonic — bisection
        /// reliably inverts it to find t for the requested X.
        /// </summary>
        internal static double EvaluateInputCurve(float[] y, double x)
        {
            if (y == null || y.Length != 5) return x;
            x = Math.Max(0, Math.Min(100, x));

            var xs = new double[] { 0, 20, 40, 60, 80, 100, 100 };
            var ys = new double[] { 0, y[0], y[1], y[2], y[3], y[4], y[4] };

            int i = (int)Math.Min(4, Math.Floor(x / 20.0));
            int p0i = i == 0 ? 0 : i - 1;
            int p2i = i + 1;
            int p3i = (i + 2 >= xs.Length) ? i + 1 : i + 2;

            double p0x = xs[p0i], p0y = ys[p0i];
            double p1x = xs[i], p1y = ys[i];
            double p2x = xs[p2i], p2y = ys[p2i];
            double p3x = xs[p3i], p3y = ys[p3i];

            double c1x = p1x + (p2x - p0x) / 6.0, c1y = p1y + (p2y - p0y) / 6.0;
            double c2x = p2x - (p3x - p1x) / 6.0, c2y = p2y - (p3y - p1y) / 6.0;

            double lo = 0, hi = 1;
            for (int iter = 0; iter < 24; iter++)
            {
                double t = (lo + hi) / 2.0;
                double bx = CubicBezier(p1x, c1x, c2x, p2x, t);
                if (bx < x) lo = t; else hi = t;
            }
            double result = CubicBezier(p1y, c1y, c2y, p2y, (lo + hi) / 2.0);
            if (result < 0) result = 0;
            if (result > 100) result = 100;
            return result;
        }

        private static double CubicBezier(double p0, double c1, double c2, double p1, double t)
        {
            double mt = 1 - t;
            return mt * mt * mt * p0 + 3 * mt * mt * t * c1 + 3 * mt * t * t * c2 + t * t * t * p1;
        }

        /// <summary>
        /// Same Catmull-Rom evaluation as <see cref="EvaluateInputCurve"/>,
        /// generalized to arbitrary (draggable) node X positions instead of
        /// the fixed 20/40/60/80/100 — used for the Sim Input Mapping output
        /// curve's horizontal node drag (<c>MBoosterDeviceSettings.CurveX</c>).
        /// Beyond the last node's X, returns that node's Y (flat plateau) —
        /// this is what makes "100% output before 100% input" work: drag the
        /// last node left and everything past it just stays at that Y.
        /// </summary>
        internal static double EvaluateCurveArbitraryX(float[] xs, float[] ys, double x)
        {
            if (xs == null || ys == null || xs.Length != 5 || ys.Length != 5) return x;

            var px = new double[] { 0, xs[0], xs[1], xs[2], xs[3], xs[4], xs[4] };
            var py = new double[] { 0, ys[0], ys[1], ys[2], ys[3], ys[4], ys[4] };

            if (x <= 0) return 0;
            if (x >= px[5]) return py[5];

            int i = 0;
            for (int k = 0; k < 5; k++)
            {
                if (x >= px[k] && x <= px[k + 1]) { i = k; break; }
            }
            int p0i = i == 0 ? 0 : i - 1;
            int p2i = i + 1;
            int p3i = (i + 2 >= px.Length) ? i + 1 : i + 2;

            double p0x = px[p0i], p0y = py[p0i];
            double p1x = px[i], p1y = py[i];
            double p2x = px[p2i], p2y = py[p2i];
            double p3x = px[p3i], p3y = py[p3i];
            if (p2x <= p1x) return p1y; // degenerate (equal X) — shouldn't happen given drag clamping

            double c1x = p1x + (p2x - p0x) / 6.0, c1y = p1y + (p2y - p0y) / 6.0;
            double c2x = p2x - (p3x - p1x) / 6.0, c2y = p2y - (p3y - p1y) / 6.0;

            double lo = 0, hi = 1;
            for (int iter = 0; iter < 24; iter++)
            {
                double t = (lo + hi) / 2.0;
                double bx = CubicBezier(p1x, c1x, c2x, p2x, t);
                if (bx < x) lo = t; else hi = t;
            }
            return CubicBezier(p1y, c1y, c2y, p2y, (lo + hi) / 2.0);
        }

        private static readonly float[] DefaultCurveX = { 20, 40, 60, 80, 100 };

        /// <summary>
        /// Resample a (possibly horizontally-dragged) output curve at the
        /// fixed 20/40/60/80/100 breakpoints the wire protocol actually
        /// supports. When <paramref name="curveX"/> is null (node never
        /// dragged), this is the identity — sampling
        /// <see cref="EvaluateCurveArbitraryX"/> exactly at a node's own X
        /// returns that node's own Y — so callers can always resample
        /// unconditionally without a "has the user customized X" branch.
        /// </summary>
        internal static float[] ResampleCurveAtFixedBreakpoints(float[]? curveX, float[] curveY)
        {
            var xs = (curveX != null && curveX.Length == 5) ? curveX : DefaultCurveX;
            var result = new float[5];
            for (int i = 0; i < 5; i++)
                result[i] = (float)EvaluateCurveArbitraryX(xs, curveY, DefaultCurveX[i]);
            return result;
        }

        /// <summary>
        /// EXPERIMENTAL / unverified — resample the output curve at 6 evenly
        /// spaced breakpoints (100/7, 200/7, ..., 600/7 percent) instead of
        /// the wire protocol's usual 20/40/60/80/100, for the
        /// <c>mbooster-brake-curve7-*</c> commands (cmdId 0xAB — see
        /// MozaCommandDatabase.cs and MozaMBoosterProtocol.EncodeCurve7Point).
        /// Spotted once in pedal_travel.pcapng sent alongside a Travel Start
        /// write; not confirmed as an actual protocol requirement. Same
        /// null-curveY-is-identity fallback as <see cref="ResampleCurveAtFixedBreakpoints"/>
        /// (via <see cref="EvaluateCurveArbitraryX"/>'s own null guard).
        /// Returned array is indexed 0..5 for wire selectors 1..6
        /// (<c>result[i]</c> is selector <c>i + 1</c>'s value).
        /// </summary>
        internal static float[] ResampleCurveAtSevenths(float[]? curveX, float[]? curveY)
        {
            var xs = (curveX != null && curveX.Length == 5) ? curveX : DefaultCurveX;
            var result = new float[6];
            for (int i = 1; i <= 6; i++)
                result[i - 1] = (float)EvaluateCurveArbitraryX(xs, curveY!, i * 100.0 / 7.0);
            return result;
        }

        /// <summary>
        /// Fallback pairing for <see cref="OnHidAxisUpdate"/> when the HID
        /// identity doesn't exactly match a known CDC identity. Per
        /// docs/protocol/devices/mbooster.md "HID identity reconciliation",
        /// the two instance IDs come from different USB interfaces of the
        /// same composite device and share every segment except the trailing
        /// interface-index one (e.g. CDC <c>a&amp;399b951f&amp;0&amp;0000</c> vs
        /// HID <c>a&amp;399b951f&amp;0&amp;0002</c>) — strip that last
        /// "&amp;NNNN" segment from both sides and match on what remains.
        /// Must be called with <see cref="_lock"/> already held.
        /// </summary>
        private MBoosterDeviceController? FindByInstancePrefixLocked(string hidIdentity)
        {
            string prefix = InstancePrefix(hidIdentity);
            if (prefix.Length == 0) return null;
            foreach (var kvp in _byIdentity)
            {
                if (string.Equals(InstancePrefix(kvp.Key), prefix, StringComparison.OrdinalIgnoreCase))
                {
                    LogPrefixPairingOnce(hidIdentity, kvp.Key);
                    return kvp.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Pair a HID axis stream to its CDC lane by Windows Container ID — the
        /// robust path when the two interfaces of one physical device get
        /// unrelated instance IDs (so exact + prefix both fail), which the
        /// real-hardware finding in docs/protocol/devices/mbooster.md shows is
        /// the norm. The Container ID is identical across all interfaces of one
        /// composite device. Must be called with <see cref="_lock"/> held.
        /// Returns null when the HID side reported no Container ID (older
        /// drivers / Wine) so the caller falls through to the other rungs.
        /// </summary>
        private MBoosterDeviceController? FindByContainerIdLocked(string containerId)
        {
            if (string.IsNullOrEmpty(containerId)) return null;
            foreach (var kvp in _byIdentity)
            {
                var c = kvp.Value;
                if (!string.IsNullOrEmpty(c.ContainerId) &&
                    string.Equals(c.ContainerId, containerId, StringComparison.OrdinalIgnoreCase))
                {
                    LogContainerPairingOnce(containerId, c.Identity);
                    return c;
                }
            }
            return null;
        }

        private readonly HashSet<string> _containerPairingsLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void LogContainerPairingOnce(string containerId, string cdcIdentity)
        {
            bool isNew;
            lock (_containerPairingsLogged)
                isNew = _containerPairingsLogged.Add(containerId);
            if (isNew)
            {
                MozaLog.Info(
                    $"[AZOM/mBooster] Paired HID axis to CDC device " +
                    $"'{MBoosterDeviceController.ShortIdentity(cdcIdentity)}' via Container ID " +
                    $"'{containerId}' (exact identity match failed — the expected multi-lane path).");
            }
        }

        private static string InstancePrefix(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            int lastAmp = id.LastIndexOf('&');
            return lastAmp > 0 ? id.Substring(0, lastAmp) : "";
        }

        private readonly HashSet<string> _prefixPairingsLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void LogPrefixPairingOnce(string hidIdentity, string cdcIdentity)
        {
            bool isNew;
            lock (_prefixPairingsLogged)
                isNew = _prefixPairingsLogged.Add(hidIdentity);
            if (isNew)
            {
                MozaLog.Info(
                    $"[AZOM/mBooster] Paired HID axis identity '{hidIdentity}' to CDC device " +
                    $"'{MBoosterDeviceController.ShortIdentity(cdcIdentity)}' via instance-prefix fallback " +
                    "(exact identity match failed — see docs/protocol/devices/mbooster.md).");
            }
        }

        private readonly HashSet<string> _singleDeviceFallbacksLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void LogSingleDeviceFallbackOnceLocked(string hidIdentity, string cdcIdentity)
        {
            bool isNew;
            lock (_singleDeviceFallbacksLogged)
                isNew = _singleDeviceFallbacksLogged.Add(hidIdentity);
            if (isNew)
            {
                MozaLog.Info(
                    $"[AZOM/mBooster] Paired HID axis identity '{hidIdentity}' to CDC device " +
                    $"'{MBoosterDeviceController.ShortIdentity(cdcIdentity)}' via single-device fallback " +
                    "(exact and prefix identity matches both failed, but only one mBooster is registered " +
                    "so there's no ambiguity — see docs/protocol/devices/mbooster.md).");
            }
        }

        /// <summary>
        /// Walk all devices in enumeration order and assign each device's
        /// position to the matching <c>MozaData</c> field if its role is set.
        /// First-wins on collision (later devices with the same role are
        /// ignored). Devices with Role=Disabled contribute nothing.
        /// </summary>
        private readonly HashSet<string> _unmatchedHidLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One-time-per-identity Warn (so it's visible in SimHub's regular
        /// log, not just the support bundle) when a HID axis identity
        /// matches no known CDC device by either exact or prefix match.
        /// Logs full, untruncated identities — needed to actually diagnose
        /// the HID/CDC reconciliation gap rather than guess at it again.
        /// Must be called with <see cref="_lock"/> already held.
        /// </summary>
        private void LogUnmatchedHidIdentityOnceLocked(string hidIdentity)
        {
            bool isNew;
            lock (_unmatchedHidLogged)
                isNew = _unmatchedHidLogged.Add(hidIdentity);
            if (!isNew) return;
            string known = _byIdentity.Count == 0 ? "(none)" : string.Join(", ", _byIdentity.Keys);
            MozaLog.Warn(
                $"[AZOM/mBooster] HID axis identity '{hidIdentity}' matched no known CDC device " +
                $"(known CDC identities: [{known}]) — position will not update for this device. " +
                "See docs/protocol/devices/mbooster.md \"HID identity reconciliation\".");
        }

        private void MergePositions()
        {
            bool throttleSet = false, brakeSet = false, clutchSet = false;
            // First-wins iteration over _order while holding the lock so the
            // controller list can't mutate underneath us. Each lane hosts up to
            // 3 pedal axes; every axis routes independently by its own role.
            lock (_lock)
            {
                for (int i = 0; i < _order.Count; i++)
                {
                    var c = _order[i];
                    var s = _settingsLookup(c.Identity);
                    int axisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                    if (axisCount > MBoosterDeviceController.MaxAxes) axisCount = MBoosterDeviceController.MaxAxes;
                    for (int a = 0; a < axisCount; a++)
                    {
                        var role = ResolveAxisRole(s, a, axisCount);
                        if (role == MBoosterRole.Disabled) continue;
                        // MozaData position fields are int (0..100, the same scale
                        // the existing HID reader writes). Round explicitly.
                        int v100 = (int)Math.Round(c.LastAxisPositions[a] * 100.0);
                        if (v100 < 0) v100 = 0; if (v100 > 100) v100 = 100;
                        switch (role)
                        {
                            case MBoosterRole.Throttle:
                                if (!throttleSet) { _data.ThrottlePosition = v100; throttleSet = true; }
                                else { LogCollisionOnce("throttle", c.Identity); }
                                break;
                            case MBoosterRole.Brake:
                                if (!brakeSet) { _data.BrakePosition = v100; brakeSet = true; }
                                else { LogCollisionOnce("brake", c.Identity); }
                                break;
                            case MBoosterRole.Clutch:
                                if (!clutchSet) { _data.ClutchPosition = v100; clutchSet = true; }
                                else { LogCollisionOnce("clutch", c.Identity); }
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Role for one axis of a lane. An explicit per-axis override
        /// (<see cref="MBoosterDeviceSettings.AxisRoles"/>, set by the UI when
        /// the user remaps) always wins. Otherwise: a single-axis device uses
        /// the legacy <see cref="MBoosterDeviceSettings.Role"/> (exact backward
        /// compat); a multi-pedal chain defaults by axis order to
        /// [Throttle, Brake, Clutch]. That order is the standard Moza pedal
        /// usage convention — real hardware (support bundle 2026-07-07) exposes
        /// the chain's pedals as GenericDesktop axes Rx(0x33)/Ry(0x34)/Rz(0x35),
        /// which ascending-sorted give index 0/1/2, and Moza maps Rx→throttle,
        /// Ry→brake, Rz→clutch (see MozaHidClass.Pedals). The user remaps via
        /// the UI if a given unit's wiring differs.
        /// </summary>
        internal static MBoosterRole ResolveAxisRole(MBoosterDeviceSettings? s, int axisIndex, int axisCount)
        {
            var roles = s?.AxisRoles;
            if (roles != null && axisIndex >= 0 && axisIndex < roles.Length)
                return roles[axisIndex];
            if (axisCount <= 1)
                return s?.Role ?? MBoosterRole.Disabled;
            switch (axisIndex)
            {
                case 0:  return MBoosterRole.Throttle;  // Rx (0x33)
                case 1:  return MBoosterRole.Brake;     // Ry (0x34)
                case 2:  return MBoosterRole.Clutch;    // Rz (0x35)
                default: return MBoosterRole.Disabled;
            }
        }

        private void LogCollisionOnce(string role, string identity)
        {
            string key = role + ":" + identity;
            bool isNew;
            lock (_collisionsLogged)
                isNew = _collisionsLogged.Add(key);
            if (isNew)
            {
                MozaLog.Warn(
                    $"[AZOM/mBooster] Role collision: {MBoosterDeviceController.ShortIdentity(identity)} " +
                    $"is configured as '{role}' but another mBooster already claimed that role — its position will be ignored.");
            }
        }

        /// <summary>Lookup a controller by identity (e.g. for UI selection).</summary>
        public MBoosterDeviceController? FindByIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return null;
            lock (_lock)
            {
                _byIdentity.TryGetValue(identity, out var c);
                return c;
            }
        }

        public void Dispose()
        {
            List<MBoosterDeviceController> all;
            lock (_lock)
            {
                all = _order.ToList();
                _order.Clear();
                _byIdentity.Clear();
            }
            foreach (var c in all)
            {
                try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Dispose {c.Identity}: {ex.Message}"); }
            }
        }
    }
}
