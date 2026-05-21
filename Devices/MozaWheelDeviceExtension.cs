using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// SimHub device extension for MOZA wheels.
    /// Injects a fake LED device driver so SimHub's effects UI works,
    /// then bridges computed LED colors to MOZA hardware via the plugin's serial protocol.
    /// Also provides per-game profile persistence through GetSettings()/SetSettings().
    /// </summary>
    internal class MozaWheelDeviceExtension : DeviceExtension
    {
        private MozaWheelExtensionSettings _settings = new MozaWheelExtensionSettings();
        private MozaLedDeviceManager? _ledDriver;
        private bool _driverInjected;
        private bool _buttonsCountSet;

        /// <summary>
        /// Expected wheel model prefix resolved from the DeviceTypeID.
        /// Null = unknown device (won't connect). Empty = generic (any wheel).
        /// </summary>
        private string? _expectedModelPrefix;

        public override string ExtentionTabTitle => "MOZA Wheel";

        public override void Init(PluginManager pluginManager)
        {
            // Resolve which wheel model this device instance represents
            var typeId = LinkedDevice.DeviceDescriptor.DeviceTypeID ?? "";
            _expectedModelPrefix = MozaDeviceConstants.GetWheelModelPrefix(typeId);

            MozaLog.Debug(
                $"[Moza] WheelDeviceExtension Init — DeviceTypeID={typeId}, modelPrefix={_expectedModelPrefix ?? "(null)"}");

            // Injection is deferred to DataUpdate() — calling it here would run before
            // LedModuleDevice.SetSettings(), causing a KeyNotFoundException in that call.

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.DeviceExtensionActive = true;
                if (_expectedModelPrefix != null && _expectedModelPrefix.Length > 0)
                    plugin.RegisterActiveModelPrefix(_expectedModelPrefix);
            }

            // Report active only when the matching wheel model is connected
            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaWheelActive",
                this.GetType(),
                () => _ledDriver?.IsConnected() ?? false);
        }

        /// <summary>
        /// Find the LedModuleDevice sub-device and replace its DeviceDriver
        /// with our MozaLedDeviceManager that always reports connected.
        /// This enables SimHub's LED effects configuration UI.
        /// </summary>
        private void InjectLedDriver()
        {
            if (_driverInjected) return;

            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                    {
                        _ledDriver = new MozaLedDeviceManager();
                        _ledDriver.ExpectedModelPrefix = _expectedModelPrefix;
                        _ledDriver.LedModuleSettings = lmd.ledModuleSettings;

                        // DeviceDriver setter is protected — use reflection
                        var prop = typeof(LedModuleSettings).GetProperty(
                            "DeviceDriver",
                            BindingFlags.Public | BindingFlags.Instance);

                        if (prop?.GetSetMethod(nonPublic: true) != null)
                        {
                            prop.GetSetMethod(nonPublic: true)!.Invoke(lmd.ledModuleSettings, new object[] { _ledDriver });
                            _driverInjected = true;

                            // Expose button LEDs — count depends on wheel model (set later when model name arrives)
                            var plugin = MozaPlugin.Instance;
                            if (plugin?.WheelModelInfo is { } modelInfo)
                            {
                                lmd.ledModuleSettings.ButtonsCount = modelInfo.ButtonLedCount;
                                if (modelInfo.KnobCount > 0)
                                    SetEncodersCount(lmd.ledModuleSettings, modelInfo.KnobCount);
                                _buttonsCountSet = true;
                            }

                            if (plugin != null)
                                plugin.DeviceExtensionActive = true;

                            MozaLog.Debug("[Moza] Injected virtual LED driver — effects UI should be available");
                        }
                        else
                        {
                            MozaLog.Warn("[Moza] Could not find DeviceDriver setter on LedModuleSettings");
                        }
                        return;
                    }
                }
                MozaLog.Debug("[Moza] No LedModuleDevice found on device instance");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Error injecting LED driver: {ex.Message}");
            }
        }

        public override void End(PluginManager pluginManager)
        {
            pluginManager.DetachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaWheelActive",
                this.GetType());

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.DeviceExtensionActive = false;
                if (_expectedModelPrefix != null)
                    plugin.UnregisterActiveModelPrefix(_expectedModelPrefix);
                MozaLog.Debug("[Moza] Device extension ended");
            }
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // LED forwarding happens in MozaLedDeviceManager.Display() —
            // SimHub calls it directly as part of its LED pipeline.
            // Inject here (not Init) so LedModuleDevice.SetSettings() has already run.
            if (!_driverInjected)
                InjectLedDriver();

            // Notify SimHub when detection state changes so it resumes/pauses Display() calls
            _ledDriver?.UpdateConnectionState();

            // Set ButtonsCount once wheel model is identified (may happen after injection)
            if (_driverInjected && !_buttonsCountSet && MozaPlugin.Instance?.WheelModelInfo is { } modelInfo)
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                    {
                        lmd.ledModuleSettings.ButtonsCount = modelInfo.ButtonLedCount;
                        if (modelInfo.KnobCount > 0)
                            SetEncodersCount(lmd.ledModuleSettings, modelInfo.KnobCount);
                        _buttonsCountSet = true;
                        MozaLog.Debug($"[Moza] Set ButtonsCount={modelInfo.ButtonLedCount}, EncodersCount={modelInfo.KnobCount} for {MozaPlugin.Instance!.Data.WheelModelName}");
                        break;
                    }
                }
            }
        }

        public override void LoadDefaultSettings()
        {
            _settings = new MozaWheelExtensionSettings();

            var plugin = MozaPlugin.Instance;
            var settings = plugin?.Settings;
            if (plugin != null && settings != null)
            {
                var profile = settings.ProfileStore?.CurrentProfile;
                _settings.CaptureFromCurrent(settings, plugin.Data, profile, _expectedModelPrefix);
            }
        }

        public override JToken GetSettings()
        {
            var plugin = MozaPlugin.Instance;
            var settings = plugin?.Settings;
            if (plugin != null && settings != null)
            {
                var profile = settings.ProfileStore?.CurrentProfile;
                _settings.CaptureFromCurrent(settings, plugin.Data, profile, _expectedModelPrefix);
            }

            return JToken.FromObject(_settings);
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            _settings = settings.ToObject<MozaWheelExtensionSettings>() ?? new MozaWheelExtensionSettings();

            if (!isDefault)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin != null)
                    plugin.ApplyWheelExtensionSettings(_settings, _expectedModelPrefix);
            }
        }

        public override Control CreateSettingControl()
        {
            return new MozaWheelSettingsControl { LinkedLedDriver = _ledDriver };
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }

        /// <summary>
        /// Set EncodersCount via reflection — property is private in SimHub's LedModuleSettings.
        /// </summary>
        private static void SetEncodersCount(LedModuleSettings settings, int count)
        {
            try
            {
                var prop = typeof(LedModuleSettings).GetProperty(
                    "EncodersCount",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                prop?.SetValue(settings, count);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Could not set EncodersCount: {ex.Message}");
            }
        }
    }
}
