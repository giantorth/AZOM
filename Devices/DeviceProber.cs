using System;
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

        internal static readonly string[] NewWheelSettingsReadCommands = new[]
        {
            "wheel-telemetry-mode",
            "wheel-knob-led-mode", "wheel-buttons-led-mode",
            "wheel-rpm-brightness", "wheel-buttons-brightness", "wheel-flags-brightness",
            // Idle-effect + sleep-light reads — captured into MozaData and
            // seeded into per-wheel-page WheelIdleByPageGuid (effects) and
            // WheelSleepByPageGuid (sleep mode/timeout/speed/color) via
            // SeedSleepBundleFromResponse. The matching idle-*-interval
            // (speed) commands are write-only on the wire (RxGroup=0xFF) so
            // they're not read here.
            "wheel-telemetry-idle-effect", "wheel-buttons-idle-effect",
            "wheel-knob-idle-effect",
            "wheel-idle-mode", "wheel-idle-timeout", "wheel-idle-speed",
            "wheel-idle-color",
            "wheel-paddles-mode", "wheel-clutch-point", "wheel-knob-mode", "wheel-stick-mode",
            "wheel-knob-signal-mode0", "wheel-knob-signal-mode1", "wheel-knob-signal-mode2",
            "wheel-knob-signal-mode3", "wheel-knob-signal-mode4",
            "wheel-rpm-color1", "wheel-rpm-color2", "wheel-rpm-color3",
            "wheel-rpm-color4", "wheel-rpm-color5", "wheel-rpm-color6",
            "wheel-rpm-color7", "wheel-rpm-color8", "wheel-rpm-color9",
            "wheel-rpm-color10", "wheel-rpm-color11", "wheel-rpm-color12",
            "wheel-rpm-color13", "wheel-rpm-color14", "wheel-rpm-color15",
            "wheel-rpm-color16", "wheel-rpm-color17", "wheel-rpm-color18",
            "wheel-button-color1",  "wheel-button-color2",  "wheel-button-color3",
            "wheel-button-color4",  "wheel-button-color5",  "wheel-button-color6",
            "wheel-button-color7",  "wheel-button-color8",  "wheel-button-color9",
            "wheel-button-color10", "wheel-button-color11", "wheel-button-color12",
            "wheel-button-color13", "wheel-button-color14",
            "wheel-flag-color1", "wheel-flag-color2", "wheel-flag-color3",
            "wheel-flag-color4", "wheel-flag-color5", "wheel-flag-color6",
            // Extended LED group presence probes (Single/Rotary/Ambient).
            "wheel-single-brightness", "wheel-knob-brightness", "wheel-ambient-brightness",
        };

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

        public DeviceProber(
            MozaPlugin plugin,
            MozaSerialConnection connection,
            MozaDeviceManager deviceManager,
            MozaData data,
            DeviceDetectionState detectionState)
        {
            _plugin = plugin;
            _connection = connection;
            _deviceManager = deviceManager;
            _data = data;
            _detectionState = detectionState;
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
            var sender = _plugin.TelemetrySender;
            if (deviceId >= 18 && deviceId <= 30 && sender != null)
                sender.DetectedDeviceMask |= (1 << (deviceId - 18));

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
                }
            }

            switch (commandName)
            {
                case "dash-rpm-indicator-mode":
                    if (!_detectionState.DashDetected)
                    {
                        _detectionState.DashDetected = true;
                        if (DeviceDefinitionDeployer.DeployDashboard(_connection.DiscoveredPid))
                            _plugin.DeviceDefinitionDeployed = true;
                        _plugin.ApplyDashToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(DashSettingsReadCommands);
                        MozaLog.Info("[Moza] Dashboard detected");
                    }
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
                        // Display sub-device probe (independent of TelemetrySender)
                        // so IsDisplayDetected flips before the user picks a profile.
                        _deviceManager.SendDisplayProbe();
                        _deviceManager.ReadSettingsPaced(NewWheelSettingsReadCommands);
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
                            if (DeviceDefinitionDeployer.DeployForModel(currentModel, _connection.DiscoveredPid))
                                _plugin.DeviceDefinitionDeployed = true;

                            // Page GUID resolvable — push the overlay-layered wheel settings.
                            _plugin.ApplyWheelToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);

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
                    if (!_detectionState.HandbrakeDetected)
                    {
                        _detectionState.HandbrakeDetected = true;
                        _plugin.ApplyHandbrakeToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(HandbrakeSettingsReadCommands);
                        MozaLog.Info("[Moza] Handbrake detected");
                    }
                    break;

                case "pedals-throttle-dir":
                    if (!_detectionState.PedalsDetected)
                    {
                        _detectionState.PedalsDetected = true;
                        _plugin.ApplyPedalsToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(PedalsSettingsReadCommands);
                        MozaLog.Info("[Moza] Pedals detected");
                    }
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
