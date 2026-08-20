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
                    _hardwareApplier.ApplyMBoosterToHardware(controller, settings);
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
                if (aConnected || roles[a] == global::MozaPlugin.Devices.MBooster.MBoosterRole.Disabled) continue;
                for (int b = 0; b < roles.Length; b++)
                {
                    if (b == a || roles[b] != roles[a]) continue;
                    if (b < connected.Length && connected[b])
                    {
                        MozaLog.Info(
                            $"[AZOM/mBooster] {shortId}: cleared stale '{roles[a]}' role from axis {a} " +
                            $"in profile '{profileName}' — the device reports no pedal wired there and " +
                            $"the wired pedal on axis {b} holds that role");
                        roles[a] = global::MozaPlugin.Devices.MBooster.MBoosterRole.Disabled;
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
                _hardwareApplier.ApplyMBoosterToHardware(controller, s);
                // Always issue a calibration read burst on detect so the panel
                // can populate (or so we learn the device ignored them).
                controller.RequestCalibrationReads();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM/mBooster] OnDetected for {controller.Identity}: {ex.Message}");
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
