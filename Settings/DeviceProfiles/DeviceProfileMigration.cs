using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MozaPlugin.Devices;
using MozaPlugin.Devices.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// Splits the device-page settings off the main <see cref="MozaProfile"/> store
    /// into one profile store per device page (<see cref="DeviceProfileRegistry"/>).
    ///
    /// <para>Every device store gets a copy of each legacy profile with the SAME
    /// ProfileId / Name / GameCode / CarChoice, and the main store's own game-link
    /// state (LastGameProfiles, activeProfileId, switching mode) verbatim — so the
    /// game that picked "X" on the main tab picks "X" on every device page.</para>
    ///
    /// <para>The legacy fields stay serialized on <see cref="MozaProfile"/> (a
    /// downgraded build still reads them) but nothing at runtime reads them again.
    /// A trimmed snapshot of them (<see cref="MozaPluginSettings.LegacyDeviceProfileSnapshot"/>)
    /// seeds any device first seen after the split, so it gets the profiles it would
    /// have had before.</para>
    /// </summary>
    internal static class DeviceProfileMigration
    {
        // Legacy MozaProfile fields that belong to a device page.
        private static readonly HashSet<string> DeviceFieldNames = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(MozaProfile.WheelTelemetryMode), nameof(MozaProfile.WheelKnobLedMode),
            nameof(MozaProfile.WheelButtonsLedMode), nameof(MozaProfile.WheelRpmBrightness),
            nameof(MozaProfile.WheelButtonsBrightness), nameof(MozaProfile.WheelFlagsBrightness),
            nameof(MozaProfile.WheelRpmIndicatorMode), nameof(MozaProfile.WheelRpmDisplayMode),
            nameof(MozaProfile.WheelESRpmBrightness),
            nameof(MozaProfile.WheelRpmColors), nameof(MozaProfile.WheelRpmBlinkColors),
            nameof(MozaProfile.WheelButtonColors), nameof(MozaProfile.WheelButtonDefaultDuringTelemetry),
            nameof(MozaProfile.WheelFlagColors), nameof(MozaProfile.WheelIdleColor),
            nameof(MozaProfile.WheelESRpmColors), nameof(MozaProfile.WheelKnobPrimaryColors),
            nameof(MozaProfile.WheelKnobRingColors), nameof(MozaProfile.WheelKnobRingBrightness),
            nameof(MozaProfile.WheelKnobDefaultDuringTelemetry), nameof(MozaProfile.WheelKnobStaticTimeoutMs),

            nameof(MozaProfile.DashRpmBrightness), nameof(MozaProfile.DashFlagsBrightness),
            nameof(MozaProfile.DashDisplayBrightness), nameof(MozaProfile.DashDisplayStandbyMin),
            nameof(MozaProfile.DashDisplayRotation), nameof(MozaProfile.DashRpmIndicatorMode),
            nameof(MozaProfile.DashRpmDisplayMode), nameof(MozaProfile.DashFlagsIndicatorMode),
            nameof(MozaProfile.DashRpmColors), nameof(MozaProfile.DashRpmBlinkColors),
            nameof(MozaProfile.DashFlagColors),

            nameof(MozaProfile.TelemetryDashboardKey), nameof(MozaProfile.WheelOverridesByPageGuid),
            nameof(MozaProfile.TelemetryChannelMappings), nameof(MozaProfile.Fsr1DashboardMappings),
            nameof(MozaProfile.Cm1FieldMappings),

            nameof(MozaProfile.BaseAmbientBrightness), nameof(MozaProfile.BaseAmbientStandbyMode),
            nameof(MozaProfile.BaseAmbientIndicatorState), nameof(MozaProfile.BaseAmbientSleepMode),
            nameof(MozaProfile.BaseAmbientSleepTimeout), nameof(MozaProfile.BaseAmbientStartupColor),
            nameof(MozaProfile.BaseAmbientShutdownColor), nameof(MozaProfile.BaseAmbientIdleColorsConstant),
            nameof(MozaProfile.BaseAmbientIdleColorsBreath), nameof(MozaProfile.BaseAmbientSleepColors),
            nameof(MozaProfile.BaseAmbientStandbyIntervals), nameof(MozaProfile.BaseAmbientSleepBreathInterval),
        };

        // MozaProfile's own properties. Any other key on a serialized profile is
        // SimHub's ProfileBase state (ProfileId, Name, GameCode, CarChoice, ...) and
        // is carried to the device profile verbatim.
        private static readonly HashSet<string> MozaProfileOwnNames = new HashSet<string>(
            typeof(MozaProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(p => p.Name),
            StringComparer.Ordinal);

        private static JsonSerializer Serializer => JsonSerializer.CreateDefault();

        /// <summary>
        /// One-shot split. Runs before the profile system initialises. Creates a store
        /// for every device the user's data or SimHub's device roster mentions; anything
        /// else is seeded from the snapshot when first seen.
        /// </summary>
        internal static void Run(MozaPluginSettings settings)
        {
            if (settings == null || settings.DeviceProfilesSplit) return;
            settings.DeviceProfilesSplit = true;

            var main = settings.ProfileStore;
            if (main == null || main.Profiles.Count == 0)
            {
                MozaLog.Info("[AZOM] Device profile split: no legacy profiles — nothing to migrate");
                return;
            }

            var snapshot = BuildSnapshot(main);
            settings.LegacyDeviceProfileSnapshot = snapshot;

            var legacy = ReadLegacyProfiles(snapshot);
            var registry = settings.DeviceProfiles ??= new DeviceProfileRegistry();

            var wheels = new Dictionary<Guid, MozaWheelProfileStore>(registry.Wheels ?? new Dictionary<Guid, MozaWheelProfileStore>());
            var bases = new Dictionary<Guid, MozaBaseDeviceProfileStore>(registry.Bases ?? new Dictionary<Guid, MozaBaseDeviceProfileStore>());
            var dashes = new Dictionary<Guid, MozaDashProfileStore>(registry.Dashes ?? new Dictionary<Guid, MozaDashProfileStore>());

            var found = Discover(settings, legacy);
            foreach (var g in found.Wheels)
                if (!wheels.ContainsKey(g)) wheels[g] = BuildWheelStore(snapshot, g);
            foreach (var g in found.Bases)
                if (!bases.ContainsKey(g)) bases[g] = BuildBaseStore(snapshot, g);
            foreach (var g in found.Dashes)
                if (!dashes.ContainsKey(g)) dashes[g] = BuildDashStore(snapshot, g);

            registry.Wheels = wheels;
            registry.Bases = bases;
            registry.Dashes = dashes;

            MozaLog.Info($"[AZOM] Device profile split: {main.Profiles.Count} profile(s) cloned into "
                         + $"{wheels.Count} wheel, {bases.Count} wheelbase, {dashes.Count} dash store(s)");
        }

        /// <summary>
        /// One-shot for installs split by a build that copied the wheel's dashboard key
        /// into the CM2 dash profiles: clear those keys so each CM2 profile takes the
        /// CM2's own pick instead. Nothing ever wrote a CM2 pick to a profile before
        /// this, so no user choice is lost.
        /// </summary>
        internal static void ResetCm2DashboardKeys(MozaPluginSettings settings)
        {
            if (settings == null || settings.Cm2DashboardKeysReset) return;
            settings.Cm2DashboardKeysReset = true;
            var dashes = settings.DeviceProfiles?.Dashes;
            if (dashes == null
                || !dashes.TryGetValue(global::MozaPlugin.MozaPlugin.Cm2PageGuid, out var store)
                || store == null) return;
            int cleared = 0;
            foreach (var p in store.Profiles)
            {
                if (p == null || string.IsNullOrEmpty(p.TelemetryDashboardKey)) continue;
                p.TelemetryDashboardKey = null;
                cleared++;
            }
            if (cleared > 0)
                MozaLog.Info($"[AZOM] Cleared {cleared} CM2 profile dashboard key(s) copied from the wheel");
        }

        /// <summary>
        /// Move each CM2 profile's legacy single mapping bucket
        /// (<see cref="global::MozaPlugin.MozaPlugin.Cm2LegacyDashKey"/>) under the dashboard
        /// that profile resolves to — its own pick, else the plugin-wide
        /// <see cref="MozaPluginSettings.Cm2SelectedDashboard"/>, the same fallback
        /// <c>MozaPlugin.Cm2MappingDashKey</c> reads with. Pickless profiles keep the bucket
        /// until a pick exists. Entries already under the dashboard key win. Idempotent;
        /// copy-on-write (the tick threads walk these dicts). Returns true when anything moved.
        /// </summary>
        internal static bool MigrateCm2MappingKeys(MozaPluginSettings settings)
        {
            var dashes = settings?.DeviceProfiles?.Dashes;
            if (dashes == null
                || !dashes.TryGetValue(global::MozaPlugin.MozaPlugin.Cm2PageGuid, out var store)
                || store == null) return false;
            string legacyKey = global::MozaPlugin.MozaPlugin.Cm2LegacyDashKey;
            string fallback = settings!.Cm2SelectedDashboard ?? "";
            int moved = 0;
            foreach (var p in store.Profiles)
            {
                var middle = p?.ChannelMappings;
                if (middle == null || !middle.ContainsKey(legacyKey)) continue;
                string? target = Telemetry.Dashboard.WheelDashboardKey.IsWheelKey(p!.TelemetryDashboardKey)
                    ? p.TelemetryDashboardKey
                    : fallback.Length > 0 ? Telemetry.Dashboard.WheelDashboardKey.Prefix + fallback : null;
                if (target == null) continue;
                p.ChannelMappings = Telemetry.ChannelMappingCoordinator.MoveBucket(middle, legacyKey, target);
                moved++;
            }
            if (moved > 0)
                MozaLog.Info($"[AZOM] Moved {moved} CM2 profile(s)' shared channel mappings under their selected dashboard");
            return moved > 0;
        }

        // ===== Store construction (also used for late devices) =====

        internal static MozaWheelProfileStore BuildWheelStore(JObject? snapshot, Guid pageGuid) =>
            BuildStore<MozaWheelProfileStore, MozaWheelProfile>(snapshot, l => ToWheel(l, pageGuid));

        internal static MozaBaseDeviceProfileStore BuildBaseStore(JObject? snapshot, Guid pageGuid) =>
            BuildStore<MozaBaseDeviceProfileStore, MozaBaseDeviceProfile>(snapshot, ToBase);

        internal static MozaDashProfileStore BuildDashStore(JObject? snapshot, Guid pageGuid) =>
            BuildStore<MozaDashProfileStore, MozaDashProfile>(snapshot, l => ToDash(l, pageGuid));

        private static TStore BuildStore<TStore, TProfile>(JObject? snapshot, Func<MozaProfile, TProfile> convert)
            where TStore : class, new()
        {
            if (snapshot?["Profiles"] is JArray profiles && profiles.Count > 0)
            {
                try
                {
                    var ser = Serializer;
                    var storeJ = new JObject();
                    foreach (var prop in snapshot.Properties())
                        if (prop.Name != "Profiles") storeJ[prop.Name] = prop.Value.DeepClone();

                    var arr = new JArray();
                    foreach (var token in profiles)
                    {
                        if (!(token is JObject pj)) continue;
                        var legacy = pj.ToObject<MozaProfile>(ser);
                        if (legacy == null) continue;
                        var dj = JObject.FromObject(convert(legacy)!, ser);
                        // Same identity + game binding as the legacy profile.
                        foreach (var prop in pj.Properties())
                            if (!MozaProfileOwnNames.Contains(prop.Name))
                                dj[prop.Name] = prop.Value.DeepClone();
                        arr.Add(dj);
                    }
                    storeJ["Profiles"] = arr;
                    var store = storeJ.ToObject<TStore>(ser);
                    if (store != null) return store;
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Device profile clone failed for {typeof(TStore).Name} — starting from Default: {ex.Message}");
                }
            }
            return new TStore();
        }

        // ===== Legacy → device field mapping =====

        private static int Eff(int overlay, int baseline) => overlay >= 0 ? overlay : baseline;

        /// <summary>A wheel's effective values, resolved exactly as the old
        /// overlay-over-baseline apply did (HardwareApplier.Eff / EffArr).</summary>
        internal static MozaWheelProfile ToWheel(MozaProfile l, Guid pageGuid)
        {
            WheelOverride? ov = null;
            l.WheelOverridesByPageGuid?.TryGetValue(pageGuid, out ov);

            var p = new MozaWheelProfile
            {
                WheelTelemetryMode     = Eff(ov?.WheelTelemetryMode ?? -1, l.WheelTelemetryMode),
                WheelKnobLedMode       = Eff(ov?.WheelKnobLedMode ?? -1, l.WheelKnobLedMode),
                WheelButtonsLedMode    = Eff(ov?.WheelButtonsLedMode ?? -1, l.WheelButtonsLedMode),
                WheelRpmBrightness     = Eff(ov?.WheelRpmBrightness ?? -1, l.WheelRpmBrightness),
                WheelButtonsBrightness = Eff(ov?.WheelButtonsBrightness ?? -1, l.WheelButtonsBrightness),
                WheelFlagsBrightness   = Eff(ov?.WheelFlagsBrightness ?? -1, l.WheelFlagsBrightness),
                WheelESRpmBrightness   = Eff(ov?.WheelESRpmBrightness ?? -1, l.WheelESRpmBrightness),
                WheelRpmIndicatorMode  = Eff(ov?.WheelRpmIndicatorMode ?? -1, l.WheelRpmIndicatorMode),
                WheelRpmDisplayMode    = Eff(ov?.WheelRpmDisplayMode ?? -1, l.WheelRpmDisplayMode),
                WheelKnobRingBrightness = Eff(ov?.WheelKnobRingBrightness ?? -1, l.WheelKnobRingBrightness),

                // Inputs were overlay-only.
                WheelPaddlesMode = ov?.WheelPaddlesMode ?? -1,
                WheelClutchPoint = ov?.WheelClutchPoint ?? -1,
                WheelKnobMode    = ov?.WheelKnobMode ?? -1,
                WheelStickMode   = ov?.WheelStickMode ?? -1,
                WheelKnobSignalModes = DeviceProfileCopy.Arr(ov?.WheelKnobSignalModes),

                WheelRpmColors         = DeviceProfileCopy.Arr(ov?.WheelRpmColors ?? l.WheelRpmColors),
                WheelRpmBlinkColors    = DeviceProfileCopy.Arr(ov?.WheelRpmBlinkColors ?? l.WheelRpmBlinkColors),
                WheelButtonColors      = DeviceProfileCopy.Arr(ov?.WheelButtonColors ?? l.WheelButtonColors),
                WheelFlagColors        = DeviceProfileCopy.Arr(ov?.WheelFlagColors ?? l.WheelFlagColors),
                WheelIdleColor         = DeviceProfileCopy.Arr(ov?.WheelIdleColor ?? l.WheelIdleColor),
                WheelESRpmColors       = DeviceProfileCopy.Arr(ov?.WheelESRpmColors ?? l.WheelESRpmColors),
                WheelKnobPrimaryColors = DeviceProfileCopy.Arr(ov?.WheelKnobPrimaryColors ?? l.WheelKnobPrimaryColors),
                WheelKnobRingColors    = DeviceProfileCopy.Arr(ov?.WheelKnobRingColors ?? l.WheelKnobRingColors),
                WheelKnobDefaultDuringTelemetry = ov?.WheelKnobDefaultDuringTelemetry ?? l.WheelKnobDefaultDuringTelemetry,
                WheelKnobStaticTimeoutMs = Eff(ov?.WheelKnobStaticTimeoutMs ?? -1, l.WheelKnobStaticTimeoutMs),

                DashDisplayBrightness = l.DashDisplayBrightness,
                DashDisplayStandbyMin = l.DashDisplayStandbyMin,
                DashDisplayRotation   = l.DashDisplayRotation,

                TelemetryProfileName  = ov?.TelemetryProfileName,
                TelemetryMzdashPath   = ov?.TelemetryMzdashPath,
                TelemetryDashboardKey = l.TelemetryDashboardKey,
            };
            var btnDefaults = ov?.WheelButtonDefaultDuringTelemetry ?? l.WheelButtonDefaultDuringTelemetry;
            p.WheelButtonDefaultDuringTelemetry = btnDefaults != null ? (bool[])btnDefaults.Clone() : null;

            if (l.TelemetryChannelMappings != null
                && l.TelemetryChannelMappings.TryGetValue(pageGuid, out var maps))
                p.ChannelMappings = DeviceProfileCopy.Mappings(maps);
            if (l.Fsr1DashboardMappings != null
                && l.Fsr1DashboardMappings.TryGetValue(pageGuid, out var fsr1))
                p.Fsr1Mappings = DeviceProfileCopy.Fsr1(fsr1);
            return p;
        }

        internal static MozaDashProfile ToDash(MozaProfile l, Guid pageGuid)
        {
            var p = new MozaDashProfile
            {
                DashRpmBrightness      = l.DashRpmBrightness,
                DashFlagsBrightness    = l.DashFlagsBrightness,
                DashDisplayBrightness  = l.DashDisplayBrightness,
                DashDisplayStandbyMin  = l.DashDisplayStandbyMin,
                DashRpmIndicatorMode   = l.DashRpmIndicatorMode,
                DashRpmDisplayMode     = l.DashRpmDisplayMode,
                DashFlagsIndicatorMode = l.DashFlagsIndicatorMode,
                DashRpmColors          = DeviceProfileCopy.Arr(l.DashRpmColors),
                DashRpmBlinkColors     = DeviceProfileCopy.Arr(l.DashRpmBlinkColors),
                DashFlagColors         = DeviceProfileCopy.Arr(l.DashFlagColors),
            };
            // TelemetryDashboardKey is deliberately NOT copied: the legacy key was
            // captured from the wheel's selection, never the CM2's. A CM2 profile with
            // no key takes the CM2's current pick (Cm2SelectedDashboard) on first apply.
            if (pageGuid == global::MozaPlugin.MozaPlugin.Cm2PageGuid)
            {
                if (l.TelemetryChannelMappings != null
                    && l.TelemetryChannelMappings.TryGetValue(pageGuid, out var maps))
                    p.ChannelMappings = DeviceProfileCopy.Mappings(maps);
            }
            if (l.Cm1FieldMappings != null && l.Cm1FieldMappings.TryGetValue(pageGuid, out var cm1))
                p.Cm1Mappings = DeviceProfileCopy.FieldMap(cm1);
            return p;
        }

        internal static MozaBaseDeviceProfile ToBase(MozaProfile l) => new MozaBaseDeviceProfile
        {
            BaseAmbientBrightness          = l.BaseAmbientBrightness,
            BaseAmbientStandbyMode         = l.BaseAmbientStandbyMode,
            BaseAmbientIndicatorState      = l.BaseAmbientIndicatorState,
            BaseAmbientSleepMode           = l.BaseAmbientSleepMode,
            BaseAmbientSleepTimeout        = l.BaseAmbientSleepTimeout,
            BaseAmbientStartupColor        = l.BaseAmbientStartupColor,
            BaseAmbientShutdownColor       = l.BaseAmbientShutdownColor,
            BaseAmbientIdleColorsConstant  = DeviceProfileCopy.Arr(l.BaseAmbientIdleColorsConstant),
            BaseAmbientIdleColorsBreath    = DeviceProfileCopy.Arr(l.BaseAmbientIdleColorsBreath),
            BaseAmbientSleepColors         = DeviceProfileCopy.Arr(l.BaseAmbientSleepColors),
            BaseAmbientStandbyIntervals    = DeviceProfileCopy.Arr(l.BaseAmbientStandbyIntervals),
            BaseAmbientSleepBreathInterval = l.BaseAmbientSleepBreathInterval,
        };

        // ===== Snapshot =====

        /// <summary>The main store's game-link state plus, per profile, its SimHub
        /// identity and only the device-scoped fields.</summary>
        private static JObject BuildSnapshot(MozaProfileStore main)
        {
            var full = JObject.FromObject(main, Serializer);
            var snap = new JObject();
            foreach (var prop in full.Properties())
            {
                if (prop.Name != "Profiles") { snap[prop.Name] = prop.Value; continue; }
                var arr = new JArray();
                if (prop.Value is JArray profiles)
                {
                    foreach (var token in profiles)
                    {
                        if (!(token is JObject pj)) continue;
                        var trimmed = new JObject();
                        foreach (var pp in pj.Properties())
                            if (!MozaProfileOwnNames.Contains(pp.Name) || DeviceFieldNames.Contains(pp.Name))
                                trimmed[pp.Name] = pp.Value;
                        arr.Add(trimmed);
                    }
                }
                snap["Profiles"] = arr;
            }
            return snap;
        }

        private static List<MozaProfile> ReadLegacyProfiles(JObject snapshot)
        {
            var list = new List<MozaProfile>();
            if (!(snapshot["Profiles"] is JArray arr)) return list;
            var ser = Serializer;
            foreach (var token in arr)
            {
                try
                {
                    var p = token.ToObject<MozaProfile>(ser);
                    if (p != null) list.Add(p);
                }
                catch (Exception ex) { MozaLog.Warn("[AZOM] Device profile split: unreadable legacy profile skipped: " + ex.Message); }
            }
            return list;
        }

        // ===== Discovery =====

        private sealed class Found
        {
            public readonly HashSet<Guid> Wheels = new HashSet<Guid>();
            public readonly HashSet<Guid> Bases = new HashSet<Guid>();
            public readonly HashSet<Guid> Dashes = new HashSet<Guid>();
        }

        private static Found Discover(MozaPluginSettings settings, List<MozaProfile> legacy)
        {
            var found = new Found();
            var cm2 = global::MozaPlugin.MozaPlugin.Cm2PageGuid;
            var cm1 = global::MozaPlugin.MozaPlugin.Cm1PageGuid;

            void AddWheel(Guid g)
            {
                if (g == Guid.Empty || g == cm2 || g == cm1) return;
                // The generic / old-protocol pages own no data — they resolve to the
                // connected wheel's own store at runtime.
                if (Matches(g, MozaDeviceConstants.WheelGenericGuid) || Matches(g, MozaDeviceConstants.WheelOldProtoGuid)) return;
                found.Wheels.Add(g);
            }
            void AddWheels<T>(IDictionary<Guid, T>? d) { if (d != null) foreach (var g in d.Keys) AddWheel(g); }

            foreach (var l in legacy)
            {
                AddWheels(l.WheelOverridesByPageGuid);
                AddWheels(l.Fsr1DashboardMappings);
                if (l.TelemetryChannelMappings != null)
                    foreach (var g in l.TelemetryChannelMappings.Keys)
                    {
                        if (g == cm2) found.Dashes.Add(cm2);
                        else AddWheel(g);
                    }
                if (l.Cm1FieldMappings != null && l.Cm1FieldMappings.ContainsKey(cm1))
                    found.Dashes.Add(cm1);
            }
            AddWheels(settings.WheelSleepByPageGuid);
            AddWheels(settings.WheelIdleByPageGuid);
            AddWheels(settings.WheelMzdashFolderByPageGuid);
            AddWheels(settings.Fsr1ActiveDashboardByWheelGuid);
            if (settings.WheelTelemetryEnabledByPageGuid != null)
                foreach (var g in settings.WheelTelemetryEnabledByPageGuid.Keys)
                {
                    if (g == cm2) found.Dashes.Add(cm2);
                    else AddWheel(g);
                }

            // Devices added in SimHub but not necessarily connected now.
            try
            {
                foreach (var entry in SimHubDeviceRoster.Enumerate())
                {
                    var typeId = entry.DeviceTypeId;
                    var wheelPrefix = MozaDeviceConstants.GetWheelModelPrefix(typeId);
                    if (!string.IsNullOrEmpty(wheelPrefix) && wheelPrefix != MozaDeviceConstants.OldProtocolMarker)
                    {
                        if (Guid.TryParse(MozaDeviceConstants.ResolveWheelGuid(wheelPrefix!), out var wg)) AddWheel(wg);
                        continue;
                    }
                    var basePrefix = MozaDeviceConstants.GetBaseModelPrefix(typeId);
                    if (!string.IsNullOrEmpty(basePrefix))
                    {
                        if (Guid.TryParse(MozaDeviceConstants.ResolveBaseGuid(basePrefix!), out var bg)) found.Bases.Add(bg);
                        continue;
                    }
                    if (Matches(typeId, MozaDeviceConstants.DashCm2Guid)) found.Dashes.Add(cm2);
                    else if (Matches(typeId, MozaDeviceConstants.DashCm1Guid)) found.Dashes.Add(cm1);
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] Device profile split: SimHub device roster scan failed: " + ex.Message);
            }
            return found;
        }

        private static bool Matches(Guid g, string id) =>
            Guid.TryParse(id, out var parsed) && parsed == g;

        private static bool Matches(string deviceTypeId, string id) =>
            deviceTypeId.Equals(id, StringComparison.OrdinalIgnoreCase)
            || deviceTypeId.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase);
    }
}
