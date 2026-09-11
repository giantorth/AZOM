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
                // key in the current profile.
                //
                // A brand-new transport-keyed placeholder gets created (below)
                // the instant the device is first detected, BEFORE its serial
                // has been read back — this is normal and happens every single
                // session. If the user starts editing (dragging a curve node,
                // say) in the brief window before OnMBoosterSerialResolved
                // fires and migrates it, those edits land on THIS placeholder.
                // The old version of this migration always kept whichever
                // object was ALREADY under the serial key and silently deleted
                // the transport-keyed one — meaning a live edit made in that
                // window was discarded outright, with no warning, the moment
                // the serial resolved (bug: a real drag-tested curve edit
                // vanished, reverting to whatever stale data pre-dated it, even
                // though the whole session shut down cleanly afterwards).
                //
                // Fix: an untouched placeholder (see IsUntouchedMBoosterPlaceholder)
                // still loses to whatever's already at the serial key, same as
                // before. Otherwise the two entries are MERGED FIELD BY FIELD,
                // keeping a real value over an untouched sentinel on either side,
                // so nothing is lost in either direction. The transport-keyed
                // entry only wins fields it actually holds a value for.
                //
                // It used to win WHOLESALE (dict[key] = stale), on the reasoning
                // that it "can only have gotten that data via a live edit moments
                // ago (it started as an empty placeholder THIS session)". That
                // premise is false: transport-keyed entries persist to disk like
                // any other key, so one comes BACK next session already
                // non-placeholder and then overwrote the serial-keyed entry with
                // its own defaults. Bug reports QR3760VJ / A6N521CS bracket
                // exactly that: the transport-keyed entry held ONE real value
                // (pedal 1's MaxForceKg), and on the strength of it took the
                // whole record — wiping the serial-keyed entry's Direction, its
                // output curve and its entire second-pedal row, 27 s before the
                // settings tab had even seeded, so no live edit existed. One
                // field should never carry fourteen others with it. There is no
                // timestamp behind "more recently touched" and there never was;
                // per-field merging removes the need for one.
                if (!string.Equals(original, key, StringComparison.OrdinalIgnoreCase)
                    && dict.TryGetValue(original, out var stale))
                {
                    bool staleUntouched = IsUntouchedMBoosterPlaceholder(stale);
                    bool keyHasEntry = dict.TryGetValue(key, out var existing);
                    if (!keyHasEntry)
                    {
                        dict[key] = stale;
                    }
                    else if (!staleUntouched)
                    {
                        // `stale` survives as the object (unchanged from before, so
                        // a UI control or worker already holding it keeps editing
                        // the live entry), backfilled with everything the
                        // serial-keyed entry had and it doesn't.
                        var conflicts = new List<string>();
                        MergeMBoosterSettings(from: existing, into: stale, conflicts: conflicts);
                        if (conflicts.Count > 0)
                            MozaLog.Warn($"[AZOM/mBooster] GetOrCreateMBoosterSettings: the transport-keyed entry ('{original}') and the serial-keyed entry ('{key}') both hold a value for {string.Join(", ", conflicts)} in profile '{profile.Name}' — merged, keeping the transport-keyed value for those field(s); every other value from both sides was preserved.");
                        dict[key] = stale;
                    }
                    dict.Remove(original);
                }

                if (!dict.TryGetValue(key, out var s) || s == null)
                {
                    // Diagnostic trail for the "curve values wrong until profile
                    // reload" class of bug — this is the moment a caller gets
                    // handed a brand-new, all-defaults placeholder instead of
                    // the real saved entry, e.g. because `key` is still the raw
                    // transport identity (serial not resolved/re-keyed yet) at
                    // the moment the settings UI first seeds from it.
                    MozaLog.Info($"[AZOM/mBooster] GetOrCreateMBoosterSettings: NEW placeholder for key='{key}' (original='{original}', resolvedSerial={!string.Equals(original, key, StringComparison.OrdinalIgnoreCase)}) in profile '{profile.Name}'");
                    s = new MBoosterDeviceSettings();
                    dict[key] = s;
                }
                return s;
            }
        }

        /// <summary>
        /// True if every field GetOrCreateMBoosterSettings's re-key migration
        /// cares about is still at its untouched sentinel/default — i.e. this
        /// looks exactly like the placeholder GetOrCreateMBoosterSettings
        /// itself creates for a just-detected device, not something a user
        /// (or an import/migration) has actually written real values into.
        /// Used to decide which of two colliding entries (transport-keyed vs
        /// serial-keyed) is safe to discard during migration — see the caller.
        /// Deliberately does NOT check the effect settings (Abs/Lockup/etc.)
        /// or CustomEffects: those aren't part of the bug this guards against,
        /// and their own field-level defaults are less clear-cut, so skipping
        /// them only makes this check slightly less strict, never wrong in a
        /// way that would newly discard real data it didn't already discard.
        /// </summary>
        private static bool IsUntouchedMBoosterPlaceholder(MBoosterDeviceSettings s)
        {
            return s.Role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Disabled
                && s.AxisRoles == null
                && string.IsNullOrEmpty(s.DisplayName)
                && IsUntouchedPedalConfig(s)
                && PedalRowsAllUntouched(s);
        }

        /// <summary>
        /// True if every per-pedal row is itself untouched (or there are none).
        ///
        /// <para>This used to be a bare <c>Pedals.Count == 0</c> test, which made
        /// the mere EXISTENCE of a row count as real data — and rows get created
        /// without any user edit (MozaMBoosterRegistry.GetOrCreatePedalConfig
        /// creates on demand, and any SaveSettings then persists it), so an entry
        /// holding nothing but one all-default row counted as "touched" and beat
        /// a serial-keyed entry full of real values. Independent of the
        /// wholesale-overwrite problem the caller describes, and no longer
        /// load-bearing now the merge is per-field — but the test was simply
        /// wrong, and it is the difference between merging and not merging at
        /// all.</para>
        /// </summary>
        private static bool PedalRowsAllUntouched(MBoosterDeviceSettings s)
        {
            if (s.Pedals == null || s.Pedals.Count == 0) return true;
            foreach (var kv in s.Pedals)
                if (kv.Value != null && !IsUntouchedPedalConfig(kv.Value)) return false;
            return true;
        }

        /// <summary>
        /// True if every calibration / Sim Input / Pedal Feel field on one pedal's
        /// config is still at its untouched sentinel. Shared by
        /// <see cref="IsUntouchedMBoosterPlaceholder"/> and the per-row check, so
        /// a device's flat fields and a chained pedal's row are judged by the same
        /// rule — both implement <see cref="IMBoosterPedalConfig"/>.
        ///
        /// <para><c>NaturalFrictionEnabled</c> is deliberately excluded: it
        /// defaults to <c>true</c>, so "off" and "never set" are not
        /// distinguishable and it cannot participate in a sentinel test.</para>
        /// </summary>
        private static bool IsUntouchedPedalConfig(global::MozaPlugin.Devices.MBooster.IMBoosterPedalConfig c)
        {
            return c.Direction < 0 && c.Min < 0 && c.Max < 0
                && c.CurveY == null && c.CurveX == null
                && c.SensorOutputRatioPct < 0 && c.MaxThresholdKg < 0
                && c.InputCurveY == null && c.InputCurveX == null
                && c.DeadzoneKg < 0 && c.MaxForceKg < 0
                && c.TravelStartMm < 0 && c.TravelEndMm < 0
                && c.EndstopFrontStiffness < 0 && c.EndstopEndStiffness < 0
                && c.NaturalFrictionPct < 0
                && c.DampingPressPct < 0 && c.DampingReleasePct < 0
                && c.SegmentedDamping.Divider1Pressed < 0 && c.SegmentedDamping.Divider2Pressed < 0
                && c.SegmentedDamping.Seg1Pressed < 0 && c.SegmentedDamping.Seg2Pressed < 0 && c.SegmentedDamping.Seg3Pressed < 0
                && c.SegmentedDamping.Divider1Released < 0 && c.SegmentedDamping.Divider2Released < 0
                && c.SegmentedDamping.Seg1Released < 0 && c.SegmentedDamping.Seg2Released < 0 && c.SegmentedDamping.Seg3Released < 0;
        }

        /// <summary>
        /// Backfill <paramref name="into"/> with every value <paramref name="from"/>
        /// holds and it does not, for the re-key migration. Device-level fields,
        /// the flat pedal config, and each per-pedal row. Field names where BOTH
        /// sides hold a real (differing) value are appended to
        /// <paramref name="conflicts"/> — <paramref name="into"/> keeps its own
        /// value for those.
        ///
        /// <para>Effect settings and CustomEffects are not merged, matching
        /// <see cref="IsUntouchedPedalConfig"/>'s deliberate omission of them: their
        /// field-level defaults are per-effect and not sentinel-shaped, so
        /// "unset" isn't detectable. An entry whose ONLY content is effects is
        /// therefore still treated as untouched and loses the migration, exactly
        /// as it did before this change.</para>
        /// </summary>
        private static void MergeMBoosterSettings(
            MBoosterDeviceSettings from, MBoosterDeviceSettings into, List<string> conflicts)
        {
            if (from == null || into == null) return;

            if (into.Role == global::MozaPlugin.Devices.MBooster.MBoosterRole.Disabled)
                into.Role = from.Role;
            else if (from.Role != global::MozaPlugin.Devices.MBooster.MBoosterRole.Disabled
                     && from.Role != into.Role)
                conflicts.Add("Role");

            if (into.AxisRoles == null) into.AxisRoles = from.AxisRoles;
            else if (from.AxisRoles != null) conflicts.Add("AxisRoles");

            if (string.IsNullOrEmpty(into.DisplayName)) into.DisplayName = from.DisplayName;
            else if (!string.IsNullOrEmpty(from.DisplayName)
                     && !string.Equals(from.DisplayName, into.DisplayName, StringComparison.Ordinal))
                conflicts.Add("DisplayName");

            MergeMBoosterPedalConfig(from, into, conflicts, "");

            if (from.Pedals != null && from.Pedals.Count > 0)
            {
                // Copy-on-write, same as GetOrCreatePedalConfig: the 50 Hz effect
                // workers read Pedals without a lock, so publish a new dictionary
                // by atomic reference swap rather than mutating in place.
                var merged = into.Pedals != null
                    ? new Dictionary<int, global::MozaPlugin.Devices.MBooster.MBoosterPedalSettings>(into.Pedals)
                    : new Dictionary<int, global::MozaPlugin.Devices.MBooster.MBoosterPedalSettings>();
                foreach (var kv in from.Pedals)
                {
                    if (kv.Value == null) continue;
                    if (!merged.TryGetValue(kv.Key, out var mine) || mine == null)
                        merged[kv.Key] = kv.Value;
                    else
                        MergeMBoosterPedalConfig(kv.Value, mine, conflicts, $"pedal {kv.Key} ");
                }
                into.Pedals = merged;
            }
        }

        /// <summary>Field-level half of <see cref="MergeMBoosterSettings"/> for one
        /// pedal's worth of config. <paramref name="label"/> prefixes any conflict
        /// names so a per-pedal clash is distinguishable from a device-level one.</summary>
        private static void MergeMBoosterPedalConfig(
            global::MozaPlugin.Devices.MBooster.IMBoosterPedalConfig from,
            global::MozaPlugin.Devices.MBooster.IMBoosterPedalConfig into,
            List<string> conflicts, string label)
        {
            void Int(string name, Func<int> get, Action<int> set, int other)
            {
                if (get() < 0) { if (other >= 0) set(other); }
                else if (other >= 0 && other != get()) conflicts.Add(label + name);
            }
            void Flt(string name, Func<float> get, Action<float> set, float other)
            {
                if (get() < 0) { if (other >= 0) set(other); }
                else if (other >= 0 && Math.Abs(other - get()) > 0.0001f) conflicts.Add(label + name);
            }
            void Arr(string name, Func<float[]?> get, Action<float[]?> set, float[]? other)
            {
                if (get() == null) { if (other != null) set(other); }
                else if (other != null) conflicts.Add(label + name);
            }

            Int("Direction", () => into.Direction, v => into.Direction = v, from.Direction);
            Int("Min", () => into.Min, v => into.Min = v, from.Min);
            Int("Max", () => into.Max, v => into.Max = v, from.Max);
            Arr("CurveY", () => into.CurveY, v => into.CurveY = v, from.CurveY);
            Arr("CurveX", () => into.CurveX, v => into.CurveX = v, from.CurveX);
            Flt("SensorOutputRatioPct", () => into.SensorOutputRatioPct, v => into.SensorOutputRatioPct = v, from.SensorOutputRatioPct);
            Flt("MaxThresholdKg", () => into.MaxThresholdKg, v => into.MaxThresholdKg = v, from.MaxThresholdKg);
            Arr("InputCurveY", () => into.InputCurveY, v => into.InputCurveY = v, from.InputCurveY);
            Arr("InputCurveX", () => into.InputCurveX, v => into.InputCurveX = v, from.InputCurveX);
            Flt("DeadzoneKg", () => into.DeadzoneKg, v => into.DeadzoneKg = v, from.DeadzoneKg);
            Flt("MaxForceKg", () => into.MaxForceKg, v => into.MaxForceKg = v, from.MaxForceKg);
            Flt("TravelStartMm", () => into.TravelStartMm, v => into.TravelStartMm = v, from.TravelStartMm);
            Flt("TravelEndMm", () => into.TravelEndMm, v => into.TravelEndMm = v, from.TravelEndMm);
            Flt("EndstopFrontStiffness", () => into.EndstopFrontStiffness, v => into.EndstopFrontStiffness = v, from.EndstopFrontStiffness);
            Flt("EndstopEndStiffness", () => into.EndstopEndStiffness, v => into.EndstopEndStiffness = v, from.EndstopEndStiffness);
            Flt("NaturalFrictionPct", () => into.NaturalFrictionPct, v => into.NaturalFrictionPct = v, from.NaturalFrictionPct);
            Flt("DampingPressPct", () => into.DampingPressPct, v => into.DampingPressPct = v, from.DampingPressPct);
            Flt("DampingReleasePct", () => into.DampingReleasePct, v => into.DampingReleasePct = v, from.DampingReleasePct);

            // MBoosterSegmentedDampingSettings is a sealed class, so these are
            // references — mutating sd edits into.SegmentedDamping in place.
            var sd = into.SegmentedDamping;
            var fd = from.SegmentedDamping;
            Flt("SegmentedDamping.Divider1Pressed", () => sd.Divider1Pressed, v => sd.Divider1Pressed = v, fd.Divider1Pressed);
            Flt("SegmentedDamping.Divider2Pressed", () => sd.Divider2Pressed, v => sd.Divider2Pressed = v, fd.Divider2Pressed);
            Flt("SegmentedDamping.Seg1Pressed", () => sd.Seg1Pressed, v => sd.Seg1Pressed = v, fd.Seg1Pressed);
            Flt("SegmentedDamping.Seg2Pressed", () => sd.Seg2Pressed, v => sd.Seg2Pressed = v, fd.Seg2Pressed);
            Flt("SegmentedDamping.Seg3Pressed", () => sd.Seg3Pressed, v => sd.Seg3Pressed = v, fd.Seg3Pressed);
            Flt("SegmentedDamping.Divider1Released", () => sd.Divider1Released, v => sd.Divider1Released = v, fd.Divider1Released);
            Flt("SegmentedDamping.Divider2Released", () => sd.Divider2Released, v => sd.Divider2Released = v, fd.Divider2Released);
            Flt("SegmentedDamping.Seg1Released", () => sd.Seg1Released, v => sd.Seg1Released = v, fd.Seg1Released);
            Flt("SegmentedDamping.Seg2Released", () => sd.Seg2Released, v => sd.Seg2Released = v, fd.Seg2Released);
            Flt("SegmentedDamping.Seg3Released", () => sd.Seg3Released, v => sd.Seg3Released = v, fd.Seg3Released);
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
            // Diagnostic trail alongside GetOrCreateMBoosterSettings's own
            // placeholder-creation log — if this fires well AFTER the settings
            // UI has already seeded from a transport-keyed placeholder for the
            // same identity, that's the race: the UI showed defaults/stale data
            // before this re-key ever ran, and nothing told it to reseed.
            MozaLog.Info($"[AZOM/mBooster] OnMBoosterSerialResolved: identity={MBoosterDeviceController.ShortIdentity(identity)} serial={serial}");
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
        // pedals (prevents a re-probe loop; a hookup change mid-session
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
        /// mBooster on the RJ45 pedal port rather than plain pedals. Spin
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
                    RememberRoutedMBoosterSlot(c.Identity, true);
                    _mboosterRegistry?.AddRoutedLane(c);
                }
                else
                {
                    // Plain pedals (CRP/SRP, or another non-mBooster pedal device) —
                    // retire the probe. Dispose skips the motor disable frames
                    // when the model never identified as an mBooster.
                    MozaLog.Debug($"[AZOM/mBooster] pedal sub-device ({c.PortName}) is '{model}', not an mBooster — routed probe retired");
                    // Drop any stale marker so pedals-* writes un-suppress after a
                    // hookup swap (mBooster replaced by CRP2 on the same port), then
                    // re-apply: the marker may have suppressed this profile's pedal
                    // calibration during Init, and nothing else would retry it.
                    if (RememberRoutedMBoosterSlot(c.Identity, false))
                    {
                        try { _hardwareApplier?.ApplyPedalsToHardware(_settings?.ProfileStore?.CurrentProfile); }
                        catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Pedals re-apply after probe retire: {ex.Message}"); }
                    }
                    try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Probe dispose: {ex.Message}"); }
                }
            }
            catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] routed model resolution: {ex.Message}"); }
        }

        /// <summary>Persist (or drop) "this pedal slot holds an mBooster" for the
        /// transport identity. Copy-on-write: the reader is the hardware write path
        /// and this runs on the connection read thread, so a fresh list is built and
        /// reference-swapped rather than mutated under a lock.</summary>
        private bool RememberRoutedMBoosterSlot(string identity, bool isMBooster)
        {
            if (string.IsNullOrEmpty(identity)) return false;
            var settings = _settings;
            if (settings == null) return false;
            try
            {
                var current = settings.RoutedMBoosterPedalSlots;
                bool present = current != null
                    && current.Any(s => string.Equals(s, identity, StringComparison.OrdinalIgnoreCase));
                if (present == isMBooster) return false;
                var next = current == null ? new List<string>() : new List<string>(current);
                if (isMBooster) next.Add(identity);
                else next.RemoveAll(s => string.Equals(s, identity, StringComparison.OrdinalIgnoreCase));
                settings.RoutedMBoosterPedalSlots = next;
                MozaLog.Info($"[AZOM/mBooster] pedal slot {MBoosterDeviceController.ShortIdentity(identity)} " +
                             $"remembered as {(isMBooster ? "mBooster" : "not an mBooster")} — " +
                             $"pedals-* writes {(isMBooster ? "suppressed" : "allowed")} from next Init");
                SaveSettings();
                return true;
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] remember routed slot: {ex.Message}"); }
            return false;
        }

        /// <summary>Does the pedal slot (dev 0x19) hold an mBooster per the persisted
        /// marker? This is the only answer available during Init, before the model
        /// probe can round-trip — the window in which a persistent-wire reload's
        /// ApplyProfile would otherwise push CRP calibration onto an mBooster.
        /// With no owner recorded the pipe is still unknown, so ANY remembered slot
        /// counts: over-suppressing for a poll tick is cheap, a CRP calibration
        /// sweep against a motorized pedal is not.</summary>
        internal bool IsRoutedMBoosterPedalSlotRemembered()
        {
            var slots = _settings?.RoutedMBoosterPedalSlots;
            if (slots == null || slots.Count == 0) return false;
            var owner = DetectionState.PedalsOwner;
            string port = owner?.Connection?.LastPortName ?? "";
            if (string.IsNullOrEmpty(port)) return true;
            string identity = "routedpedals:" + port;
            return slots.Any(s => string.Equals(s, identity, StringComparison.OrdinalIgnoreCase));
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

        // Old 5-node output curve's fixed X breakpoints — what CurveX
        // defaulted to (and what InputCurveY was always implicitly fixed
        // at) before the redesign to 6 nodes. Used only by the one-shot
        // migration below.
        private static readonly float[] LegacyMBoosterCurveDefaultX = { 20, 40, 60, 80, 100 };

        /// <summary>
        /// One-shot migration (see
        /// <see cref="MozaPluginSettings.MBoosterCurveArraysMigratedTo6"/>):
        /// resamples every saved mBooster CurveY/CurveX (Sim Input Mapping)
        /// and InputCurveY (Pedal Feel) array from its old 5-node shape to
        /// the current 6-node one, across every profile's master settings
        /// and every chained pedal — preserving each curve's visual shape
        /// instead of letting the ordinary "wrong length = unset" guards
        /// elsewhere silently discard it to a default. CurveX itself does
        /// not carry over (the old dragged X positions don't map cleanly
        /// onto the new node count) — only the resulting Y-shape does; a
        /// fresh CurveX default takes over on the next edit.
        /// </summary>
        private void MigrateMBoosterCurveArraysTo6()
        {
            var profiles = _settings?.ProfileStore?.Profiles;
            if (profiles == null) return;
            foreach (var profile in profiles)
            {
                if (profile?.MBoosterSettings == null) continue;
                foreach (var device in profile.MBoosterSettings.Values)
                {
                    if (device == null) continue;
                    MigrateOneMBoosterCurveSet(device);
                    if (device.Pedals != null)
                        foreach (var pedal in device.Pedals.Values)
                            if (pedal != null) MigrateOneMBoosterCurveSet(pedal);
                }
            }
        }

        private static void MigrateOneMBoosterCurveSet(global::MozaPlugin.Devices.MBooster.IMBoosterPedalConfig cfg)
        {
            const int oldNodeCount = 5;
            if (cfg.CurveY != null && cfg.CurveY.Length == oldNodeCount)
            {
                var oldXs = (cfg.CurveX != null && cfg.CurveX.Length == oldNodeCount)
                    ? cfg.CurveX : LegacyMBoosterCurveDefaultX;
                var newY = new float[global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SimInputMappingNodeCount];
                for (int i = 0; i < newY.Length; i++)
                {
                    double x = (i + 1) * 100.0 / 6.0;
                    newY[i] = (float)global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.EvaluateCurveArbitraryX(oldXs, cfg.CurveY, x);
                }
                cfg.CurveY = newY;
                cfg.CurveX = null;
            }
            if (cfg.InputCurveY != null && cfg.InputCurveY.Length == oldNodeCount)
            {
                var newInput = new float[global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.PedalFeelNodeCount];
                for (int i = 0; i < newInput.Length; i++)
                {
                    double x = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.FeelCurveFractions[i] * 100.0;
                    newInput[i] = (float)global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.EvaluateCurveArbitraryX(LegacyMBoosterCurveDefaultX, cfg.InputCurveY, x);
                }
                cfg.InputCurveY = newInput;
            }
        }

        // The Sim Input Mapping curve's default X breakpoints used to be
        // 100/7 * k (last node ~85.7%, not 100% — see DefaultCurveX's
        // history in Devices/MBooster/MozaMBoosterRegistry.cs). Any profile
        // that hit MBoosterCurveArraysMigratedTo6, or simply clicked a preset
        // button, under that bug got a CurveY baked to one of these too-low
        // shapes. Matched against UI.SettingsControl's MBoosterCurvePresets
        // (old → new) so the follow-up migration below can restore the exact
        // preset shape a user actually clicked, not just the default.
        private static readonly float[][] OldMBoosterCurvePresetsSeventhsBug =
        {
            new float[] { 14, 29, 43, 57, 71, 86 }, // Linear
            new float[] { 5, 12, 30, 70, 88, 95 },  // S Curve
            new float[] { 4, 9, 16, 25, 41, 66 },   // Exponential
            new float[] { 34, 59, 75, 84, 91, 96 }, // Parabolic
        };
        private static readonly float[][] NewMBoosterCurvePresetsSeventhsBug =
        {
            new float[] { 17, 33, 50, 67, 83, 100 }, // Linear
            new float[] { 6, 16, 50, 84, 94, 100 },  // S Curve
            new float[] { 5, 11, 20, 35, 61, 100 },  // Exponential
            new float[] { 39, 65, 80, 89, 95, 100 }, // Parabolic
        };

        /// <summary>
        /// One-shot follow-up migration (see
        /// <see cref="MozaPluginSettings.MBoosterCurveArraysFixedSeventhsBug"/>):
        /// a saved Sim Input Mapping curve that exactly matches one of the
        /// old, too-low preset shapes (baked in by the 100/7 breakpoint bug,
        /// either directly via a preset button or via
        /// <see cref="MigrateMBoosterCurveArraysTo6"/> before this fix) is
        /// swapped for the corresponding corrected shape. A curve the user
        /// has since custom-dragged away from any preset is left alone —
        /// the original 5-node source is long gone, so there's nothing
        /// reliable to re-derive it from; a fresh Linear/S-Curve/etc. click
        /// or a small manual touch-up fixes it going forward.
        /// </summary>
        private void FixMBoosterCurveArraysSeventhsBug()
        {
            var profiles = _settings?.ProfileStore?.Profiles;
            if (profiles == null) return;
            foreach (var profile in profiles)
            {
                if (profile?.MBoosterSettings == null) continue;
                foreach (var device in profile.MBoosterSettings.Values)
                {
                    if (device == null) continue;
                    FixOneMBoosterCurveSeventhsBug(device);
                    if (device.Pedals != null)
                        foreach (var pedal in device.Pedals.Values)
                            if (pedal != null) FixOneMBoosterCurveSeventhsBug(pedal);
                }
            }
        }

        private static void FixOneMBoosterCurveSeventhsBug(global::MozaPlugin.Devices.MBooster.IMBoosterPedalConfig cfg)
        {
            if (cfg.CurveX != null) return; // user has dragged X — not a stock preset shape
            if (cfg.CurveY == null || cfg.CurveY.Length != global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SimInputMappingNodeCount) return;
            for (int p = 0; p < OldMBoosterCurvePresetsSeventhsBug.Length; p++)
            {
                var old = OldMBoosterCurvePresetsSeventhsBug[p];
                bool match = true;
                for (int i = 0; i < old.Length; i++)
                    if (Math.Abs(cfg.CurveY[i] - old[i]) > 0.01f) { match = false; break; }
                if (match)
                {
                    cfg.CurveY = (float[])NewMBoosterCurvePresetsSeventhsBug[p].Clone();
                    return;
                }
            }
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
