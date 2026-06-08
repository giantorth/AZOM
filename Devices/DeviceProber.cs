using System;
using System.Collections.Generic;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Decodes command responses and flips per-device detection flags on first sight.
    /// </summary>
    internal sealed class DeviceProber
    {
        // Per-device settings read commands. Sent after the corresponding
        // device is detected, rather than blasting all commands on connect.

        internal static readonly string[] BaseSettingsReadCommands = new[]
        {
            "base-limit", "base-ffb-strength", "base-torque", "base-speed",
            "base-damper", "base-friction", "base-inertia", "base-spring",
            "base-protection", "base-natural-inertia",
            "base-speed-damping", "base-speed-damping-point",
            "base-soft-limit-stiffness", "base-soft-limit-retain",
            "base-ffb-reverse", "base-temp-strategy", "base-gearshift-vibration",
            "main-get-work-mode", "main-get-led-status",
            "main-get-damper-gain", "main-get-friction-gain",
            "main-get-inertia-gain", "main-get-spring-gain",
            "main-get-ble-mode",
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6",
            "base-ffb-curve-y1", "base-ffb-curve-y2", "base-ffb-curve-y3", "base-ffb-curve-y4", "base-ffb-curve-y5",
        };

        /// <summary>
        /// Per-wheel reads that don't depend on which LEDs are present. Sent at
        /// new-protocol detection regardless of the wheel model. LED-related
        /// reads (per-zone modes, per-zone brightness, per-LED colors, LED
        /// group probes) are deferred to <see cref="BuildNewWheelLedReadCommands"/>
        /// once <see cref="WheelModelInfo"/> has resolved — this stops the
        /// plugin hammering wheels (e.g. the original CS, which has only RPM
        /// LEDs) with reads for buttons / flags / knobs they can't service.
        /// PendingResponseTracker burns its retry budget on those timeouts.
        /// </summary>
        internal static readonly string[] NewWheelCoreReadCommands = new[]
        {
            "wheel-telemetry-mode",
            // Input modes — paddles/clutch/stick exist on every new-protocol wheel.
            // Knob input modes (wheel-knob-mode, wheel-knob-signal-modeN) are
            // gated below on WheelModelInfo.KnobCount.
            //
            // Sleep-light reads (wheel-idle-mode/timeout/speed/color) are
            // deliberately NOT here. This list is issued at first wheel-detect,
            // before wheel-model-name resolves, so it cannot be gated on
            // capability. The legacy bare-"CS" wheel (HasSleepLight=false) does
            // not implement those parameters — reading them drives its firmware
            // into a Table 8 read-fail storm that makes it intermittently stop
            // answering presence polls (the plugin then resets it ~every 20 s in
            // a loop). The idle reads are deferred to
            // BuildNewWheelLedReadCommands, gated on WheelModelInfo.HasSleepLight,
            // once the model is known.
            "wheel-paddles-mode", "wheel-clutch-point", "wheel-stick-mode",
        };

        /// <summary>
        /// Build the LED-related per-wheel read list filtered by the wheel's
        /// actual LED layout. Reads omitted for LED groups the wheel doesn't
        /// have keep PendingResponseTracker from churning through retries on
        /// every cold-start of a wheel like the original "CS" (10 RPM LEDs
        /// only) or any future wheel registered in <see cref="WheelModelInfo.KnownModels"/>.
        ///
        /// For wheels mapped to <see cref="WheelModelInfo.Default"/> (firmware
        /// model name not in KnownModels) the LED-group probes still go out so
        /// we can detect Single / Rotary / Ambient groups we don't know about
        /// statically.
        /// </summary>
        internal static string[] BuildNewWheelLedReadCommands(WheelModelInfo? info)
        {
            info ??= WheelModelInfo.Default;
            var cmds = new List<string>();

            // Sleep-light (idle breathing) settings — read only on wheels that
            // implement the feature. Captured into MozaData and seeded into the
            // per-wheel-page WheelSleepByPageGuid bundle via
            // SeedSleepBundleFromResponse. Deferred here (rather than the
            // model-blind NewWheelCoreReadCommands) so they can be gated on
            // HasSleepLight: the legacy bare-"CS" wheel lacks these parameters,
            // and reading them triggers a Table 8 read-fail storm in its
            // firmware that makes it periodically stop answering presence polls.
            // The matching idle-*-interval commands are write-only on the wire
            // (RxGroup=0xFF) so they're not read here; idle-speed (0x22) is
            // readable and is.
            if (info.HasSleepLight)
            {
                cmds.Add("wheel-idle-mode");
                cmds.Add("wheel-idle-timeout");
                cmds.Add("wheel-idle-speed");
                cmds.Add("wheel-idle-color");
            }

            // Per-zone LED modes + brightness + idle effect, gated on whether
            // the wheel actually has that zone.
            if (info.RpmLedCount > 0)
            {
                cmds.Add("wheel-rpm-brightness");
                cmds.Add("wheel-telemetry-idle-effect");
            }
            if (info.ButtonLedCount > 0)
            {
                cmds.Add("wheel-buttons-led-mode");
                cmds.Add("wheel-buttons-brightness");
                cmds.Add("wheel-buttons-idle-effect");
            }
            if (info.KnobCount > 0)
            {
                cmds.Add("wheel-knob-led-mode");
                cmds.Add("wheel-knob-idle-effect");
                // Knob input config (encoder signal mode per knob).
                cmds.Add("wheel-knob-mode");
                for (int i = 0; i < info.KnobCount && i < 5; i++)
                    cmds.Add($"wheel-knob-signal-mode{i}");
            }

            // Per-LED color reads, capped at the LED count this wheel reports.
            for (int i = 1; i <= info.RpmLedCount; i++)
                cmds.Add($"wheel-rpm-color{i}");
            for (int i = 1; i <= info.ButtonLedCount; i++)
                cmds.Add($"wheel-button-color{i}");
            if (info.HasFlagLeds)
            {
                for (int i = 1; i <= 6; i++)
                    cmds.Add($"wheel-flag-color{i}");
            }

            // Extended LED group probes (Single/Rotary/Ambient). For known
            // models the WheelModelInfo already tells us what's there; only
            // probe when the model is unknown, or when the model claims knobs
            // (so wheel-knob-brightness lights the Rotary-group presence flag
            // used by knob-ring writes).
            bool isUnknown = ReferenceEquals(info, WheelModelInfo.Default);
            if (isUnknown)
            {
                cmds.Add("wheel-single-brightness");
                cmds.Add("wheel-ambient-brightness");
            }
            if (info.KnobCount > 0 || isUnknown)
            {
                cmds.Add("wheel-knob-brightness");
            }

            return cmds.ToArray();
        }

        internal static readonly string[] OldWheelSettingsReadCommands = new[]
        {
            "wheel-rpm-indicator-mode", "wheel-get-rpm-display-mode",
            "wheel-old-rpm-brightness",
            "wheel-old-rpm-color1", "wheel-old-rpm-color2", "wheel-old-rpm-color3",
            "wheel-old-rpm-color4", "wheel-old-rpm-color5", "wheel-old-rpm-color6",
            "wheel-old-rpm-color7", "wheel-old-rpm-color8", "wheel-old-rpm-color9",
            "wheel-old-rpm-color10",
        };

        internal static readonly string[] DashSettingsReadCommands = new[]
        {
            "dash-rpm-indicator-mode", "dash-flags-indicator-mode",
            "dash-rpm-display-mode",
            "dash-rpm-brightness", "dash-flags-brightness",
            "dash-rpm-color1", "dash-rpm-color2", "dash-rpm-color3",
            "dash-rpm-color4", "dash-rpm-color5", "dash-rpm-color6",
            "dash-rpm-color7", "dash-rpm-color8", "dash-rpm-color9",
            "dash-rpm-color10",
            "dash-flag-color1", "dash-flag-color2", "dash-flag-color3",
            "dash-flag-color4", "dash-flag-color5", "dash-flag-color6",
        };

        internal static readonly string[] BaseAmbientReadCommands = new[]
        {
            "base-ambient-brightness",
            "base-ambient-standby-mode",
            "base-ambient-indicator-state",
            "base-ambient-sleep-mode",
            "base-ambient-sleep-timeout",
            "base-ambient-startup-color",
            "base-ambient-shutdown-color",
        };

        internal static readonly string[] HandbrakeSettingsReadCommands = new[]
        {
            "handbrake-direction", "handbrake-min", "handbrake-max",
            "handbrake-mode", "handbrake-button-threshold",
            "handbrake-y1", "handbrake-y2", "handbrake-y3", "handbrake-y4", "handbrake-y5",
        };

        internal static readonly string[] PedalsSettingsReadCommands = new[]
        {
            "pedals-throttle-dir", "pedals-throttle-min", "pedals-throttle-max",
            "pedals-brake-dir", "pedals-brake-min", "pedals-brake-max", "pedals-brake-angle-ratio",
            "pedals-clutch-dir", "pedals-clutch-min", "pedals-clutch-max",
            "pedals-throttle-y1", "pedals-throttle-y2", "pedals-throttle-y3", "pedals-throttle-y4", "pedals-throttle-y5",
            "pedals-brake-y1", "pedals-brake-y2", "pedals-brake-y3", "pedals-brake-y4", "pedals-brake-y5",
            "pedals-clutch-y1", "pedals-clutch-y2", "pedals-clutch-y3", "pedals-clutch-y4", "pedals-clutch-y5",
        };

        internal static readonly string[] HubReadCommands = new[]
        {
            "hub-base-power", "hub-port1-power", "hub-port2-power", "hub-port3-power",
            "hub-pedals1-power", "hub-pedals2-power", "hub-pedals3-power",
        };

        private readonly MozaPlugin _plugin;
        private readonly MozaSerialConnection _connection;
        private readonly MozaDeviceManager _deviceManager;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;
        // True for the primary (base/hub) prober, which drives the singular
        // TelemetrySender; false for the dedicated Universal Hub prober, which
        // only enumerates peripherals and must not touch the primary sender's
        // heartbeat mask.
        private readonly bool _drivesTelemetry;

        public DeviceProber(
            MozaPlugin plugin,
            MozaSerialConnection connection,
            MozaDeviceManager deviceManager,
            MozaData data,
            DeviceDetectionState detectionState,
            bool drivesTelemetry = true)
        {
            _plugin = plugin;
            _connection = connection;
            _deviceManager = deviceManager;
            _data = data;
            _detectionState = detectionState;
            _drivesTelemetry = drivesTelemetry;
        }

        /// <summary>
        /// First-sight detection cascade for the dashboard sub-device. Called
        /// from the data-response path (parser case "dash-rpm-indicator-mode")
        /// and from the empty-presence-probe path
        /// (<see cref="MozaPlugin.OnPresenceProbeAck"/>). Idempotent — only the
        /// first call does work.
        /// </summary>
        public void MarkDashDetected()
        {
            if (_detectionState.DashDetected) return;
            _detectionState.DashDetected = true;

            // CM2 reached through the wheelbase is driven at the bridge/main id
            // 0x12 (where PitHouse sends its LED config + telemetry stream).
            // Deploy the CM2 profile and probe its display identity.
            bool cm2BehindBase = _plugin.IsCm2BehindBaseCandidate;
            if (cm2BehindBase)
                _deviceManager.SendDisplayProbe(MozaProtocol.DeviceDash);

            if (DeviceDefinitionDeployer.DeployDashboard(
                    _connection.DiscoveredPid, forceCm2: cm2BehindBase ? true : (bool?)null))
                _plugin.DeviceDefinitionDeployed = true;
            _plugin.ApplyDashToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            // DashSettingsReadCommands are legacy SHDP dash registers (group 0x33).
            // A CM2 is driven by the 0x43 telemetry path, not these — sending them
            // to the CM2 is pointless bleedthrough, so only read them for a legacy dash.
            if (!cm2BehindBase)
                _deviceManager.ReadSettings(DashSettingsReadCommands);
            MozaLog.Info(cm2BehindBase
                ? "[Moza] Dashboard detected (CM2 on wheelbase bus — deployed CM2 profile, probing display identity at 0x12)"
                : "[Moza] Dashboard detected");

            // CM2-on-base: retarget screen telemetry to 0x12 and start it.
            if (cm2BehindBase)
            {
                try { _plugin.ApplyTelemetrySettings(); _plugin.StartTelemetryIfReady(); }
                catch (Exception ex) { MozaLog.Debug($"[Moza] CM2-on-base telemetry start skipped: {ex.Message}"); }
            }

            // Dual-screen: a wheel that has its OWN screen (FSR1 / tier-def display wheel)
            // plus a bus-bridged dash → ensure the concurrent dash pipeline spins up so the
            // CM1 discriminator (or the tier-def CM2 sender) starts. Idempotent / gated.
            try { _plugin.EnsureCm2Pipeline(); }
            catch (Exception ex) { MozaLog.Debug($"[Moza] EnsureCm2Pipeline on dash-detect skipped: {ex.Message}"); }
        }

        /// <summary>First-sight detection cascade for the handbrake sub-device.
        /// <paramref name="issueReads"/> false skips the settings-read cascade —
        /// used by a standalone pipe that has confirmed presence (connect or PID)
        /// but not yet that the device answers our binary protocol, so doomed
        /// reads don't spam the pending tracker.</summary>
        public void MarkHandbrakeDetected(bool issueReads = true)
        {
            if (_detectionState.HandbrakeDetected) return;
            // Record the owning pipe BEFORE flipping the flag so HardwareApplier
            // (which reads flag-then-owner) never sees detected==true paired with
            // a null/stale owner. First responder across the base + hub pipes wins.
            _detectionState.HandbrakeOwner = _deviceManager;
            _detectionState.HandbrakeDetected = true;
            _plugin.ApplyHandbrakeToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            if (issueReads)
                _deviceManager.ReadSettings(HandbrakeSettingsReadCommands);
            MozaLog.Info("[Moza] Handbrake detected");
        }

        /// <summary>First-sight detection cascade for the pedals sub-device.
        /// See <see cref="MarkHandbrakeDetected"/> for <paramref name="issueReads"/>.</summary>
        public void MarkPedalsDetected(bool issueReads = true)
        {
            if (_detectionState.PedalsDetected) return;
            // Owner first, then flag (see MarkHandbrakeDetected). The owning
            // MozaDeviceManager is this prober's — base pipe for the primary
            // prober, hub pipe for the dedicated hub prober.
            _detectionState.PedalsOwner = _deviceManager;
            _detectionState.PedalsDetected = true;
            _plugin.ApplyPedalsToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
            if (issueReads)
                _deviceManager.ReadSettings(PedalsSettingsReadCommands);
            MozaLog.Info("[Moza] Pedals detected");
        }

        /// <summary>
        /// Auto-detect connected devices based on response commands.
        /// First sight of a known response flips the matching detection flag
        /// and queues per-device settings reads + Apply*ToHardware.
        /// </summary>
        public void DetectDevices(string commandName, int value, byte deviceId)
        {
            // wheel-mcu-uid / display-mcu-uid responses parse to negative int32
            // (BE-encoded 0xBE prefix), but UpdateFromArray has already stored
            // the raw 12 bytes — log before the `value < 0` guard.
            if (commandName == "wheel-mcu-uid" && _data.WheelMcuUid.Length > 0)
            {
                MozaLog.Debug(
                    $"[Moza] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                    MozaLog.RedactBytesHex(_data.WheelMcuUid));
                return;
            }
            if (commandName == "display-mcu-uid" && _data.DisplayMcuUid.Length > 0)
            {
                MozaLog.Debug(
                    $"[Moza] Display MCU UID ({_data.DisplayMcuUid.Length}B): " +
                    MozaLog.RedactBytesHex(_data.DisplayMcuUid));
                return;
            }

            if (value < 0) return;

            // TelemetrySender's heartbeat mask: only ping detected devices.
            // Only the primary prober drives the singular sender; the dedicated
            // hub prober enumerates peripherals on its own pipe and must not
            // toggle heartbeats on the primary (base) pipe.
            if (_drivesTelemetry)
            {
                var sender = _plugin.TelemetrySender;
                if (deviceId >= 18 && deviceId <= 30 && sender != null)
                    sender.DetectedDeviceMask |= (1 << (deviceId - 18));
            }

            // Base detection — IsBaseConnected was just set by UpdateFromCommand;
            // re-apply the profile so base settings get pushed.
            if (commandName == "base-mcu-temp" && !_detectionState.BaseDetected)
            {
                _detectionState.BaseDetected = true;
                MozaLog.Info("[Moza] Base detected");
                // Writes queue first, reads after — device processes FIFO so
                // read responses reflect the values we just wrote.
                var profile = _plugin.Settings.ProfileStore.CurrentProfile;
                if (profile != null)
                    _plugin.ApplyProfile(profile);
                _deviceManager.ReadSettings(BaseSettingsReadCommands);

                // Capability probe for the wheelbase ambient strip — R21/R25/R27
                // family replies on group 0xA2; R9/R12 silently drop the read.
                // Reply is handled in the "base-ambient-brightness" case and
                // gates DeviceDefinitionDeployer.DeployBaseAmbient.
                if (!_detectionState.BaseAmbientProbed)
                {
                    _detectionState.BaseAmbientProbed = true;
                    _deviceManager.ReadSetting("base-ambient-brightness");
                    _deviceManager.ReadSettingForDevice("wheel-model-name", MozaProtocol.DeviceMain);
                    // Base-identity probes (dev 0x13 direct). Populates
                    // MozaData.BaseMcuUid / BaseSwVersion / BaseHwVersion /
                    // BaseHwSubVersion / BaseModelName / BaseIdentity11 so
                    // DeviceCatalog can synthesise the Motor + Wheel Base
                    // manifest entries iRacing requires. PitHouse capture
                    // 2026-05-23 issues the same probes at cold-start.
                    //
                    // ES-wheel caveat: on ES wheels device 0x13 *is* the
                    // wheel, so these probes shadow wheel-model-name and the
                    // base-* handlers populate Base* fields with the wheel's
                    // identity. DeviceCatalog guards against acting on that
                    // mis-attribution by checking _deviceManager.WheelDeviceId
                    // before synthesising Motor / Wheel Base manifest entries.
                    // We still issue the probes here because wheel detection
                    // (and hence WheelDeviceId lock-in) happens *after* base
                    // detection in the typical R5/R9/R12/R21/R25 flow — the
                    // probes have to fly before we know whether to skip them.
                    _deviceManager.ReadSetting("base-model-name");
                    _deviceManager.ReadSetting("base-sw-version");
                    _deviceManager.ReadSetting("base-hw-version");
                    _deviceManager.ReadSetting("base-hw-sub");
                    _deviceManager.ReadSetting("base-mcu-uid");
                    _deviceManager.ReadSetting("base-identity-11");
                }
            }

            switch (commandName)
            {
                case "dash-rpm-indicator-mode":
                    MarkDashDetected();
                    break;

                case "base-ambient-brightness":
                    if (!_detectionState.BaseAmbientLedSupported)
                    {
                        _detectionState.BaseAmbientLedSupported = true;
                        if (DeviceDefinitionDeployer.DeployBaseAmbient(_connection.DiscoveredPid))
                            _plugin.DeviceDefinitionDeployed = true;
                        _plugin.ApplyBaseAmbientToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(BaseAmbientReadCommands);
                        MozaLog.Info(
                            $"[Moza] Base ambient LEDs detected (model='{(string.IsNullOrEmpty(_data.BaseModelName) ? "unknown" : _data.BaseModelName)}')");
                    }
                    break;

                case "wheel-telemetry-mode":
                    if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected)
                    {
                        _detectionState.NewWheelDetected = true;
                        // Stamp first-detect time for the display-wedge watchdog
                        // (PollStatus bounds the post-detect display-boot wait).
                        _plugin.NoteWheelDetected();
                        _deviceManager.LockWheelId(deviceId);
                        // Don't apply here — page GUID isn't resolvable until
                        // wheel-model-name arrives. Apply runs in the
                        // wheel-model-name case below.
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        // PitHouse's 12-frame identity handshake.
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        // Display sub-device probe is deferred to the
                        // wheel-model-name handler below — sending 11 frames on
                        // the dashboard session group (0x43 dev=0x17) to a
                        // screenless wheel appears to put its command parser
                        // into a half-engaged state where settings reads start
                        // timing out. By waiting for the model-name response we
                        // can skip the probe entirely when WheelModelInfo says
                        // the wheel has no display.
                        //
                        // Send the LED-layout-independent reads now. The
                        // model-aware LED reads (per-zone modes, brightness,
                        // per-LED colors, group probes) are kicked off in the
                        // wheel-model-name handler below, once WheelModelInfo
                        // is resolved.
                        _deviceManager.ReadSettingsPaced(NewWheelCoreReadCommands);
                        MozaLog.Info($"[Moza] New-protocol wheel detected on ID {deviceId}");
                        // Telemetry start is deferred until wheel-model-name responds;
                        // ShouldDriveDashboard() needs WheelModelInfo to decide.
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        _plugin.ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "wheel-model-name":
                    // ES wheels share device 0x13 with the base, so their model
                    // name response is the base name — skip the resolve path.
                    if (_detectionState.NewWheelDetected)
                    {
                        var currentModel = _data.WheelModelName;
                        if (string.IsNullOrEmpty(currentModel))
                            break;

                        // Hot-swap detection by model name change.
                        if (!string.IsNullOrEmpty(_detectionState.LastKnownWheelModel) &&
                            _detectionState.LastKnownWheelModel != currentModel)
                        {
                            _plugin.ResetWheelDetection(
                                $"Wheel model changed from '{_detectionState.LastKnownWheelModel}' " +
                                $"to '{currentModel}' — hot-swap detected");
                            break;
                        }

                        // First-sight: resolve LED layout and deploy device defs.
                        if (string.IsNullOrEmpty(_detectionState.LastKnownWheelModel))
                        {
                            _detectionState.LastKnownWheelModel = currentModel;
                            _plugin.WheelModelInfo = WheelModelInfo.FromModelName(currentModel);
                            var info = _plugin.WheelModelInfo;
                            MozaLog.Debug(
                                $"[Moza] Wheel model: {currentModel} " +
                                $"(rpm={info!.RpmLedCount}, buttons={info.ButtonLedCount}, flags={info.HasFlagLeds}, knobs={info.KnobCount})");
                            // Display sub-device probe — deferred from the
                            // initial wheel-detection site so we can skip it
                            // entirely for known-no-display wheels. The probe
                            // sends 11 frames on the dashboard session group
                            // (0x43 dev=0x17); screenless wheels appear to
                            // interpret those as dashboard-pipeline traffic and
                            // stop servicing settings reads. For unknown wheels
                            // (HasDisplay==null) the probe still runs so the UI
                            // can light the dashboard section when a display
                            // sub-device responds.
                            if (info.HasDisplay != false)
                                _deviceManager.SendDisplayProbe();
                            // Now that WheelModelInfo is resolved, send the
                            // LED-group-filtered reads. Skipping reads for LEDs
                            // the wheel doesn't have keeps PendingResponseTracker
                            // from churning on inevitable timeouts.
                            _deviceManager.ReadSettingsPaced(BuildNewWheelLedReadCommands(info));
                            if (DeviceDefinitionDeployer.DeployForModel(currentModel, _connection.DiscoveredPid))
                                _plugin.DeviceDefinitionDeployed = true;

                            // First-sight wheel detect — fire the FULL ApplyProfile,
                            // not just ApplyWheelToHardware. The Init-time
                            // AutoApplyProfileOnLaunch call at MozaPlugin.InitProfileSystem
                            // fired when no wheel was detected yet, so all the
                            // per-section gates (NewWheelDetected, DashDetected, ...)
                            // were false and nothing wrote. Now that the wheel is
                            // up, re-run the full profile apply so colors / brightness
                            // / modes / dash / base etc. all land on hardware. The
                            // call is idempotent: Apply*ToHardware reads from the
                            // profile + overlay, not from device-modified _data
                            // state, so re-firing after the Init-time no-op pass
                            // produces the same writes regardless of how much of
                            // _data has been populated by intervening read responses.
                            var initialProfile = _plugin.Settings?.ProfileStore?.CurrentProfile;
                            if (initialProfile != null)
                                _plugin.ApplyProfile(initialProfile);

                            // Auto-load this wheel's mzdash folder if configured.
                            var ovFolder = _plugin.ActiveTelemetryMzdashFolder;
                            if (!string.IsNullOrEmpty(ovFolder) && System.IO.Directory.Exists(ovFolder))
                            {
                                MozaLog.Debug($"[Moza] Loading per-wheel mzdash folder from overlay: {ovFolder}");
                                _plugin.DashCache?.LoadFromFolder(ovFolder);
                            }

                            try { _plugin.ApplyTelemetrySettings(); }
                            catch (Exception ex)
                            {
                                MozaLog.Warn($"[Moza] ApplyTelemetrySettings after wheel-model-name failed: {ex.Message}");
                            }

                            // Wheel hot-swap path: the saved profile's dashboard
                            // preference (TelemetryDashboardKey) needs to be
                            // re-asserted against the freshly-attached wheel.
                            // Without this the wheel sits on whatever slot it
                            // boots to (typically its persisted "last-used"
                            // dashboard, NOT the host's saved choice), so the
                            // host's tier-def emissions target one slot while
                            // the wheel renders another. ApplyProfile already
                            // does this on game-switch; queue the same retry
                            // path here so PollStatus's TickPendingDashboardRetry
                            // picks it up once configJson state arrives (~200 ms
                            // post-detect on healthy connect, can be later on
                            // hot-attach).
                            _plugin.RequestSavedDashboardReapply();

                            // ShouldDriveDashboard now has real input — make the keep/skip decision.
                            _plugin.StartTelemetryIfReady();
                        }
                    }
                    else
                    {
                        MozaLog.Debug($"[Moza] Wheel model (ES/base): {_data.WheelModelName}");
                    }
                    break;

                case "wheel-sw-version":
                    MozaLog.Debug($"[Moza] Wheel FW: {_data.WheelSwVersion}");
                    break;

                case "wheel-serial-b":
                    if (!string.IsNullOrEmpty(_data.WheelSerialNumber))
                        MozaLog.Debug($"[Moza] Wheel serial: {MozaLog.RedactId(_data.WheelSerialNumber)}");
                    break;

                case "wheel-hw-sub":
                    if (!string.IsNullOrEmpty(_data.WheelHwSubVersion))
                        MozaLog.Debug($"[Moza] Wheel HW sub: {_data.WheelHwSubVersion}");
                    break;

                case "wheel-mcu-uid":
                    if (_data.WheelMcuUid.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                            MozaLog.RedactBytesHex(_data.WheelMcuUid));
                    break;

                case "wheel-device-type":
                    if (_data.WheelDeviceType.Length > 0)
                        MozaLog.Debug($"[Moza] Wheel device type: {BitConverter.ToString(_data.WheelDeviceType)}");
                    break;

                case "wheel-capabilities":
                    if (_data.WheelCapabilities.Length > 0)
                        MozaLog.Debug($"[Moza] Wheel capabilities: {BitConverter.ToString(_data.WheelCapabilities)}");
                    break;

                case "wheel-presence":
                    MozaLog.Debug($"[Moza] Wheel presence/ready: sub_device_count={_data.WheelSubDeviceCount}");
                    break;

                case "wheel-device-presence":
                    MozaLog.Debug($"[Moza] Wheel device presence byte: 0x{_data.WheelDevicePresence:X2}");
                    break;

                case "wheel-identity-11":
                    if (_data.WheelIdentity11.Length > 0)
                        MozaLog.Debug($"[Moza] Wheel identity-11: {BitConverter.ToString(_data.WheelIdentity11)}");
                    break;

                case "display-model-name":
                    if (!string.IsNullOrEmpty(_data.DisplayModelName))
                    {
                        MozaLog.Debug($"[Moza] Display model: {_data.DisplayModelName}");
                        // CM2-on-base confirmed by display identity — re-assert 0x12 routing.
                        if (_plugin.IsCm2BehindBaseCandidate)
                        {
                            MozaLog.Info($"[Moza] CM2-on-base display confirmed: {_data.DisplayModelName} — routing screen telemetry to 0x12");
                            if (DeviceDefinitionDeployer.DeployDashboard(_connection.DiscoveredPid, forceCm2: true))
                                _plugin.DeviceDefinitionDeployed = true;
                            try { _plugin.ApplyTelemetrySettings(); }
                            catch (Exception ex) { MozaLog.Debug($"[Moza] CM2-on-base ApplyTelemetrySettings skipped: {ex.Message}"); }
                            // Push the CM2 meter LED config (modes/thresholds/colors,
                            // group 0x32) now that the base-bridged CM2 is CONFIRMED.
                            // The earlier MarkDashDetected apply races ahead of this —
                            // it runs before the profile is loaded and before
                            // IsCm2BehindBaseCandidate is true, so ApplyCm2DashboardConfig
                            // never fires there. Without this re-apply the meter is never
                            // put into telemetry LED mode and its RPM/flag LEDs stay dark
                            // (KS+CM2 bundle 2026-06-06: zero group-0x32 frames on the wire).
                            try { _plugin.ApplyDashToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile); }
                            catch (Exception ex) { MozaLog.Debug($"[Moza] CM2-on-base ApplyDashToHardware skipped: {ex.Message}"); }
                        }
                        // Re-arm the wedge-recovery one-shot now that we know
                        // a display is responsive — a future wheel hot-swap
                        // that wedges should get its own recovery attempt.
                        _plugin.ClearDisplayWedgeRecovery();
                        // Wheels not in KnownModels (HasDisplay==null) get their
                        // authoritative "has display" signal from this probe;
                        // trigger StartTelemetryIfReady so the fallback path
                        // (HasDisplay==null → IsDisplayDetected) actually starts.
                        _plugin.StartTelemetryIfReady();
                    }
                    break;
                case "display-hw-version":
                    if (!string.IsNullOrEmpty(_data.DisplayHwVersion))
                        MozaLog.Debug($"[Moza] Display HW: {_data.DisplayHwVersion}");
                    break;
                case "display-sw-version":
                    if (!string.IsNullOrEmpty(_data.DisplaySwVersion))
                        MozaLog.Debug($"[Moza] Display FW: {_data.DisplaySwVersion}");
                    break;
                case "display-serial":
                    if (!string.IsNullOrEmpty(_data.DisplaySerialNumber))
                        MozaLog.Debug($"[Moza] Display serial: {MozaLog.RedactId(_data.DisplaySerialNumber)}");
                    break;
                case "display-presence":
                    MozaLog.Debug($"[Moza] Display presence/ready: sub_device_count={_data.DisplaySubDeviceCount}");
                    break;
                case "display-device-presence":
                    MozaLog.Debug($"[Moza] Display device presence byte: 0x{_data.DisplayDevicePresence:X2}");
                    break;
                case "display-device-type":
                    if (_data.DisplayDeviceType.Length > 0)
                        MozaLog.Debug($"[Moza] Display device type: {BitConverter.ToString(_data.DisplayDeviceType)}");
                    break;
                case "display-capabilities":
                    if (_data.DisplayCapabilities.Length > 0)
                        MozaLog.Debug($"[Moza] Display capabilities: {BitConverter.ToString(_data.DisplayCapabilities)}");
                    break;
                case "display-identity-11":
                    if (_data.DisplayIdentity11.Length > 0)
                        MozaLog.Debug($"[Moza] Display identity-11: {BitConverter.ToString(_data.DisplayIdentity11)}");
                    break;
                case "display-mcu-uid":
                    // Already logged before the value<0 guard at the top.
                    break;

                case "wheel-rpm-value1":
                    if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected)
                    {
                        _detectionState.OldWheelDetected = true;
                        // Stamp first-detect time (mirror of new-protocol path).
                        // Old-protocol wheels are always HasDisplay=false so the
                        // wedge watchdog never actually fires for them, but the
                        // timestamp is cheap and keeps both branches symmetric.
                        _plugin.NoteWheelDetected();
                        _deviceManager.LockWheelId(deviceId);
                        _plugin.ApplyWheelToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        _deviceManager.ReadSettingsPaced(OldWheelSettingsReadCommands);
                        if (DeviceDefinitionDeployer.DeployOldProtoWheel(_connection.DiscoveredPid))
                            _plugin.DeviceDefinitionDeployed = true;
                        MozaLog.Info($"[Moza] Old-protocol wheel detected on ID {deviceId}");
                        _plugin.StartTelemetryIfReady();
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        _plugin.ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "handbrake-direction":
                    MarkHandbrakeDetected();
                    break;

                case "pedals-throttle-dir":
                    MarkPedalsDetected();
                    break;

                case "hub-port1-power":
                    if (!_detectionState.HubDetected)
                    {
                        _detectionState.HubDetected = true;
                        _deviceManager.ReadSettings(HubReadCommands);
                        // Mirror to the connection so TelemetrySender's hub-only
                        // 5-slot enumeration burst still fires for hub-attached
                        // wheels. With registry-based discovery we don't probe at
                        // port-discovery time; first 0xE4 hub reply is the trigger.
                        try { _connection.MarkHubDetected(); } catch { }
                        MozaLog.Info("[Moza] Universal Hub detected");
                    }
                    break;
            }
        }
    }
}
