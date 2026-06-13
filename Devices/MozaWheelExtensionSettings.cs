using System;
using System.Collections.Generic;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Wheel-specific settings stored in SimHub device profiles.
    /// Serialized to/from JSON via GetSettings()/SetSettings() on the device extension.
    /// Uses -1 sentinel for "not included" (same convention as MozaProfile).
    /// Colors packed as R&lt;&lt;16 | G&lt;&lt;8 | B.
    /// </summary>
    public class MozaWheelExtensionSettings
    {
        // Wheel model this extension was last captured against (empty for legacy
        // pre-per-wheel-slot JSON). Lets ApplyTo route settings into the matching
        // slot instead of clobbering whichever wheel is currently active.
        public string WheelModelName { get; set; } = "";

        // Wheel LED mode
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;
        public int WheelKnobIdleEffect { get; set; } = -1;
        public int WheelKnobLedMode { get; set; } = -1;
        public int WheelButtonsLedMode { get; set; } = -1;
        public int WheelTelemetryIdleSpeedMs { get; set; } = -1;
        public int WheelButtonsIdleSpeedMs { get; set; } = -1;
        public int WheelKnobIdleSpeedMs { get; set; } = -1;
        public int WheelSleepMode { get; set; } = -1;
        public int WheelSleepTimeoutMin { get; set; } = -1;
        public int WheelSleepSpeedMs { get; set; } = -1;
        public int[]? WheelSleepColor { get; set; }

        // Brightness (new wheels 0-100, ES wheels 0-15)
        public int WheelRpmBrightness { get; set; } = -1;
        public int WheelButtonsBrightness { get; set; } = -1;
        public int WheelFlagsBrightness { get; set; } = -1;
        public int WheelESRpmBrightness { get; set; } = -1;

        // ES/Old wheel
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;

        // Dashboard telemetry (per-wheel-profile).
        // NOTE: TelemetryEnabled / TelemetryProfileName / TelemetryMzdashPath
        // intentionally NOT persisted here — they are global plugin settings on
        // MozaPluginSettings. Persisting them per-wheel-profile caused stale
        // extension JSON to clobber the freshly-loaded global value on startup
        // (e.g. user picks a .mzdash file, restart, path resets to empty).
        public bool TelemetrySettingsPresent { get; set; } = false;

        // Color arrays (packed as R<<16 | G<<8 | B)
        public int[]? WheelRpmColors { get; set; }
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? WheelButtonColors { get; set; }
        public bool[]? WheelButtonDefaultDuringTelemetry { get; set; }
        public int[]? WheelFlagColors { get; set; }
        public int[]? WheelIdleColor { get; set; }
        public int[]? WheelESRpmColors { get; set; }
        public int[]? WheelKnobBackgroundColors { get; set; }
        public int[]? WheelKnobPrimaryColors { get; set; }

        // Group 3 per-LED ring colors (packed R<<16|G<<8|B per LED, up to 56)
        public int[]? WheelKnobRingColors { get; set; }
        public int WheelKnobRingBrightness { get; set; } = -1;
        public bool? WheelKnobDefaultDuringTelemetry { get; set; }
        public int WheelKnobStaticTimeoutMs { get; set; } = -1;

        /// <summary>
        /// Capture current wheel state from the plugin. When <paramref name="profile"/>
        /// and <paramref name="pageModelPrefix"/> are provided, capture from the
        /// profile's overlay for that page GUID instead of the flat-field path —
        /// the new R5 source of truth.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data,
            MozaProfile? profile = null, string? pageModelPrefix = null)
        {
            if (!data.BaseSettingsRead) return;

            // If we have a profile + page identity, capture from the overlay
            // rather than flat fields. The overlay is the new source of truth.
            if (profile != null && !string.IsNullOrEmpty(pageModelPrefix)
                && TryGetPageGuid(pageModelPrefix!, out var pageGuid)
                && profile.WheelOverridesByPageGuid != null
                && profile.WheelOverridesByPageGuid.TryGetValue(pageGuid, out var ov)
                && ov != null)
            {
                // Sleep-light + idle-effect/speed bundles are keyed by the same
                // page GUID on the shared MozaPluginSettings dicts (per-wheel-page,
                // not per-game).
                WheelSleepSettings? sleep = null;
                if (settings.WheelSleepByPageGuid != null)
                    settings.WheelSleepByPageGuid.TryGetValue(pageGuid, out sleep);
                WheelIdleSettings? idle = null;
                if (settings.WheelIdleByPageGuid != null)
                    settings.WheelIdleByPageGuid.TryGetValue(pageGuid, out idle);
                CaptureFromOverlay(ov, data, sleep, idle);
                return;
            }

            WheelModelName = data.WheelModelName ?? "";
            WheelTelemetryMode = settings.WheelTelemetryMode;
            WheelKnobLedMode = settings.WheelKnobLedMode;
            WheelButtonsLedMode = settings.WheelButtonsLedMode;
            // Idle effect/speed live on settings.WheelIdleByPageGuid (per-wheel,
            // shared across profiles). Without a page identity here we fall
            // back to leaving the DTO fields at sentinel — the device-side JSON
            // for a "no wheel identified" snapshot has no meaningful idle pick.
            // WheelSleep* now lives on settings.WheelSleepByPageGuid (per-wheel,
            // shared across profiles). The DTO still carries them so older
            // SimHub-side device blobs round-trip cleanly during migration; the
            // ApplyTo path drains them into the per-page dict.
            WheelSleepMode = settings.WheelSleepMode;
            WheelSleepTimeoutMin = settings.WheelSleepTimeoutMin;
            WheelSleepSpeedMs = settings.WheelSleepSpeedMs;
            WheelSleepColor = settings.WheelSleepColor;
            WheelRpmBrightness = settings.WheelRpmBrightness;
            WheelButtonsBrightness = settings.WheelButtonsBrightness;
            WheelFlagsBrightness = settings.WheelFlagsBrightness;
            WheelESRpmBrightness = settings.WheelESRpmBrightness;
            WheelRpmIndicatorMode = settings.WheelRpmIndicatorMode;
            WheelRpmDisplayMode = settings.WheelRpmDisplayMode;

            TelemetrySettingsPresent = true;

            WheelRpmColors = MozaProfile.PackColors(data.WheelRpmColors);
            WheelRpmBlinkColors = MozaProfile.PackColors(data.WheelRpmBlinkColors);
            WheelButtonColors = MozaProfile.PackColors(data.WheelButtonColors);
            WheelButtonDefaultDuringTelemetry = (bool[])data.WheelButtonDefaultDuringTelemetry.Clone();
            WheelFlagColors = MozaProfile.PackColors(data.WheelFlagColors);
            WheelIdleColor = new[] { MozaProfile.PackColor(data.WheelIdleColor) };
            WheelESRpmColors = MozaProfile.PackColors(data.WheelESRpmColors);
            WheelKnobBackgroundColors = MozaProfile.PackColors(data.WheelKnobBackgroundColors);
            WheelKnobPrimaryColors = MozaProfile.PackColors(data.WheelKnobPrimaryColors);
            WheelKnobRingColors = MozaProfile.PackColors(data.KnobRingColors);
            WheelKnobRingBrightness = data.KnobRingBrightness;
            WheelKnobDefaultDuringTelemetry = data.WheelKnobDefaultDuringTelemetry;
            WheelKnobStaticTimeoutMs = data.WheelKnobStaticTimeoutMs;
        }

        /// <summary>
        /// Apply these settings into the profile's wheel-page overlay (single
        /// source of truth) and mirror colors into <paramref name="data"/> so the
        /// UI sees them immediately. Does NOT write to hardware — caller routes
        /// through <c>ApplyWheelToHardware</c> after this.
        /// </summary>
        public void ApplyTo(MozaPluginSettings settings, MozaData data,
            MozaProfile? profile = null, string? pageModelPrefix = null)
        {
            // Migration is one-shot. The plugin settings file (bundle + overlay)
            // is the authoritative source for the user's wheel-LED / sleep /
            // idle settings; this DTO is only the SimHub-side device JSON,
            // which mirrors but can lag behind. Once we've drained any legacy
            // values it carried into the live in-memory state, every subsequent
            // SetSettings call is a no-op — otherwise a stale DTO field will
            // win on restart whenever SimHub failed to call GetSettings before
            // shutdown.
            //
            // Gate on MozaPluginSettings.WheelExtensionDrained — saved with the
            // plugin's own debounce timer + End() flush, so it's reliable.
            // Affects every wheel device-extension instance (one per saved-on-
            // disk device GUID — the user can accumulate several): once the
            // first one runs the merge, the flag locks for all of them.
            MozaLog.Info($"[AZOM] APPLYTO: prefix='{pageModelPrefix ?? "(null)"}' Drained={(settings?.WheelExtensionDrained == true)} dtoSleepTimeoutMin={WheelSleepTimeoutMin}");
            if (settings != null && settings.WheelExtensionDrained) return;

            // Write into the profile's overlay for this page GUID. No-op when the
            // profile or page GUID can't be resolved (extension's SetSettings
            // arrived before profile system initialized — the extension JSON on
            // disk still has the values, so they'll re-apply on next call).
            if (profile != null && !string.IsNullOrEmpty(pageModelPrefix)
                && TryGetPageGuid(pageModelPrefix!, out var pageGuid))
            {
                if (profile.WheelOverridesByPageGuid == null)
                    profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
                if (!profile.WheelOverridesByPageGuid.TryGetValue(pageGuid, out var ov) || ov == null)
                {
                    ov = new WheelOverride();
                    profile.WheelOverridesByPageGuid[pageGuid] = ov;
                }
                MergeIntoOverlay(ov);

                // Sleep-light bundle is per-wheel-page (NOT per-game), so it
                // lives on MozaPluginSettings.WheelSleepByPageGuid. The plugin
                // settings file is loaded BEFORE this method is invoked, so the
                // bundle already holds the user's most recent value at this
                // point. The DTO can lag behind (SimHub only re-serializes the
                // device JSON when it calls GetSettings, which doesn't always
                // happen before shutdown) — so treat it as a FILL-ONLY source:
                // only adopt a DTO value when the bundle field is at its
                // sentinel. Otherwise the stale device JSON clobbers the
                // user's freshly-loaded setting on every restart.
                // Sleep/idle bundles live on plugin-wide settings; skip when
                // the caller passed a null settings (no-op for bundles, the
                // profile-overlay merge above still runs).
                if (settings != null && (WheelSleepMode >= 0 || WheelSleepTimeoutMin >= 0
                    || WheelSleepSpeedMs >= 0 || WheelSleepColor != null))
                {
                    if (settings.WheelSleepByPageGuid == null)
                        settings.WheelSleepByPageGuid = new Dictionary<Guid, WheelSleepSettings>();
                    if (!settings.WheelSleepByPageGuid.TryGetValue(pageGuid, out var bundle) || bundle == null)
                    {
                        bundle = new WheelSleepSettings();
                        settings.WheelSleepByPageGuid[pageGuid] = bundle;
                    }
                    if (bundle.Mode       < 0 && WheelSleepMode       >= 0) bundle.Mode       = WheelSleepMode;
                    if (bundle.TimeoutMin < 0 && WheelSleepTimeoutMin >= 0) bundle.TimeoutMin = WheelSleepTimeoutMin;
                    if (bundle.SpeedMs    < 0 && WheelSleepSpeedMs    >= 0) bundle.SpeedMs    = WheelSleepSpeedMs;
                    if (bundle.Color   == null && WheelSleepColor    != null) bundle.Color    = (int[])WheelSleepColor.Clone();
                }

                // Idle effect/speed bundle is also per-wheel-page (schema v9).
                // Same fill-only semantics as the sleep bundle above.
                if (settings != null && (WheelIdleEffect >= 0 || WheelButtonsIdleEffect >= 0
                    || WheelKnobIdleEffect >= 0 || WheelTelemetryIdleSpeedMs >= 0
                    || WheelButtonsIdleSpeedMs >= 0 || WheelKnobIdleSpeedMs >= 0))
                {
                    if (settings.WheelIdleByPageGuid == null)
                        settings.WheelIdleByPageGuid = new Dictionary<Guid, WheelIdleSettings>();
                    if (!settings.WheelIdleByPageGuid.TryGetValue(pageGuid, out var idle) || idle == null)
                    {
                        idle = new WheelIdleSettings();
                        settings.WheelIdleByPageGuid[pageGuid] = idle;
                    }
                    if (idle.TelemetryEffect  < 0 && WheelIdleEffect            >= 0) idle.TelemetryEffect  = WheelIdleEffect;
                    if (idle.ButtonsEffect    < 0 && WheelButtonsIdleEffect     >= 0) idle.ButtonsEffect    = WheelButtonsIdleEffect;
                    if (idle.KnobEffect       < 0 && WheelKnobIdleEffect        >= 0) idle.KnobEffect       = WheelKnobIdleEffect;
                    if (idle.TelemetrySpeedMs < 0 && WheelTelemetryIdleSpeedMs  >= 0) idle.TelemetrySpeedMs = WheelTelemetryIdleSpeedMs;
                    if (idle.ButtonsSpeedMs   < 0 && WheelButtonsIdleSpeedMs    >= 0) idle.ButtonsSpeedMs   = WheelButtonsIdleSpeedMs;
                    if (idle.KnobSpeedMs      < 0 && WheelKnobIdleSpeedMs       >= 0) idle.KnobSpeedMs      = WheelKnobIdleSpeedMs;
                }
            }

            // Mirror colors into _data so the UI's swatches reflect the loaded
            // values immediately. Hardware writes flow through ApplyWheelToHardware
            // which sources from the overlay we just populated.
            MozaProfile.UnpackColorsInto(WheelRpmColors, data.WheelRpmColors);
            MozaProfile.UnpackColorsInto(WheelRpmBlinkColors, data.WheelRpmBlinkColors);
            MozaProfile.UnpackColorsInto(WheelButtonColors, data.WheelButtonColors);
            if (WheelButtonDefaultDuringTelemetry != null)
            {
                int n = Math.Min(WheelButtonDefaultDuringTelemetry.Length, data.WheelButtonDefaultDuringTelemetry.Length);
                for (int i = 0; i < n; i++)
                    data.WheelButtonDefaultDuringTelemetry[i] = WheelButtonDefaultDuringTelemetry[i];
            }
            MozaProfile.UnpackColorsInto(WheelFlagColors, data.WheelFlagColors);
            if (WheelIdleColor != null && WheelIdleColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(WheelIdleColor[0]);
                Array.Copy(rgb, data.WheelIdleColor, 3);
            }
            MozaProfile.UnpackColorsInto(WheelESRpmColors, data.WheelESRpmColors);
            MozaProfile.UnpackColorsInto(WheelKnobBackgroundColors, data.WheelKnobBackgroundColors);
            MozaProfile.UnpackColorsInto(WheelKnobPrimaryColors, data.WheelKnobPrimaryColors);
            MozaProfile.UnpackColorsInto(WheelKnobRingColors, data.KnobRingColors);
            if (WheelKnobRingBrightness >= 0) data.KnobRingBrightness = WheelKnobRingBrightness;
            if (WheelKnobDefaultDuringTelemetry.HasValue)
                data.WheelKnobDefaultDuringTelemetry = WheelKnobDefaultDuringTelemetry.Value;
            if (WheelKnobStaticTimeoutMs >= 0) data.WheelKnobStaticTimeoutMs = WheelKnobStaticTimeoutMs;

            // Migration done. Stamp the plugin-side flag so every subsequent
            // SetSettings on any wheel extension skips this method entirely.
            // The plugin's debounce + End() flush will persist this reliably.
            if (settings != null) settings.WheelExtensionDrained = true;
        }

        /// <summary>
        /// Resolve the SimHub page GUID from a wheel-model prefix (or empty for
        /// the generic wheel page). Returns false for unknown / unresolvable
        /// prefixes; callers should fall back to the legacy slot/flat path.
        /// </summary>
        private static bool TryGetPageGuid(string modelPrefix, out Guid guid)
        {
            guid = Guid.Empty;
            if (modelPrefix == null) return false;
            // Empty prefix = generic wheel page
            if (modelPrefix.Length == 0)
                return Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out guid);
            if (modelPrefix == MozaDeviceConstants.OldProtocolMarker)
                return Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out guid);
            var s = MozaDeviceConstants.ResolveWheelGuid(modelPrefix);
            return Guid.TryParse(s, out guid);
        }

        /// <summary>
        /// Copy non-sentinel fields from this DTO into the given overlay.
        /// FILL-ONLY: a DTO value only writes into the overlay when the overlay
        /// field is still at its sentinel. The overlay (under the profile in
        /// MozaPluginSettings) is loaded before SetSettings fires, so the
        /// user's most recent value is already in place when this runs — and
        /// the device JSON can lag behind (SimHub doesn't always call
        /// GetSettings before shutdown). Unconditional overwrites here were
        /// the cause of user picks (sleep timeout, RPM colors, etc.) randomly
        /// vanishing across restarts.
        /// </summary>
        private void MergeIntoOverlay(WheelOverride ov)
        {
            if (ov.WheelTelemetryMode      < 0 && WheelTelemetryMode      >= 0) ov.WheelTelemetryMode      = WheelTelemetryMode;
            if (ov.WheelKnobLedMode        < 0 && WheelKnobLedMode        >= 0) ov.WheelKnobLedMode        = WheelKnobLedMode;
            if (ov.WheelButtonsLedMode     < 0 && WheelButtonsLedMode     >= 0) ov.WheelButtonsLedMode     = WheelButtonsLedMode;
            // WheelSleep* are no longer on the overlay — handled by the
            // ApplyTo caller into MozaPluginSettings.WheelSleepByPageGuid.
            // WheelIdleEffect / WheelButtonsIdleEffect / WheelKnobIdleEffect /
            // WheelTelemetryIdleSpeedMs / WheelButtonsIdleSpeedMs /
            // WheelKnobIdleSpeedMs are also no longer on the overlay (schema v9)
            // — handled into MozaPluginSettings.WheelIdleByPageGuid.
            if (ov.WheelRpmBrightness      < 0 && WheelRpmBrightness      >= 0) ov.WheelRpmBrightness      = WheelRpmBrightness;
            if (ov.WheelButtonsBrightness  < 0 && WheelButtonsBrightness  >= 0) ov.WheelButtonsBrightness  = WheelButtonsBrightness;
            if (ov.WheelFlagsBrightness    < 0 && WheelFlagsBrightness    >= 0) ov.WheelFlagsBrightness    = WheelFlagsBrightness;
            if (ov.WheelESRpmBrightness    < 0 && WheelESRpmBrightness    >= 0) ov.WheelESRpmBrightness    = WheelESRpmBrightness;
            if (ov.WheelRpmIndicatorMode   < 0 && WheelRpmIndicatorMode   >= 0) ov.WheelRpmIndicatorMode   = WheelRpmIndicatorMode;
            if (ov.WheelRpmDisplayMode     < 0 && WheelRpmDisplayMode     >= 0) ov.WheelRpmDisplayMode     = WheelRpmDisplayMode;
            if (ov.WheelRpmColors       == null && WheelRpmColors          != null) ov.WheelRpmColors       = (int[])WheelRpmColors.Clone();
            if (ov.WheelRpmBlinkColors  == null && WheelRpmBlinkColors     != null) ov.WheelRpmBlinkColors  = (int[])WheelRpmBlinkColors.Clone();
            if (ov.WheelButtonColors    == null && WheelButtonColors       != null) ov.WheelButtonColors    = (int[])WheelButtonColors.Clone();
            if (ov.WheelButtonDefaultDuringTelemetry == null && WheelButtonDefaultDuringTelemetry != null)
                ov.WheelButtonDefaultDuringTelemetry = (bool[])WheelButtonDefaultDuringTelemetry.Clone();
            if (ov.WheelFlagColors      == null && WheelFlagColors         != null) ov.WheelFlagColors      = (int[])WheelFlagColors.Clone();
            if (ov.WheelIdleColor       == null && WheelIdleColor          != null) ov.WheelIdleColor       = (int[])WheelIdleColor.Clone();
            if (ov.WheelESRpmColors     == null && WheelESRpmColors        != null) ov.WheelESRpmColors     = (int[])WheelESRpmColors.Clone();
            if (ov.WheelKnobBackgroundColors == null && WheelKnobBackgroundColors != null)
                ov.WheelKnobBackgroundColors = (int[])WheelKnobBackgroundColors.Clone();
            if (ov.WheelKnobPrimaryColors    == null && WheelKnobPrimaryColors    != null)
                ov.WheelKnobPrimaryColors    = (int[])WheelKnobPrimaryColors.Clone();
            if (ov.WheelKnobRingColors       == null && WheelKnobRingColors       != null)
                ov.WheelKnobRingColors       = (int[])WheelKnobRingColors.Clone();
            if (ov.WheelKnobRingBrightness   < 0 && WheelKnobRingBrightness >= 0) ov.WheelKnobRingBrightness = WheelKnobRingBrightness;
            if (ov.WheelKnobDefaultDuringTelemetry == null && WheelKnobDefaultDuringTelemetry != null)
                ov.WheelKnobDefaultDuringTelemetry = WheelKnobDefaultDuringTelemetry;
            if (ov.WheelKnobStaticTimeoutMs < 0 && WheelKnobStaticTimeoutMs >= 0)
                ov.WheelKnobStaticTimeoutMs = WheelKnobStaticTimeoutMs;
        }

        /// <summary>
        /// Populate this DTO from an overlay + per-page sleep bundle — the
        /// reverse of MergeIntoOverlay (plus the per-page dict write). Used
        /// by GetSettings so SimHub's persisted device-page JSON reflects
        /// the live state.
        /// </summary>
        internal void CaptureFromOverlay(WheelOverride ov, MozaData data,
            WheelSleepSettings? sleep = null, WheelIdleSettings? idle = null)
        {
            WheelModelName = data.WheelModelName ?? "";
            WheelTelemetryMode      = ov.WheelTelemetryMode;
            WheelKnobLedMode        = ov.WheelKnobLedMode;
            WheelButtonsLedMode     = ov.WheelButtonsLedMode;
            // WheelSleep* + WheelIdle*/Speed* now live on per-page dicts in
            // MozaPluginSettings — populated here from the optional bundles
            // the caller looked up by the same page GUID. SimHub-side device
            // JSON still includes them so users with multiple wheels round-trip
            // correctly when they un-/re-install.
            if (sleep != null)
            {
                WheelSleepMode       = sleep.Mode;
                WheelSleepTimeoutMin = sleep.TimeoutMin;
                WheelSleepSpeedMs    = sleep.SpeedMs;
                WheelSleepColor      = sleep.Color;
            }
            if (idle != null)
            {
                WheelIdleEffect            = idle.TelemetryEffect;
                WheelButtonsIdleEffect     = idle.ButtonsEffect;
                WheelKnobIdleEffect        = idle.KnobEffect;
                WheelTelemetryIdleSpeedMs  = idle.TelemetrySpeedMs;
                WheelButtonsIdleSpeedMs    = idle.ButtonsSpeedMs;
                WheelKnobIdleSpeedMs       = idle.KnobSpeedMs;
            }
            WheelRpmBrightness      = ov.WheelRpmBrightness;
            WheelButtonsBrightness  = ov.WheelButtonsBrightness;
            WheelFlagsBrightness    = ov.WheelFlagsBrightness;
            WheelESRpmBrightness    = ov.WheelESRpmBrightness;
            WheelRpmIndicatorMode   = ov.WheelRpmIndicatorMode;
            WheelRpmDisplayMode     = ov.WheelRpmDisplayMode;
            TelemetrySettingsPresent = true;
            WheelRpmColors          = ov.WheelRpmColors;
            WheelRpmBlinkColors     = ov.WheelRpmBlinkColors;
            WheelButtonColors       = ov.WheelButtonColors;
            WheelButtonDefaultDuringTelemetry = ov.WheelButtonDefaultDuringTelemetry;
            WheelFlagColors         = ov.WheelFlagColors;
            WheelIdleColor          = ov.WheelIdleColor;
            WheelESRpmColors        = ov.WheelESRpmColors;
            WheelKnobBackgroundColors = ov.WheelKnobBackgroundColors;
            WheelKnobPrimaryColors    = ov.WheelKnobPrimaryColors;
            WheelKnobRingColors       = ov.WheelKnobRingColors;
            WheelKnobRingBrightness = ov.WheelKnobRingBrightness;
            WheelKnobDefaultDuringTelemetry = ov.WheelKnobDefaultDuringTelemetry;
            WheelKnobStaticTimeoutMs = ov.WheelKnobStaticTimeoutMs;
        }
    }
}
