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

        /// <summary>
        /// Look up (or lazily create) the per-device mBooster settings entry
        /// in the current profile. Called by the registry and the effect
        /// worker on every tick — must be allocation-free for known devices.
        /// </summary>
        // Transport-identity → "mbooster:<serial>" once a lane's serial is
        // interrogated. Populated on OnMBoosterSerialResolved; read lock-free in
        // GetOrCreateMBoosterSettings. Deliberately NOT resolved via the
        // registry there — MergePositions calls in while holding the registry
        // lock, so consulting the registry under _mboosterSettingsLock would
        // invert the lock order.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _mboosterSerialByIdentity =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _mboosterSettingsLock = new object();

        internal MBoosterDeviceSettings GetOrCreateMBoosterSettings(string identity)
        {
            // Resolve a transport identity to the device's stable serial key so
            // per-device settings follow the physical unit across USB ports.
            string key = identity ?? "";
            string original = key;
            if (!string.IsNullOrEmpty(key) && _mboosterSerialByIdentity.TryGetValue(key, out var serialKey))
                key = serialKey;

            lock (_mboosterSettingsLock)
            {
                var profile = _settings?.ProfileStore?.CurrentProfile;
                if (profile == null) return new MBoosterDeviceSettings();
                if (profile.MBoosterSettings == null)
                    profile.MBoosterSettings = new Dictionary<string, MBoosterDeviceSettings>(StringComparer.OrdinalIgnoreCase);
                var dict = profile.MBoosterSettings;

                // Lazily migrate a transient transport-keyed entry to the serial
                // key in the current profile. A serial-keyed entry, if one
                // already exists (the user's saved config from a prior session),
                // wins — the transport entry is a just-created placeholder.
                if (!string.Equals(original, key, StringComparison.OrdinalIgnoreCase)
                    && dict.TryGetValue(original, out var stale))
                {
                    if (!dict.ContainsKey(key)) dict[key] = stale;
                    dict.Remove(original);
                }

                if (!dict.TryGetValue(key, out var s) || s == null)
                {
                    s = new MBoosterDeviceSettings();
                    dict[key] = s;
                }
                return s;
            }
        }

        /// <summary>
        /// A lane's 32-char Moza serial has been interrogated. Record the
        /// identity→serial mapping (so settings lookups re-key to it), migrate
        /// the current profile's entry, and re-apply the now serial-keyed
        /// settings to the device — at detect we applied the transient
        /// transport-keyed entry, but the real config may live under the serial
        /// key from a prior session. Runs on the connection read thread.
        /// </summary>
        private void OnMBoosterSerialResolved(string identity, string serial)
        {
            if (IsShuttingDown || string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(serial)) return;
            _mboosterSerialByIdentity[identity] = "mbooster:" + serial;
            try
            {
                var settings = GetOrCreateMBoosterSettings(identity); // resolves + migrates current profile
                var controller = _mboosterRegistry?.FindByIdentity(identity);
                if (controller != null)
                {
                    // Replug on a NEW port: the transport-identity connectivity
                    // seed missed at controller creation, but the serial-keyed
                    // cache entry can seed now — still well ahead of the
                    // device's own once-a-minute broadcast. No-op if live
                    // connectivity already arrived.
                    controller.SeedConnectedAxes(LookupMBoosterKnownPedals(identity));
                    ApplyMBoosterToHardware(controller, settings);
                }
            }
            catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] serial re-key for {MBoosterDeviceController.ShortIdentity(identity)}: {ex.Message}"); }
        }

        /// <summary>Persisted last-known pedal connectivity for a lane —
        /// checked under the serial key when the identity has been re-keyed,
        /// falling back to the transport identity (the cache is written under
        /// both). Null when never seen.</summary>
        private bool[]? LookupMBoosterKnownPedals(string identity)
        {
            var cache = _settings?.MBoosterKnownPedals;
            if (cache == null || string.IsNullOrEmpty(identity)) return null;
            string key = _mboosterSerialByIdentity.TryGetValue(identity, out var serialKey) ? serialKey : identity;
            lock (_mboosterSettingsLock)
            {
                if (cache.TryGetValue(key, out var v) && v != null) return v;
                return cache.TryGetValue(identity, out v) ? v : null;
            }
        }

        /// <summary>
        /// Live connectivity parsed from the device's own diagnostic. Persist
        /// it (under both the serial key and the transport identity, so the
        /// next controller can be seeded before the serial is re-interrogated)
        /// and heal provably-stale role assignments: a role held by an axis
        /// the device says has NO pedal, duplicating a role held by a wired
        /// axis, can only be a leftover from before connectivity was known —
        /// it first-wins the real pedal out of the merge on any build without
        /// the phantom-axis guard, and blanks it during the unseeded window
        /// otherwise. Healed across ALL profiles: the proof is physical
        /// (device-reported wiring), not a per-profile preference. Runs on the
        /// connection read thread, at most once per distinct diagnostic line
        /// per session.
        /// </summary>
        private void OnMBoosterConnectivityResolved(string identity, bool[] connected)
        {
            if (IsShuttingDown || string.IsNullOrEmpty(identity) || connected == null || connected.Length == 0) return;
            try
            {
                bool changed = false;
                string? serialKey = _mboosterSerialByIdentity.TryGetValue(identity, out var sk) ? sk : null;
                lock (_mboosterSettingsLock)
                {
                    var cache = _settings?.MBoosterKnownPedals;
                    if (cache != null)
                    {
                        changed |= StoreKnownPedals(cache, identity, connected);
                        if (serialKey != null) changed |= StoreKnownPedals(cache, serialKey, connected);
                    }

                    var profiles = _settings?.ProfileStore?.Profiles;
                    if (profiles != null)
                    {
                        foreach (var profile in profiles)
                        {
                            var dict = profile?.MBoosterSettings;
                            if (dict == null) continue;
                            foreach (var key in new[] { serialKey, identity })
                            {
                                if (key == null || !dict.TryGetValue(key, out var s) || s == null) continue;
                                changed |= HealMBoosterAxisRoles(
                                    s, connected, profile!.Name ?? "?", MBoosterDeviceController.ShortIdentity(identity));
                            }
                        }
                    }
                }
                if (changed) SaveSettings();
            }
            catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] connectivity persist/heal for {MBoosterDeviceController.ShortIdentity(identity)}: {ex.Message}"); }
        }

        private static bool StoreKnownPedals(Dictionary<string, bool[]> cache, string key, bool[] connected)
        {
            if (cache.TryGetValue(key, out var old) && old != null && old.SequenceEqual(connected)) return false;
            cache[key] = (bool[])connected.Clone();
            return true;
        }

        // One routed-mBooster probe/lane per owning pipe (base and hub each
        // count separately). An entry persists for the session once created —
        // as a registered lane when the pedal device identified as an
        // mBooster, or as a retired negative when it turned out to be plain
        // SGP pedals (prevents a re-probe loop; a hookup change mid-session
        // needs a plugin restart to be picked up).
        private readonly object _routedMBoosterLock = new object();
        private readonly Dictionary<MozaDeviceManager, MBoosterDeviceController> _routedMBoosterProbes =
            new Dictionary<MozaDeviceManager, MBoosterDeviceController>();
        private readonly Dictionary<MozaDeviceManager, int> _routedMBoosterProbeAttempts =
            new Dictionary<MozaDeviceManager, int>();
        // 5 s reconnect-timer cadence × 24 = give a silent pedal device two
        // minutes of identity re-bursts before writing it off for the session.
        private const int RoutedMBoosterProbeMaxAttempts = 24;

        /// <summary>
        /// A pedal sub-device was detected on a base/hub pipe — it may be an
        /// mBooster on the RJ45 pedal port rather than plain SGP pedals. Spin
        /// up a ROUTED controller against the pipe's shared connection (dev
        /// 0x19) and interrogate its identity; registration with the registry
        /// happens only when the model-name read confirms an mBooster (both
        /// device families answer the same identity groups at 0x19, so the
        /// model string is the discriminator). Reads-only until then.
        /// </summary>
        internal void ProbeRoutedMBooster(MozaDeviceManager owner)
        {
            if (IsShuttingDown || owner == null || _mboosterRegistry == null) return;
            lock (_routedMBoosterLock)
            {
                if (_routedMBoosterProbes.ContainsKey(owner)) return;
                string port = owner.Connection?.LastPortName ?? "";
                string identity = "routedpedals:" + (string.IsNullOrEmpty(port) ? "pipe" : port);
                var c = new MBoosterDeviceController(
                    identity,
                    owner.Connection!,
                    MozaProtocol.DevicePedals,
                    portLabel: string.IsNullOrEmpty(port) ? "via base" : $"via {port}",
                    settingsLookup: () => GetOrCreateMBoosterSettings(identity),
                    isShuttingDown: () => IsShuttingDown,
                    customEffectFormulaEvaluator: CreateHapticsFormulaResolver());
                c.ModelNameResolved += name => OnRoutedMBoosterModelResolved(c, name);
                _routedMBoosterProbes[owner] = c;
                _routedMBoosterProbeAttempts[owner] = 1;
                c.SendIdentityReads();
            }
        }

        /// <summary>Re-burst identity reads for probes that never got a model
        /// answer (frame lost / pipe busy at detect time). Runs from the 5 s
        /// reconnect timer; capped so silent non-mBooster pedals don't get
        /// probed forever.</summary>
        private void NudgeRoutedMBoosterProbes()
        {
            if (IsShuttingDown) return;
            List<MBoosterDeviceController>? pending = null;
            lock (_routedMBoosterLock)
            {
                foreach (var kv in _routedMBoosterProbes)
                {
                    var c = kv.Value;
                    if (c == null || !string.IsNullOrEmpty(c.ModelName) || !c.IsConnected) continue;
                    if (!_routedMBoosterProbeAttempts.TryGetValue(kv.Key, out int n)) n = 0;
                    if (n >= RoutedMBoosterProbeMaxAttempts) continue;
                    _routedMBoosterProbeAttempts[kv.Key] = n + 1;
                    (pending ??= new List<MBoosterDeviceController>()).Add(c);
                }
            }
            if (pending == null) return;
            foreach (var c in pending)
            {
                try { c.SendIdentityReads(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Routed identity re-burst: {ex.Message}"); }
            }
        }

        /// <summary>Teardown for routed probes/lanes — registered lanes are
        /// disposed by the registry too, but Dispose latches so the double
        /// call is harmless; unresolved probes are only reachable from here.
        /// Routed Dispose never touches the shared base/hub pipe itself.</summary>
        private void DisposeRoutedMBoosterProbes()
        {
            List<MBoosterDeviceController> all;
            lock (_routedMBoosterLock)
            {
                all = new List<MBoosterDeviceController>(_routedMBoosterProbes.Values);
                _routedMBoosterProbes.Clear();
                _routedMBoosterProbeAttempts.Clear();
            }
            foreach (var c in all)
            {
                try { c?.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Routed probe dispose: {ex.Message}"); }
            }
        }

        private void OnRoutedMBoosterModelResolved(MBoosterDeviceController c, string model)
        {
            if (IsShuttingDown || c == null) return;
            try
            {
                if (!string.IsNullOrEmpty(model) && model.IndexOf("mBooster", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    MozaLog.Info($"[AZOM/mBooster] mBooster identified on the pedal port ({c.PortName}) — registering routed lane (dev 0x{c.HostDeviceId:x2})");
                    _mboosterRegistry?.AddRoutedLane(c);
                }
                else
                {
                    // Plain SGP pedals (or another non-mBooster pedal device) —
                    // retire the probe. Dispose skips the motor disable frames
                    // when the model never identified as an mBooster.
                    MozaLog.Debug($"[AZOM/mBooster] pedal sub-device ({c.PortName}) is '{model}', not an mBooster — routed probe retired");
                    try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Probe dispose: {ex.Message}"); }
                }
            }
            catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] routed model resolution: {ex.Message}"); }
        }

        /// <summary>One heal pass over a single profile's device entry — the
        /// conclusive-only rule from <see cref="OnMBoosterConnectivityResolved"/>.</summary>
        private static bool HealMBoosterAxisRoles(MBoosterDeviceSettings s, bool[] connected, string profileName, string shortId)
        {
            var roles = s.AxisRoles;
            if (roles == null) return false;
            bool changed = false;
            for (int a = 0; a < roles.Length; a++)
            {
                bool aConnected = a < connected.Length && connected[a];
                if (aConnected || roles[a] == global::MozaPlugin.Devices.MBoosterRole.Disabled) continue;
                for (int b = 0; b < roles.Length; b++)
                {
                    if (b == a || roles[b] != roles[a]) continue;
                    if (b < connected.Length && connected[b])
                    {
                        MozaLog.Info(
                            $"[AZOM/mBooster] {shortId}: cleared stale '{roles[a]}' role from axis {a} " +
                            $"in profile '{profileName}' — the device reports no pedal wired there and " +
                            $"the wired pedal on axis {b} holds that role");
                        roles[a] = global::MozaPlugin.Devices.MBoosterRole.Disabled;
                        changed = true;
                        break;
                    }
                }
            }
            return changed;
        }

        /// <summary>
        /// Called once per detection rising edge by the registry. Pushes any
        /// saved calibration values to the device and kicks off a read-back
        /// for unset calibration fields. The doc warns this surface may not
        /// be honored by mBooster firmware — we attempt it anyway since the
        /// user opted in.
        /// </summary>
        private void OnMBoosterDeviceDetected(MBoosterDeviceController controller)
        {
            if (IsShuttingDown || controller == null) return;
            try
            {
                MozaLog.Info($"[AZOM/mBooster] Applying settings for {MBoosterDeviceController.ShortIdentity(controller.Identity)} (experimental calibration surface)");
                var s = GetOrCreateMBoosterSettings(controller.Identity);
                ApplyMBoosterToHardware(controller, s);
                // Always issue a calibration read burst on detect so the panel
                // can populate (or so we learn the device ignored them).
                controller.RequestCalibrationReads();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM/mBooster] OnDetected for {controller.Identity}: {ex.Message}");
            }
        }

        /// <summary>
        /// Push calibration values (direction / min / max / curve) for one
        /// mBooster to its device. Sentinel-guarded — values left at -1 (or
        /// null array) are skipped, so a fresh profile with no overrides
        /// produces zero hardware writes. Per protocol note § 6 these
        /// commands are "likely but unverified" on mBooster firmware.
        /// </summary>
        internal void ApplyMBoosterToHardware(MBoosterDeviceController controller, MBoosterDeviceSettings s)
        {
            if (controller == null || s == null || !controller.IsConnected) return;

            // Route Direction / Min / Max / output-curve to the command slot
            // matching the pedal's ROLE. This used to be hardcoded to the
            // "throttle" slot, which is wrong for a mBooster used as a brake or
            // clutch (and for a chain whose master pedal isn't the throttle) —
            // the calibration silently landed on the wrong pedal's command.
            // The role is the master pedal's (axis 0): ResolveAxisRole gives the
            // legacy Role for a single unit or the chain default/override
            // otherwise. Per-pedal calibration for the OTHER chained pedals is a
            // follow-up (needs a per-pedal settings UI); this fixes the routing
            // for the pedal the current single calibration set configures.
            int axisCount = controller.AxisCount > 0 ? controller.AxisCount : 1;
            // Apply EACH hosted pedal's calibration to its role-specific command.
            // Pedal 0 (master) keeps its calibration in the flat fields (the
            // existing UI); the additional chained pedals (axes 1+) store theirs
            // in s.Pedals[axis]. An unconfigured pedal (all -1 / null) writes
            // nothing. Once connectivity is known, phantom axes (no pedal
            // wired) are skipped, and a lane's sole connected pedal falls back
            // to the flat fields when it has no per-pedal entry — see
            // MBoosterDeviceController.SoleConnectedAxis.
            var connectedAxes = controller.ConnectedAxes;
            int soleAxis = controller.SoleConnectedAxis();
            for (int axis = 0; axis < axisCount && axis < global::MozaPlugin.Devices.MBoosterDeviceController.MaxAxes; axis++)
            {
                if (connectedAxes != null && (axis >= connectedAxes.Length || !connectedAxes[axis]))
                    continue;
                var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(s, axis, axisCount);
                string? prefix =
                    role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? "throttle"
                    : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? "brake"
                    : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? "clutch"
                    : null;
                if (prefix == null) continue;

                // This pedal's full config: master flat fields (axis 0) or its
                // per-pedal entry. An unconfigured chained pedal writes nothing.
                global::MozaPlugin.Devices.IMBoosterPedalConfig cfg;
                if (axis == 0) cfg = s;
                else if (s.Pedals != null && s.Pedals.TryGetValue(axis, out var p) && p != null) cfg = p;
                else if (axis == soleAxis) cfg = s;
                else continue;

                bool wroteAnyCalibration = false;

                // Every per-pedal calibration here is a PHYSICAL setting stored
                // on that pedal's own mBooster unit (confirmed on hardware: each
                // unit reports only its own pedal's calibration, under its own
                // role register). Address it by the pedal's ROLE through the
                // calibration-derived chain map (same as the effects — see
                // MBoosterEffectWorker.TargetDevice), NOT the raw HID axis: the
                // motor/config device id follows the chain plug position, which
                // doesn't match the HID axis order, so an axis-index device
                // sends these writes to the wrong physical pedal. Falls back to
                // the axis mapping (0x12 for a standalone) until the map resolves.
                int roleIdx = role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? 0
                            : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? 1
                            : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? 2 : -1;
                byte dev = controller.MotorDeviceForRole(roleIdx, axis);

                if (cfg.Direction >= 0) { controller.SendIntWrite($"mbooster-{prefix}-dir", cfg.Direction, dev); wroteAnyCalibration = true; }
                if (cfg.Min >= 0) { controller.SendIntWrite($"mbooster-{prefix}-min", cfg.Min, dev); wroteAnyCalibration = true; }
                if (cfg.Max >= 0) { controller.SendIntWrite($"mbooster-{prefix}-max", cfg.Max, dev); wroteAnyCalibration = true; }
                if (cfg.CurveY != null && cfg.CurveY.Length == 5)
                {
                    wroteAnyCalibration = true;
                    // Resample at the fixed 20/40/60/80/100 breakpoints in case
                    // CurveX has been horizontally dragged (see
                    // MozaMBoosterRegistry.ResampleCurveAtFixedBreakpoints) —
                    // identity when it hasn't.
                    var resampled = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResampleCurveAtFixedBreakpoints(cfg.CurveX, cfg.CurveY);
                    for (int k = 0; k < 5; k++)
                        controller.SendFloatWrite($"mbooster-{prefix}-y{k + 1}", resampled[k], dev);
                }
                // Travel / End Stop / Natural Friction / Segmented Damping are
                // load-cell + motor Pedal Feel features living on brake-named
                // SINGLETON cmdIds (0x84/0x85, 0xB2, 0xAE, 0xB7) with no
                // per-pedal selector, so they can only ever configure the pedal
                // that owns that hardware. Pushing them from a PASSIVE pedal's
                // stored config doesn't configure that pedal — it overwrites the
                // active pedal's registers (bundle KY3HK4QP: the passive
                // throttle's 3.8/35.9mm is what the brake unit committed as
                // Params 48/49). The UI hides these controls for a passive pedal;
                // this stops values saved before that gate existed from still
                // being replayed on every connect.
                bool ownsPedalFeelHardware = controller.IsAxisMotorized(axis);
                if (ownsPedalFeelHardware && cfg.TravelStartMm >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-travel-start",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(cfg.TravelStartMm), dev);
                    wroteAnyCalibration = true;
                }
                if (ownsPedalFeelHardware && cfg.TravelEndMm >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-travel-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(cfg.TravelEndMm), dev);
                    wroteAnyCalibration = true;
                }
                if (ownsPedalFeelHardware && cfg.EndstopFrontStiffness >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-endstop-front",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(cfg.EndstopFrontStiffness), dev);
                    wroteAnyCalibration = true;
                }
                if (ownsPedalFeelHardware && cfg.EndstopEndStiffness >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-endstop-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(cfg.EndstopEndStiffness), dev);
                    wroteAnyCalibration = true;
                }
                if (ownsPedalFeelHardware && cfg.NaturalFrictionPct >= 0)
                {
                    int frictionRaw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(cfg.NaturalFrictionPct);
                    controller.SendIntWrite("mbooster-brake-friction-0", frictionRaw, dev);
                    controller.SendIntWrite("mbooster-brake-friction-1", frictionRaw, dev);
                    wroteAnyCalibration = true;
                }
                // Segmented Damping (both "When Pressed" and "When
                // Released" — see cfg.SegmentedDamping). One wire command
                // carries the whole feature's state at once, so a fresh
                // profile with no override on EITHER side still sends
                // nothing here (guarded like every other calibration write
                // above); once ANY field on either side is set, the frame
                // is filled out using factory defaults for whichever side
                // still has no override.
                var sd = ownsPedalFeelHardware ? cfg.SegmentedDamping : null;
                if (sd != null && (sd.Divider1Pressed >= 0 || sd.Divider2Pressed >= 0
                    || sd.Seg1Pressed >= 0 || sd.Seg2Pressed >= 0 || sd.Seg3Pressed >= 0
                    || sd.Divider1Released >= 0 || sd.Divider2Released >= 0
                    || sd.Seg1Released >= 0 || sd.Seg2Released >= 0 || sd.Seg3Released >= 0))
                {
                    var c = global::MozaPlugin.Devices.MBoosterUiConstants.SegDampSegDefaultPct;
                    var frame = global::MozaPlugin.Protocol.MozaMBoosterProtocol.BuildSegmentedDampingFrame(
                        sd.Divider1Pressed >= 0 ? sd.Divider1Pressed : global::MozaPlugin.Devices.MBoosterUiConstants.SegDampDivider1PressedDefaultPct,
                        sd.Divider2Pressed >= 0 ? sd.Divider2Pressed : global::MozaPlugin.Devices.MBoosterUiConstants.SegDampDivider2PressedDefaultPct,
                        sd.Divider1Released >= 0 ? sd.Divider1Released : global::MozaPlugin.Devices.MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct,
                        sd.Divider2Released >= 0 ? sd.Divider2Released : global::MozaPlugin.Devices.MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct,
                        sd.Seg1Pressed >= 0 ? sd.Seg1Pressed : c,
                        sd.Seg1Released >= 0 ? sd.Seg1Released : c,
                        sd.Seg2Pressed >= 0 ? sd.Seg2Pressed : c,
                        sd.Seg2Released >= 0 ? sd.Seg2Released : c,
                        sd.Seg3Pressed >= 0 ? sd.Seg3Pressed : c,
                        sd.Seg3Released >= 0 ? sd.Seg3Released : c,
                        dev);
                    controller.SendOneShot(frame);
                    wroteAnyCalibration = true;
                }
                if (role == global::MozaPlugin.Devices.MBoosterRole.Brake)
                {
                    if (cfg.SensorOutputRatioPct >= 0)
                    {
                        controller.SendFloatWrite("mbooster-brake-angle-ratio", cfg.SensorOutputRatioPct, dev);
                        wroteAnyCalibration = true;
                    }
                    if (cfg.MaxThresholdKg >= 0)
                    {
                        controller.SendIntWrite("mbooster-brake-threshold",
                            global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeThresholdKg(cfg.MaxThresholdKg), dev);
                        wroteAnyCalibration = true;
                    }
                }

                // EXPERIMENTAL / unverified — confirmed on hardware to be
                // required for a Travel edit to actually take effect; applied
                // here too on the theory the same firmware requirement covers
                // every write above, not just Travel. See
                // MBoosterDeviceController.PushCurve7Resync. Guarded like the
                // writes above (not unconditional) to preserve this method's
                // "fresh profile with no overrides produces zero hardware
                // writes" guarantee.
                if (wroteAnyCalibration)
                    controller.PushCurve7Resync(cfg.CurveX, cfg.CurveY, dev);
            }
        }

        // Resolve a dashboard name to its parsed MultiStreamProfile without firing
        // Resolves a profile by name (cache → builtin) without touching the
        // current telemetry profile — used by SwitchToProfile to avoid racing
        // ApplyTelemetrySettings's full-stack reload.
        internal MultiStreamProfile? ResolveDashboardProfileByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (DashCache != null)
            {
                var p = DashCache.TryGetByName(name);
                if (p != null) return p;
            }
            var builtins = DashProfileStore.BuiltinProfiles;
            foreach (var p in builtins)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }
    }
}
