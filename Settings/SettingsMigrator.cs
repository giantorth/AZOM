using System;
using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Devices;
using MozaPlugin.Telemetry.Era;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// One-shot upgrade of <see cref="MozaPluginSettings"/> from any prior on-disk
    /// schema (v0..v7) to v8. Idempotent: re-running on an already-v8 settings
    /// returns false without touching anything.
    /// </summary>
    internal sealed class SettingsMigrator
    {
        private readonly MozaPluginSettings _settings;

        public SettingsMigrator(MozaPluginSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Run the v0→v8 migration. Returns true iff anything changed (caller
        /// should persist <c>_settings</c>).
        /// </summary>
        public bool MigrateToSchemaV2()
        {
            if (_settings == null || _settings.SettingsSchemaVersion >= 10)
                return false;

            var store = _settings.ProfileStore;
            var profiles = store?.Profiles?.Where(p => p != null).ToList()
                ?? new List<MozaProfile>();

            // v4..v9: per-page dict seeding from flat fields. Hoisted above the
            // empty-profiles branch so pre-refactor users (no profiles in JSON)
            // still get their flat fields carried over. Helpers are idempotent.
            bool ranV4Plus = false;
            if (_settings.SettingsSchemaVersion < 10)
            {
                ranV4Plus = true;
                MigrateMzdashFolderToPerPage(profiles);
                MigrateTelemetryEnabledToPerPage(profiles);
                MigrateWheelSleepToPerPage(profiles);
                MigrateWheelIdleToPerPage(profiles);
            }

            if (profiles.Count == 0)
            {
                // No profiles yet — InitProfileSystem will create a default and
                // seed its baselines from the flat fields via
                // SeedProfileBaselineFromFlatFields. Bump straight to v9.
                _settings.SettingsSchemaVersion = 10;
                ClearLegacyAfterMigration();
                MozaLog.Debug("[AZOM] Schema v9: no profiles present, marking migrated (default profile will be seeded by InitProfileSystem)");
                return true;
            }

            if (_settings.SettingsSchemaVersion >= 3 && _settings.SettingsSchemaVersion < 10)
            {
                // v3+ → v9 path. The per-page dicts above are the only data-
                // carrying step; the v7 baseline-reseed runs unconditionally
                // to repair users stuck at the broken schema-6 short-circuit.
                foreach (var profile in profiles)
                    SeedProfileBaselineFromFlatFields(profile);

                _settings.SettingsSchemaVersion = 10;
                ClearLegacyAfterMigration();
                if (ranV4Plus)
                    MozaLog.Info("[AZOM] Schema v9 migration: moved mzdash folder + telemetry-enable + wheel-era + sleep-light + idle-effect/speed to per-wheel-page dicts; reseeded profile baselines from flat fields where sentinel.");
                else
                    MozaLog.Info("[AZOM] Schema v9 repair: reseeded profile baselines from flat fields where sentinel.");
                return true;
            }

            // Resolve "single model" for UID-keyed translation.
            Guid? singleModelGuid = null;
            if (_settings.PerWheelSlots != null && _settings.PerWheelSlots.Count == 1)
            {
                var modelName = _settings.PerWheelSlots.Keys.First();
                var prefix = WheelModelInfo.ExtractPrefix(modelName ?? "");
                if (!string.IsNullOrEmpty(prefix))
                {
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (Guid.TryParse(guidStr, out var g)) singleModelGuid = g;
                }
            }

            int slotsCount = 0, channelMappingsCount = 0, uidSlotCount = 0, folderCount = 0;

            // PerWheelSlots → overlays on every profile.
            if (_settings.PerWheelSlots != null)
            {
                foreach (var kvp in _settings.PerWheelSlots)
                {
                    var modelName = kvp.Key;
                    var slot = kvp.Value;
                    if (string.IsNullOrEmpty(modelName) || slot == null) continue;

                    var prefix = WheelModelInfo.ExtractPrefix(modelName);
                    if (string.IsNullOrEmpty(prefix)) continue;
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (!Guid.TryParse(guidStr, out var pageGuid)) continue;

                    foreach (var profile in profiles)
                    {
                        if (profile.WheelOverridesByPageGuid == null)
                            profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
                        if (!profile.WheelOverridesByPageGuid.TryGetValue(pageGuid, out var ov) || ov == null)
                        {
                            ov = new WheelOverride();
                            profile.WheelOverridesByPageGuid[pageGuid] = ov;
                        }
                        MergeSlotIntoOverlay(slot, ov);
                    }

                    // Schema v8: sleep-light lives on the per-page dict (shared
                    // across profiles), not the overlay.
                    if (slot.WheelSleepMode >= 0 || slot.WheelSleepTimeoutMin >= 0
                        || slot.WheelSleepSpeedMs >= 0 || slot.WheelSleepColor != null)
                    {
                        var bundle = GetOrCreateSleepBundle(pageGuid);
                        if (bundle.Mode       < 0 && slot.WheelSleepMode       >= 0) bundle.Mode       = slot.WheelSleepMode;
                        if (bundle.TimeoutMin < 0 && slot.WheelSleepTimeoutMin >= 0) bundle.TimeoutMin = slot.WheelSleepTimeoutMin;
                        if (bundle.SpeedMs    < 0 && slot.WheelSleepSpeedMs    >= 0) bundle.SpeedMs    = slot.WheelSleepSpeedMs;
                        if (bundle.Color == null && slot.WheelSleepColor != null)
                            bundle.Color = (int[])slot.WheelSleepColor.Clone();
                    }
                    // Schema v9: idle effect/speed also moved to a per-page dict
                    // (WheelIdleByPageGuid). Drain the slot's idle fields here so
                    // the per-wheel slot's intent survives migration without
                    // round-tripping through the overlay (which no longer carries
                    // these fields).
                    if (slot.WheelIdleEffect >= 0 || slot.WheelButtonsIdleEffect >= 0
                        || slot.WheelKnobIdleEffect >= 0 || slot.WheelTelemetryIdleSpeedMs >= 0
                        || slot.WheelButtonsIdleSpeedMs >= 0 || slot.WheelKnobIdleSpeedMs >= 0)
                    {
                        var ib = GetOrCreateIdleBundle(pageGuid);
                        if (ib.TelemetryEffect < 0 && slot.WheelIdleEffect >= 0)             ib.TelemetryEffect  = slot.WheelIdleEffect;
                        if (ib.ButtonsEffect < 0   && slot.WheelButtonsIdleEffect >= 0)      ib.ButtonsEffect    = slot.WheelButtonsIdleEffect;
                        if (ib.KnobEffect < 0      && slot.WheelKnobIdleEffect >= 0)         ib.KnobEffect       = slot.WheelKnobIdleEffect;
                        if (ib.TelemetrySpeedMs < 0 && slot.WheelTelemetryIdleSpeedMs >= 0)  ib.TelemetrySpeedMs = slot.WheelTelemetryIdleSpeedMs;
                        if (ib.ButtonsSpeedMs < 0   && slot.WheelButtonsIdleSpeedMs >= 0)    ib.ButtonsSpeedMs   = slot.WheelButtonsIdleSpeedMs;
                        if (ib.KnobSpeedMs < 0      && slot.WheelKnobIdleSpeedMs >= 0)       ib.KnobSpeedMs      = slot.WheelKnobIdleSpeedMs;
                    }
                    slotsCount++;
                }
            }

            // TelemetryChannelMappingsByWheel → profile.TelemetryChannelMappings.
            if (_settings.TelemetryChannelMappingsByWheel != null)
            {
                foreach (var kvp in _settings.TelemetryChannelMappingsByWheel)
                {
                    var uidHex = kvp.Key;
                    var dashMap = kvp.Value;
                    if (dashMap == null || dashMap.Count == 0) continue;

                    Guid targetGuid;
                    if (singleModelGuid.HasValue)
                    {
                        targetGuid = singleModelGuid.Value;
                    }
                    else if (string.IsNullOrEmpty(uidHex))
                    {
                        // Legacy "" slot — park under Guid.Empty for a later
                        // page-GUID-aware load to surface.
                        targetGuid = Guid.Empty;
                    }
                    else
                    {
                        MozaLog.Warn($"[AZOM] Schema v2: cannot resolve UID {uidHex} to a wheel model (PerWheelSlots has {_settings.PerWheelSlots?.Count ?? 0} entries) — leaving channel mappings under legacy key");
                        continue;
                    }

                    foreach (var profile in profiles)
                    {
                        if (profile.TelemetryChannelMappings == null)
                            profile.TelemetryChannelMappings = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();
                        if (!profile.TelemetryChannelMappings.TryGetValue(targetGuid, out var middle) || middle == null)
                        {
                            middle = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                            profile.TelemetryChannelMappings[targetGuid] = middle;
                        }
                        foreach (var dashKvp in dashMap)
                        {
                            if (string.IsNullOrEmpty(dashKvp.Key) || dashKvp.Value == null) continue;
                            // First-wins.
                            if (middle.ContainsKey(dashKvp.Key)) continue;
                            middle[dashKvp.Key] = new Dictionary<string, string>(dashKvp.Value, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    channelMappingsCount++;
                }
            }

            // TelemetryByWheelUid → overlay telemetry fields. Only when we have
            // a single-model fallback; first UID's slot wins.
            if (singleModelGuid.HasValue && _settings.TelemetryByWheelUid != null && _settings.TelemetryByWheelUid.Count > 0)
            {
                var firstSlot = _settings.TelemetryByWheelUid
                    .Where(x => x.Value != null)
                    .Select(x => x.Value)
                    .FirstOrDefault();
                if (firstSlot != null)
                {
                    foreach (var profile in profiles)
                    {
                        if (profile.WheelOverridesByPageGuid == null)
                            profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
                        if (!profile.WheelOverridesByPageGuid.TryGetValue(singleModelGuid.Value, out var ov) || ov == null)
                        {
                            ov = new WheelOverride();
                            profile.WheelOverridesByPageGuid[singleModelGuid.Value] = ov;
                        }
                        if (!string.IsNullOrEmpty(firstSlot.TelemetryProfileName))
                            ov.TelemetryProfileName = firstSlot.TelemetryProfileName;
                        if (!string.IsNullOrEmpty(firstSlot.TelemetryMzdashPath))
                            ov.TelemetryMzdashPath = firstSlot.TelemetryMzdashPath;
                    }
                    // TelemetryEnabled is now per-wheel-page (not per-overlay).
                    if (firstSlot.TelemetryEnabled && singleModelGuid.HasValue)
                        _settings.WheelTelemetryEnabledByPageGuid[singleModelGuid.Value] = true;
                    uidSlotCount = _settings.TelemetryByWheelUid.Count;
                }
            }

            // Flat fields → profile / overlay (single read pass).
            // Folder migration ran via the hoisted call above; track the count for the log.
            folderCount = _settings.WheelMzdashFolderByUid?.Count ?? 0;

            // Profile-level baseline seeding (dash + base-ambient + gearshift).
            foreach (var profile in profiles)
                SeedProfileBaselineFromFlatFields(profile);

            // Per-wheel-page overlay seeding from flat Wheel*/Telemetry* fields
            // when PerWheelSlots is empty (rare — pre-PerWheelSlots builds).
            if (singleModelGuid.HasValue)
            {
                foreach (var profile in profiles)
                {
                    if (profile.WheelOverridesByPageGuid == null)
                        profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
                    if (!profile.WheelOverridesByPageGuid.TryGetValue(singleModelGuid.Value, out var ov) || ov == null)
                    {
                        ov = new WheelOverride();
                        profile.WheelOverridesByPageGuid[singleModelGuid.Value] = ov;
                    }

                    if (ov.WheelTelemetryMode      < 0) ov.WheelTelemetryMode      = _settings.WheelTelemetryMode;
                    if (ov.WheelKnobLedMode        < 0) ov.WheelKnobLedMode        = _settings.WheelKnobLedMode;
                    if (ov.WheelButtonsLedMode     < 0) ov.WheelButtonsLedMode     = _settings.WheelButtonsLedMode;
                    // Idle effect/speed: drained into WheelIdleByPageGuid by
                    // MigrateWheelIdleToPerPage below — not seeded into the overlay.
                    if (ov.WheelRpmBrightness      < 0) ov.WheelRpmBrightness      = _settings.WheelRpmBrightness;
                    if (ov.WheelButtonsBrightness  < 0) ov.WheelButtonsBrightness  = _settings.WheelButtonsBrightness;
                    if (ov.WheelFlagsBrightness    < 0) ov.WheelFlagsBrightness    = _settings.WheelFlagsBrightness;
                    if (ov.WheelESRpmBrightness    < 0) ov.WheelESRpmBrightness    = _settings.WheelESRpmBrightness;
                    if (ov.WheelRpmIndicatorMode   < 0) ov.WheelRpmIndicatorMode   = _settings.WheelRpmIndicatorMode;
                    if (ov.WheelRpmDisplayMode     < 0) ov.WheelRpmDisplayMode     = _settings.WheelRpmDisplayMode;
                    if (ov.WheelPaddlesMode        < 0) ov.WheelPaddlesMode        = _settings.WheelPaddlesMode;
                    if (ov.WheelClutchPoint        < 0) ov.WheelClutchPoint        = _settings.WheelClutchPoint;
                    if (ov.WheelKnobMode           < 0) ov.WheelKnobMode           = _settings.WheelKnobMode;
                    if (ov.WheelStickMode          < 0) ov.WheelStickMode          = _settings.WheelStickMode;
                    if (ov.WheelRpmBlinkColors == null && _settings.WheelRpmBlinkColors != null)
                        ov.WheelRpmBlinkColors = (int[])_settings.WheelRpmBlinkColors.Clone();
                    if (ov.WheelKnobBackgroundColors == null && _settings.WheelKnobBackgroundColors != null)
                        ov.WheelKnobBackgroundColors = (int[])_settings.WheelKnobBackgroundColors.Clone();
                    if (ov.WheelKnobPrimaryColors == null && _settings.WheelKnobPrimaryColors != null)
                        ov.WheelKnobPrimaryColors = (int[])_settings.WheelKnobPrimaryColors.Clone();
                    if (ov.WheelKnobRingColors == null && _settings.WheelKnobRingColors != null)
                        ov.WheelKnobRingColors = (int[])_settings.WheelKnobRingColors.Clone();
                    if (ov.WheelKnobRingBrightness < 0) ov.WheelKnobRingBrightness = _settings.WheelKnobRingBrightness;

                    if (string.IsNullOrEmpty(ov.TelemetryProfileName)
                        && !string.IsNullOrEmpty(_settings.TelemetryProfileName))
                        ov.TelemetryProfileName = _settings.TelemetryProfileName;
                    if (string.IsNullOrEmpty(ov.TelemetryMzdashPath)
                        && !string.IsNullOrEmpty(_settings.TelemetryMzdashPath))
                        ov.TelemetryMzdashPath = _settings.TelemetryMzdashPath;
                }
            }

            // v4..v9 step: per-wheel-page dict seeding. Re-run unconditionally
            // so older settings JSONs get their flat-field values hoisted into
            // the per-page dicts.
            MigrateMzdashFolderToPerPage(profiles);
            MigrateTelemetryEnabledToPerPage(profiles);
            MigrateWheelSleepToPerPage(profiles);
            MigrateWheelIdleToPerPage(profiles);

            _settings.SettingsSchemaVersion = 10;
            MozaLog.Info(
                $"[AZOM] Schema v9 migration: PerWheelSlots={slotsCount}, " +
                $"ChannelMappings={channelMappingsCount}, TelemetryByUid={uidSlotCount}, " +
                $"MzdashFolderByUid={folderCount} → applied across {profiles.Count} profile(s); " +
                $"flat-field seeding done");
            ClearLegacyAfterMigration();
            return true;
        }

        /// <summary>Seed dash/base-ambient/gearshift baselines from flat settings (sentinel-only, idempotent).</summary>
        public void SeedProfileBaselineFromFlatFields(MozaProfile profile)
        {
            if (_settings == null || profile == null) return;

            // Dash brightness baselines.
            if (profile.DashRpmBrightness     < 0) profile.DashRpmBrightness     = _settings.DashRpmBrightness;
            if (profile.DashFlagsBrightness   < 0) profile.DashFlagsBrightness   = _settings.DashFlagsBrightness;
            if (profile.DashDisplayBrightness < 0) profile.DashDisplayBrightness = _settings.DashDisplayBrightness;
            if (profile.DashDisplayStandbyMin < 0) profile.DashDisplayStandbyMin = _settings.DashDisplayStandbyMin;
            if (profile.DashRpmBlinkColors == null && _settings.DashRpmBlinkColors != null)
                profile.DashRpmBlinkColors = (int[])_settings.DashRpmBlinkColors.Clone();

            // Base ambient.
            if (profile.BaseAmbientBrightness     < 0) profile.BaseAmbientBrightness     = _settings.BaseAmbientBrightness;
            if (profile.BaseAmbientStandbyMode    < 0) profile.BaseAmbientStandbyMode    = _settings.BaseAmbientStandbyMode;
            if (profile.BaseAmbientIndicatorState < 0) profile.BaseAmbientIndicatorState = _settings.BaseAmbientIndicatorState;
            if (profile.BaseAmbientSleepMode      < 0) profile.BaseAmbientSleepMode      = _settings.BaseAmbientSleepMode;
            if (profile.BaseAmbientSleepTimeout   < 0) profile.BaseAmbientSleepTimeout   = _settings.BaseAmbientSleepTimeout;
            if (profile.BaseAmbientStartupColor   < 0) profile.BaseAmbientStartupColor   = _settings.BaseAmbientStartupColor;
            if (profile.BaseAmbientShutdownColor  < 0) profile.BaseAmbientShutdownColor  = _settings.BaseAmbientShutdownColor;

            // Gearshift.
            if (profile.GearshiftVibrateOnNeutral < 0) profile.GearshiftVibrateOnNeutral = _settings.GearshiftVibrateOnNeutral ? 1 : 0;
            if (profile.GearshiftDebounceMs       < 0) profile.GearshiftDebounceMs       = _settings.GearshiftDebounceMs;
        }

        /// <summary>
        /// Hoist wheel idle-effect / idle-speed settings off per-game overlay
        /// onto WheelIdleByPageGuid (schema v9). First non-sentinel per page
        /// wins. Drains overlay LegacyJsonFields, profile baseline LegacyJsonFields,
        /// and the _settings flat fields.
        /// </summary>
        private void MigrateWheelIdleToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelIdleByPageGuid == null)
                _settings.WheelIdleByPageGuid = new Dictionary<Guid, WheelIdleSettings>();

            // 1. Drain pre-v9 per-overlay idle values from LegacyJsonFields.
            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    var bundle = GetOrCreateIdleBundle(kvp.Key);
                    DrainIdleKeysInto(ov.LegacyJsonFields, bundle);
                    if (ov.LegacyJsonFields.Count == 0) ov.LegacyJsonFields = null;
                }
            }

            // 2. Drain pre-v9 profile-baseline idle values (applied universally).
            foreach (var profile in profiles)
            {
                if (profile.LegacyJsonFields == null) continue;
                var staged = new WheelIdleSettings();
                bool any = DrainIdleKeysInto(profile.LegacyJsonFields, staged);
                if (any) SeedIdleUniversally(staged);
                if (profile.LegacyJsonFields.Count == 0) profile.LegacyJsonFields = null;
            }

            // 3. Seed every known wheel page GUID from the flat fields.
            var flat = new WheelIdleSettings
            {
                TelemetryEffect = _settings.WheelIdleEffect,
                ButtonsEffect = _settings.WheelButtonsIdleEffect,
                KnobEffect = _settings.WheelKnobIdleEffect,
                TelemetrySpeedMs = _settings.WheelTelemetryIdleSpeedMs,
                ButtonsSpeedMs = _settings.WheelButtonsIdleSpeedMs,
                KnobSpeedMs = _settings.WheelKnobIdleSpeedMs,
            };
            if (flat.TelemetryEffect >= 0 || flat.ButtonsEffect >= 0 || flat.KnobEffect >= 0
                || flat.TelemetrySpeedMs >= 0 || flat.ButtonsSpeedMs >= 0 || flat.KnobSpeedMs >= 0)
                SeedIdleUniversally(flat);

            _settings.WheelIdleEffect = -1;
            _settings.WheelButtonsIdleEffect = -1;
            _settings.WheelKnobIdleEffect = -1;
            _settings.WheelTelemetryIdleSpeedMs = -1;
            _settings.WheelButtonsIdleSpeedMs = -1;
            _settings.WheelKnobIdleSpeedMs = -1;
        }

        /// <summary>
        /// Pull WheelIdle*/WheelButtonsIdle*/WheelKnobIdle* keys out of a
        /// LegacyJsonFields dict into an idle bundle. First-non-sentinel wins
        /// per field. Returns true iff any field set.
        /// </summary>
        private static bool DrainIdleKeysInto(
            Dictionary<string, Newtonsoft.Json.Linq.JToken> legacy,
            WheelIdleSettings bundle)
        {
            bool any = false;
            bool TryDrainInt(string key, ref int dst)
            {
                if (dst >= 0) { legacy.Remove(key); return false; }
                if (!legacy.TryGetValue(key, out var tok) || tok == null) { legacy.Remove(key); return false; }
                try { var v = tok.ToObject<int>(); if (v >= 0) { dst = v; legacy.Remove(key); return true; } }
                catch { }
                legacy.Remove(key);
                return false;
            }
            // Overlay JSON field name (WheelIdleEffect) ≠ bundle field name
            // (TelemetryEffect) — the overlay was telemetry/RPM-scoped even
            // though it was named "WheelIdleEffect". Wire bytes match.
            int t = bundle.TelemetryEffect;
            int b = bundle.ButtonsEffect;
            int k = bundle.KnobEffect;
            int ts = bundle.TelemetrySpeedMs;
            int bs = bundle.ButtonsSpeedMs;
            int ks = bundle.KnobSpeedMs;
            if (TryDrainInt("WheelIdleEffect", ref t)) any = true;
            if (TryDrainInt("WheelButtonsIdleEffect", ref b)) any = true;
            if (TryDrainInt("WheelKnobIdleEffect", ref k)) any = true;
            if (TryDrainInt("WheelTelemetryIdleSpeedMs", ref ts)) any = true;
            if (TryDrainInt("WheelButtonsIdleSpeedMs", ref bs)) any = true;
            if (TryDrainInt("WheelKnobIdleSpeedMs", ref ks)) any = true;
            bundle.TelemetryEffect = t;
            bundle.ButtonsEffect = b;
            bundle.KnobEffect = k;
            bundle.TelemetrySpeedMs = ts;
            bundle.ButtonsSpeedMs = bs;
            bundle.KnobSpeedMs = ks;
            return any;
        }

        private WheelIdleSettings GetOrCreateIdleBundle(Guid pageGuid)
        {
            if (!_settings!.WheelIdleByPageGuid.TryGetValue(pageGuid, out var bundle) || bundle == null)
            {
                bundle = new WheelIdleSettings();
                _settings.WheelIdleByPageGuid[pageGuid] = bundle;
            }
            return bundle;
        }

        private void SeedIdleUniversally(WheelIdleSettings staged)
        {
            void Seed(Guid pageGuid)
            {
                var dst = GetOrCreateIdleBundle(pageGuid);
                if (dst.TelemetryEffect < 0 && staged.TelemetryEffect >= 0)   dst.TelemetryEffect  = staged.TelemetryEffect;
                if (dst.ButtonsEffect < 0   && staged.ButtonsEffect >= 0)     dst.ButtonsEffect    = staged.ButtonsEffect;
                if (dst.KnobEffect < 0      && staged.KnobEffect >= 0)        dst.KnobEffect       = staged.KnobEffect;
                if (dst.TelemetrySpeedMs < 0 && staged.TelemetrySpeedMs >= 0) dst.TelemetrySpeedMs = staged.TelemetrySpeedMs;
                if (dst.ButtonsSpeedMs < 0   && staged.ButtonsSpeedMs >= 0)   dst.ButtonsSpeedMs   = staged.ButtonsSpeedMs;
                if (dst.KnobSpeedMs < 0      && staged.KnobSpeedMs >= 0)      dst.KnobSpeedMs      = staged.KnobSpeedMs;
            }
            foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
            {
                var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                if (Guid.TryParse(guidStr, out var pageGuid)) Seed(pageGuid);
            }
            if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)) Seed(gg);
            if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)) Seed(og);
        }

        /// <summary>
        /// Hoist wheel sleep settings off per-game overlay onto WheelSleepByPageGuid.
        /// First non-sentinel per page wins. Drains overlay/profile/flat sources.
        /// </summary>
        private void MigrateWheelSleepToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelSleepByPageGuid == null)
                _settings.WheelSleepByPageGuid = new Dictionary<Guid, WheelSleepSettings>();

            // 1. Drain pre-v8 per-overlay sleep values from LegacyJsonFields.
            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    var bundle = GetOrCreateSleepBundle(kvp.Key);
                    DrainSleepKeysInto(ov.LegacyJsonFields, bundle);
                    if (ov.LegacyJsonFields.Count == 0) ov.LegacyJsonFields = null;
                }
            }

            // 2. Drain pre-v8 profile-baseline sleep values (applied universally).
            foreach (var profile in profiles)
            {
                if (profile.LegacyJsonFields == null) continue;
                var staged = new WheelSleepSettings();
                bool any = DrainSleepKeysInto(profile.LegacyJsonFields, staged);
                if (any) SeedSleepUniversally(staged);
                if (profile.LegacyJsonFields.Count == 0) profile.LegacyJsonFields = null;
            }

            // 3. Seed every known wheel page GUID from the flat fields.
            var flat = new WheelSleepSettings
            {
                Mode = _settings.WheelSleepMode,
                TimeoutMin = _settings.WheelSleepTimeoutMin,
                SpeedMs = _settings.WheelSleepSpeedMs,
                Color = _settings.WheelSleepColor,
            };
            if (flat.Mode >= 0 || flat.TimeoutMin >= 0 || flat.SpeedMs >= 0 || flat.Color != null)
                SeedSleepUniversally(flat);

            _settings.WheelSleepMode = -1;
            _settings.WheelSleepTimeoutMin = -1;
            _settings.WheelSleepSpeedMs = -1;
            _settings.WheelSleepColor = null;
        }

        /// <summary>
        /// Pull WheelSleep* keys out of a LegacyJsonFields dict into a bundle.
        /// First-non-sentinel wins per field. Returns true iff any field set.
        /// </summary>
        private static bool DrainSleepKeysInto(
            Dictionary<string, Newtonsoft.Json.Linq.JToken> legacy,
            WheelSleepSettings bundle)
        {
            bool any = false;
            if (bundle.Mode < 0 && legacy.TryGetValue("WheelSleepMode", out var mTok) && mTok != null)
            {
                try { var v = mTok.ToObject<int>(); if (v >= 0) { bundle.Mode = v; any = true; } } catch { }
            }
            if (bundle.TimeoutMin < 0 && legacy.TryGetValue("WheelSleepTimeoutMin", out var tTok) && tTok != null)
            {
                try { var v = tTok.ToObject<int>(); if (v >= 0) { bundle.TimeoutMin = v; any = true; } } catch { }
            }
            if (bundle.SpeedMs < 0 && legacy.TryGetValue("WheelSleepSpeedMs", out var sTok) && sTok != null)
            {
                try { var v = sTok.ToObject<int>(); if (v >= 0) { bundle.SpeedMs = v; any = true; } } catch { }
            }
            if (bundle.Color == null && legacy.TryGetValue("WheelSleepColor", out var cTok) && cTok != null)
            {
                try { var v = cTok.ToObject<int[]>(); if (v != null && v.Length > 0) { bundle.Color = v; any = true; } } catch { }
            }
            legacy.Remove("WheelSleepMode");
            legacy.Remove("WheelSleepTimeoutMin");
            legacy.Remove("WheelSleepSpeedMs");
            legacy.Remove("WheelSleepColor");
            return any;
        }

        private WheelSleepSettings GetOrCreateSleepBundle(Guid pageGuid)
        {
            if (!_settings!.WheelSleepByPageGuid.TryGetValue(pageGuid, out var bundle) || bundle == null)
            {
                bundle = new WheelSleepSettings();
                _settings.WheelSleepByPageGuid[pageGuid] = bundle;
            }
            return bundle;
        }

        private void SeedSleepUniversally(WheelSleepSettings staged)
        {
            void Seed(Guid pageGuid)
            {
                var dst = GetOrCreateSleepBundle(pageGuid);
                if (dst.Mode < 0 && staged.Mode >= 0)             dst.Mode       = staged.Mode;
                if (dst.TimeoutMin < 0 && staged.TimeoutMin >= 0) dst.TimeoutMin = staged.TimeoutMin;
                if (dst.SpeedMs < 0 && staged.SpeedMs >= 0)       dst.SpeedMs    = staged.SpeedMs;
                if (dst.Color == null && staged.Color != null)    dst.Color      = (int[])staged.Color.Clone();
            }
            foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
            {
                var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                if (Guid.TryParse(guidStr, out var pageGuid)) Seed(pageGuid);
            }
            if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)) Seed(gg);
            if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)) Seed(og);
        }

        /// <summary>
        /// Move <c>TelemetryEnabled</c> from per-overlay storage onto
        /// <see cref="MozaPluginSettings.WheelTelemetryEnabledByPageGuid"/>.
        /// OR-merges across profiles.
        /// </summary>
        private void MigrateTelemetryEnabledToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelTelemetryEnabledByPageGuid == null)
                _settings.WheelTelemetryEnabledByPageGuid = new Dictionary<Guid, bool>();

            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    if (ov.LegacyJsonFields.TryGetValue("TelemetryEnabled", out var tok)
                        && tok != null)
                    {
                        int v;
                        try { v = tok.ToObject<int>(); }
                        catch { v = -1; }
                        if (v == 1)
                            _settings.WheelTelemetryEnabledByPageGuid[kvp.Key] = true;
                    }
                    ov.LegacyJsonFields.Remove("TelemetryEnabled");
                    ov.LegacyJsonFields.Remove("TelemetryMzdashFolder");
                    if (ov.LegacyJsonFields.Count == 0) ov.LegacyJsonFields = null;
                }
            }

            if (_settings.TelemetryEnabled)
            {
                foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
                {
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (!Guid.TryParse(guidStr, out var pageGuid)) continue;
                    if (!_settings.WheelTelemetryEnabledByPageGuid.ContainsKey(pageGuid))
                        _settings.WheelTelemetryEnabledByPageGuid[pageGuid] = true;
                }
                if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)
                    && !_settings.WheelTelemetryEnabledByPageGuid.ContainsKey(gg))
                    _settings.WheelTelemetryEnabledByPageGuid[gg] = true;
                if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)
                    && !_settings.WheelTelemetryEnabledByPageGuid.ContainsKey(og))
                    _settings.WheelTelemetryEnabledByPageGuid[og] = true;
            }
            _settings.TelemetryEnabled = false;
        }

        /// <summary>
        /// Move mzdash folder from per-overlay storage onto
        /// <see cref="MozaPluginSettings.WheelMzdashFolderByPageGuid"/> (one
        /// folder per wheel-page, shared across all profiles).
        /// </summary>
        private void MigrateMzdashFolderToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelMzdashFolderByPageGuid == null)
                _settings.WheelMzdashFolderByPageGuid = new Dictionary<Guid, string>();

            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    if (ov.LegacyJsonFields.TryGetValue("TelemetryMzdashFolder", out var tok)
                        && tok != null)
                    {
                        string folder = "";
                        try { folder = tok.ToObject<string>() ?? ""; }
                        catch { }
                        if (!string.IsNullOrEmpty(folder)
                            && !_settings.WheelMzdashFolderByPageGuid.ContainsKey(kvp.Key))
                            _settings.WheelMzdashFolderByPageGuid[kvp.Key] = folder;
                    }
                }
            }

            // Seed from flat field when unset; fall back to per-UID dict's first
            // entry (older builds stored the folder there).
            string flatFolder = _settings.TelemetryMzdashFolder ?? "";
            if (string.IsNullOrEmpty(flatFolder)
                && _settings.WheelMzdashFolderByUid != null
                && _settings.WheelMzdashFolderByUid.Count > 0)
            {
                flatFolder = _settings.WheelMzdashFolderByUid
                    .Where(x => !string.IsNullOrEmpty(x.Value))
                    .Select(x => x.Value)
                    .FirstOrDefault() ?? "";
            }
            if (!string.IsNullOrEmpty(flatFolder))
            {
                foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
                {
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (!Guid.TryParse(guidStr, out var pageGuid)) continue;
                    if (!_settings.WheelMzdashFolderByPageGuid.ContainsKey(pageGuid))
                        _settings.WheelMzdashFolderByPageGuid[pageGuid] = flatFolder;
                }
                if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)
                    && !_settings.WheelMzdashFolderByPageGuid.ContainsKey(gg))
                    _settings.WheelMzdashFolderByPageGuid[gg] = flatFolder;
                if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)
                    && !_settings.WheelMzdashFolderByPageGuid.ContainsKey(og))
                    _settings.WheelMzdashFolderByPageGuid[og] = flatFolder;
            }
            _settings.TelemetryMzdashFolder = "";
        }

        /// <summary>
        /// Null out / clear every legacy <see cref="MozaPluginSettings"/> store
        /// after migration. Subsequent saves serialize them as null/empty.
        /// </summary>
        private void ClearLegacyAfterMigration()
        {
            if (_settings == null) return;
            _settings.PerWheelSlots = new Dictionary<string, PerWheelSlot>(StringComparer.OrdinalIgnoreCase);
            _settings.TelemetryByWheelUid = new Dictionary<string, TelemetryWheelSlot>(StringComparer.OrdinalIgnoreCase);
            _settings.WheelMzdashFolderByUid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _settings.TelemetryChannelMappingsByWheel = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            _settings.TelemetryChannelMappings = null;
            _settings.TelemetryProfileName = "";
            _settings.TelemetryMzdashPath = "";
        }

        /// <summary>
        /// Copy non-sentinel fields from a legacy <see cref="PerWheelSlot"/> into
        /// a <see cref="WheelOverride"/>. Re-running migration is safe (only writes
        /// when the slot field is non-sentinel and ignores existing overlay values).
        /// </summary>
        private static void MergeSlotIntoOverlay(PerWheelSlot slot, WheelOverride ov)
        {
            if (slot.WheelTelemetryMode     >= 0) ov.WheelTelemetryMode     = slot.WheelTelemetryMode;
            if (slot.WheelKnobLedMode       >= 0) ov.WheelKnobLedMode       = slot.WheelKnobLedMode;
            if (slot.WheelButtonsLedMode    >= 0) ov.WheelButtonsLedMode    = slot.WheelButtonsLedMode;
            // WheelSleep* is migrated by the caller into WheelSleepByPageGuid.
            // WheelIdle* / WheelButtonsIdle* / WheelKnobIdle* and the matching
            // *SpeedMs fields are migrated by the caller into WheelIdleByPageGuid.
            if (slot.WheelRpmBrightness     >= 0) ov.WheelRpmBrightness     = slot.WheelRpmBrightness;
            if (slot.WheelButtonsBrightness >= 0) ov.WheelButtonsBrightness = slot.WheelButtonsBrightness;
            if (slot.WheelFlagsBrightness   >= 0) ov.WheelFlagsBrightness   = slot.WheelFlagsBrightness;
            if (slot.WheelESRpmBrightness   >= 0) ov.WheelESRpmBrightness   = slot.WheelESRpmBrightness;
            if (slot.WheelRpmIndicatorMode  >= 0) ov.WheelRpmIndicatorMode  = slot.WheelRpmIndicatorMode;
            if (slot.WheelRpmDisplayMode    >= 0) ov.WheelRpmDisplayMode    = slot.WheelRpmDisplayMode;
            if (slot.WheelPaddlesMode       >= 0) ov.WheelPaddlesMode       = slot.WheelPaddlesMode;
            if (slot.WheelClutchPoint       >= 0) ov.WheelClutchPoint       = slot.WheelClutchPoint;
            if (slot.WheelKnobMode          >= 0) ov.WheelKnobMode          = slot.WheelKnobMode;
            if (slot.WheelStickMode         >= 0) ov.WheelStickMode         = slot.WheelStickMode;
            if (slot.WheelRpmBlinkColors    != null) ov.WheelRpmBlinkColors = (int[])slot.WheelRpmBlinkColors.Clone();
            if (slot.WheelKnobBackgroundColors != null) ov.WheelKnobBackgroundColors = (int[])slot.WheelKnobBackgroundColors.Clone();
            if (slot.WheelKnobPrimaryColors    != null) ov.WheelKnobPrimaryColors    = (int[])slot.WheelKnobPrimaryColors.Clone();
            if (slot.WheelKnobRingColors       != null) ov.WheelKnobRingColors       = (int[])slot.WheelKnobRingColors.Clone();
            if (slot.WheelKnobRingBrightness   >= 0) ov.WheelKnobRingBrightness      = slot.WheelKnobRingBrightness;
        }
    }
}
