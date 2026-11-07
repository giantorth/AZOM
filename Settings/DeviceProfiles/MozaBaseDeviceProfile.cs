using System.Windows.Controls;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// One wheelbase model's ambient-LED settings per game, in that base page's own
    /// profile store (<see cref="DeviceProfileRegistry.Bases"/>, keyed by the base
    /// page GUID). Base FFB stays on the main <see cref="MozaProfile"/>.
    /// -1 / null = leave the base's stored value alone.
    /// </summary>
    public class MozaBaseDeviceProfile
        : ProfileBase<MozaBaseDeviceProfile, MozaBaseDeviceProfileStore>,
          IProfile, IProfile<MozaBaseDeviceProfile, MozaBaseDeviceProfileStore>
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        public int BaseAmbientBrightness { get; set; } = -1;
        public int BaseAmbientStandbyMode { get; set; } = -1;
        public int BaseAmbientIndicatorState { get; set; } = -1;
        public int BaseAmbientSleepMode { get; set; } = -1;
        public int BaseAmbientSleepTimeout { get; set; } = -1;
        public int BaseAmbientStartupColor { get; set; } = -1;   // packed RGB
        public int BaseAmbientShutdownColor { get; set; } = -1;  // packed RGB

        // Per-LED palettes, stride BaseModelInfo.MaxLedsPerStrip (see MozaProfile).
        public int[]? BaseAmbientIdleColorsConstant { get; set; }
        public int[]? BaseAmbientIdleColorsBreath { get; set; }
        public int[]? BaseAmbientSleepColors { get; set; }

        // Indexed by standby mode 0..5 (only 2..5 have a register).
        public int[]? BaseAmbientStandbyIntervals { get; set; }
        public int BaseAmbientSleepBreathInterval { get; set; } = -1;  // ms

        public override void CopyProfilePropertiesFrom(MozaBaseDeviceProfile p)
        {
            if (p == null) return;
            BaseAmbientBrightness = p.BaseAmbientBrightness;
            BaseAmbientStandbyMode = p.BaseAmbientStandbyMode;
            BaseAmbientIndicatorState = p.BaseAmbientIndicatorState;
            BaseAmbientSleepMode = p.BaseAmbientSleepMode;
            BaseAmbientSleepTimeout = p.BaseAmbientSleepTimeout;
            BaseAmbientStartupColor = p.BaseAmbientStartupColor;
            BaseAmbientShutdownColor = p.BaseAmbientShutdownColor;
            BaseAmbientIdleColorsConstant = DeviceProfileCopy.Arr(p.BaseAmbientIdleColorsConstant);
            BaseAmbientIdleColorsBreath = DeviceProfileCopy.Arr(p.BaseAmbientIdleColorsBreath);
            BaseAmbientSleepColors = DeviceProfileCopy.Arr(p.BaseAmbientSleepColors);
            BaseAmbientStandbyIntervals = DeviceProfileCopy.Arr(p.BaseAmbientStandbyIntervals);
            BaseAmbientSleepBreathInterval = p.BaseAmbientSleepBreathInterval;
        }

        /// <summary>Fill sentinels from the plugin-wide defaults. Idempotent.</summary>
        public void SeedBaselineFromFlatFields(MozaPluginSettings settings)
        {
            if (settings == null) return;
            if (BaseAmbientBrightness     < 0) BaseAmbientBrightness     = settings.BaseAmbientBrightness;
            if (BaseAmbientStandbyMode    < 0) BaseAmbientStandbyMode    = settings.BaseAmbientStandbyMode;
            if (BaseAmbientIndicatorState < 0) BaseAmbientIndicatorState = settings.BaseAmbientIndicatorState;
            if (BaseAmbientSleepMode      < 0) BaseAmbientSleepMode      = settings.BaseAmbientSleepMode;
            if (BaseAmbientSleepTimeout   < 0) BaseAmbientSleepTimeout   = settings.BaseAmbientSleepTimeout;
            if (BaseAmbientStartupColor   < 0) BaseAmbientStartupColor   = settings.BaseAmbientStartupColor;
            if (BaseAmbientShutdownColor  < 0) BaseAmbientShutdownColor  = settings.BaseAmbientShutdownColor;
        }
    }

    public class MozaBaseDeviceProfileStore
        : ActiveProfileTrackingStore<MozaBaseDeviceProfile, MozaBaseDeviceProfileStore>,
          IProfileSettings<MozaBaseDeviceProfile>, IProfileSettings
    {
        public override string FileFilter => "Moza wheelbase profile (*.shmozabase)|*.shmozabase";

        public override void InitProfile(MozaBaseDeviceProfile p) { }
    }
}
