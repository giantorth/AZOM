using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// One dash's per-game settings (CM2 or CM1), in that dash's own profile store
    /// (<see cref="DeviceProfileRegistry.Dashes"/>, keyed by
    /// <c>MozaPlugin.Cm2PageGuid</c> / <c>MozaPlugin.Cm1PageGuid</c>).
    /// -1 / null = leave the dash's stored value alone.
    /// </summary>
    public class MozaDashProfile
        : ProfileBase<MozaDashProfile, MozaDashProfileStore>,
          IProfile, IProfile<MozaDashProfile, MozaDashProfileStore>, IDashboardKeyProfile
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        public int DashRpmBrightness { get; set; } = -1;
        public int DashFlagsBrightness { get; set; } = -1;
        public int DashDisplayBrightness { get; set; } = -1;
        public int DashDisplayStandbyMin { get; set; } = -1;
        public int DashRpmIndicatorMode { get; set; } = -1;
        public int DashRpmDisplayMode { get; set; } = -1;
        public int DashFlagsIndicatorMode { get; set; } = -1;
        public int[]? DashRpmColors { get; set; }
        public int[]? DashRpmBlinkColors { get; set; }
        public int[]? DashFlagColors { get; set; }

        /// <summary>CM2 dashboard pick (screenless-wheel / no-wheel rigs drive it from here).</summary>
        public string? TelemetryDashboardKey { get; set; }

        /// <summary>CM2: dashboard key → channel URL → SimHub property path.</summary>
        public Dictionary<string, Dictionary<string, string>> ChannelMappings { get; set; }
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>CM1 group-0x35 fields: field id → mapping.</summary>
        public Dictionary<string, Fsr1FieldMapping> Cm1Mappings { get; set; }
            = new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase);

        public override void CopyProfilePropertiesFrom(MozaDashProfile p)
        {
            if (p == null) return;
            DashRpmBrightness = p.DashRpmBrightness;
            DashFlagsBrightness = p.DashFlagsBrightness;
            DashDisplayBrightness = p.DashDisplayBrightness;
            DashDisplayStandbyMin = p.DashDisplayStandbyMin;
            DashRpmIndicatorMode = p.DashRpmIndicatorMode;
            DashRpmDisplayMode = p.DashRpmDisplayMode;
            DashFlagsIndicatorMode = p.DashFlagsIndicatorMode;
            DashRpmColors = DeviceProfileCopy.Arr(p.DashRpmColors);
            DashRpmBlinkColors = DeviceProfileCopy.Arr(p.DashRpmBlinkColors);
            DashFlagColors = DeviceProfileCopy.Arr(p.DashFlagColors);
            TelemetryDashboardKey = p.TelemetryDashboardKey;
            ChannelMappings = DeviceProfileCopy.Mappings(p.ChannelMappings);
            Cm1Mappings = DeviceProfileCopy.FieldMap(p.Cm1Mappings);
        }

        /// <summary>Fill sentinels from the plugin-wide defaults. Idempotent; SimHub
        /// creates per-game profiles all-sentinel, which would leave the display dark.</summary>
        public void SeedBaselineFromFlatFields(MozaPluginSettings settings)
        {
            if (settings == null) return;
            if (DashRpmBrightness     < 0) DashRpmBrightness     = settings.DashRpmBrightness;
            if (DashFlagsBrightness   < 0) DashFlagsBrightness   = settings.DashFlagsBrightness;
            if (DashDisplayBrightness < 0) DashDisplayBrightness = settings.DashDisplayBrightness;
            if (DashDisplayStandbyMin < 0) DashDisplayStandbyMin = settings.DashDisplayStandbyMin;
            if (DashRpmBlinkColors == null && settings.DashRpmBlinkColors != null)
                DashRpmBlinkColors = (int[])settings.DashRpmBlinkColors.Clone();
        }
    }

    public class MozaDashProfileStore
        : ActiveProfileTrackingStore<MozaDashProfile, MozaDashProfileStore>,
          IProfileSettings<MozaDashProfile>, IProfileSettings
    {
        public override string FileFilter => "Moza dash profile (*.shmozadash)|*.shmozadash";

        public override void InitProfile(MozaDashProfile p) { }
    }
}
