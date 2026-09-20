using System;
using System.Windows.Controls;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices.DeviceExtensions;
using MozaPlugin.Devices.Haptics;
using MozaPlugin.Devices.Led;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Device extension for the pedal-haptics unit's SimHub device. Much
    /// smaller than <see cref="MozaBaseDeviceExtension"/>: that one juggles an
    /// LED module, an ambient strip and a legacy settings import alongside the
    /// haptics takeover, whereas this definition declares HapticsFeature and
    /// nothing else. Two jobs:
    ///
    /// <list type="number">
    /// <item>Swap our connection manager in for SimHub's
    /// <c>StandardProtocolConnectionDevice</c> one. Load-bearing, not cosmetic:
    /// a haptics definition's connection sub-device is the composite's only
    /// primary, and if it never reports Connected the device is inert.</item>
    /// <item>Keep <see cref="MozaPedalHapticsChannelsProvider"/> installed over
    /// SimHub's stock channels provider, so the three motors read as Clutch /
    /// Brake / Throttle, in the unit's port order. Re-asserted on a ~1 Hz tick
    /// because a profile switch runs <c>CreateOutputManager</c> again and stamps
    /// the stock one back.</item>
    /// </list>
    ///
    /// Unlike the wheelbase, the Connection tab is left in place — there is no
    /// LEDs tab here for its state and identity to live on instead.
    /// </summary>
    internal class MozaPedalHapticsDeviceExtension : DeviceExtension
    {
        // Same cadence and reasoning as MozaBaseDeviceExtension: the install is
        // several reflective reads, and the event it guards against (a profile
        // switch) is rare. Starts at the limit so the first DataUpdate tries.
        private const int ProviderInstallEveryNFrames = 60;
        private int _providerInstallTick = ProviderInstallEveryNFrames;

        private bool _driverInjected;
        private bool _connectionSwapAttempted;

        private object? _motorsDevice;
        private object? _connectionDevice;
        private MozaPedalHapticsConnectionManager? _connectionManager;
        private object? _originalConnectionManager;

        // Which motor port this device drives. Resolved in Init from the
        // DeviceTypeID; the three pedal devices differ only by this.
        private byte _pedal = (byte)PedalHapticsPedal.Throttle;

        public override string ExtentionTabTitle =>
            "MOZA " + MozaPedalHapticsProtocol.PedalLabel(_pedal) + " Haptics";

        public override void Init(PluginManager pluginManager)
        {
            byte pedal = MozaDeviceConstants.PedalHapticsPedalFor(
                LinkedDevice.DeviceDescriptor?.DeviceTypeID ?? "");
            if (pedal != 0) _pedal = pedal;

            // Injection is deferred to DataUpdate() — running it here would beat
            // SimHub's own sub-device setup, same as on the wheelbase.
            TryInstallProvider();
        }

        /// <summary>
        /// Find the connection sub-device and take over its manager. Retries on
        /// the next tick while <c>GetInstances()</c> is still empty — SimHub can
        /// call DataUpdate before it has finished composing the device.
        /// </summary>
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
                MozaLog.Debug($"[AZOM] Pedal-haptics driver injection deferred: {ex.Message}");
            }
        }

        private static bool IsConnectionDevice(object instance) =>
            string.Equals(instance?.GetType().FullName,
                "SimHub.Plugins.OutputPlugins.CommonDevices.Devices.StandardProtocolConnectionDevice",
                StringComparison.Ordinal);

        private void InjectConnectionManager(object instance)
        {
            _connectionSwapAttempted = true;

            // Guarded: the manager type binds 9.12-era SimHub/BA63 members, so it
            // must never be loaded on a build that lacks them.
            if (!MozaPedalHapticsBridge.IsSupported) return;

            try
            {
                var manager = (MozaPedalHapticsConnectionManager)MozaPedalHapticsBridge.CreateConnectionManager(_pedal);
                var previous = LedDriverInjection.SwapConnectionManager(instance, manager);
                if (previous == null) return;

                _connectionDevice = instance;
                _connectionManager = manager;
                _originalConnectionManager = previous;
                MozaLog.Info("[AZOM] Took over the pedal-haptics device's connection manager");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not take over the pedal-haptics connection manager: {ex.Message}");
            }
        }

        private void TryInstallProvider()
        {
            try
            {
                if (_motorsDevice == null)
                    _motorsDevice = FindMotorsDevice();
                if (_motorsDevice == null) return;

                MozaPedalHapticsBridge.TryInstallChannelsProvider(_motorsDevice, _pedal);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Pedal-haptics provider install skipped: {ex.Message}");
            }
        }

        private object? FindMotorsDevice()
        {
            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    // Same sub-device type as the wheelbase's Haptics section, so
                    // the wheelbase bridge's type-name matcher is reused.
                    if (MozaBaseHapticsBridge.IsMotorsDeviceExtension(instance))
                        return instance;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Could not locate the pedal-haptics Haptics sub-device: {ex.Message}");
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
        }

        // The unit has no per-device settings of its own — every knob lives in
        // ShakeIt. These stay minimal rather than persisting an empty object.
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
