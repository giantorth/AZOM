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
    /// SimHub device extension for MOZA Dashboard.
    /// Injects a virtual LED driver so SimHub's effects UI works for the dash,
    /// then bridges computed LED colors to MOZA hardware via bitmask telemetry.
    /// </summary>
    internal class MozaDashDeviceExtension : DeviceExtension
    {
        private MozaDashExtensionSettings _settings = new MozaDashExtensionSettings();
        private MozaDashLedDeviceManager? _ledDriver;
        private bool _driverInjected;

        public override string ExtentionTabTitle => "MOZA Dashboard";

        public override void Init(PluginManager pluginManager)
        {
            // Injection is deferred to DataUpdate() — calling it here would run before
            // LedModuleDevice.SetSettings(), causing a KeyNotFoundException in that call.

            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaDashActive",
                this.GetType(),
                () => MozaPlugin.Instance?.IsDashDetected ?? false);

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                plugin.DashDeviceExtensionActive = true;
        }

        /// <summary>
        /// Find the LedModuleDevice sub-device and replace its DeviceDriver
        /// with our MozaDashLedDeviceManager that always reports connected.
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
                        _ledDriver = new MozaDashLedDeviceManager();
                        _ledDriver.LedModuleSettings = lmd.ledModuleSettings;

                        var prop = typeof(LedModuleSettings).GetProperty(
                            "DeviceDriver",
                            BindingFlags.Public | BindingFlags.Instance);

                        if (prop?.GetSetMethod(nonPublic: true) != null)
                        {
                            prop.GetSetMethod(nonPublic: true)!.Invoke(lmd.ledModuleSettings, new object[] { _ledDriver });
                            _driverInjected = true;
                            MozaLog.Debug("[AZOM] Injected virtual LED driver for dashboard");
                        }
                        else
                        {
                            MozaLog.Warn("[AZOM] Could not find DeviceDriver setter on LedModuleSettings (dash)");
                        }
                        return;
                    }
                }
                MozaLog.Debug("[AZOM] No LedModuleDevice found on dash device instance");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error injecting dash LED driver: {ex.Message}");
            }
        }

        public override void End(PluginManager pluginManager)
        {
            pluginManager.DetachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaDashActive",
                this.GetType());

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.DashDeviceExtensionActive = false;
                MozaLog.Debug("[AZOM] Dash device extension ended");
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
            _settings = new MozaDashExtensionSettings();

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
            _settings = settings.ToObject<MozaDashExtensionSettings>() ?? new MozaDashExtensionSettings();

            if (!isDefault)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin != null)
                    plugin.ApplyDashExtensionSettings(_settings);
            }
        }

        public override Control CreateSettingControl()
        {
            return new MozaDashSettingsControl { LinkedLedDriver = _ledDriver };
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
