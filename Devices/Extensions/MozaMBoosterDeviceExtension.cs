using System;
using System.Windows.Controls;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices.DeviceExtensions;
using MozaPlugin.Devices.Haptics;
using MozaPlugin.Devices.Led;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Device extension for the mBooster pedals' ShakeIt device. Two jobs, as
    /// on the S12 combined device minus its oscillator sweep (one oscillator per
    /// pedal leaves nothing to assign):
    ///
    /// <list type="number">
    /// <item>Swap our connection manager in for SimHub's
    /// <c>StandardProtocolConnectionDevice</c> one — the composite's only
    /// primary, so the device is inert unless it reports Connected.</item>
    /// <item>Keep <see cref="Integration.MozaMBoosterChannelsProvider"/>
    /// installed; a profile switch stamps the stock provider back.</item>
    /// </list>
    /// </summary>
    internal class MozaMBoosterDeviceExtension : DeviceExtension
    {
        private const int ProviderInstallEveryNFrames = 60;
        private int _providerInstallTick = ProviderInstallEveryNFrames;

        private bool _driverInjected;
        private bool _connectionSwapAttempted;
        private bool _registeredActive;

        private object? _motorsDevice;
        private object? _connectionDevice;
        private MozaHapticsConnectionManager? _connectionManager;
        private object? _originalConnectionManager;

        public override string ExtentionTabTitle => "MOZA mBooster";

        public override void Init(PluginManager pluginManager)
        {
            if (!_registeredActive)
            {
                MozaPlugin.MBoosterDeviceExtensionStarted();
                _registeredActive = true;
            }

            // Injection is deferred to DataUpdate() — running it here would beat
            // SimHub's own sub-device setup.
            TryInstallProvider();
        }

        private void InjectDrivers()
        {
            if (_driverInjected) return;

            bool sawConnection = false;
            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (!sawConnection && IsConnectionDevice(instance))
                    {
                        sawConnection = true;
                        if (!_connectionSwapAttempted)
                            InjectConnectionManager(instance);
                    }
                }

                if (sawConnection) _driverInjected = true;
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] mBooster driver injection deferred: {ex.Message}");
            }
        }

        private static bool IsConnectionDevice(object instance) =>
            string.Equals(instance?.GetType().FullName,
                "SimHub.Plugins.OutputPlugins.CommonDevices.Devices.StandardProtocolConnectionDevice",
                StringComparison.Ordinal);

        private void InjectConnectionManager(object instance)
        {
            _connectionSwapAttempted = true;

            // The manager binds 9.12-era SimHub/BA63 members.
            if (!MozaMBoosterHapticsBridge.IsSupported) return;

            try
            {
                var manager = (MozaHapticsConnectionManager)MozaMBoosterHapticsBridge.CreateConnectionManager();
                var previous = LedDriverInjection.SwapConnectionManager(instance, manager);
                if (previous == null) return;

                _connectionDevice = instance;
                _connectionManager = manager;
                _originalConnectionManager = previous;
                MozaLog.Info("[AZOM] Took over the mBooster device's connection manager");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not take over the mBooster connection manager: {ex.Message}");
            }
        }

        private void TryInstallProvider()
        {
            try
            {
                if (_motorsDevice == null)
                    _motorsDevice = FindMotorsDevice();
                if (_motorsDevice == null) return;

                MozaMBoosterHapticsBridge.TryInstallChannelsProvider(_motorsDevice);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] mBooster provider install skipped: {ex.Message}");
            }
        }

        private object? FindMotorsDevice()
        {
            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (MozaBaseHapticsBridge.IsMotorsDeviceExtension(instance))
                        return instance;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Could not locate the mBooster Haptics sub-device: {ex.Message}");
            }
            return null;
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_driverInjected)
                InjectDrivers();

            _connectionManager?.UpdateConnectionState();

            if (++_providerInstallTick >= ProviderInstallEveryNFrames)
            {
                _providerInstallTick = 0;
                TryInstallProvider();
            }
        }

        public override void End(PluginManager pluginManager)
        {
            LedDriverInjection.RestoreConnectionManager(
                _connectionDevice, _connectionManager, _originalConnectionManager);
            _connectionDevice = null;
            _connectionManager = null;
            _originalConnectionManager = null;
            _connectionSwapAttempted = false;
            _motorsDevice = null;
            _driverInjected = false;

            if (_registeredActive)
            {
                MozaPlugin.MBoosterDeviceExtensionEnded();
                _registeredActive = false;
            }
        }

        // Every knob lives in ShakeIt.
        public override void LoadDefaultSettings() => TryInstallProvider();

        public override JToken GetSettings() => new JObject();

        public override void SetSettings(JToken settings, bool isDefault) => TryInstallProvider();

        public override Control CreateSettingControl() => null!;

        public override System.Collections.Generic.IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
