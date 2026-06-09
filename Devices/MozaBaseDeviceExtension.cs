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
    /// SimHub device extension for the MOZA wheel base ambient LED strip.
    /// Injects a virtual LED driver so SimHub's effects UI works for the
    /// 18-LED telemetry strip; the driver bridges computed colors to the
    /// base via group 0x20 / device 0x12 protocol commands.
    /// </summary>
    internal class MozaBaseDeviceExtension : DeviceExtension
    {
        private MozaBaseExtensionSettings _settings = new MozaBaseExtensionSettings();
        private MozaBaseLedDeviceManager? _ledDriver;
        private bool _driverInjected;

        public override string ExtentionTabTitle => "MOZA Wheel Base";

        public override void Init(PluginManager pluginManager)
        {
            // Injection is deferred to DataUpdate() — calling it here would run before
            // LedModuleDevice.SetSettings(), causing a KeyNotFoundException in that call.

            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaBaseAmbientActive",
                this.GetType(),
                () => MozaPlugin.Instance?.IsBaseAmbientLedSupported ?? false);

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                plugin.BaseAmbientDeviceExtensionActive = true;
        }

        /// <summary>
        /// Find the LedModuleDevice sub-device and replace its DeviceDriver
        /// with our MozaBaseLedDeviceManager that gates connection on the
        /// runtime base-ambient detection flag.
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
                        _ledDriver = new MozaBaseLedDeviceManager();
                        _ledDriver.LedModuleSettings = lmd.ledModuleSettings;

                        var prop = typeof(LedModuleSettings).GetProperty(
                            "DeviceDriver",
                            BindingFlags.Public | BindingFlags.Instance);

                        if (prop?.GetSetMethod(nonPublic: true) != null)
                        {
                            prop.GetSetMethod(nonPublic: true)!.Invoke(lmd.ledModuleSettings, new object[] { _ledDriver });
                            _driverInjected = true;
                            MozaLog.Debug("[AZOM] Injected virtual LED driver for wheel base ambient strip");
                        }
                        else
                        {
                            MozaLog.Warn("[AZOM] Could not find DeviceDriver setter on LedModuleSettings (base ambient)");
                        }
                        return;
                    }
                }
                MozaLog.Debug("[AZOM] No LedModuleDevice found on base ambient device instance");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error injecting base ambient LED driver: {ex.Message}");
            }
        }

        public override void End(PluginManager pluginManager)
        {
            pluginManager.DetachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaBaseAmbientActive",
                this.GetType());

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.BaseAmbientDeviceExtensionActive = false;
                MozaLog.Debug("[AZOM] Base ambient device extension ended");
            }
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Inject here (not Init) so LedModuleDevice.SetSettings() has already run.
            if (!_driverInjected)
                InjectLedDriver();

            // Notify SimHub when detection state changes so it resumes/pauses Display() calls
            _ledDriver?.UpdateConnectionState();
        }

        public override void LoadDefaultSettings()
        {
            _settings = new MozaBaseExtensionSettings();

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data, plugin.Settings?.ProfileStore?.CurrentProfile);
        }

        public override JToken GetSettings()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data, plugin.Settings?.ProfileStore?.CurrentProfile);

            return JToken.FromObject(_settings);
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            _settings = settings.ToObject<MozaBaseExtensionSettings>() ?? new MozaBaseExtensionSettings();

            if (!isDefault)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin != null)
                    plugin.ApplyBaseExtensionSettings(_settings);
            }
        }

        public override Control CreateSettingControl()
        {
            return new MozaBaseSettingsControl { LinkedLedDriver = _ledDriver };
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
