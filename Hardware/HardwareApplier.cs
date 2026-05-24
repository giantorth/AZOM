using System;
using MozaPlugin.Devices;
using MozaPlugin.Settings;

namespace MozaPlugin.Hardware
{
    /// <summary>
    /// All hardware-side writes: Apply*ToHardware + the detection-gated WriteIf*
    /// family. Profile + per-page overlay are the source of truth; writes are
    /// detection-gated and sentinel-guarded.
    /// </summary>
    internal sealed class HardwareApplier
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly MozaDeviceManager _deviceManager;
        private readonly MozaAb9DeviceManager _ab9Manager;
        private readonly DeviceDetectionState _detectionState;

        public HardwareApplier(
            MozaPlugin plugin,
            MozaData data,
            MozaDeviceManager deviceManager,
            MozaAb9DeviceManager ab9Manager,
            DeviceDetectionState detectionState)
        {
            _plugin = plugin;
            _data = data;
            _deviceManager = deviceManager;
            _ab9Manager = ab9Manager;
            _detectionState = detectionState;
        }

        private static int Eff(int overlayVal, int baselineVal) =>
            overlayVal >= 0 ? overlayVal : baselineVal;

        private static int[]? EffArr(int[]? overlayArr, int[]? baselineArr) =>
            overlayArr ?? baselineArr;

        // ===== Apply*ToHardware =====

        /// <summary>Push wheel-scoped settings to the connected wheel. Mirrors to _data unconditionally; writes detection-gated.</summary>
        public void ApplyWheelToHardware(MozaProfile? profile)
        {
            if (profile == null) return;
            bool deviceLive = _data.IsConnected;

            var ov = _plugin.GetCurrentWheelOverlay(profile);

            int telemMode      = Eff(ov?.WheelTelemetryMode ?? -1, profile.WheelTelemetryMode);
            // Idle-effect / idle-speed bundle is per-wheel-page (schema v9); null
            // = leave wheel's value alone. These six fields used to live on the
            // per-game overlay + profile baseline; v9 promoted them to a
            // wheel-level bundle because the idle animation is a property of
            // the wheel, not the game.
            var idleBundle     = _plugin.ActiveWheelIdle;
            int idleEffect     = idleBundle?.TelemetryEffect ?? -1;
            int btnIdleEffect  = idleBundle?.ButtonsEffect ?? -1;
            int knobIdleEffect = idleBundle?.KnobEffect ?? -1;
            int idleSpeed      = idleBundle?.TelemetrySpeedMs ?? -1;
            int btnIdleSpeed   = idleBundle?.ButtonsSpeedMs ?? -1;
            int knobIdleSpeed  = idleBundle?.KnobSpeedMs ?? -1;
            int knobLedMode    = Eff(ov?.WheelKnobLedMode ?? -1, profile.WheelKnobLedMode);
            int btnLedMode     = Eff(ov?.WheelButtonsLedMode ?? -1, profile.WheelButtonsLedMode);
            // Sleep bundle is per-wheel-page (schema v8); null = leave wheel's value alone.
            var sleepBundle    = _plugin.ActiveWheelSleep;
            int sleepMode      = sleepBundle?.Mode ?? -1;
            int sleepTimeout   = sleepBundle?.TimeoutMin ?? -1;
            int sleepSpeed     = sleepBundle?.SpeedMs ?? -1;
            int[]? sleepColor  = sleepBundle?.Color;
            int rpmBri         = Eff(ov?.WheelRpmBrightness ?? -1, profile.WheelRpmBrightness);
            int btnBri         = Eff(ov?.WheelButtonsBrightness ?? -1, profile.WheelButtonsBrightness);
            int flagsBri       = Eff(ov?.WheelFlagsBrightness ?? -1, profile.WheelFlagsBrightness);
            int rpmInd         = Eff(ov?.WheelRpmIndicatorMode ?? -1, profile.WheelRpmIndicatorMode);
            int rpmDisp        = Eff(ov?.WheelRpmDisplayMode ?? -1, profile.WheelRpmDisplayMode);
            int esRpmBri       = Eff(ov?.WheelESRpmBrightness ?? -1, profile.WheelESRpmBrightness);
            // Inputs — overlay-only (no profile baseline).
            int paddles        = ov?.WheelPaddlesMode ?? -1;
            int clutchPoint    = ov?.WheelClutchPoint ?? -1;
            int knobMode       = ov?.WheelKnobMode ?? -1;
            int stickMode      = ov?.WheelStickMode ?? -1;
            int knobRingBri    = Eff(ov?.WheelKnobRingBrightness ?? -1, profile.WheelKnobRingBrightness);

            // _data mirror (UI binding).
            if (telemMode      >= 0) _data.WheelTelemetryMode      = telemMode;
            if (idleEffect     >= 0) _data.WheelTelemetryIdleEffect = idleEffect;
            if (btnIdleEffect  >= 0) _data.WheelButtonsIdleEffect  = btnIdleEffect;
            if (knobIdleEffect >= 0) _data.WheelKnobIdleEffect     = knobIdleEffect;
            if (knobLedMode    >= 0) _data.WheelKnobLedMode        = knobLedMode;
            if (btnLedMode     >= 0) _data.WheelButtonsLedMode     = btnLedMode;
            if (idleSpeed      >= 0) _data.WheelTelemetryIdleSpeedMs = idleSpeed;
            if (btnIdleSpeed   >= 0) _data.WheelButtonsIdleSpeedMs = btnIdleSpeed;
            if (knobIdleSpeed  >= 0) _data.WheelKnobIdleSpeedMs    = knobIdleSpeed;
            if (sleepMode      >= 0) _data.WheelIdleMode           = sleepMode;
            if (sleepTimeout   >= 0) _data.WheelIdleTimeout        = sleepTimeout;
            if (sleepSpeed     >= 0) _data.WheelIdleSpeed          = sleepSpeed;
            if (sleepColor != null && sleepColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(sleepColor[0]);
                _data.WheelIdleColor[0] = rgb[0];
                _data.WheelIdleColor[1] = rgb[1];
                _data.WheelIdleColor[2] = rgb[2];
            }
            if (rpmBri    >= 0) _data.WheelRpmBrightness     = rpmBri;
            if (btnBri    >= 0) _data.WheelButtonsBrightness = btnBri;
            if (flagsBri  >= 0) _data.WheelFlagsBrightness   = flagsBri;
            if (esRpmBri  >= 0) _data.WheelESRpmBrightness   = esRpmBri;
            if (rpmInd    >= 0) _data.WheelRpmIndicatorMode  = rpmInd;
            if (rpmDisp   >= 0) _data.WheelRpmDisplayMode    = rpmDisp;
            if (paddles   >= 0) _data.WheelPaddlesMode       = paddles;
            if (clutchPoint >= 0) _data.WheelClutchPoint     = clutchPoint;
            if (knobMode  >= 0) _data.WheelKnobMode          = knobMode;
            if (stickMode >= 0) _data.WheelStickMode         = stickMode;

            int[]? rpmColors          = EffArr(ov?.WheelRpmColors, profile.WheelRpmColors);
            int[]? rpmBlinkColors     = EffArr(ov?.WheelRpmBlinkColors, profile.WheelRpmBlinkColors);
            int[]? buttonColors       = EffArr(ov?.WheelButtonColors, profile.WheelButtonColors);
            bool[]? buttonDefaults    = ov?.WheelButtonDefaultDuringTelemetry
                                        ?? profile.WheelButtonDefaultDuringTelemetry;
            int[]? flagColors         = EffArr(ov?.WheelFlagColors, profile.WheelFlagColors);
            int[]? idleColor          = EffArr(ov?.WheelIdleColor, profile.WheelIdleColor);
            int[]? esRpmColors        = EffArr(ov?.WheelESRpmColors, profile.WheelESRpmColors);
            int[]? knobBgColors       = EffArr(ov?.WheelKnobBackgroundColors, profile.WheelKnobBackgroundColors);
            int[]? knobPrimaryColors  = EffArr(ov?.WheelKnobPrimaryColors, profile.WheelKnobPrimaryColors);
            int[]? knobRingColors     = EffArr(ov?.WheelKnobRingColors, profile.WheelKnobRingColors);

            // Mirror colors into _data (UI uses _data.* for swatches).
            MozaProfile.UnpackColorsInto(rpmColors, _data.WheelRpmColors);
            MozaProfile.UnpackColorsInto(rpmBlinkColors, _data.WheelRpmBlinkColors);
            MozaProfile.UnpackColorsInto(buttonColors, _data.WheelButtonColors);
            if (buttonDefaults != null)
            {
                int n = Math.Min(buttonDefaults.Length, _data.WheelButtonDefaultDuringTelemetry.Length);
                for (int i = 0; i < n; i++)
                    _data.WheelButtonDefaultDuringTelemetry[i] = buttonDefaults[i];
            }
            MozaProfile.UnpackColorsInto(flagColors, _data.WheelFlagColors);
            if (idleColor != null && idleColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(idleColor[0]);
                _data.WheelIdleColor[0] = rgb[0];
                _data.WheelIdleColor[1] = rgb[1];
                _data.WheelIdleColor[2] = rgb[2];
            }
            MozaProfile.UnpackColorsInto(esRpmColors, _data.WheelESRpmColors);
            MozaProfile.UnpackColorsInto(knobBgColors, _data.WheelKnobBackgroundColors);
            MozaProfile.UnpackColorsInto(knobPrimaryColors, _data.WheelKnobPrimaryColors);
            MozaProfile.UnpackColorsInto(knobRingColors, _data.KnobRingColors);
            if (knobRingBri >= 0) _data.KnobRingBrightness = knobRingBri;

            // Hardware writes (skipped when wheel/connection isn't live).
            if (!deviceLive) return;
            if (_detectionState.NewWheelDetected)
            {
                // Capability snapshot for the active wheel. Falls back to
                // Default (10 RPM / 14 buttons / no flags / no knobs) when the
                // wheel hasn't been identified yet, so we still write a
                // reasonable subset rather than nothing.
                var model = _plugin.WheelModelInfo ?? WheelModelInfo.Default;
                int rpmCount = model.RpmLedCount;
                int btnCount = model.ButtonLedCount;
                bool hasRpm  = rpmCount > 0;
                bool hasBtn  = btnCount > 0;
                bool hasKnob = model.KnobCount > 0;

                if (telemMode      >= 0)            _deviceManager.WriteSetting("wheel-telemetry-mode", telemMode);
                if (idleEffect     >= 0 && hasRpm)  _deviceManager.WriteSetting("wheel-telemetry-idle-effect", idleEffect);
                if (btnIdleEffect  >= 0 && hasBtn)  _deviceManager.WriteSetting("wheel-buttons-idle-effect", btnIdleEffect);
                if (knobIdleEffect >= 0 && hasKnob) _deviceManager.WriteSetting("wheel-knob-idle-effect", knobIdleEffect);
                if (knobLedMode    >= 0 && hasKnob) _deviceManager.WriteSetting("wheel-knob-led-mode", knobLedMode);
                if (btnLedMode     >= 0 && hasBtn)  _deviceManager.WriteSetting("wheel-buttons-led-mode", btnLedMode);
                if (idleEffect >= 0 && idleSpeed >= 0 && hasRpm)
                    _deviceManager.WriteArray("wheel-telemetry-idle-interval",
                        BuildIdleIntervalPayload(idleEffect, idleSpeed));
                if (btnIdleEffect >= 0 && btnIdleSpeed >= 0 && hasBtn)
                    _deviceManager.WriteArray("wheel-buttons-idle-interval",
                        BuildIdleIntervalPayload(btnIdleEffect, btnIdleSpeed));
                if (knobIdleEffect >= 0 && knobIdleSpeed >= 0 && hasKnob)
                    _deviceManager.WriteArray("wheel-knob-idle-interval",
                        BuildIdleIntervalPayload(knobIdleEffect, knobIdleSpeed));
                if (sleepMode    >= 0) _deviceManager.WriteSetting("wheel-idle-mode", sleepMode);
                if (sleepTimeout >= 0) _deviceManager.WriteSetting("wheel-idle-timeout", sleepTimeout);
                if (sleepMode >= 0 && sleepSpeed >= 0)
                    _deviceManager.WriteArray("wheel-idle-speed",
                        BuildIdleIntervalPayload(sleepMode, sleepSpeed));
                if (sleepColor != null && sleepColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(sleepColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                if (rpmBri   >= 0 && hasRpm) _deviceManager.WriteSetting("wheel-rpm-brightness", rpmBri);
                if (btnBri   >= 0 && hasBtn) _deviceManager.WriteSetting("wheel-buttons-brightness", btnBri);
                if (flagsBri >= 0 && _detectionState.DashDetected)
                    _deviceManager.WriteSetting("dash-flags-brightness", flagsBri);

                WriteColorArray(rpmColors, "wheel-rpm-color", rpmCount);
                if (hasRpm)
                    WriteColorArray(rpmBlinkColors, "wheel-rpm-blink-color", Math.Min(10, rpmCount));
                WriteColorArray(buttonColors, "wheel-button-color", btnCount);
                if (_detectionState.DashDetected)
                    WriteColorArray(flagColors, "dash-flag-color", 6);
                if (idleColor != null && idleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(idleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                // Invalidate the live cache after each Apply pass so the next live tick
                // re-sends instead of dedup'ing against a frame whose underlying wheel
                // state we just rewrote with the persisted static colours.
                MozaLedDeviceManager.InvalidateLiveCacheAny(
                    LedKind.Rpm | LedKind.Button | LedKind.Flag);
                WriteKnobColors(knobBgColors, knobPrimaryColors);
                WriteKnobRingColors(knobRingColors, knobRingBri);

                // If we have no saved active-colour overlay (fresh install, or
                // user never touched the centre swatch), read the wheel's own
                // stored values back — they'll land in MozaData via
                // UpdateFromArray for wheel-knob{N}-active-color and surface
                // as the visible defaults instead of black-on-load.
                if (knobPrimaryColors == null && hasKnob)
                {
                    int knobs = model.KnobCount;
                    for (int i = 0; i < knobs && i < 5; i++)
                        _deviceManager.ReadSetting($"wheel-knob{i + 1}-active-color");
                }
            }

            if (_detectionState.OldWheelDetected)
            {
                if (rpmInd   >= 0) _deviceManager.WriteSetting("wheel-rpm-indicator-mode", rpmInd + 1);
                if (rpmDisp  >= 0) _deviceManager.WriteSetting("wheel-set-rpm-display-mode", rpmDisp);
                if (esRpmBri >= 0) _deviceManager.WriteSetting("wheel-old-rpm-brightness", esRpmBri);
                WriteColorArray(esRpmColors, "wheel-old-rpm-color", 10);
            }
        }

        /// <summary>
        /// Push dashboard-scoped settings (brightness, indicator modes, colors,
        /// display brightness/standby) to the dash. _data mirrored always;
        /// writes gated on detection.
        /// </summary>
        public void ApplyDashToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // SimHub auto-creates per-game profiles with all-sentinel dash fields
            // and never routes them through SettingsMigrator. Without seeding,
            // the >=0 guards below skip every write — _data keeps its sentinel
            // default and the wire push never fires, so the wheel display sits
            // at whatever value happened to be on it. Seed sentinels from the
            // global defaults here; the helper is idempotent (sentinel-only).
            if (_plugin.Settings != null)
                new SettingsMigrator(_plugin.Settings).SeedProfileBaselineFromFlatFields(profile);

            if (profile.DashRpmBrightness     >= 0) _data.DashRpmBrightness     = profile.DashRpmBrightness;
            if (profile.DashFlagsBrightness   >= 0) _data.DashFlagsBrightness   = profile.DashFlagsBrightness;
            if (profile.DashDisplayBrightness >= 0) _data.DashDisplayBrightness = profile.DashDisplayBrightness;
            if (profile.DashDisplayStandbyMin >= 0) _data.DashDisplayStandbyMin = profile.DashDisplayStandbyMin;
            MozaProfile.UnpackColorsInto(profile.DashRpmColors, _data.DashRpmColors);
            MozaProfile.UnpackColorsInto(profile.DashRpmBlinkColors, _data.DashRpmBlinkColors);
            MozaProfile.UnpackColorsInto(profile.DashFlagColors, _data.DashFlagColors);

            if (!_detectionState.DashDetected || !_data.IsConnected) return;

            // CM2 standalone path: route via the verified group-0x32 / dev=0x12
            // surface (`cm2-*` commands). The legacy dash-* writes at dev=0x14
            // had no visible effect on CM2 in usb-capture/CM2.md lab tests, so
            // skip the per-LED color and indicator-mode writes for CM2.
            // Brightness goes through both paths as belt-and-suspenders: the
            // cm2-indicator-brightness write is the authoritative one, but
            // sending the legacy dash-rpm-brightness too costs nothing and
            // might engage on different firmware revisions.
            bool isCm2 = _plugin.ShouldUseStandaloneDashboardTarget();

            if (profile.DashRpmBrightness   >= 0) _deviceManager.WriteSetting("dash-rpm-brightness", profile.DashRpmBrightness);
            if (profile.DashFlagsBrightness >= 0) _deviceManager.WriteSetting("dash-flags-brightness", profile.DashFlagsBrightness);
            var sender = _plugin.TelemetrySender;
            if (profile.DashDisplayBrightness   >= 0) sender?.SendDashDisplayBrightness(profile.DashDisplayBrightness);
            if (profile.DashDisplayStandbyMin >= 0) sender?.SendDashDisplayStandbyMinutes(profile.DashDisplayStandbyMin);

            if (isCm2)
            {
                ApplyCm2DashboardConfig(profile);
            }
            else
            {
                // Legacy SHDP dashboard: dash-flags-indicator-mode forced to 1
                // (firmware default 0 silently drops flag colour writes).
                _deviceManager.WriteSetting("dash-flags-indicator-mode", 1);

                WriteColorArray(profile.DashRpmColors, "dash-rpm-color", 10);
                WriteColorArray(profile.DashRpmBlinkColors, "dash-rpm-blink-color", 10);
                WriteColorArray(profile.DashFlagColors, "dash-flag-color", 6);
            }
        }

        /// <summary>
        /// Write the CM2-specific meter-config + persistent-color stack on
        /// dev=0x12 (CM2 bridge/main). Sub-cmds and behavior verified in
        /// usb-capture/CM2.md (2026-05-21 lab notes). Called from
        /// <see cref="ApplyDashToHardware"/> when the connection target is a
        /// standalone CM2 (PID 0x0025).
        /// </summary>
        private void ApplyCm2DashboardConfig(MozaProfile profile)
        {
            // Meter mode toggles — required to put CM2 firmware in SimHub
            // telemetry mode so screen widgets + LED ramp follow value frames.
            // TODO(cm2): cm2-normal-mode 1 vs 2 visually similar in CM2.md
            // lab — confirm 1 is the correct SimHub-mode value via capture.
            _deviceManager.WriteSetting("cm2-normal-mode", 1);
            _deviceManager.WriteSetting("cm2-rpm-group-mode", 1);
            _deviceManager.WriteSetting("cm2-flag-group-mode", 1);

            // RPM regulation mode + thresholds. CM2.md notes percent-vs-absolute
            // encoding is not independently verified, so we write BOTH (percent
            // mode + percent thresholds, plus absolute thresholds derived from
            // MaxRpm) and let the firmware honour whichever it actually uses.
            // TODO(cm2): confirm regulation-mode encoding via capture.
            _deviceManager.WriteSetting("cm2-rpm-regulation-mode", 0);

            // Default percent ramp: 50,55,60,…,95 covering the upper half of
            // the rev range. CM2 has 16 physical LEDs but the firmware accepts
            // a 10-entry percent ramp (one entry per "rung"; the firmware
            // interpolates across physical positions).
            byte[] percentRamp = new byte[] { 50, 55, 60, 65, 70, 75, 80, 85, 90, 95 };
            _deviceManager.WriteArray("cm2-rpm-percent-thresholds", percentRamp);

            // Absolute thresholds derived from MaxRpm (fallback 8000 per
            // CM2.md). Each rung gets (rpm * (i+1) / 10) so the 10 thresholds
            // span 10%..100% of the configured max.
            int maxRpm = 8000;
            // TODO(cm2): plumb MaxRpm from active game when SimHub provides it
            // (currently using a sensible static default).
            for (byte i = 0; i < 10; i++)
            {
                int threshold = (int)((long)maxRpm * (i + 1) / 10);
                _deviceManager.WriteSetting($"cm2-rpm-absolute-threshold{i + 1}", threshold);
            }

            // Indicator brightness — authoritative path for CM2. Reuse the
            // existing DashRpmBrightness slider as the source so the UI knob
            // does not double up; the legacy dash-rpm-brightness write above
            // is kept for compatibility.
            if (profile.DashRpmBrightness >= 0)
                _deviceManager.WriteSetting("cm2-indicator-brightness", profile.DashRpmBrightness);

            // 16 stored per-LED colors. The legacy SHDP profile only has 10
            // RPM + 6 flag colors; map them across the 16 physical CM2
            // positions: RPM colors 1-10 → cm2-stored-color1..10; flag colors
            // 1-6 → cm2-stored-color11..16. This makes the existing dash UI
            // continue to drive useful colors until a CM2-specific 16-color
            // settings field replaces it. CM2.md confirms 1B 00 FF <i> + RGB
            // visibly updates the matching LED at dev=0x12.
            if (profile.DashRpmColors != null)
            {
                int rpmCount = System.Math.Min(profile.DashRpmColors.Length, 10);
                for (int i = 0; i < rpmCount; i++)
                {
                    var rgb = MozaProfile.UnpackColor(profile.DashRpmColors[i]);
                    _deviceManager.WriteColor($"cm2-stored-color{i + 1}", rgb[0], rgb[1], rgb[2]);
                }
            }
            if (profile.DashFlagColors != null)
            {
                int flagCount = System.Math.Min(profile.DashFlagColors.Length, 6);
                for (int i = 0; i < flagCount; i++)
                {
                    var rgb = MozaProfile.UnpackColor(profile.DashFlagColors[i]);
                    _deviceManager.WriteColor($"cm2-stored-color{i + 11}", rgb[0], rgb[1], rgb[2]);
                }
            }
        }

        /// <summary>
        /// Push wheel-base ambient LED settings. No-op unless the runtime probe
        /// confirmed strip support.
        /// </summary>
        public void ApplyBaseAmbientToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.BaseAmbientBrightness     >= 0) _data.BaseAmbientBrightness     = profile.BaseAmbientBrightness;
            if (profile.BaseAmbientStandbyMode    >= 0) _data.BaseAmbientStandbyMode    = profile.BaseAmbientStandbyMode;
            if (profile.BaseAmbientIndicatorState >= 0) _data.BaseAmbientIndicatorState = profile.BaseAmbientIndicatorState;
            if (profile.BaseAmbientSleepMode      >= 0) _data.BaseAmbientSleepMode      = profile.BaseAmbientSleepMode;
            if (profile.BaseAmbientSleepTimeout   >= 0) _data.BaseAmbientSleepTimeout   = profile.BaseAmbientSleepTimeout;
            if (profile.BaseAmbientStartupColor   >= 0) UnpackPackedColor(profile.BaseAmbientStartupColor, _data.BaseAmbientStartupColor);
            if (profile.BaseAmbientShutdownColor  >= 0) UnpackPackedColor(profile.BaseAmbientShutdownColor, _data.BaseAmbientShutdownColor);

            if (!_detectionState.BaseAmbientLedSupported || !_data.IsConnected) return;
            if (profile.BaseAmbientBrightness     >= 0) _deviceManager.WriteSetting("base-ambient-brightness", profile.BaseAmbientBrightness);
            if (profile.BaseAmbientStandbyMode    >= 0) _deviceManager.WriteSetting("base-ambient-standby-mode", profile.BaseAmbientStandbyMode);
            if (profile.BaseAmbientIndicatorState >= 0) _deviceManager.WriteSetting("base-ambient-indicator-state", profile.BaseAmbientIndicatorState);
            if (profile.BaseAmbientSleepMode      >= 0) _deviceManager.WriteSetting("base-ambient-sleep-mode", profile.BaseAmbientSleepMode);
            if (profile.BaseAmbientSleepTimeout   >= 0) _deviceManager.WriteSetting("base-ambient-sleep-timeout", profile.BaseAmbientSleepTimeout);
            if (profile.BaseAmbientStartupColor   >= 0) WritePackedColor("base-ambient-startup-color", profile.BaseAmbientStartupColor);
            if (profile.BaseAmbientShutdownColor  >= 0) WritePackedColor("base-ambient-shutdown-color", profile.BaseAmbientShutdownColor);
        }

        /// <summary>Push handbrake settings. No-op unless detected.</summary>
        public void ApplyHandbrakeToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.HandbrakeMode            >= 0) _data.HandbrakeMode            = profile.HandbrakeMode;
            if (profile.HandbrakeButtonThreshold >= 0) _data.HandbrakeButtonThreshold = profile.HandbrakeButtonThreshold;
            if (profile.HandbrakeDirection       >= 0) _data.HandbrakeDirection       = profile.HandbrakeDirection;
            if (profile.HandbrakeMin             >= 0) _data.HandbrakeMin             = profile.HandbrakeMin;
            if (profile.HandbrakeMax             >= 0) _data.HandbrakeMax             = profile.HandbrakeMax;
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                    _data.HandbrakeCurve[i] = profile.HandbrakeCurve[i];
            }

            if (!_detectionState.HandbrakeDetected) return;
            if (profile.HandbrakeMode            >= 0) _deviceManager.WriteSetting("handbrake-mode", profile.HandbrakeMode);
            if (profile.HandbrakeButtonThreshold >= 0) _deviceManager.WriteSetting("handbrake-button-threshold", profile.HandbrakeButtonThreshold);
            if (profile.HandbrakeDirection       >= 0) _deviceManager.WriteSetting("handbrake-direction", profile.HandbrakeDirection);
            if (profile.HandbrakeMin             >= 0) _deviceManager.WriteSetting("handbrake-min", profile.HandbrakeMin);
            if (profile.HandbrakeMax             >= 0) _deviceManager.WriteSetting("handbrake-max", profile.HandbrakeMax);
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                    _deviceManager.WriteFloat($"handbrake-y{i + 1}", profile.HandbrakeCurve[i]);
            }
        }

        /// <summary>Push pedal settings. No-op unless detected.</summary>
        public void ApplyPedalsToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.PedalsThrottleDir      >= 0) _data.PedalsThrottleDir      = profile.PedalsThrottleDir;
            if (profile.PedalsBrakeDir         >= 0) _data.PedalsBrakeDir         = profile.PedalsBrakeDir;
            if (profile.PedalsClutchDir        >= 0) _data.PedalsClutchDir        = profile.PedalsClutchDir;
            if (profile.PedalsBrakeAngleRatio  >= 0) _data.PedalsBrakeAngleRatio  = profile.PedalsBrakeAngleRatio;
            if (profile.PedalsThrottleCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsThrottleCurve.Length); i++)
                    _data.PedalsThrottleCurve[i] = profile.PedalsThrottleCurve[i];
            if (profile.PedalsBrakeCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsBrakeCurve.Length); i++)
                    _data.PedalsBrakeCurve[i] = profile.PedalsBrakeCurve[i];
            if (profile.PedalsClutchCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsClutchCurve.Length); i++)
                    _data.PedalsClutchCurve[i] = profile.PedalsClutchCurve[i];

            if (!_detectionState.PedalsDetected) return;
            if (profile.PedalsThrottleDir      >= 0) _deviceManager.WriteSetting("pedals-throttle-dir", profile.PedalsThrottleDir);
            if (profile.PedalsBrakeDir         >= 0) _deviceManager.WriteSetting("pedals-brake-dir", profile.PedalsBrakeDir);
            if (profile.PedalsClutchDir        >= 0) _deviceManager.WriteSetting("pedals-clutch-dir", profile.PedalsClutchDir);
            if (profile.PedalsBrakeAngleRatio  >= 0) _deviceManager.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio);
            if (profile.PedalsThrottleCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsThrottleCurve.Length); i++)
                    _deviceManager.WriteFloat($"pedals-throttle-y{i + 1}", profile.PedalsThrottleCurve[i]);
            if (profile.PedalsBrakeCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsBrakeCurve.Length); i++)
                    _deviceManager.WriteFloat($"pedals-brake-y{i + 1}", profile.PedalsBrakeCurve[i]);
            if (profile.PedalsClutchCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsClutchCurve.Length); i++)
                    _deviceManager.WriteFloat($"pedals-clutch-y{i + 1}", profile.PedalsClutchCurve[i]);
        }

        /// <summary>
        /// Push base/FFB settings (motor limits, FFB curve breakpoints) to the
        /// wheelbase. _data is mirrored always; writes gated on base-connected.
        /// </summary>
        public void ApplyBaseToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            ApplyBaseSettingIfSet(profile.Limit, v => { _data.Limit = v; _data.MaxAngle = v; }, "base-limit", "base-max-angle");
            ApplyBaseSettingIfSet(profile.FfbStrength, v => _data.FfbStrength = v, "base-ffb-strength");
            ApplyBaseSettingIfSet(profile.Torque, v => _data.Torque = v, "base-torque");
            ApplyBaseSettingIfSet(profile.Speed, v => _data.Speed = v, "base-speed");
            ApplyBaseSettingIfSet(profile.Damper, v => _data.Damper = v, "base-damper");
            ApplyBaseSettingIfSet(profile.Friction, v => _data.Friction = v, "base-friction");
            ApplyBaseSettingIfSet(profile.Inertia, v => _data.Inertia = v, "base-inertia");
            ApplyBaseSettingIfSet(profile.Spring, v => _data.Spring = v, "base-spring");
            ApplyBaseSettingIfSet(profile.SpeedDamping, v => _data.SpeedDamping = v, "base-speed-damping");
            ApplyBaseSettingIfSet(profile.SpeedDampingPoint, v => _data.SpeedDampingPoint = v, "base-speed-damping-point");
            ApplyBaseSettingIfSet(profile.NaturalInertia, v => _data.NaturalInertia = v, "base-natural-inertia");
            ApplyBaseSettingIfSet(profile.SoftLimitStiffness, v => _data.SoftLimitStiffness = v, "base-soft-limit-stiffness");
            ApplyBaseSettingIfSet(profile.SoftLimitRetain, v => _data.SoftLimitRetain = v, "base-soft-limit-retain");
            ApplyBaseSettingIfSet(profile.FfbReverse, v => _data.FfbReverse = v, "base-ffb-reverse");
            ApplyBaseSettingIfSet(profile.Protection, v => _data.Protection = v, "base-protection");
            ApplyBaseSettingIfSet(profile.GameDamper, v => _data.GameDamper = v, "main-set-damper-gain");
            ApplyBaseSettingIfSet(profile.GameFriction, v => _data.GameFriction = v, "main-set-friction-gain");
            ApplyBaseSettingIfSet(profile.GameInertia, v => _data.GameInertia = v, "main-set-inertia-gain");
            ApplyBaseSettingIfSet(profile.GameSpring, v => _data.GameSpring = v, "main-set-spring-gain");
            ApplyBaseSettingIfSet(profile.WorkMode, v => _data.WorkMode = v, "main-set-work-mode");

            // FFB Equalizer (sentinel = -1000): mirror always, write when live.
            void ApplyEq(int val, Action<int> setData, string cmd)
            {
                if (val <= -1000) return;
                setData(val);
                if (_data.IsBaseConnected) _deviceManager.WriteSetting(cmd, val);
            }
            ApplyEq(profile.Equalizer1, v => _data.Equalizer1 = v, "base-equalizer1");
            ApplyEq(profile.Equalizer2, v => _data.Equalizer2 = v, "base-equalizer2");
            ApplyEq(profile.Equalizer3, v => _data.Equalizer3 = v, "base-equalizer3");
            ApplyEq(profile.Equalizer4, v => _data.Equalizer4 = v, "base-equalizer4");
            ApplyEq(profile.Equalizer5, v => _data.Equalizer5 = v, "base-equalizer5");
            ApplyEq(profile.Equalizer6, v => _data.Equalizer6 = v, "base-equalizer6");

            // FFB Curve Y values: mirror always; write when live.
            if (profile.FfbCurveY1 >= 0) _data.FfbCurveY1 = profile.FfbCurveY1;
            if (profile.FfbCurveY2 >= 0) _data.FfbCurveY2 = profile.FfbCurveY2;
            if (profile.FfbCurveY3 >= 0) _data.FfbCurveY3 = profile.FfbCurveY3;
            if (profile.FfbCurveY4 >= 0) _data.FfbCurveY4 = profile.FfbCurveY4;
            if (profile.FfbCurveY5 >= 0) _data.FfbCurveY5 = profile.FfbCurveY5;
            if (!_data.IsBaseConnected) return;
            // X breakpoints always written when live (device doesn't persist them).
            _deviceManager.WriteSetting("base-ffb-curve-x1", 20);
            _deviceManager.WriteSetting("base-ffb-curve-x2", 40);
            _deviceManager.WriteSetting("base-ffb-curve-x3", 60);
            _deviceManager.WriteSetting("base-ffb-curve-x4", 80);
            _deviceManager.WriteSetting("base-ffb-curve-y1", _data.FfbCurveY1);
            _deviceManager.WriteSetting("base-ffb-curve-y2", _data.FfbCurveY2);
            _deviceManager.WriteSetting("base-ffb-curve-y3", _data.FfbCurveY3);
            _deviceManager.WriteSetting("base-ffb-curve-y4", _data.FfbCurveY4);
            _deviceManager.WriteSetting("base-ffb-curve-y5", _data.FfbCurveY5);
        }

        /// <summary>
        /// Push AB9 active-shifter settings to the AB9 manager. No-op unless AB9
        /// is detected and the profile carries an Ab9 block.
        /// </summary>
        public void ApplyAb9ToHardware(MozaProfile? profile)
        {
            if (profile?.Ab9 == null) return;
            if (!_detectionState.Ab9Detected || _ab9Manager == null || !_ab9Manager.IsConnected) return;

            var ab9 = profile.Ab9;
            _ab9Manager.SendMode(ab9.Mode);
            _ab9Manager.SendSlider(Ab9Slider.MechanicalResistance, ab9.MechanicalResistance);
            _ab9Manager.SendSlider(Ab9Slider.Spring,               ab9.Spring);
            _ab9Manager.SendSlider(Ab9Slider.NaturalDamping,       ab9.NaturalDamping);
            _ab9Manager.SendSlider(Ab9Slider.NaturalFriction,      ab9.NaturalFriction);
            _ab9Manager.SendSlider(Ab9Slider.MaxTorqueLimit,       ab9.MaxTorqueLimit);
            _ab9Manager.SendGearShiftVibrationIntensity(ab9.GearShiftVibrationIntensity);
        }

        /// <summary>
        /// Run all Apply*ToHardware methods for a profile. The orchestrator
        /// (persist + dashboard-pending + telemetry sync) stays with
        /// <see cref="MozaPlugin.ApplyProfile"/>; this is the hardware-write half.
        /// </summary>
        public void ApplyProfileHardware(MozaProfile profile)
        {
            // Guard: a profile with all core base settings at zero was captured
            // from uninitialized device data (first-launch race). Reset to
            // sentinels so they're skipped — device keeps its own values.
            if (profile.Limit == 0 && profile.FfbStrength == 0 && profile.Torque == 0 && profile.Speed == 0)
            {
                MozaLog.Warn("[Moza] Profile has zeroed base settings — resetting to sentinels");
                profile.Limit = -1; profile.FfbStrength = -1; profile.Torque = -1; profile.Speed = -1;
                profile.Damper = -1; profile.Friction = -1; profile.Inertia = -1; profile.Spring = -1;
                profile.SpeedDamping = -1; profile.SpeedDampingPoint = -1;
                profile.NaturalInertia = -1; profile.SoftLimitStiffness = -1;
                profile.SoftLimitRetain = -1; profile.FfbReverse = -1; profile.Protection = -1;
                profile.GameDamper = -1; profile.GameFriction = -1;
                profile.GameInertia = -1; profile.GameSpring = -1;
                profile.WorkMode = -1;
            }

            ApplyBaseToHardware(profile);
            ApplyWheelToHardware(profile);
            ApplyDashToHardware(profile);
            ApplyBaseAmbientToHardware(profile);
            ApplyHandbrakeToHardware(profile);
            ApplyPedalsToHardware(profile);
            ApplyAb9ToHardware(profile);
        }

        // ===== Device-extension entry points =====

        /// <summary>
        /// Apply wheel settings from the SimHub device extension profile system.
        /// Updates settings, _data, overlay, then writes through ApplyWheelToHardware.
        /// </summary>
        public void ApplyWheelExtensionSettings(MozaWheelExtensionSettings extSettings, string? pageModelPrefix = null)
        {
            MozaLog.Debug($"[Moza] Applying wheel device extension settings (prefix={pageModelPrefix ?? "(null)"})");

            var settings = _plugin.Settings;
            var profile = settings?.ProfileStore?.CurrentProfile;
            extSettings.ApplyTo(settings!, _data, profile, pageModelPrefix);

            // Hardware writes gated on model match — other-model extensions
            // must not poke the active wheel's hardware.
            string extModel = extSettings.WheelModelName ?? "";
            string activeModel = _data.WheelModelName ?? "";
            bool hasExtModel = !string.IsNullOrEmpty(extModel);
            bool modelMatches = hasExtModel &&
                string.Equals(extModel, activeModel, StringComparison.OrdinalIgnoreCase);
            bool writeHardware = !hasExtModel || modelMatches;

            if (writeHardware)
                ApplyWheelToHardware(profile);

            _plugin.PersistSettings();

            // SimHub invokes SetSettings on every registered extension at
            // startup; gate live telemetry pushes on modelMatches so a
            // non-matching extension can't bleed its TelemetryProfileName
            // into the active sender.
            if (extSettings.TelemetrySettingsPresent && modelMatches)
            {
                if (settings!.TelemetryEnabled)
                {
                    _plugin.ApplyTelemetrySettings();
                    _plugin.StartTelemetryIfReady();
                }
                else
                {
                    _plugin.TelemetrySender?.Stop();
                }
            }
        }

        public void ApplyDashExtensionSettings(MozaDashExtensionSettings extSettings)
        {
            MozaLog.Debug("[Moza] Applying dash device extension settings");

            var settings = _plugin.Settings;
            extSettings.ApplyTo(settings!, _data, settings?.ProfileStore?.CurrentProfile);
            ApplyDashToHardware(settings?.ProfileStore?.CurrentProfile);
            _plugin.PersistSettings();
        }

        public void ApplyBaseExtensionSettings(MozaBaseExtensionSettings extSettings)
        {
            MozaLog.Debug("[Moza] Applying base ambient device extension settings");

            var settings = _plugin.Settings;
            extSettings.ApplyTo(settings!, _data, settings?.ProfileStore?.CurrentProfile);
            ApplyBaseAmbientToHardware(settings?.ProfileStore?.CurrentProfile);
            _plugin.PersistSettings();
        }

        // ===== WriteIf* (UI handler hardening) =====
        // Skip the wire when the matching device isn't detected. Sentinel-guard
        // numeric values to drop "no opinion" writes.

        public void WriteIfWheelDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.NewWheelDetected || _detectionState.OldWheelDetected)
                _deviceManager.WriteSetting(command, value);
        }
        public void WriteIfDashDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.DashDetected) _deviceManager.WriteSetting(command, value);
        }
        public void WriteIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_data.IsBaseConnected) _deviceManager.WriteSetting(command, value);
        }
        public void WriteFloatIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_data.IsBaseConnected) _deviceManager.WriteFloat(command, value);
        }
        public void WriteIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.HandbrakeDetected) _deviceManager.WriteSetting(command, value);
        }
        public void WriteFloatIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.HandbrakeDetected) _deviceManager.WriteFloat(command, value);
        }
        public void WriteIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.PedalsDetected) _deviceManager.WriteSetting(command, value);
        }
        public void WriteFloatIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.PedalsDetected) _deviceManager.WriteFloat(command, value);
        }
        public void WriteIfBaseAmbientSupported(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.BaseAmbientLedSupported) _deviceManager.WriteSetting(command, value);
        }
        public void WriteColorIfWheelDetected(string command, byte r, byte g, byte b)
        {
            if (_detectionState.NewWheelDetected || _detectionState.OldWheelDetected)
                _deviceManager.WriteColor(command, r, g, b);
        }

        /// <summary>
        /// Wheel LED colour write from UI handlers that also invalidates the live
        /// cache for <paramref name="kind"/>. Writing during live telemetry is safe
        /// per user confirmation (the next live frame overpaints the static value);
        /// the cache invalidation just ensures the live pipeline can't dedup its
        /// next frame against a stale cache after our write changed the wheel's
        /// frame buffer. Use this from UI handlers for every wheel-LED colour
        /// command (knob active, knob bg, RPM, button, flag); use the plain
        /// <see cref="WriteColorIfWheelDetected"/> for non-LED commands (idle/sleep
        /// colour, ambient colour, etc.).
        /// </summary>
        public void WriteLedColorIfWheelDetected(string command, byte r, byte g, byte b, LedKind kind)
        {
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected) return;
            _deviceManager.WriteColor(command, r, g, b);
            MozaLedDeviceManager.InvalidateLiveCacheAny(kind);
        }
        public void WriteColorIfDashDetected(string command, byte r, byte g, byte b)
        {
            if (_detectionState.DashDetected) _deviceManager.WriteColor(command, r, g, b);
        }
        public void WriteColorIfBaseAmbientSupported(string command, byte r, byte g, byte b)
        {
            if (_detectionState.BaseAmbientLedSupported) _deviceManager.WriteColor(command, r, g, b);
        }
        public void WriteArrayIfWheelDetected(string command, byte[] payload)
        {
            if (_detectionState.NewWheelDetected || _detectionState.OldWheelDetected)
                _deviceManager.WriteArray(command, payload);
        }

        // ===== Per-cluster sentinel-guarded helpers =====

        public void ApplyBaseSettingIfSet(int value, Action<int> setData, params string[] commands)
        {
            if (value < 0) return;
            setData(value);
            if (_data.IsBaseConnected)
            {
                foreach (var cmd in commands)
                    _deviceManager.WriteSetting(cmd, value);
            }
        }

        public void ApplyHandbrakeSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_detectionState.HandbrakeDetected)
                _deviceManager.WriteSetting(command, value);
        }

        public void ApplyPedalSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_detectionState.PedalsDetected)
                _deviceManager.WriteSetting(command, value);
        }

        public void ApplyCurveIfSet(int[]? curve, int[] dataArray, string commandPrefix, bool deviceConnected)
        {
            if (curve == null) return;
            for (int i = 0; i < Math.Min(5, curve.Length); i++)
            {
                dataArray[i] = curve[i];
                if (deviceConnected)
                    _deviceManager.WriteFloat($"{commandPrefix}{i + 1}", curve[i]);
            }
        }

        /// <summary>
        /// Build the 3-byte payload shared by per-effect speed commands:
        /// <c>wheel-{telemetry,buttons,knob}-idle-interval</c> = <c>[effect_id, ms_msb, ms_lsb]</c>;
        /// <c>wheel-idle-speed</c> = <c>[mode, ms_msb, ms_lsb]</c>.
        /// </summary>
        public static byte[] BuildIdleIntervalPayload(int selector, int ms)
        {
            ms = Math.Max(0, Math.Min(0xFFFF, ms));
            return new byte[] {
                (byte)(selector & 0xFF),
                (byte)((ms >> 8) & 0xFF),
                (byte)(ms & 0xFF),
            };
        }

        // ===== LED color helpers =====

        public void WriteColorArray(int[]? packedColors, string commandPrefix, int count)
        {
            if (packedColors == null) return;
            int len = Math.Min(packedColors.Length, count);
            for (int i = 0; i < len; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"{commandPrefix}{i + 1}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Push per-knob "bulk Inactive default" + per-knob "Active" colors. No-op
        /// unless the active wheel exposes knob LED rings (W17 CS Pro / W18 KS Pro).
        /// Bulk Inactive fans out to all ring LEDs via per-LED writes (cmd 0x1F 0x03 0x01);
        /// Active drives cmd 0x27 ROLE=0.
        ///
        /// Writes are unconditional with respect to live telemetry — per user
        /// confirmation, cmd 0x27 / cmd 0x1F 0x03 0x01 during live frames does not
        /// visibly flicker the live overlay (the next live frame overpaints). After
        /// every successful write batch we invalidate the live cache so the next live
        /// tick re-sends instead of dedup'ing against a frame whose underlying wheel
        /// state we just rewrote.
        /// </summary>
        public void WriteKnobColors(int[]? packedBulkInactive, int[]? packedActive)
        {
            var model = _plugin.WheelModelInfo;
            int knobs = model?.KnobCount ?? 0;
            if (knobs <= 0) return;

            // Per-knob Active LED color (cmd 0x27 ROLE=0).
            if (packedActive != null)
            {
                int len = Math.Min(packedActive.Length, knobs);
                for (int i = 0; i < len; i++)
                {
                    var rgb = MozaProfile.UnpackColor(packedActive[i]);
                    _deviceManager.WriteColor($"wheel-knob{i + 1}-active-color", rgb[0], rgb[1], rgb[2]);
                }
            }

            // Per-knob "bulk Inactive default" fanned over ring LEDs.
            if (packedBulkInactive != null
                && model?.KnobRingLeds != null
                && _detectionState.IsWheelLedGroupPresent(3))
            {
                int kLen = Math.Min(packedBulkInactive.Length, knobs);
                for (int k = 0; k < kLen; k++)
                {
                    var rgb = MozaProfile.UnpackColor(packedBulkInactive[k]);
                    int startIdx = model.KnobRingStartIndex(k);
                    int count = model.KnobRingLeds[k];
                    for (int i = 0; i < count; i++)
                    {
                        int ledIdx = startIdx + i;
                        _deviceManager.WriteColor($"wheel-knob-bg-color{ledIdx + 1}", rgb[0], rgb[1], rgb[2]);
                    }
                }
            }
            MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Knob);
        }

        /// <summary>
        /// Push per-LED ring colors (cmd 0x1F 0x03 0x01). No-op unless the active
        /// wheel has KnobRingLeds and Group 3 is present. Brightness &lt; 0 skips
        /// the brightness write.
        /// </summary>
        public void WriteKnobRingColors(int[]? packedColors, int brightness)
        {
            var model = _plugin.WheelModelInfo;
            if (model?.KnobRingLeds == null || !_detectionState.IsWheelLedGroupPresent(3)) return;
            if (brightness >= 0)
                _deviceManager.WriteSetting("wheel-knob-brightness", brightness);
            if (packedColors == null) return;
            int total = Math.Min(packedColors.Length, model.KnobRingLedTotal);
            for (int i = 0; i < total; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"wheel-knob-bg-color{i + 1}", rgb[0], rgb[1], rgb[2]);
            }
            MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Knob);
        }

        public static void UnpackPackedColor(int packed, byte[] dst)
        {
            dst[0] = (byte)((packed >> 16) & 0xFF);
            dst[1] = (byte)((packed >> 8) & 0xFF);
            dst[2] = (byte)(packed & 0xFF);
        }

        public void WritePackedColor(string command, int packed)
        {
            byte r = (byte)((packed >> 16) & 0xFF);
            byte g = (byte)((packed >> 8) & 0xFF);
            byte b = (byte)(packed & 0xFF);
            _deviceManager.WriteColor(command, r, g, b);
        }

        /// <summary>Send all-off to wheel and dash LEDs via the device manager.</summary>
        public void ClearLedsOnHardware()
        {
            if (_plugin.Connection == null || !_plugin.Connection.IsConnected) return;
            int rpmCount = _plugin.WheelModelInfo?.RpmLedCount ?? 0;
            _deviceManager.WriteArray("wheel-send-rpm-telemetry",
                MozaLedDeviceManager.BuildRpmBitmaskBytes(0, rpmCount));
            _deviceManager.WriteArray("wheel-send-buttons-telemetry", new byte[] { 0, 0 });
            _deviceManager.WriteSetting("wheel-old-send-telemetry", 0);
            _deviceManager.WriteSetting("dash-send-telemetry", 0);
        }
    }
}
