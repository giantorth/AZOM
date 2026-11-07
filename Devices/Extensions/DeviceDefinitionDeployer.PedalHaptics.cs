using System;
using System.IO;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Device-definition deployment for the pedal-haptics unit. Split out
    /// because it shares almost nothing with the wheel/base/dash generators:
    /// no LEDs, no buttons, no model catalog — a HapticsFeature
    /// block and the detection anchor, and that is the whole file.
    ///
    /// Two layouts: one device per motor port, or one combined device with a
    /// channel per pedal (<c>PedalHapticsCombinedDevice</c>). Only the selected
    /// layout is deployed, and the other is never removed — a user switching
    /// over needs the old devices' effects to copy from.
    /// </summary>
    internal static partial class DeviceDefinitionDeployer
    {
        /// <summary>Device names, one per motor port. Also the folder under DevicesDefinitions/User.</summary>
        private static string PedalHapticsDeviceName(byte pedal)
            => "MOZA S12 " + MozaPedalHapticsProtocol.PedalLabel(pedal);

        /// <summary>
        /// The combined device. Must not collide with <see cref="LegacyPedalHapticsDeviceNames"/>,
        /// which are deleted on every deploy.
        /// </summary>
        internal const string PedalHapticsCombinedDeviceName = "MOZA S12 Pedal Haptics";

        /// <summary>
        /// Single all-pedals devices this replaced, removed on deploy. Two names
        /// because the device was renamed mid-development and a pre-release build
        /// went out under each; neither can route anywhere now.
        /// </summary>
        private static readonly string[] LegacyPedalHapticsDeviceNames =
        {
            "MOZA S12 Pedal Vibration",
            "MOZA Pedal Haptics",
        };

        /// <summary>Product-render key under DeviceTemplates/Thumbnails, deployed as a thumbnail.png sidecar.</summary>
        private const string PedalHapticsThumbnailKey = "S12";

        /// <summary>
        /// Content version of the generated pedal-haptics device.json. Bump when
        /// the generated body changes in a way that must re-deploy over an
        /// otherwise-unchanged file. v3: one device per motor port, each with its
        /// own channel list, replacing the single 27-channel device.
        /// </summary>
        private const int GeneratedPedalHapticsSchemaVersion = 4;

        /// <summary>
        /// Write (or refresh) the selected layout's definitions, once a unit is
        /// actually present. Deploying unconditionally would put permanently-
        /// disconnected devices in every user's SimHub list.
        ///
        /// Per-pedal: each pedal carries its own ShakeIt profile and effect
        /// defaults. Combined: one profile, and each effect picks its pedals.
        /// </summary>
        /// <param name="discoveredPid">
        /// The PID SimHub should detect on. For a routed unit this is the host
        /// wheelbase's PID — the unit itself never enumerates, and the
        /// HardwareInterface block is only a detection anchor (the extension
        /// swaps our driver in, so nothing is written over HID). The wheel
        /// definition already binds the base's PID the same way. For a USB unit
        /// it is that unit's own PID.
        /// </param>
        /// <param name="combined">Deploy the combined device instead of the per-pedal ones.</param>
        public static bool DeployForPedalHaptics(string? discoveredPid, bool combined)
        {
            RemoveLegacyPedalHapticsDefinition();

            if (combined)
                return DeployPedalHapticsCombinedDevice(discoveredPid);

            bool any = false;
            for (byte pedal = MozaPedalHapticsProtocol.MinPedal;
                 pedal <= MozaPedalHapticsProtocol.MaxPedal; pedal++)
            {
                any |= DeployPedalHapticsDevice(pedal, discoveredPid);
            }
            return any;
        }

        /// <summary>
        /// Bulk-redeploy path: force-rewrite the selected layout's definitions.
        /// A detected unit stamps its own detection PID; otherwise the routed
        /// binding (host wheelbase PID) is assumed, and the live detection path
        /// re-stamps it if a USB unit turns up later.
        /// </summary>
        private static (int Written, int Total) RedeployAllPedalHaptics(string wheelbasePid)
        {
            RemoveLegacyPedalHapticsDefinition();

            // Routed to the plugin's S12 tab: no SimHub device is wanted.
            var plugin = MozaPlugin.Instance;
            if (plugin != null && !plugin.PedalHapticsRoutedToShakeIt) return (0, 0);

            string pid = wheelbasePid;
            var registry = plugin?.PedalHapticsRegistry;
            if (registry != null)
            {
                foreach (var controller in registry.Devices)
                {
                    if (!controller.Detected) continue;
                    pid = controller.DiscoveredPid ?? wheelbasePid;
                    break;
                }
            }

            if (plugin?.PedalHapticsCombined == true)
                return (DeployPedalHapticsCombinedDevice(pid, force: true) ? 1 : 0, 1);

            int written = 0, total = 0;
            for (byte pedal = MozaPedalHapticsProtocol.MinPedal;
                 pedal <= MozaPedalHapticsProtocol.MaxPedal; pedal++)
            {
                total++;
                if (DeployPedalHapticsDevice(pedal, pid, force: true))
                    written++;
            }
            return (written, total);
        }

        private static bool DeployPedalHapticsDevice(byte pedal, string? discoveredPid, bool force = false)
            => DeployHapticsOnlyDefinition(
                PedalHapticsDefinition(PedalHapticsDeviceName(pedal), PedalHapticsGuidFor(pedal),
                    "S12 " + MozaPedalHapticsProtocol.PedalLabel(pedal), MozaPedalHapticsProtocol.ChannelsPerPedal),
                discoveredPid, force);

        // MotorsCount is what SimHub's stock provider shows before ours is
        // installed: one motor per pedal.
        private static bool DeployPedalHapticsCombinedDevice(string? discoveredPid, bool force = false)
            => DeployHapticsOnlyDefinition(
                PedalHapticsDefinition(PedalHapticsCombinedDeviceName, MozaDeviceConstants.PedalHapticsCombinedGuid,
                    "S12 Pedal Haptics", MozaPedalHapticsProtocol.PedalCount),
                discoveredPid, force);

        private static HapticsOnlyDefinition PedalHapticsDefinition(
            string deviceName, string guid, string productName, int motorsCount)
            => new HapticsOnlyDefinition(deviceName, guid, productName, motorsCount,
                MozaPedalHapticsProtocol.MinFrequencyHz, MozaPedalHapticsProtocol.MaxFrequencyHz,
                GeneratedPedalHapticsSchemaVersion, PedalHapticsThumbnailKey, "pedal-haptics");

        /// <summary>
        /// Drop the single all-pedals definition this replaced, so SimHub does
        /// not keep offering a device whose channels no longer route anywhere.
        /// Its ShakeIt profile is orphaned either way — the channel list changed
        /// shape — so leaving the entry behind only creates confusion.
        /// </summary>
        private static void RemoveLegacyPedalHapticsDefinition()
        {
            foreach (var name in LegacyPedalHapticsDeviceNames)
            {
                try
                {
                    var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "DevicesDefinitions", "User", name);
                    if (!File.Exists(Path.Combine(dir, "device.json"))) continue;

                    Directory.Delete(dir, recursive: true);
                    MozaLog.Info($"[AZOM] Removed the superseded '{name}' definition "
                               + "(replaced by one device per pedal; restart SimHub to drop the entry)");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Could not remove '{name}': {ex.Message}");
                }
            }
        }

        private static string PedalHapticsGuidFor(byte pedal) => pedal switch
        {
            (byte)PedalHapticsPedal.Brake  => MozaDeviceConstants.PedalHapticsBrakeGuid,
            (byte)PedalHapticsPedal.Clutch => MozaDeviceConstants.PedalHapticsClutchGuid,
            _ => MozaDeviceConstants.PedalHapticsThrottleGuid,
        };
    }
}
