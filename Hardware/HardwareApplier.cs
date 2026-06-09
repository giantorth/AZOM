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

        // ── Write-on-change cache for persistent (flash-backed) wheel settings ──
        // The wheel persists LED / idle / brightness / colour settings to its
        // parameter flash. ApplyWheelToHardware runs on every (re)detection, so
        // re-writing unchanged settings each time needlessly wears that flash —
        // a re-detect loop has been observed to wear it until reads AND writes
        // start failing (firmware "Table 8: Failed to Read/Write Parameter"),
        // which kills the wheel's identity readback and bricks it. PitHouse
        // writes these once. So: only write a persistent setting when its value
        // actually changed since the last write to THIS wheel. Keyed by MCU UID
        // so a genuinely different wheel (hot-swap) re-writes its config once.
        private readonly System.Collections.Generic.Dictionary<string, long> _wheelCfgCache
            = new System.Collections.Generic.Dictionary<string, long>();
        private byte[] _wheelCfgCacheUid = System.Array.Empty<byte>();

        private void SyncWheelCfgCache()
        {
            var uid = _data.WheelMcuUid ?? System.Array.Empty<byte>();
            bool same = uid.Length == _wheelCfgCacheUid.Length;
            for (int i = 0; same && i < uid.Length; i++)
                if (uid[i] != _wheelCfgCacheUid[i]) same = false;
            if (!same)
            {
                _wheelCfgCache.Clear();
                _wheelCfgCacheUid = (byte[])uid.Clone();
            }
        }

        /// <summary>True (and records the new value) iff this flash-backed setting
        /// differs from the last value written to the current wheel — i.e. an actual
        /// change worth a flash write. Returns false to skip a redundant re-write.</summary>
        private bool WheelCfgChanged(string key, long value)
        {
            if (_wheelCfgCache.TryGetValue(key, out var prev) && prev == value) return false;
            _wheelCfgCache[key] = value;
            return true;
        }

        private static long Fnv(long h, long v) { unchecked { return (h ^ v) * 1099511628211L; } }

        private bool WheelCfgChangedArr(string key, int[]? arr)
        {
            long h = unchecked((long)1469598103934665603UL);
            if (arr == null) h = Fnv(h, -7);
            else { h = Fnv(h, arr.Length); foreach (var v in arr) h = Fnv(h, v); }
            return WheelCfgChanged(key, h);
        }

        private bool WheelCfgChangedArr(string key, bool[]? arr)
        {
            long h = unchecked((long)1469598103934665603UL);
            if (arr == null) h = Fnv(h, -8);
            else { h = Fnv(h, arr.Length); foreach (var v in arr) h = Fnv(h, v ? 1 : 0); }
            return WheelCfgChanged(key, h);
        }

        // Resolve the pipe that owns pedals / handbrake. Pedals or a handbrake
        // can be attached to the base OR to a dedicated Universal Hub pipe, so
        // settings reads and calibration writes must target whichever connection
        // detected the device (recorded owner-before-flag by DeviceProber). Null
        // owner → no opinion → fall back to the primary manager (today's behavior
        // for base-attached peripherals).
        private MozaDeviceManager PedalsManager => _detectionState.PedalsOwner ?? _deviceManager;
        private MozaDeviceManager HandbrakeManager => _detectionState.HandbrakeOwner ?? _deviceManager;
        // Base FFB/motor/ambient writes must target whichever pipe detected the
        // base. Normally that's the primary; after a base→hub primary migration
        // (broken base, wheel on hub) the base lives on a dedicated base-aux pipe,
        // so its writes must NOT follow the now-hub-bound primary _deviceManager.
        // Null owner → fall back to the primary (today's behavior). Mirrors the
        // Pedals/Handbrake resolvers above.
        private MozaDeviceManager BaseManager => _detectionState.BaseOwner ?? _deviceManager;

        private static int Eff(int overlayVal, int baselineVal) =>
            overlayVal >= 0 ? overlayVal : baselineVal;

        private static int[]? EffArr(int[]? overlayArr, int[]? baselineArr) =>
            overlayArr ?? baselineArr;

        // ===== Apply*ToHardware =====

        /// <summary>Push wheel-scoped settings to the connected wheel. Mirrors to _data unconditionally; writes detection-gated.</summary>
        public void ApplyWheelToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

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
            // Per-knob signal modes — overlay-only (no profile baseline), mirrored
            // for UI display like the other input modes. Not re-pushed to hardware
            // here; the wheel firmware persists them (newer FW drops readback).
            if (ov?.WheelKnobSignalModes != null)
                for (int i = 0; i < Math.Min(_data.WheelKnobSignalModes.Length, ov.WheelKnobSignalModes.Length); i++)
                    if (ov.WheelKnobSignalModes[i] >= 0) _data.WheelKnobSignalModes[i] = ov.WheelKnobSignalModes[i];

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

            // Hardware writes — gated per-section on the matching detection
            // flag. NOT gated on _data.IsConnected: that's the "any device
            // responded" proxy, which is false on a fresh MozaData (hot-reload
            // case where _data = new MozaData() but the serial wire and the
            // wheel's prior detection state are preserved via
            // s_persistentConnection / s_persistentDetectionState). Gating
            // the whole method on IsConnected meant ApplyProfile fired at
            // Init couldn't write anything until base-mcu-temp / hub-* /
            // dash-* echoed back to flip IsConnected, and DeviceProber's
            // wheel-model-name handler skips its re-apply path when
            // LastKnownWheelModel was preserved — so static-mode colors
            // never reached the wheel on game switch. _deviceManager.WriteX
            // already checks _connection.IsConnected and returns false on
            // a dead wire, so per-section gates plus the connection-level
            // check is enough.
            // Persistent (flash-backed) wheel settings below: only write when the
            // value changed since the last write to THIS wheel, so re-detection /
            // re-apply doesn't re-flash unchanged settings and wear the wheel's
            // parameter store. PitHouse writes each of these exactly once per connect.
            SyncWheelCfgCache();

            // Only push persistent config once the wheel's identity has resolved.
            // Previously this fell back to WheelModelInfo.Default and wrote a generic
            // subset to UNIDENTIFIED wheels — but an older/unknown wheel may not
            // support those params, and writing/reading params a wheel doesn't have
            // can wedge its firmware (observed: "Table 8: Failed to Read/Write
            // Parameter" → dead identity → re-detect loop → bricked). PitHouse
            // identifies first, then writes only that model's params. A genuine wheel
            // resolves identity within ~1-2 s and DeviceProber re-applies then; a wheel
            // we can't identify is left alone rather than poked with guessed config.
            var model = _plugin.WheelModelInfo;   // null until identity resolves
            if (_detectionState.NewWheelDetected && model != null)
            {
                // Capability snapshot for the (now identified) active wheel.
                int rpmCount = model.RpmLedCount;
                int btnCount = model.ButtonLedCount;
                bool hasRpm        = rpmCount > 0;
                bool hasBtn        = btnCount > 0;
                bool hasKnob       = model.KnobCount > 0;
                bool hasSleepLight = model.HasSleepLight;

                if (telemMode      >= 0            && WheelCfgChanged("wheel-telemetry-mode", telemMode))          _deviceManager.WriteSetting("wheel-telemetry-mode", telemMode);
                if (idleEffect     >= 0 && hasRpm  && WheelCfgChanged("wheel-telemetry-idle-effect", idleEffect)) _deviceManager.WriteSetting("wheel-telemetry-idle-effect", idleEffect);
                if (btnIdleEffect  >= 0 && hasBtn  && WheelCfgChanged("wheel-buttons-idle-effect", btnIdleEffect))_deviceManager.WriteSetting("wheel-buttons-idle-effect", btnIdleEffect);
                if (knobIdleEffect >= 0 && hasKnob && WheelCfgChanged("wheel-knob-idle-effect", knobIdleEffect))  _deviceManager.WriteSetting("wheel-knob-idle-effect", knobIdleEffect);
                if (knobLedMode    >= 0 && hasKnob && WheelCfgChanged("wheel-knob-led-mode", knobLedMode))        _deviceManager.WriteSetting("wheel-knob-led-mode", knobLedMode);
                if (btnLedMode     >= 0 && hasBtn  && WheelCfgChanged("wheel-buttons-led-mode", btnLedMode))      _deviceManager.WriteSetting("wheel-buttons-led-mode", btnLedMode);
                if (idleEffect >= 0 && idleSpeed >= 0 && hasRpm
                        && WheelCfgChanged("wheel-telemetry-idle-interval", ((long)idleEffect << 32) | (uint)idleSpeed))
                    _deviceManager.WriteArray("wheel-telemetry-idle-interval",
                        BuildIdleIntervalPayload(idleEffect, idleSpeed));
                if (btnIdleEffect >= 0 && btnIdleSpeed >= 0 && hasBtn
                        && WheelCfgChanged("wheel-buttons-idle-interval", ((long)btnIdleEffect << 32) | (uint)btnIdleSpeed))
                    _deviceManager.WriteArray("wheel-buttons-idle-interval",
                        BuildIdleIntervalPayload(btnIdleEffect, btnIdleSpeed));
                if (knobIdleEffect >= 0 && knobIdleSpeed >= 0 && hasKnob
                        && WheelCfgChanged("wheel-knob-idle-interval", ((long)knobIdleEffect << 32) | (uint)knobIdleSpeed))
                    _deviceManager.WriteArray("wheel-knob-idle-interval",
                        BuildIdleIntervalPayload(knobIdleEffect, knobIdleSpeed));
                if (sleepMode    >= 0 && hasSleepLight && WheelCfgChanged("wheel-idle-mode", sleepMode))       _deviceManager.WriteSetting("wheel-idle-mode", sleepMode);
                if (sleepTimeout >= 0 && hasSleepLight && WheelCfgChanged("wheel-idle-timeout", sleepTimeout)) _deviceManager.WriteSetting("wheel-idle-timeout", sleepTimeout);
                if (sleepMode >= 0 && sleepSpeed >= 0 && hasSleepLight
                        && WheelCfgChanged("wheel-idle-speed", ((long)sleepMode << 32) | (uint)sleepSpeed))
                    _deviceManager.WriteArray("wheel-idle-speed",
                        BuildIdleIntervalPayload(sleepMode, sleepSpeed));
                if (sleepColor != null && sleepColor.Length > 0 && hasSleepLight)
                {
                    var rgb = MozaProfile.UnpackColor(sleepColor[0]);
                    if (WheelCfgChanged("wheel-idle-color", ((long)rgb[0] << 16) | ((long)rgb[1] << 8) | rgb[2]))
                        _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                if (rpmBri   >= 0 && hasRpm && WheelCfgChanged("wheel-rpm-brightness", rpmBri))     _deviceManager.WriteSetting("wheel-rpm-brightness", rpmBri);
                if (btnBri   >= 0 && hasBtn && WheelCfgChanged("wheel-buttons-brightness", btnBri)) _deviceManager.WriteSetting("wheel-buttons-brightness", btnBri);
                if (flagsBri >= 0 && _detectionState.DashDetected && WheelCfgChanged("dash-flags-brightness", flagsBri))
                    _deviceManager.WriteSetting("dash-flags-brightness", flagsBri);

                if (WheelCfgChangedArr("wheel-rpm-color", rpmColors))
                    WriteColorArray(rpmColors, "wheel-rpm-color", rpmCount);
                if (hasRpm && WheelCfgChangedArr("wheel-rpm-blink-color", rpmBlinkColors))
                    WriteColorArray(rpmBlinkColors, "wheel-rpm-blink-color", Math.Min(10, rpmCount));
                bool btnColChg = WheelCfgChangedArr("wheel-button-color", buttonColors);
                bool btnDefChg = WheelCfgChangedArr("wheel-button-defaults", buttonDefaults);
                if (btnColChg || btnDefChg) WriteButtonStaticColors(buttonColors, model);
                if (_detectionState.DashDetected && WheelCfgChangedArr("dash-flag-color", flagColors))
                    WriteColorArray(flagColors, "dash-flag-color", 6);
                if (idleColor != null && idleColor.Length > 0 && hasSleepLight)
                {
                    var rgb = MozaProfile.UnpackColor(idleColor[0]);
                    if (WheelCfgChanged("wheel-idle-color", ((long)rgb[0] << 16) | ((long)rgb[1] << 8) | rgb[2]))
                        _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                bool knobBgChg  = WheelCfgChangedArr("wheel-knob-bg-color", knobBgColors);
                bool knobPriChg = WheelCfgChangedArr("wheel-knob-primary-color", knobPrimaryColors);
                bool knobRingChg = WheelCfgChangedArr("wheel-knob-ring-color", knobRingColors)
                                   | WheelCfgChanged("wheel-knob-ring-brightness", knobRingBri);
                // Invalidate the live cache after each Apply pass so the next live tick
                // re-sends instead of dedup'ing against a frame whose underlying wheel
                // state we may have just rewritten. Live cache is volatile (no flash
                // cost), so keep this unconditional as before.
                MozaLedDeviceManager.InvalidateLiveCacheAny(
                    LedKind.Rpm | LedKind.Button | LedKind.Flag);
                if (knobBgChg || knobPriChg) WriteKnobColors(knobBgColors, knobPrimaryColors);
                if (knobRingChg) WriteKnobRingColors(knobRingColors, knobRingBri);

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
                if (rpmInd   >= 0 && WheelCfgChanged("wheel-rpm-indicator-mode", rpmInd))     _deviceManager.WriteSetting("wheel-rpm-indicator-mode", rpmInd + 1);
                if (rpmDisp  >= 0 && WheelCfgChanged("wheel-set-rpm-display-mode", rpmDisp))   _deviceManager.WriteSetting("wheel-set-rpm-display-mode", rpmDisp);
                if (esRpmBri >= 0 && WheelCfgChanged("wheel-old-rpm-brightness", esRpmBri))    _deviceManager.WriteSetting("wheel-old-rpm-brightness", esRpmBri);
                if (WheelCfgChangedArr("wheel-old-rpm-color", esRpmColors))
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
            if (profile.DashRpmIndicatorMode   >= 0) _data.DashRpmIndicatorMode   = profile.DashRpmIndicatorMode;
            if (profile.DashRpmDisplayMode     >= 0) _data.DashRpmDisplayMode     = profile.DashRpmDisplayMode;
            if (profile.DashFlagsIndicatorMode >= 0) _data.DashFlagsIndicatorMode = profile.DashFlagsIndicatorMode;
            MozaProfile.UnpackColorsInto(profile.DashRpmColors, _data.DashRpmColors);
            MozaProfile.UnpackColorsInto(profile.DashRpmBlinkColors, _data.DashRpmBlinkColors);
            MozaProfile.UnpackColorsInto(profile.DashFlagColors, _data.DashFlagColors);

            // Per-section gate only — _data.IsConnected check dropped for
            // the hot-reload-with-persistent-wire case (see ApplyWheelToHardware
            // comment). _deviceManager.WriteX bails on a dead wire.
            if (!_detectionState.DashDetected) return;

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
                if (profile.DashRpmIndicatorMode >= 0)
                    _deviceManager.WriteSetting("dash-rpm-indicator-mode", profile.DashRpmIndicatorMode);
                if (profile.DashRpmDisplayMode >= 0)
                    _deviceManager.WriteSetting("dash-rpm-display-mode", profile.DashRpmDisplayMode);
                // Legacy SHDP dashboard: dash-flags-indicator-mode defaults to 1
                // when the profile has no stored value (firmware default 0 silently
                // drops flag colour writes); a saved profile value wins.
                _deviceManager.WriteSetting("dash-flags-indicator-mode",
                    profile.DashFlagsIndicatorMode >= 0 ? profile.DashFlagsIndicatorMode : 1);

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

            // STANDBY per-LED colors only (idle appearance, shown when no game
            // is running). rs21_parameter.db: SetIndicatorGroupStandbyModeColor
            // (0x1B 00 FF <i>) + RGB. RPM colors 1-10 → cm2-stored-color1..10,
            // flag colors 1-6 → cm2-stored-color11..16.
            //
            // The LIVE/active colors (0x0B) are NOT written here — they are
            // pushed per-frame through the SimHub LED pipeline
            // (MozaDashLedDeviceManager.SendCm2LiveColors), exactly like the
            // wheel's RPM LEDs. Config sets up the device + idle look only.
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

            // Per-section gate only — see ApplyWheelToHardware comment for why
            // _data.IsConnected was dropped here.
            if (!_detectionState.BaseAmbientLedSupported) return;
            if (profile.BaseAmbientBrightness     >= 0) BaseManager.WriteSetting("base-ambient-brightness", profile.BaseAmbientBrightness);
            if (profile.BaseAmbientStandbyMode    >= 0) BaseManager.WriteSetting("base-ambient-standby-mode", profile.BaseAmbientStandbyMode);
            if (profile.BaseAmbientIndicatorState >= 0) BaseManager.WriteSetting("base-ambient-indicator-state", profile.BaseAmbientIndicatorState);
            if (profile.BaseAmbientSleepMode      >= 0) BaseManager.WriteSetting("base-ambient-sleep-mode", profile.BaseAmbientSleepMode);
            if (profile.BaseAmbientSleepTimeout   >= 0) BaseManager.WriteSetting("base-ambient-sleep-timeout", profile.BaseAmbientSleepTimeout);
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
            var dm = HandbrakeManager;
            if (profile.HandbrakeMode            >= 0) dm.WriteSetting("handbrake-mode", profile.HandbrakeMode);
            if (profile.HandbrakeButtonThreshold >= 0) dm.WriteSetting("handbrake-button-threshold", profile.HandbrakeButtonThreshold);
            if (profile.HandbrakeDirection       >= 0) dm.WriteSetting("handbrake-direction", profile.HandbrakeDirection);
            if (profile.HandbrakeMin             >= 0) dm.WriteSetting("handbrake-min", profile.HandbrakeMin);
            if (profile.HandbrakeMax             >= 0) dm.WriteSetting("handbrake-max", profile.HandbrakeMax);
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                    dm.WriteFloat($"handbrake-y{i + 1}", profile.HandbrakeCurve[i]);
            }
        }

        /// <summary>Push pedal settings. No-op unless detected.</summary>
        public void ApplyPedalsToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.PedalsThrottleDir      >= 0) _data.PedalsThrottleDir      = profile.PedalsThrottleDir;
            if (profile.PedalsThrottleMin      >= 0) _data.PedalsThrottleMin      = profile.PedalsThrottleMin;
            if (profile.PedalsThrottleMax      >= 0) _data.PedalsThrottleMax      = profile.PedalsThrottleMax;
            if (profile.PedalsBrakeDir         >= 0) _data.PedalsBrakeDir         = profile.PedalsBrakeDir;
            if (profile.PedalsBrakeMin         >= 0) _data.PedalsBrakeMin         = profile.PedalsBrakeMin;
            if (profile.PedalsBrakeMax         >= 0) _data.PedalsBrakeMax         = profile.PedalsBrakeMax;
            if (profile.PedalsClutchDir        >= 0) _data.PedalsClutchDir        = profile.PedalsClutchDir;
            if (profile.PedalsClutchMin        >= 0) _data.PedalsClutchMin        = profile.PedalsClutchMin;
            if (profile.PedalsClutchMax        >= 0) _data.PedalsClutchMax        = profile.PedalsClutchMax;
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
            var dm = PedalsManager;
            if (profile.PedalsThrottleDir      >= 0) dm.WriteSetting("pedals-throttle-dir", profile.PedalsThrottleDir);
            if (profile.PedalsThrottleMin      >= 0) dm.WriteSetting("pedals-throttle-min", profile.PedalsThrottleMin);
            if (profile.PedalsThrottleMax      >= 0) dm.WriteSetting("pedals-throttle-max", profile.PedalsThrottleMax);
            if (profile.PedalsBrakeDir         >= 0) dm.WriteSetting("pedals-brake-dir", profile.PedalsBrakeDir);
            if (profile.PedalsBrakeMin         >= 0) dm.WriteSetting("pedals-brake-min", profile.PedalsBrakeMin);
            if (profile.PedalsBrakeMax         >= 0) dm.WriteSetting("pedals-brake-max", profile.PedalsBrakeMax);
            if (profile.PedalsClutchDir        >= 0) dm.WriteSetting("pedals-clutch-dir", profile.PedalsClutchDir);
            if (profile.PedalsClutchMin        >= 0) dm.WriteSetting("pedals-clutch-min", profile.PedalsClutchMin);
            if (profile.PedalsClutchMax        >= 0) dm.WriteSetting("pedals-clutch-max", profile.PedalsClutchMax);
            if (profile.PedalsBrakeAngleRatio  >= 0) dm.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio);
            if (profile.PedalsThrottleCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsThrottleCurve.Length); i++)
                    dm.WriteFloat($"pedals-throttle-y{i + 1}", profile.PedalsThrottleCurve[i]);
            if (profile.PedalsBrakeCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsBrakeCurve.Length); i++)
                    dm.WriteFloat($"pedals-brake-y{i + 1}", profile.PedalsBrakeCurve[i]);
            if (profile.PedalsClutchCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsClutchCurve.Length); i++)
                    dm.WriteFloat($"pedals-clutch-y{i + 1}", profile.PedalsClutchCurve[i]);
        }

        /// <summary>
        /// Push base/FFB settings (motor limits, FFB curve breakpoints) to the
        /// wheelbase. _data is mirrored always; writes gated on base-connected.
        /// </summary>
        public void ApplyBaseToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // Debug-level — fires on every game switch / wheel-detect /
            // dashboard re-apply, which is too noisy for SimHub.txt. The
            // in-process MozaLog ring buffer still records it so future bug
            // reports can pull it from the Diagnostics tab export.
            // Show both the persisted BaseDetected (the actual gate) and the
            // volatile IsBaseConnected (which is false on the hot-reload that
            // ate the writes before the 2026-05-27 gate fix).
            MozaLog.Debug(
                $"[AZOM] ApplyBaseToHardware '{profile.Name}': " +
                $"Limit={profile.Limit} ({(profile.Limit >= 0 ? (profile.Limit * 2) + "°" : "skip")}), " +
                $"FfbStrength={profile.FfbStrength}, Torque={profile.Torque}, Speed={profile.Speed}, " +
                $"BaseDetected={_detectionState.BaseDetected}, " +
                $"_data.IsBaseConnected={_data.IsBaseConnected}, baseSettingsRead={_data.BaseSettingsRead}");

            // Each call below is the SOLE site that names a base/motor field
            // in this method. The helper handles the full lifecycle in one
            // pass: sentinel→seed-from-_data (so SimHub-auto-created profiles
            // inherit current device state instead of silently skipping the
            // write — the 2026-05-27 "rotation angle carries over to new
            // profile" pattern) → mirror to _data → write to the wire when
            // BaseDetected (persisted across plugin reloads). Adding a new
            // base/motor setting requires one line here, one line each in
            // MozaProfile.CopyProfilePropertiesFrom and CaptureFromCurrent,
            // and the field declaration itself — no parallel seed list to
            // drift out of sync.
            Apply(() => profile.Limit,              v => profile.Limit              = v,
                  () => _data.Limit,                v => { _data.Limit = v; _data.MaxAngle = v; },
                  "base-limit", "base-max-angle");
            Apply(() => profile.FfbStrength,        v => profile.FfbStrength        = v,
                  () => _data.FfbStrength,          v => _data.FfbStrength          = v,
                  "base-ffb-strength");
            Apply(() => profile.Torque,             v => profile.Torque             = v,
                  () => _data.Torque,               v => _data.Torque               = v,
                  "base-torque");
            Apply(() => profile.Speed,              v => profile.Speed              = v,
                  () => _data.Speed,                v => _data.Speed                = v,
                  "base-speed");
            Apply(() => profile.Damper,             v => profile.Damper             = v,
                  () => _data.Damper,               v => _data.Damper               = v,
                  "base-damper");
            Apply(() => profile.Friction,           v => profile.Friction           = v,
                  () => _data.Friction,             v => _data.Friction             = v,
                  "base-friction");
            Apply(() => profile.Inertia,            v => profile.Inertia            = v,
                  () => _data.Inertia,              v => _data.Inertia              = v,
                  "base-inertia");
            Apply(() => profile.Spring,             v => profile.Spring             = v,
                  () => _data.Spring,               v => _data.Spring               = v,
                  "base-spring");
            Apply(() => profile.SpeedDamping,       v => profile.SpeedDamping       = v,
                  () => _data.SpeedDamping,         v => _data.SpeedDamping         = v,
                  "base-speed-damping");
            Apply(() => profile.SpeedDampingPoint,  v => profile.SpeedDampingPoint  = v,
                  () => _data.SpeedDampingPoint,    v => _data.SpeedDampingPoint    = v,
                  "base-speed-damping-point");
            Apply(() => profile.NaturalInertia,     v => profile.NaturalInertia     = v,
                  () => _data.NaturalInertia,       v => _data.NaturalInertia       = v,
                  "base-natural-inertia");
            Apply(() => profile.SoftLimitStiffness, v => profile.SoftLimitStiffness = v,
                  () => _data.SoftLimitStiffness,   v => _data.SoftLimitStiffness   = v,
                  "base-soft-limit-stiffness");
            Apply(() => profile.SoftLimitRetain,    v => profile.SoftLimitRetain    = v,
                  () => _data.SoftLimitRetain,      v => _data.SoftLimitRetain      = v,
                  "base-soft-limit-retain");
            Apply(() => profile.FfbReverse,         v => profile.FfbReverse         = v,
                  () => _data.FfbReverse,           v => _data.FfbReverse           = v,
                  "base-ffb-reverse");
            Apply(() => profile.Protection,         v => profile.Protection         = v,
                  () => _data.Protection,           v => _data.Protection           = v,
                  "base-protection");
            Apply(() => profile.GameDamper,         v => profile.GameDamper         = v,
                  () => _data.GameDamper,           v => _data.GameDamper           = v,
                  "main-set-damper-gain");
            Apply(() => profile.GameFriction,       v => profile.GameFriction       = v,
                  () => _data.GameFriction,         v => _data.GameFriction         = v,
                  "main-set-friction-gain");
            Apply(() => profile.GameInertia,        v => profile.GameInertia        = v,
                  () => _data.GameInertia,          v => _data.GameInertia          = v,
                  "main-set-inertia-gain");
            Apply(() => profile.GameSpring,         v => profile.GameSpring         = v,
                  () => _data.GameSpring,           v => _data.GameSpring           = v,
                  "main-set-spring-gain");
            Apply(() => profile.WorkMode,           v => profile.WorkMode           = v,
                  () => _data.WorkMode,             v => _data.WorkMode             = v,
                  "main-set-work-mode");
            Apply(() => profile.GearshiftVibration, v => profile.GearshiftVibration = v,
                  () => _data.GearshiftVibration,   v => _data.GearshiftVibration   = v,
                  "base-gearshift-vibration");
            Apply(() => profile.TempStrategy,       v => profile.TempStrategy       = v,
                  () => _data.TempStrategy,         v => _data.TempStrategy         = v,
                  "base-temp-strategy");

            // Local helper — does seed + mirror + write in one pass. Closes
            // over `profile` and `_data` via the enclosing scope so callers
            // only need to provide the field accessors and command names.
            void Apply(
                Func<int> profileGet, Action<int> profileSet,
                Func<int> dataGet,    Action<int> dataSet,
                params string[] commands)
            {
                int val = profileGet();
                if (val < 0)
                {
                    // Profile field at sentinel — seed from current device state.
                    // Requires BaseSettingsRead so we don't propagate uninitialized
                    // zeros from a fresh MozaData (hot-reload before first echo).
                    if (!_data.BaseSettingsRead) return;
                    int seed = dataGet();
                    if (seed < 0) return;  // _data sentinel too — nothing to apply
                    val = seed;
                    profileSet(val);
                }
                dataSet(val);
                if (_detectionState.BaseDetected)
                    foreach (var cmd in commands)
                        BaseManager.WriteSetting(cmd, val);
            }

            // FFB Equalizer (sentinel = -1000): mirror always, write when live.
            // Gate on the persisted BaseDetected (not volatile _data.IsBaseConnected)
            // for the same reason as ApplyBaseSettingIfSet — see the comment there.
            void ApplyEq(int val, Action<int> setData, string cmd)
            {
                if (val <= -1000) return;
                setData(val);
                if (_detectionState.BaseDetected) BaseManager.WriteSetting(cmd, val);
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
            // Persisted BaseDetected gate (see ApplyBaseSettingIfSet comment).
            if (!_detectionState.BaseDetected) return;
            // X breakpoints always written when live (device doesn't persist them).
            BaseManager.WriteSetting("base-ffb-curve-x1", 20);
            BaseManager.WriteSetting("base-ffb-curve-x2", 40);
            BaseManager.WriteSetting("base-ffb-curve-x3", 60);
            BaseManager.WriteSetting("base-ffb-curve-x4", 80);
            BaseManager.WriteSetting("base-ffb-curve-y1", _data.FfbCurveY1);
            BaseManager.WriteSetting("base-ffb-curve-y2", _data.FfbCurveY2);
            BaseManager.WriteSetting("base-ffb-curve-y3", _data.FfbCurveY3);
            BaseManager.WriteSetting("base-ffb-curve-y4", _data.FfbCurveY4);
            BaseManager.WriteSetting("base-ffb-curve-y5", _data.FfbCurveY5);
        }

        /// <summary>
        /// Push AB9 active-shifter settings to the AB9 manager. No-op unless AB9
        /// is detected/connected. A profile with no Ab9 block applies factory
        /// defaults so the device follows the active per-game profile (reset
        /// semantics) instead of retaining the previously-applied profile's
        /// settings.
        /// </summary>
        public void ApplyAb9ToHardware(MozaProfile? profile)
        {
            if (!_detectionState.Ab9Detected || _ab9Manager == null || !_ab9Manager.IsConnected) return;

            var ab9 = profile?.Ab9 ?? new Ab9Settings();
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
                MozaLog.Warn("[AZOM] Profile has zeroed base settings — resetting to sentinels");
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
            MozaLog.Debug($"[AZOM] Applying wheel device extension settings (prefix={pageModelPrefix ?? "(null)"})");

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
            MozaLog.Debug("[AZOM] Applying dash device extension settings");

            var settings = _plugin.Settings;
            extSettings.ApplyTo(settings!, _data, settings?.ProfileStore?.CurrentProfile);
            ApplyDashToHardware(settings?.ProfileStore?.CurrentProfile);
            _plugin.PersistSettings();
        }

        public void ApplyBaseExtensionSettings(MozaBaseExtensionSettings extSettings)
        {
            MozaLog.Debug("[AZOM] Applying base ambient device extension settings");

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
        // The "base connected" gate uses the persisted DetectionState flag
        // (set on first base-mcu-temp echo and preserved across SimHub plugin
        // reloads via s_persistentDetectionState), not the volatile
        // _data.IsBaseConnected — see the ApplyBaseSettingIfSet comment for
        // the hot-reload rationale. _deviceManager.WriteSetting still bails
        // on a dead wire, so this is correct even mid-reconnect.
        public void WriteIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.BaseDetected) BaseManager.WriteSetting(command, value);
        }
        public void WriteFloatIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.BaseDetected) BaseManager.WriteFloat(command, value);
        }
        public void WriteIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.HandbrakeDetected) HandbrakeManager.WriteSetting(command, value);
        }
        public void WriteFloatIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.HandbrakeDetected) HandbrakeManager.WriteFloat(command, value);
        }
        public void WriteIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.PedalsDetected) PedalsManager.WriteSetting(command, value);
        }
        public void WriteFloatIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.PedalsDetected) PedalsManager.WriteFloat(command, value);
        }
        public void WriteIfBaseAmbientSupported(string command, int value)
        {
            if (value < 0) return;
            if (_detectionState.BaseAmbientLedSupported) BaseManager.WriteSetting(command, value);
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

            // Per-group SimHub-mode gate, symmetric with the read-side gate
            // in MozaWheelSettingsControl.WheelTabs_SelectionChanged. Mode
            // values per group (MozaData): 0=Off, 1=SimHub/telemetry-driven,
            // 2=Static. Suppress UI-initiated static-color writes when the
            // group is in SimHub mode — the live pipeline owns the wheel's
            // color registers for that group, and a static write briefly
            // clobbers the live frame buffer until the next live frame
            // overpaints (visible 1-keepalive flicker). Caller writes to
            // _data + persisted overlay BEFORE invoking us, so the user's
            // intent survives — it just doesn't reach the wheel while live
            // is rendering this group. When the user switches the group
            // back to Static, the mode-change handler in
            // MozaWheelSettingsControl re-pushes the stored palette so the
            // EEPROM catches up with anything edited during SimHub mode.
            int? groupMode = kind switch
            {
                LedKind.Rpm    => _data.WheelTelemetryMode,
                LedKind.Button => _data.WheelButtonsLedMode,
                LedKind.Knob   => _data.WheelKnobLedMode,
                _ => (int?)null,  // Flag / None / combined: no mode tracking, write through
            };
            if (groupMode == 1)
            {
                MozaLog.Debug(
                    $"[AZOM] LED write '{command}' suppressed: group {kind} in SimHub mode " +
                    "(live pipeline owns the frame buffer; _data and overlay updated regardless)");
                return;
            }

            _deviceManager.WriteColor(command, r, g, b);
            MozaLedDeviceManager.InvalidateLiveCacheAny(kind);
        }

        /// <summary>
        /// Re-push the stored static palette for a group from <c>_data</c> to the
        /// wheel's EEPROM. Called from the per-group mode combo handlers when the
        /// user transitions a group to Static mode (val=2). Required because
        /// <see cref="WriteLedColorIfWheelDetected"/> suppresses static-color
        /// writes while the group is in SimHub mode; the suppressed writes still
        /// land in <c>_data</c> and the persisted overlay, but the wheel EEPROM
        /// falls out of sync. Re-pushing on transition-to-Static brings EEPROM
        /// back to match <c>_data</c> so the static colors the user picked while
        /// in SimHub mode actually appear when they switch back.
        ///
        /// Bypasses the SimHub-mode gate intentionally: this is invoked AFTER
        /// the mode flip, when the group is already in Static, so the gate
        /// would allow writes anyway — going direct keeps the call independent
        /// of any future gate changes.
        /// </summary>
        public void RepushStaticPalette(LedKind kind)
        {
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected) return;
            var model = _plugin.WheelModelInfo;
            if (model == null) return;

            switch (kind)
            {
                case LedKind.Rpm:
                {
                    int count = model.RpmLedCount;
                    if (count <= 0 || !_detectionState.IsWheelLedGroupPresent(0)) return;
                    var src = _data.WheelRpmColors;
                    int len = Math.Min(src.Length, count);
                    for (int i = 0; i < len; i++)
                    {
                        var rgb = src[i];
                        _deviceManager.WriteColor($"wheel-rpm-color{i + 1}", rgb[0], rgb[1], rgb[2]);
                    }
                    MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Rpm);
                    break;
                }
                case LedKind.Button:
                {
                    int count = model.ButtonLedCount;
                    if (count <= 0 || !_detectionState.IsWheelLedGroupPresent(1)) return;
                    var src = _data.WheelButtonColors;
                    int len = Math.Min(src.Length, count);
                    for (int i = 0; i < len; i++)
                    {
                        var rgb = src[i];
                        _deviceManager.WriteColor($"wheel-button-color{i + 1}", rgb[0], rgb[1], rgb[2]);
                    }
                    MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Button);
                    break;
                }
                case LedKind.Knob:
                {
                    int knobs = model.KnobCount;
                    if (knobs <= 0) return;

                    // Per-knob "Active" LED color (cmd 0x27 ROLE=0).
                    var prim = _data.WheelKnobPrimaryColors;
                    int primLen = Math.Min(prim.Length, knobs);
                    for (int i = 0; i < primLen; i++)
                    {
                        var rgb = prim[i];
                        _deviceManager.WriteColor($"wheel-knob{i + 1}-active-color", rgb[0], rgb[1], rgb[2]);
                    }

                    // Per-ring-LED "background" color (cmd 0x1F 0x03 0x01).
                    if (model.KnobRingLeds != null && _detectionState.IsWheelLedGroupPresent(3))
                    {
                        var bg = _data.WheelKnobBackgroundColors;
                        int bgLen = Math.Min(bg.Length, model.KnobRingLedTotal);
                        for (int i = 0; i < bgLen; i++)
                        {
                            var rgb = bg[i];
                            _deviceManager.WriteColor($"wheel-knob-bg-color{i + 1}", rgb[0], rgb[1], rgb[2]);
                        }
                    }
                    MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Knob);
                    break;
                }
            }
        }

        public void WriteColorIfDashDetected(string command, byte r, byte g, byte b)
        {
            if (_detectionState.DashDetected) _deviceManager.WriteColor(command, r, g, b);
        }
        public void WriteColorIfBaseAmbientSupported(string command, byte r, byte g, byte b)
        {
            if (_detectionState.BaseAmbientLedSupported) BaseManager.WriteColor(command, r, g, b);
        }
        public void WriteArrayIfWheelDetected(string command, byte[] payload)
        {
            if (_detectionState.NewWheelDetected || _detectionState.OldWheelDetected)
                _deviceManager.WriteArray(command, payload);
        }

        // ===== Per-cluster sentinel-guarded helpers =====
        //
        // Base/motor settings used to live in a public ApplyBaseSettingIfSet
        // here, called once per field from ApplyBaseToHardware alongside a
        // parallel seed-from-_data block. Those merged into the local `Apply`
        // helper inside ApplyBaseToHardware (one call site per field, seed +
        // mirror + write in one pass) — single source of truth, no parallel
        // list to drift out of sync. See the comment block above the helper.

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
        /// Write the persisted static button colours. <c>WheelButtonColors</c> is
        /// protocol-indexed (14 slots); on a non-contiguous wheel (e.g. CS V2.1 →
        /// protocol indices 0,1,3,6,8,9) a flat 0..N-1 loop writes phantom slots and
        /// skips the high physical buttons. Drive each mapped protocol index directly
        /// (`wheel-button-color{p+1}` addresses protocol index p) so 6/8/9 aren't lost.
        /// Contiguous-button wheels (ButtonLedMap == null) keep the flat write.
        /// </summary>
        private void WriteButtonStaticColors(int[]? packedColors, WheelModelInfo model)
        {
            if (packedColors == null) return;
            int[]? map = model.ButtonLedMap;
            if (map == null)
            {
                WriteColorArray(packedColors, "wheel-button-color", model.ButtonLedCount);
                return;
            }
            foreach (int p in map)
            {
                if (p < 0 || p >= packedColors.Length) continue;
                var rgb = MozaProfile.UnpackColor(packedColors[p]);
                _deviceManager.WriteColor($"wheel-button-color{p + 1}", rgb[0], rgb[1], rgb[2]);
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
            // Only the base-ambient startup/shutdown colours route here — target
            // the base-owning pipe (see BaseManager).
            BaseManager.WriteColor(command, r, g, b);
        }

        /// <summary>Send all-off to wheel and dash LEDs via the device manager.</summary>
        public void ClearLedsOnHardware()
        {
            if (_plugin.Connection == null || !_plugin.Connection.IsConnected) return;
            var modelInfo = _plugin.WheelModelInfo;
            int rpmCount = modelInfo?.RpmLedCount ?? 0;
            int rpmWindow = rpmCount > 0 ? (1 << rpmCount) - 1 : 0;
            // 8-byte active+window form (active=0 = all off), matching the live path.
            _deviceManager.WriteArray("wheel-send-rpm-telemetry",
                MozaLedDeviceManager.BuildWindowedBitmaskBytes(0, rpmWindow));
            _deviceManager.WriteArray("wheel-send-buttons-telemetry",
                MozaLedDeviceManager.BuildWindowedBitmaskBytes(0, modelInfo?.ButtonWindowMask ?? 0));
            _deviceManager.WriteSetting("wheel-old-send-telemetry", 0);
            _deviceManager.WriteSetting("dash-send-telemetry", 0);
        }
    }
}
