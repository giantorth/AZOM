using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Device-definition deployment for the mBooster pedals' ShakeIt device: one
    /// combined device, one channel per pedal role (Throttle / Brake / Clutch).
    /// </summary>
    internal static partial class DeviceDefinitionDeployer
    {
        internal const string MBoosterShakeItDeviceName = "MOZA mBooster Pedals";

        // Embedded art under DeviceTemplates/Thumbnails; EnsureThumbnail skips a missing key.
        private const string MBoosterThumbnailKey = "mBooster";

        /// <summary>Content version of the generated mBooster device.json.</summary>
        private const int GeneratedMBoosterSchemaVersion = 1;

        internal const int MBoosterShakeItChannels = 3;

        private static HapticsOnlyDefinition MBoosterDefinition => new HapticsOnlyDefinition(
            MBoosterShakeItDeviceName, MozaDeviceConstants.MBoosterShakeItGuid, "mBooster Pedals",
            MBoosterShakeItChannels,
            MozaMBoosterProtocol.ShakeItMinFrequencyHz, MozaMBoosterProtocol.ShakeItMaxFrequencyHz,
            GeneratedMBoosterSchemaVersion, MBoosterThumbnailKey, "mBooster");

        /// <summary>
        /// Write (or refresh) the mBooster definition once a unit has answered.
        /// </summary>
        /// <param name="discoveredPid">
        /// The PID SimHub should detect on: the unit's own for a USB lane, the host
        /// wheelbase's for a routed one (same anchor rule as the S12).
        /// </param>
        public static bool DeployForMBooster(string? discoveredPid, bool force = false)
            => DeployHapticsOnlyDefinition(MBoosterDefinition, discoveredPid, force);

        /// <summary>
        /// Bulk-redeploy path. Only with a unit present, so users without an
        /// mBooster never get a permanently-disconnected device; nothing when the
        /// plugin tab drives the pedals.
        /// </summary>
        private static (int Written, int Total) RedeployAllMBooster(string wheelbasePid)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null || !plugin.MBoosterRoutedToShakeIt || !plugin.IsMBoosterDetected) return (0, 0);

            string pid = plugin.MBoosterDetectionPid() ?? wheelbasePid;
            return (DeployForMBooster(pid, force: true) ? 1 : 0, 1);
        }
    }
}
