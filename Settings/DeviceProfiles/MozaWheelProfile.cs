using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// One wheel's per-game settings, in that wheel page's own profile store
    /// (<see cref="MozaWheelProfileStore"/>, keyed by the wheel page GUID in
    /// <see cref="DeviceProfileRegistry.Wheels"/>).
    ///
    /// Field names match the retired <see cref="WheelOverride"/> overlay, flattened:
    /// there is no profile-wide baseline any more. Every scalar uses -1 (or null for
    /// arrays/strings) for "no opinion — leave the wheel's stored value alone".
    /// </summary>
    public class MozaWheelProfile
        : ProfileBase<MozaWheelProfile, MozaWheelProfileStore>,
          IProfile, IProfile<MozaWheelProfile, MozaWheelProfileStore>, IDashboardKeyProfile, IWheelLedFields
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        // LED / mode
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelKnobLedMode { get; set; } = -1;
        public int WheelButtonsLedMode { get; set; } = -1;

        // Brightness
        public int WheelRpmBrightness { get; set; } = -1;
        public int WheelButtonsBrightness { get; set; } = -1;
        public int WheelFlagsBrightness { get; set; } = -1;
        public int WheelESRpmBrightness { get; set; } = -1;

        // ES wheel
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;

        // Inputs (newer FW drops readback — the profile is the source of truth)
        public int WheelPaddlesMode { get; set; } = -1;
        public int WheelClutchPoint { get; set; } = -1;
        public int WheelKnobMode { get; set; } = -1;
        public int WheelStickMode { get; set; } = -1;
        public int[]? WheelKnobSignalModes { get; set; }

        // Colours (packed R<<16|G<<8|B; knob palettes are sparse, -1 = unset slot)
        public int[]? WheelRpmColors { get; set; }
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? WheelButtonColors { get; set; }
        public bool[]? WheelButtonDefaultDuringTelemetry { get; set; }
        public int[]? WheelFlagColors { get; set; }
        public int[]? WheelIdleColor { get; set; }
        public int[]? WheelESRpmColors { get; set; }
        public int[]? WheelKnobPrimaryColors { get; set; }
        public int[]? WheelKnobRingColors { get; set; }
        public int WheelKnobRingBrightness { get; set; } = -1;
        // null = off.
        public bool? WheelKnobDefaultDuringTelemetry { get; set; }
        // Knob static-hold restore timeout (ms). -1 = unset (applies as 0 = off).
        public int WheelKnobStaticTimeoutMs { get; set; } = -1;

        // Wheel screen
        public int DashDisplayBrightness { get; set; } = -1;
        public int DashDisplayStandbyMin { get; set; } = -1;
        // VGS display rotation (0=off, 1=smooth, 2=immediate).
        public int DashDisplayRotation { get; set; } = -1;

        // Dashboard pick for this game on this wheel.
        public string? TelemetryProfileName { get; set; }
        public string? TelemetryMzdashPath { get; set; }
        /// <summary>Same key format as <c>ChannelMappingCoordinator.GetActiveDashboardKeyCandidates</c>.</summary>
        public string? TelemetryDashboardKey { get; set; }

        /// <summary>Dashboard key → channel URL → SimHub property path.</summary>
        public Dictionary<string, Dictionary<string, string>> ChannelMappings { get; set; }
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>FSR V1 group-0x42 fields: record-type key → field id → mapping.</summary>
        public Dictionary<string, Dictionary<string, Fsr1FieldMapping>> Fsr1Mappings { get; set; }
            = new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(StringComparer.OrdinalIgnoreCase);

        public override void CopyProfilePropertiesFrom(MozaWheelProfile p)
        {
            if (p == null) return;
            WheelTelemetryMode = p.WheelTelemetryMode;
            WheelKnobLedMode = p.WheelKnobLedMode;
            WheelButtonsLedMode = p.WheelButtonsLedMode;
            WheelRpmBrightness = p.WheelRpmBrightness;
            WheelButtonsBrightness = p.WheelButtonsBrightness;
            WheelFlagsBrightness = p.WheelFlagsBrightness;
            WheelESRpmBrightness = p.WheelESRpmBrightness;
            WheelRpmIndicatorMode = p.WheelRpmIndicatorMode;
            WheelRpmDisplayMode = p.WheelRpmDisplayMode;
            WheelPaddlesMode = p.WheelPaddlesMode;
            WheelClutchPoint = p.WheelClutchPoint;
            WheelKnobMode = p.WheelKnobMode;
            WheelStickMode = p.WheelStickMode;
            WheelKnobSignalModes = DeviceProfileCopy.Arr(p.WheelKnobSignalModes);
            WheelRpmColors = DeviceProfileCopy.Arr(p.WheelRpmColors);
            WheelRpmBlinkColors = DeviceProfileCopy.Arr(p.WheelRpmBlinkColors);
            WheelButtonColors = DeviceProfileCopy.Arr(p.WheelButtonColors);
            WheelButtonDefaultDuringTelemetry = p.WheelButtonDefaultDuringTelemetry != null
                ? (bool[])p.WheelButtonDefaultDuringTelemetry.Clone() : null;
            WheelFlagColors = DeviceProfileCopy.Arr(p.WheelFlagColors);
            WheelIdleColor = DeviceProfileCopy.Arr(p.WheelIdleColor);
            WheelESRpmColors = DeviceProfileCopy.Arr(p.WheelESRpmColors);
            WheelKnobPrimaryColors = DeviceProfileCopy.Arr(p.WheelKnobPrimaryColors);
            WheelKnobRingColors = DeviceProfileCopy.Arr(p.WheelKnobRingColors);
            WheelKnobRingBrightness = p.WheelKnobRingBrightness;
            WheelKnobDefaultDuringTelemetry = p.WheelKnobDefaultDuringTelemetry;
            WheelKnobStaticTimeoutMs = p.WheelKnobStaticTimeoutMs;
            DashDisplayBrightness = p.DashDisplayBrightness;
            DashDisplayStandbyMin = p.DashDisplayStandbyMin;
            DashDisplayRotation = p.DashDisplayRotation;
            TelemetryProfileName = p.TelemetryProfileName;
            TelemetryMzdashPath = p.TelemetryMzdashPath;
            TelemetryDashboardKey = p.TelemetryDashboardKey;
            ChannelMappings = DeviceProfileCopy.Mappings(p.ChannelMappings);
            Fsr1Mappings = DeviceProfileCopy.Fsr1(p.Fsr1Mappings);
        }

        /// <summary>Fill the wheel-screen sentinels from the plugin-wide defaults, so a
        /// profile SimHub creates (all sentinel) still has a display brightness to show.</summary>
        public void SeedBaselineFromFlatFields(MozaPluginSettings settings)
        {
            if (settings == null) return;
            if (DashDisplayBrightness < 0) DashDisplayBrightness = settings.DashDisplayBrightness;
            if (DashDisplayStandbyMin < 0) DashDisplayStandbyMin = settings.DashDisplayStandbyMin;
            if (DashDisplayRotation   < 0) DashDisplayRotation   = settings.DashDisplayRotation;
        }
    }

    /// <summary>One wheel page's profile list. Its own file type so wheel profiles
    /// can't be imported into another device's store.</summary>
    public class MozaWheelProfileStore
        : ActiveProfileTrackingStore<MozaWheelProfile, MozaWheelProfileStore>,
          IProfileSettings<MozaWheelProfile>, IProfileSettings
    {
        public override string FileFilter => "Moza wheel profile (*.shmozawheel)|*.shmozawheel";

        public override void InitProfile(MozaWheelProfile p) { }
    }

    /// <summary>A device profile that records which dashboard a game uses.</summary>
    public interface IDashboardKeyProfile
    {
        string Name { get; }
        string? TelemetryDashboardKey { get; set; }
    }
}
