namespace MozaPlugin.Settings
{
    /// <summary>The wheel page's LED tabs that can be switched to global storage.</summary>
    public enum WheelLedGroup
    {
        Rpm = 0,
        Buttons = 1,
        Knobs = 2,
    }

    /// <summary>
    /// The LED fields the RPM / Buttons / Knobs tabs edit. Implemented by the wheel
    /// profile and by <see cref="WheelGlobalLedSettings"/>, so one editor can write
    /// to whichever store the tab's "Global settings" toggle selects.
    /// </summary>
    public interface IWheelLedFields
    {
        // RPM tab
        int WheelTelemetryMode { get; set; }
        int[]? WheelRpmColors { get; set; }
        int[]? WheelFlagColors { get; set; }
        // Buttons tab
        int WheelButtonsLedMode { get; set; }
        int[]? WheelButtonColors { get; set; }
        bool[]? WheelButtonDefaultDuringTelemetry { get; set; }
        // Knobs tab
        int WheelKnobLedMode { get; set; }
        bool? WheelKnobDefaultDuringTelemetry { get; set; }
        int WheelKnobStaticTimeoutMs { get; set; }
        int[]? WheelKnobPrimaryColors { get; set; }
        int[]? WheelKnobRingColors { get; set; }
    }

    /// <summary>
    /// One wheel page's LED settings shared by every profile, per tab, keyed by page
    /// GUID on <see cref="MozaPluginSettings.WheelGlobalLedByPageGuid"/>. A tab whose
    /// toggle is on reads and writes these instead of the active device profile; the
    /// toggles themselves live here too, never on a profile.
    /// </summary>
    public sealed class WheelGlobalLedSettings : IWheelLedFields
    {
        public bool RpmGlobal { get; set; }
        public bool ButtonsGlobal { get; set; }
        public bool KnobsGlobal { get; set; }

        // Set the first time a tab goes global, when it is seeded from the active
        // profile. Afterwards the global values persist across off/on.
        public bool RpmSeeded { get; set; }
        public bool ButtonsSeeded { get; set; }
        public bool KnobsSeeded { get; set; }

        public int WheelTelemetryMode { get; set; } = -1;
        public int[]? WheelRpmColors { get; set; }
        public int[]? WheelFlagColors { get; set; }
        public int WheelButtonsLedMode { get; set; } = -1;
        public int[]? WheelButtonColors { get; set; }
        public bool[]? WheelButtonDefaultDuringTelemetry { get; set; }
        public int WheelKnobLedMode { get; set; } = -1;
        public bool? WheelKnobDefaultDuringTelemetry { get; set; }
        public int WheelKnobStaticTimeoutMs { get; set; } = -1;
        public int[]? WheelKnobPrimaryColors { get; set; }
        public int[]? WheelKnobRingColors { get; set; }

        public bool IsGlobal(WheelLedGroup g) => g switch
        {
            WheelLedGroup.Rpm => RpmGlobal,
            WheelLedGroup.Buttons => ButtonsGlobal,
            _ => KnobsGlobal,
        };

        internal bool AnyGlobal => RpmGlobal || ButtonsGlobal || KnobsGlobal;

        internal void SetGlobal(WheelLedGroup g, bool on)
        {
            switch (g)
            {
                case WheelLedGroup.Rpm: RpmGlobal = on; break;
                case WheelLedGroup.Buttons: ButtonsGlobal = on; break;
                default: KnobsGlobal = on; break;
            }
        }

        internal bool IsSeeded(WheelLedGroup g) => g switch
        {
            WheelLedGroup.Rpm => RpmSeeded,
            WheelLedGroup.Buttons => ButtonsSeeded,
            _ => KnobsSeeded,
        };

        internal void MarkSeeded(WheelLedGroup g)
        {
            switch (g)
            {
                case WheelLedGroup.Rpm: RpmSeeded = true; break;
                case WheelLedGroup.Buttons: ButtonsSeeded = true; break;
                default: KnobsSeeded = true; break;
            }
        }

        /// <summary>Deep-copy one tab's fields between two stores.</summary>
        internal static void CopyGroup(IWheelLedFields from, IWheelLedFields to, WheelLedGroup g)
        {
            switch (g)
            {
                case WheelLedGroup.Rpm:
                    to.WheelTelemetryMode = from.WheelTelemetryMode;
                    to.WheelRpmColors = DeviceProfileCopy.Arr(from.WheelRpmColors);
                    to.WheelFlagColors = DeviceProfileCopy.Arr(from.WheelFlagColors);
                    break;
                case WheelLedGroup.Buttons:
                    to.WheelButtonsLedMode = from.WheelButtonsLedMode;
                    to.WheelButtonColors = DeviceProfileCopy.Arr(from.WheelButtonColors);
                    to.WheelButtonDefaultDuringTelemetry = from.WheelButtonDefaultDuringTelemetry != null
                        ? (bool[])from.WheelButtonDefaultDuringTelemetry.Clone() : null;
                    break;
                default:
                    to.WheelKnobLedMode = from.WheelKnobLedMode;
                    to.WheelKnobDefaultDuringTelemetry = from.WheelKnobDefaultDuringTelemetry;
                    to.WheelKnobStaticTimeoutMs = from.WheelKnobStaticTimeoutMs;
                    to.WheelKnobPrimaryColors = DeviceProfileCopy.Arr(from.WheelKnobPrimaryColors);
                    to.WheelKnobRingColors = DeviceProfileCopy.Arr(from.WheelKnobRingColors);
                    break;
            }
        }
    }
}
