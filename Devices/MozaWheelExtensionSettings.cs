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
                // Sleep-light bundle is keyed by the same page GUID on the
                // shared MozaPluginSettings dict (per-wheel-page, not per-game).
                WheelSleepSettings? sleep = null;
                if (settings.WheelSleepByPageGuid != null)
                    settings.WheelSleepByPageGuid.TryGetValue(pageGuid, out sleep);
                CaptureFromOverlay(ov, data, sleep);
                return;
            }

            WheelModelName = data.WheelModelName ?? "";
            WheelTelemetryMode = settings.WheelTelemetryMode;
            WheelIdleEffect = settings.WheelIdleEffect;
            WheelButtonsIdleEffect = settings.WheelButtonsIdleEffect;
            WheelKnobIdleEffect = settings.WheelKnobIdleEffect;
            WheelKnobLedMode = settings.WheelKnobLedMode;
            WheelButtonsLedMode = settings.WheelButtonsLedMode;
            WheelTelemetryIdleSpeedMs = settings.WheelTelemetryIdleSpeedMs;
            WheelButtonsIdleSpeedMs = settings.WheelButtonsIdleSpeedMs;
            WheelKnobIdleSpeedMs = settings.WheelKnobIdleSpeedMs;
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
                // lives on MozaPluginSettings.WheelSleepByPageGuid. Merge any
                // non-sentinel values from the DTO into that dict keyed by the
                // same page GUID we used for the overlay.
                if (WheelSleepMode >= 0 || WheelSleepTimeoutMin >= 0
                    || WheelSleepSpeedMs >= 0 || WheelSleepColor != null)
                {
                    if (settings.WheelSleepByPageGuid == null)
                        settings.WheelSleepByPageGuid = new Dictionary<Guid, WheelSleepSettings>();
                    if (!settings.WheelSleepByPageGuid.TryGetValue(pageGuid, out var bundle) || bundle == null)
                    {
                        bundle = new WheelSleepSettings();
                        settings.WheelSleepByPageGuid[pageGuid] = bundle;
                    }
                    if (WheelSleepMode       >= 0)   bundle.Mode       = WheelSleepMode;
                    if (WheelSleepTimeoutMin >= 0)   bundle.TimeoutMin = WheelSleepTimeoutMin;
                    if (WheelSleepSpeedMs    >= 0)   bundle.SpeedMs    = WheelSleepSpeedMs;
                    if (WheelSleepColor      != null) bundle.Color     = (int[])WheelSleepColor.Clone();
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
        /// Mirrors the legacy slot-write path inside ApplyTo.
        /// </summary>
        private void MergeIntoOverlay(WheelOverride ov)
        {
            if (WheelTelemetryMode      >= 0) ov.WheelTelemetryMode      = WheelTelemetryMode;
            if (WheelIdleEffect         >= 0) ov.WheelIdleEffect         = WheelIdleEffect;
            if (WheelButtonsIdleEffect  >= 0) ov.WheelButtonsIdleEffect  = WheelButtonsIdleEffect;
            if (WheelKnobIdleEffect     >= 0) ov.WheelKnobIdleEffect     = WheelKnobIdleEffect;
            if (WheelKnobLedMode        >= 0) ov.WheelKnobLedMode        = WheelKnobLedMode;
            if (WheelButtonsLedMode     >= 0) ov.WheelButtonsLedMode     = WheelButtonsLedMode;
            if (WheelTelemetryIdleSpeedMs >= 0) ov.WheelTelemetryIdleSpeedMs = WheelTelemetryIdleSpeedMs;
            if (WheelButtonsIdleSpeedMs   >= 0) ov.WheelButtonsIdleSpeedMs   = WheelButtonsIdleSpeedMs;
            if (WheelKnobIdleSpeedMs      >= 0) ov.WheelKnobIdleSpeedMs      = WheelKnobIdleSpeedMs;
            // WheelSleep* are no longer on the overlay — handled by the
            // ApplyTo caller into MozaPluginSettings.WheelSleepByPageGuid.
            if (WheelRpmBrightness      >= 0) ov.WheelRpmBrightness      = WheelRpmBrightness;
            if (WheelButtonsBrightness  >= 0) ov.WheelButtonsBrightness  = WheelButtonsBrightness;
            if (WheelFlagsBrightness    >= 0) ov.WheelFlagsBrightness    = WheelFlagsBrightness;
            if (WheelESRpmBrightness    >= 0) ov.WheelESRpmBrightness    = WheelESRpmBrightness;
            if (WheelRpmIndicatorMode   >= 0) ov.WheelRpmIndicatorMode   = WheelRpmIndicatorMode;
            if (WheelRpmDisplayMode     >= 0) ov.WheelRpmDisplayMode     = WheelRpmDisplayMode;
            if (WheelRpmColors          != null) ov.WheelRpmColors       = (int[])WheelRpmColors.Clone();
            if (WheelRpmBlinkColors     != null) ov.WheelRpmBlinkColors  = (int[])WheelRpmBlinkColors.Clone();
            if (WheelButtonColors       != null) ov.WheelButtonColors    = (int[])WheelButtonColors.Clone();
            if (WheelButtonDefaultDuringTelemetry != null)
                ov.WheelButtonDefaultDuringTelemetry = (bool[])WheelButtonDefaultDuringTelemetry.Clone();
            if (WheelFlagColors         != null) ov.WheelFlagColors      = (int[])WheelFlagColors.Clone();
            if (WheelIdleColor          != null) ov.WheelIdleColor       = (int[])WheelIdleColor.Clone();
            if (WheelESRpmColors        != null) ov.WheelESRpmColors     = (int[])WheelESRpmColors.Clone();
            if (WheelKnobBackgroundColors != null) ov.WheelKnobBackgroundColors = (int[])WheelKnobBackgroundColors.Clone();
            if (WheelKnobPrimaryColors    != null) ov.WheelKnobPrimaryColors    = (int[])WheelKnobPrimaryColors.Clone();
            if (WheelKnobRingColors       != null) ov.WheelKnobRingColors       = (int[])WheelKnobRingColors.Clone();
            if (WheelKnobRingBrightness >= 0) ov.WheelKnobRingBrightness = WheelKnobRingBrightness;
        }

        /// <summary>
        /// Populate this DTO from an overlay + per-page sleep bundle — the
        /// reverse of MergeIntoOverlay (plus the per-page dict write). Used
        /// by GetSettings so SimHub's persisted device-page JSON reflects
        /// the live state.
        /// </summary>
        internal void CaptureFromOverlay(WheelOverride ov, MozaData data, WheelSleepSettings? sleep = null)
        {
            WheelModelName = data.WheelModelName ?? "";
            WheelTelemetryMode      = ov.WheelTelemetryMode;
            WheelIdleEffect         = ov.WheelIdleEffect;
            WheelButtonsIdleEffect  = ov.WheelButtonsIdleEffect;
            WheelKnobIdleEffect     = ov.WheelKnobIdleEffect;
            WheelKnobLedMode        = ov.WheelKnobLedMode;
            WheelButtonsLedMode     = ov.WheelButtonsLedMode;
            WheelTelemetryIdleSpeedMs = ov.WheelTelemetryIdleSpeedMs;
            WheelButtonsIdleSpeedMs   = ov.WheelButtonsIdleSpeedMs;
            WheelKnobIdleSpeedMs      = ov.WheelKnobIdleSpeedMs;
            // WheelSleep* live in MozaPluginSettings.WheelSleepByPageGuid now —
            // populated here from the optional bundle the caller looked up by
            // the same page GUID. SimHub-side device JSON still includes them
            // so users with multiple wheels round-trip correctly when they
            // un-/re-install.
            if (sleep != null)
            {
                WheelSleepMode       = sleep.Mode;
                WheelSleepTimeoutMin = sleep.TimeoutMin;
                WheelSleepSpeedMs    = sleep.SpeedMs;
                WheelSleepColor      = sleep.Color;
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
        }
    }
}
