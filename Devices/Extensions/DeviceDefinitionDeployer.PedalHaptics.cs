using System;
using System.IO;
using Newtonsoft.Json.Linq;
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

            string pid = wheelbasePid;
            var plugin = MozaPlugin.Instance;
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
            => DeployPedalHapticsDefinition(
                PedalHapticsDeviceName(pedal), PedalHapticsGuidFor(pedal),
                "S12 " + MozaPedalHapticsProtocol.PedalLabel(pedal),
                MozaPedalHapticsProtocol.ChannelsPerPedal, discoveredPid, force);

        // MotorsCount is what SimHub's stock provider shows before ours is
        // installed: one motor per pedal.
        private static bool DeployPedalHapticsCombinedDevice(string? discoveredPid, bool force = false)
            => DeployPedalHapticsDefinition(
                PedalHapticsCombinedDeviceName, MozaDeviceConstants.PedalHapticsCombinedGuid,
                "S12 Pedal Haptics", MozaPedalHapticsProtocol.PedalCount, discoveredPid, force);

        private static bool DeployPedalHapticsDefinition(
            string deviceName, string guid, string productName, int motorsCount, string? discoveredPid, bool force)
        {
            var pid = discoveredPid ?? FallbackPid;

            try
            {
                var deviceDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "DevicesDefinitions", "User", deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");
                bool fileExists = File.Exists(deviceJsonPath);

                if (fileExists && !force && !IsPedalHapticsDefinitionStale(deviceJsonPath, guid, pid))
                {
                    // Current, but the artwork may still be missing.
                    EnsureThumbnail(deviceDir, PedalHapticsThumbnailKey);
                    return false;
                }

                Directory.CreateDirectory(deviceDir);
                WriteAllTextAtomic(deviceJsonPath, GeneratePedalHapticsDeviceJson(guid, productName, motorsCount, pid));
                EnsureThumbnail(deviceDir, PedalHapticsThumbnailKey);

                MozaLog.Info(
                    $"[AZOM] {(fileExists ? "Refreshed" : "Deployed")} pedal-haptics device definition: "
                    + $"{deviceName} (guid={guid}, "
                    + $"channels={motorsCount}, pid={pid}; "
                    + "restart SimHub to pick it up)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error deploying the {deviceName} device definition: {ex.Message}");
                return false;
            }
        }
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

        /// <summary>Rewrite when identity, content version or PID drift.</summary>
        private static bool IsPedalHapticsDefinitionStale(string deviceJsonPath, string guid, string pid)
        {
            try
            {
                var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));

                if (!string.Equals(existing["DescriptorUniqueId"]?.Value<string>(), guid, StringComparison.OrdinalIgnoreCase))
                    return true;
                if ((existing["SchemaVersion"]?.Value<int>() ?? 0) != GeneratedPedalHapticsSchemaVersion)
                    return true;
                if (!string.Equals(
                        existing.SelectToken("HardwareInterface.HardwareInterface.DeviceDetection.Pid")?.Value<string>(),
                        pid, StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            }
            catch
            {
                // Unparseable — rewrite it.
                return true;
            }
        }

        private static string GeneratePedalHapticsDeviceJson(string guid, string productName, int motorsCount, string pid)
        {
            var device = new JObject
            {
                ["DescriptorUniqueId"] = guid,
                ["SchemaVersion"] = GeneratedPedalHapticsSchemaVersion,
                // HapticsFeature and the MotorsWithFrequency provider types are 9.12+.
                ["MinimumSimHubVersion"] = "9.12.0",
                ["DeviceDescription"] = new JObject
                {
                    ["BrandName"] = "MOZA",
                    ["ProductName"] = productName
                },
                ["HapticsFeature"] = new JObject
                {
                    ["MotorsCount"] = motorsCount,
                    ["HasFrequency"] = true,
                    ["MinimumFrequency"] = MozaPedalHapticsProtocol.MinFrequencyHz,
                    ["MaximumFrequency"] = MozaPedalHapticsProtocol.MaxFrequencyHz,
                    ["IsEnabled"] = true
                },
                ["HardwareInterface"] = new JObject
                {
                    ["HardwareInterface"] = new JObject
                    {
                        ["TypeName"] = "LedsStandardHIDProtocol",
                        ["IsSerialNumberPickerEnabled"] = false,
                        ["HIDUsagePage"] = "0xFF00",
                        ["HIDUsage"] = "0x77",
                        ["HIDReportId"] = "0x68",
                        ["HIDReportSize"] = 64,
                        ["HIDFansReportId"] = HidFansReportId,
                        ["HIDMotorsReportId"] = HidMotorsReportId,
                        ["DeviceDetection"] = new JObject
                        {
                            ["Vid"] = "0x346E",
                            ["Pid"] = pid
                        }
                    }
                },
                ["IsLocked"] = true
            };

            return device.ToString(Newtonsoft.Json.Formatting.Indented);
        }
    }
}
