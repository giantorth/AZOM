using System;
using BA63Driver;
using BA63Driver.Interfaces;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using MozaPlugin.Integration;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// The mBooster side of a haptics-only device definition (SimHub 9.12+). Same
    /// takeover as <see cref="MozaPedalHapticsBridge"/>: a
    /// <see cref="MozaHapticsConnectionManager"/> replaces the
    /// StandardProtocolConnectionDevice manager, and
    /// <see cref="MozaMBoosterChannelsProvider"/> replaces the stock channels
    /// provider. Only touched behind <see cref="MozaBaseHapticsBridge.IsSupported"/>.
    /// </summary>
    internal static class MozaMBoosterHapticsBridge
    {
        public static bool IsSupported => MozaBaseHapticsBridge.IsSupported;

        /// <summary>Build the replacement manager lazily, inside a caller's guard.</summary>
        public static object CreateConnectionManager() => new MozaHapticsConnectionManager(
            () => MozaPlugin.Instance?.IsMBoosterShakeItActive == true,
            new MozaMBoosterMotorsDriver());

        /// <summary>
        /// Keep <see cref="MozaMBoosterChannelsProvider"/> installed on every
        /// output-manager slot; a profile switch stamps the stock one back.
        /// </summary>
        public static void TryInstallChannelsProvider(object motorsDeviceExtension)
            => MozaBaseHapticsBridge.InstallOnOutputManagers(motorsDeviceExtension, Install, "mBooster");

        private static void Install(object? outputManager, object settings)
        {
            if (!(outputManager is MotorsOutputManagerBase manager)) return;
            if (manager.ShakeItChannelsInfoProvider is MozaMBoosterChannelsProvider) return;

            var provider = new MozaMBoosterChannelsProvider();
            manager.ShakeItChannelsInfoProvider = provider;

            if (settings is SimHub.Plugins.DataPlugins.ShakeItV3.Settings.ShakeItSettings shakeItSettings)
                provider.SetSettings(shakeItSettings);

            MozaLog.Info("[AZOM] Installed the MOZA mBooster channels provider "
                       + $"({MozaMBoosterChannelsProvider.ChannelCount} pedal roles)");
        }
    }

    /// <summary>
    /// Sink for SimHub's ShakeIt motors mixer, one slot per pedal role. Only
    /// reached before our channels provider is installed, whose UpdateOutput
    /// posts directly.
    /// </summary>
    internal sealed class MozaMBoosterMotorsDriver : IMotorsDriver
    {
        public bool IsConnected => MozaPlugin.Instance?.IsMBoosterShakeItActive == true;

        public string SerialNumber => "";
        public string FirmwareVersion => "";

        public bool SendMotors(MotorStates states, bool forceRefresh)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return false;

            var s = states?.States;
            if (s == null)
            {
                Clear();
                return true;
            }

            // Length comes from the definition on disk; never assume it.
            int n = Math.Min(s.Length, MozaMBoosterChannelsProvider.ChannelCount);
            for (int role = 0; role < n; role++)
                plugin.PostShakeItMBooster(role, s[role].Gain, s[role].Frequency);
            return true;
        }

        public void Clear() => MozaPlugin.Instance?.ClearShakeItMBooster();

        public void Dispose() => Clear();
    }
}
