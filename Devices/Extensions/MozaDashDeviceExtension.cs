using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using MozaPlugin.Devices.Led;
using MozaPlugin.Devices.Ui;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// SimHub device extension for MOZA Dashboard.
    /// Injects a virtual LED driver so SimHub's effects UI works for the dash,
    /// then bridges computed LED colors to MOZA hardware via bitmask telemetry.
    /// </summary>
    internal class MozaDashDeviceExtension : MozaLedDeviceExtensionBase<MozaDashLedDeviceManager>
    {
        private MozaDashExtensionSettings _settings = new MozaDashExtensionSettings();

        public override string ExtentionTabTitle => "MOZA Dashboard";

        protected override string LogLabel => "dash";
        protected override MozaDashLedDeviceManager CreateDriver() => new MozaDashLedDeviceManager();
        protected override void UpdateDriverConnection(MozaDashLedDeviceManager driver) => driver.UpdateConnectionState();
        // Release clears the Latest diagnostics static.
        protected override void ReleaseDriver(MozaDashLedDeviceManager driver) => driver.Release();

        public override void Init(PluginManager pluginManager)
        {
            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaDashActive",
                this.GetType(),
                () => MozaPlugin.Instance?.IsDashDetected ?? false);

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                plugin.DashDeviceExtensionActive = true;
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

            RestoreLedDriver();
        }

        /// <summary>This dash's own profile store (CM2 or CM1).</summary>
        private Guid PageGuid =>
            Settings.DeviceProfileCoordinator.DashPageGuidFor(LinkedDevice?.DeviceDescriptor?.DeviceTypeID);

        public override void LoadDefaultSettings()
        {
            _settings = new MozaDashExtensionSettings();

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data,
                    plugin.DeviceProfiles?.DashProfileFor(PageGuid, seed: false));
        }

        public override JToken GetSettings()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data,
                    plugin.DeviceProfiles?.DashProfileFor(PageGuid, seed: false));

            return JToken.FromObject(_settings);
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            _settings = settings.ToObject<MozaDashExtensionSettings>() ?? new MozaDashExtensionSettings();

            if (!isDefault)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin != null)
                    plugin.HardwareApplier.ApplyDashExtensionSettings(_settings, PageGuid);
            }
        }

        public override Control CreateSettingControl()
        {
            return new MozaDashSettingsControl { DashPageGuid = PageGuid };
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
