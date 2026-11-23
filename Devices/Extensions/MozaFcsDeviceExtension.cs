using System.Collections.Generic;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using MozaPlugin.Devices.Led;
using MozaPlugin.Devices.Ui;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// SimHub device extension for the MOZA FCS: drives its six button LEDs from
    /// SimHub's LED profile and hosts the FCS settings page (inputs + LEDs).
    /// </summary>
    internal class MozaFcsDeviceExtension : MozaLedDeviceExtensionBase<MozaFcsLedDeviceManager>
    {
        private MozaFcsExtensionSettings _settings = new MozaFcsExtensionSettings();

        public override string ExtentionTabTitle => "MOZA FCS";

        protected override string LogLabel => "fcs";
        protected override MozaFcsLedDeviceManager CreateDriver() => new MozaFcsLedDeviceManager();
        protected override void UpdateDriverConnection(MozaFcsLedDeviceManager driver) => driver.UpdateConnectionState();
        protected override void ReleaseDriver(MozaFcsLedDeviceManager driver) => driver.Release();

        // The generated definition declares six button LEDs; set the module count too
        // (the wheel extension does the same).
        protected override void OnInjected(LedModuleDevice lmd)
            => lmd.ledModuleSettings.ButtonsCount = DeviceDefinitionDeployer.FcsButtonLedCount;

        public override void Init(PluginManager pluginManager)
        {
            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaFcsActive",
                this.GetType(),
                () => MozaPlugin.Instance?.IsFcsDetected ?? false);

            MozaPlugin.FcsDeviceExtensionActive = true;
        }

        public override void End(PluginManager pluginManager)
        {
            pluginManager.DetachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaFcsActive",
                this.GetType());

            MozaPlugin.FcsDeviceExtensionActive = false;

            RestoreLedDriver();
        }

        public override void LoadDefaultSettings()
        {
            _settings = new MozaFcsExtensionSettings();
            Capture();
        }

        public override JToken GetSettings()
        {
            Capture();
            return JToken.FromObject(_settings);
        }

        private void Capture()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Data, plugin.DeviceProfiles?.FcsProfile(seed: false));
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            _settings = settings.ToObject<MozaFcsExtensionSettings>() ?? new MozaFcsExtensionSettings();
            if (!isDefault)
                MozaPlugin.Instance?.HardwareApplier.ApplyFcsExtensionSettings(_settings);
        }

        public override Control CreateSettingControl() => new MozaFcsSettingsControl();

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
