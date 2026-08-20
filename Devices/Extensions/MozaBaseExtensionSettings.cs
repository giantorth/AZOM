using MozaPlugin.Settings;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Wheel-base ambient LED settings stored in SimHub device profiles.
    /// Serialized to/from JSON via GetSettings()/SetSettings() on the device extension.
    /// Uses -1 sentinel for "not included" (same convention as MozaProfile / MozaDashExtensionSettings).
    /// Colors packed as R&lt;&lt;16 | G&lt;&lt;8 | B.
    /// </summary>
    public class MozaBaseExtensionSettings
    {
        public int BaseAmbientBrightness { get; set; } = -1;       // 0..255 wire range
        public int BaseAmbientStandbyMode { get; set; } = -1;      // 0=const, 1=?, 2=breath, 3=cycle, 4=rainbow, 5=flow
        public int BaseAmbientIndicatorState { get; set; } = -1;   // 0/1
        public int BaseAmbientSleepMode { get; set; } = -1;        // 0/1
        public int BaseAmbientSleepTimeout { get; set; } = -1;
        public int BaseAmbientStartupColor { get; set; } = -1;     // packed RGB
        public int BaseAmbientShutdownColor { get; set; } = -1;    // packed RGB

        /// <summary>
        /// Capture current base ambient state from the plugin. When
        /// <paramref name="profile"/> is provided, prefer profile values over
        /// the flat-field fallback — the new R5 source of truth.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data,
            MozaProfile? profile = null)
        {
            int Pick(int p, int s) => p >= 0 ? p : s;
            if (profile != null)
            {
                BaseAmbientBrightness     = Pick(profile.BaseAmbientBrightness,     settings.BaseAmbientBrightness);
                BaseAmbientStandbyMode    = Pick(profile.BaseAmbientStandbyMode,    settings.BaseAmbientStandbyMode);
                BaseAmbientIndicatorState = Pick(profile.BaseAmbientIndicatorState, settings.BaseAmbientIndicatorState);
                BaseAmbientSleepMode      = Pick(profile.BaseAmbientSleepMode,      settings.BaseAmbientSleepMode);
                BaseAmbientSleepTimeout   = Pick(profile.BaseAmbientSleepTimeout,   settings.BaseAmbientSleepTimeout);
                BaseAmbientStartupColor   = Pick(profile.BaseAmbientStartupColor,   settings.BaseAmbientStartupColor);
                BaseAmbientShutdownColor  = Pick(profile.BaseAmbientShutdownColor,  settings.BaseAmbientShutdownColor);
            }
            else
            {
                BaseAmbientBrightness = settings.BaseAmbientBrightness;
                BaseAmbientStandbyMode = settings.BaseAmbientStandbyMode;
                BaseAmbientIndicatorState = settings.BaseAmbientIndicatorState;
                BaseAmbientSleepMode = settings.BaseAmbientSleepMode;
                BaseAmbientSleepTimeout = settings.BaseAmbientSleepTimeout;
                BaseAmbientStartupColor = settings.BaseAmbientStartupColor;
                BaseAmbientShutdownColor = settings.BaseAmbientShutdownColor;
            }
        }

        /// <summary>
        /// Apply these settings into the active profile (single source of truth)
        /// and mirror into <paramref name="data"/> so the UI sees them immediately.
        /// Does NOT write to hardware — caller routes through
        /// <c>ApplyBaseAmbientToHardware</c> after this.
        /// </summary>
        public void ApplyTo(MozaPluginSettings settings, MozaData data, MozaProfile? profile = null)
        {
            if (BaseAmbientBrightness     >= 0) data.BaseAmbientBrightness     = BaseAmbientBrightness;
            if (BaseAmbientStandbyMode    >= 0) data.BaseAmbientStandbyMode    = BaseAmbientStandbyMode;
            if (BaseAmbientIndicatorState >= 0) data.BaseAmbientIndicatorState = BaseAmbientIndicatorState;
            if (BaseAmbientSleepMode      >= 0) data.BaseAmbientSleepMode      = BaseAmbientSleepMode;
            if (BaseAmbientSleepTimeout   >= 0) data.BaseAmbientSleepTimeout   = BaseAmbientSleepTimeout;
            if (BaseAmbientStartupColor   >= 0) UnpackColor(BaseAmbientStartupColor,  data.BaseAmbientStartupColor);
            if (BaseAmbientShutdownColor  >= 0) UnpackColor(BaseAmbientShutdownColor, data.BaseAmbientShutdownColor);

            if (profile == null) return;
            if (BaseAmbientBrightness     >= 0) profile.BaseAmbientBrightness     = BaseAmbientBrightness;
            if (BaseAmbientStandbyMode    >= 0) profile.BaseAmbientStandbyMode    = BaseAmbientStandbyMode;
            if (BaseAmbientIndicatorState >= 0) profile.BaseAmbientIndicatorState = BaseAmbientIndicatorState;
            if (BaseAmbientSleepMode      >= 0) profile.BaseAmbientSleepMode      = BaseAmbientSleepMode;
            if (BaseAmbientSleepTimeout   >= 0) profile.BaseAmbientSleepTimeout   = BaseAmbientSleepTimeout;
            if (BaseAmbientStartupColor   >= 0) profile.BaseAmbientStartupColor   = BaseAmbientStartupColor;
            if (BaseAmbientShutdownColor  >= 0) profile.BaseAmbientShutdownColor  = BaseAmbientShutdownColor;
        }

        private static void UnpackColor(int packed, byte[] dst)
        {
            dst[0] = (byte)((packed >> 16) & 0xFF);
            dst[1] = (byte)((packed >> 8) & 0xFF);
            dst[2] = (byte)(packed & 0xFF);
        }
    }
}
