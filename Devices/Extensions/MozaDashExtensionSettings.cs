using System;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Dashboard-specific settings stored in SimHub device profiles.
    /// Serialized to/from JSON via GetSettings()/SetSettings() on the device extension.
    /// Uses -1 sentinel for "not included" (same convention as MozaProfile).
    /// Colors packed as R&lt;&lt;16 | G&lt;&lt;8 | B.
    /// </summary>
    public class MozaDashExtensionSettings
    {
        // Brightness (0-100)
        public int DashRpmBrightness { get; set; } = -1;
        public int DashFlagsBrightness { get; set; } = -1;
        public int DashDisplayBrightness { get; set; } = -1;
        public int DashDisplayStandbyMin { get; set; } = -1;

        // Indicator/display modes
        public int DashRpmIndicatorMode { get; set; } = -1;
        public int DashFlagsIndicatorMode { get; set; } = -1;
        public int DashRpmDisplayMode { get; set; } = -1;

        // Color arrays (packed as R<<16 | G<<8 | B)
        public int[]? DashRpmColors { get; set; }
        public int[]? DashRpmBlinkColors { get; set; }
        public int[]? DashFlagColors { get; set; }

        /// <summary>
        /// Capture current dash state from the plugin. When <paramref name="profile"/>
        /// is provided, capture from the profile (the new R5 source of truth)
        /// rather than the flat-field path.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data,
            MozaProfile? profile = null)
        {
            if (!data.BaseSettingsRead) return;

            if (profile != null)
            {
                if (profile.DashRpmBrightness     >= 0) DashRpmBrightness     = profile.DashRpmBrightness;
                else                                    DashRpmBrightness     = settings.DashRpmBrightness;
                if (profile.DashFlagsBrightness   >= 0) DashFlagsBrightness   = profile.DashFlagsBrightness;
                else                                    DashFlagsBrightness   = settings.DashFlagsBrightness;
                if (profile.DashDisplayBrightness >= 0) DashDisplayBrightness = profile.DashDisplayBrightness;
                else                                    DashDisplayBrightness = settings.DashDisplayBrightness;
                if (profile.DashDisplayStandbyMin >= 0) DashDisplayStandbyMin = profile.DashDisplayStandbyMin;
                else                                    DashDisplayStandbyMin = settings.DashDisplayStandbyMin;
                DashRpmColors      = profile.DashRpmColors      ?? MozaProfile.PackColors(data.DashRpmColors);
                DashFlagColors     = profile.DashFlagColors     ?? MozaProfile.PackColors(data.DashFlagColors);
                DashRpmBlinkColors = profile.DashRpmBlinkColors ?? settings.DashRpmBlinkColors;
            }
            else
            {
                DashRpmBrightness = settings.DashRpmBrightness;
                DashFlagsBrightness = settings.DashFlagsBrightness;
                DashDisplayBrightness = settings.DashDisplayBrightness;
                DashDisplayStandbyMin = settings.DashDisplayStandbyMin;
                DashRpmColors = MozaProfile.PackColors(data.DashRpmColors);
                DashRpmBlinkColors = settings.DashRpmBlinkColors;
                DashFlagColors = MozaProfile.PackColors(data.DashFlagColors);
            }
            DashRpmIndicatorMode = data.DashRpmIndicatorMode;
            DashFlagsIndicatorMode = data.DashFlagsIndicatorMode;
            DashRpmDisplayMode = data.DashRpmDisplayMode;
        }

        /// <summary>
        /// Apply these settings into the active profile (single source of truth)
        /// and mirror colors / indicator modes into <paramref name="data"/> so the
        /// UI sees them immediately. Does NOT write to hardware — caller routes
        /// through <c>ApplyDashToHardware</c> after this.
        /// </summary>
        public void ApplyTo(MozaPluginSettings settings, MozaData data, MozaProfile? profile = null)
        {
            // Indicator/display modes are device-read state — UI binds to _data.
            if (DashRpmIndicatorMode   >= 0) data.DashRpmIndicatorMode   = DashRpmIndicatorMode;
            if (DashFlagsIndicatorMode >= 0) data.DashFlagsIndicatorMode = DashFlagsIndicatorMode;
            if (DashRpmDisplayMode     >= 0) data.DashRpmDisplayMode     = DashRpmDisplayMode;

            // Colors → _data for swatch display, profile for persistence.
            MozaProfile.UnpackColorsInto(DashRpmColors,      data.DashRpmColors);
            MozaProfile.UnpackColorsInto(DashRpmBlinkColors, data.DashRpmBlinkColors);
            MozaProfile.UnpackColorsInto(DashFlagColors,     data.DashFlagColors);

            // Brightness / colors → profile (no flat-field mirror; the profile is
            // the only persisted store).
            if (profile == null) return;
            if (DashRpmBrightness     >= 0) profile.DashRpmBrightness     = DashRpmBrightness;
            if (DashFlagsBrightness   >= 0) profile.DashFlagsBrightness   = DashFlagsBrightness;
            if (DashDisplayBrightness >= 0) profile.DashDisplayBrightness = DashDisplayBrightness;
            if (DashDisplayStandbyMin >= 0) profile.DashDisplayStandbyMin = DashDisplayStandbyMin;
            if (DashRpmColors      != null) profile.DashRpmColors      = (int[])DashRpmColors.Clone();
            if (DashRpmBlinkColors != null) profile.DashRpmBlinkColors = (int[])DashRpmBlinkColors.Clone();
            if (DashFlagColors     != null) profile.DashFlagColors     = (int[])DashFlagColors.Clone();

            // _data brightness mirror for UI feedback (apply path will refresh too).
            if (DashRpmBrightness     >= 0) data.DashRpmBrightness     = DashRpmBrightness;
            if (DashFlagsBrightness   >= 0) data.DashFlagsBrightness   = DashFlagsBrightness;
            if (DashDisplayBrightness >= 0) data.DashDisplayBrightness = DashDisplayBrightness;
            if (DashDisplayStandbyMin >= 0) data.DashDisplayStandbyMin = DashDisplayStandbyMin;
        }
    }
}
