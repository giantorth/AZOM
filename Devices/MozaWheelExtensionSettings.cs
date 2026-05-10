using System;

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
        /// Capture current wheel state from the plugin.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data)
        {
            if (!data.BaseSettingsRead) return;

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
        /// Apply these settings to the plugin's settings and data model.
        /// Does NOT write to hardware — caller is responsible for that.
        /// </summary>
        public void ApplyTo(MozaPluginSettings settings, MozaData data)
        {
            // Route wheel-model-scoped fields into the slot for this extension's
            // captured model rather than overwriting the active flat state.
            // - If WheelModelName matches the currently-connected wheel, also
            //   update the flat fields so hardware-write paths see the values.
            // - If WheelModelName is empty (legacy JSON), fall back to writing
            //   flat directly — matches pre-slot behaviour for single-wheel setups.
            string extModel = WheelModelName ?? "";
            string activeModel = data.WheelModelName ?? "";
            bool hasExtModel = !string.IsNullOrEmpty(extModel);
            bool activeMatches = hasExtModel &&
                string.Equals(extModel, activeModel, StringComparison.OrdinalIgnoreCase);
            bool writeFlat = !hasExtModel || activeMatches;

            if (hasExtModel)
            {
                var slot = settings.GetOrCreateSlot(extModel);
                if (WheelTelemetryMode     >= 0) slot.WheelTelemetryMode     = WheelTelemetryMode;
                if (WheelIdleEffect        >= 0) slot.WheelIdleEffect        = WheelIdleEffect;
                if (WheelButtonsIdleEffect >= 0) slot.WheelButtonsIdleEffect = WheelButtonsIdleEffect;
                if (WheelKnobIdleEffect    >= 0) slot.WheelKnobIdleEffect    = WheelKnobIdleEffect;
                if (WheelKnobLedMode       >= 0) slot.WheelKnobLedMode       = WheelKnobLedMode;
                if (WheelButtonsLedMode    >= 0) slot.WheelButtonsLedMode    = WheelButtonsLedMode;
                if (WheelTelemetryIdleSpeedMs >= 0) slot.WheelTelemetryIdleSpeedMs = WheelTelemetryIdleSpeedMs;
                if (WheelButtonsIdleSpeedMs   >= 0) slot.WheelButtonsIdleSpeedMs   = WheelButtonsIdleSpeedMs;
                if (WheelKnobIdleSpeedMs      >= 0) slot.WheelKnobIdleSpeedMs      = WheelKnobIdleSpeedMs;
                if (WheelSleepMode         >= 0) slot.WheelSleepMode          = WheelSleepMode;
                if (WheelSleepTimeoutMin   >= 0) slot.WheelSleepTimeoutMin    = WheelSleepTimeoutMin;
                if (WheelSleepSpeedMs      >= 0) slot.WheelSleepSpeedMs       = WheelSleepSpeedMs;
                if (WheelSleepColor        != null) slot.WheelSleepColor      = WheelSleepColor;
                if (WheelRpmBrightness     >= 0) slot.WheelRpmBrightness     = WheelRpmBrightness;
                if (WheelButtonsBrightness >= 0) slot.WheelButtonsBrightness = WheelButtonsBrightness;
                if (WheelFlagsBrightness   >= 0) slot.WheelFlagsBrightness   = WheelFlagsBrightness;
                if (WheelESRpmBrightness   >= 0) slot.WheelESRpmBrightness   = WheelESRpmBrightness;
                if (WheelRpmIndicatorMode  >= 0) slot.WheelRpmIndicatorMode  = WheelRpmIndicatorMode;
                if (WheelRpmDisplayMode    >= 0) slot.WheelRpmDisplayMode    = WheelRpmDisplayMode;
            }

            if (writeFlat)
            {
                if (WheelTelemetryMode     >= 0) settings.WheelTelemetryMode     = WheelTelemetryMode;
                if (WheelIdleEffect        >= 0) settings.WheelIdleEffect        = WheelIdleEffect;
                if (WheelButtonsIdleEffect >= 0) settings.WheelButtonsIdleEffect = WheelButtonsIdleEffect;
                if (WheelKnobIdleEffect    >= 0) settings.WheelKnobIdleEffect    = WheelKnobIdleEffect;
                if (WheelKnobLedMode       >= 0) settings.WheelKnobLedMode       = WheelKnobLedMode;
                if (WheelButtonsLedMode    >= 0) settings.WheelButtonsLedMode    = WheelButtonsLedMode;
                if (WheelTelemetryIdleSpeedMs >= 0) settings.WheelTelemetryIdleSpeedMs = WheelTelemetryIdleSpeedMs;
                if (WheelButtonsIdleSpeedMs   >= 0) settings.WheelButtonsIdleSpeedMs   = WheelButtonsIdleSpeedMs;
                if (WheelKnobIdleSpeedMs      >= 0) settings.WheelKnobIdleSpeedMs      = WheelKnobIdleSpeedMs;
                if (WheelSleepMode         >= 0) settings.WheelSleepMode          = WheelSleepMode;
                if (WheelSleepTimeoutMin   >= 0) settings.WheelSleepTimeoutMin    = WheelSleepTimeoutMin;
                if (WheelSleepSpeedMs      >= 0) settings.WheelSleepSpeedMs       = WheelSleepSpeedMs;
                if (WheelSleepColor        != null) settings.WheelSleepColor      = WheelSleepColor;
                if (WheelRpmBrightness     >= 0) settings.WheelRpmBrightness     = WheelRpmBrightness;
                if (WheelButtonsBrightness >= 0) settings.WheelButtonsBrightness = WheelButtonsBrightness;
                if (WheelFlagsBrightness   >= 0) settings.WheelFlagsBrightness   = WheelFlagsBrightness;
                if (WheelESRpmBrightness   >= 0) settings.WheelESRpmBrightness   = WheelESRpmBrightness;
                if (WheelRpmIndicatorMode  >= 0) settings.WheelRpmIndicatorMode  = WheelRpmIndicatorMode;
                if (WheelRpmDisplayMode    >= 0) settings.WheelRpmDisplayMode    = WheelRpmDisplayMode;
            }

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

            // Knob colours are wheel-model-scoped (only W17/W18 expose them) and the
            // wire is write-only, so the plugin-level persisted slot needs the same
            // values — mirror into settings (flat + slot when this extension owns the
            // active wheel model).
            if (hasExtModel)
            {
                var slot = settings.GetOrCreateSlot(extModel);
                slot.WheelKnobBackgroundColors = WheelKnobBackgroundColors;
                slot.WheelKnobPrimaryColors    = WheelKnobPrimaryColors;
                slot.WheelKnobRingColors       = WheelKnobRingColors;
                slot.WheelKnobRingBrightness   = WheelKnobRingBrightness;
            }
            if (writeFlat)
            {
                settings.WheelKnobBackgroundColors = WheelKnobBackgroundColors;
                settings.WheelKnobPrimaryColors    = WheelKnobPrimaryColors;
                settings.WheelKnobRingColors       = WheelKnobRingColors;
                settings.WheelKnobRingBrightness   = WheelKnobRingBrightness;
            }
        }
    }
}
