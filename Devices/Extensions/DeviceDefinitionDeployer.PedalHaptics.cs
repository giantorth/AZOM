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
    /// </summary>
    internal static partial class DeviceDefinitionDeployer
    {
        /// <summary>Device name, and therefore the folder under DevicesDefinitions/User.</summary>
        /// <summary>Device names, one per motor port. Also the folder under DevicesDefinitions/User.</summary>
        private static string PedalHapticsDeviceName(byte pedal)
            => "MOZA S12 " + MozaPedalHapticsProtocol.PedalLabel(pedal);

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
        /// Write (or refresh) one definition per motor port, once a unit is
        /// actually present. Deploying unconditionally would put three
        /// permanently-disconnected devices in every user's SimHub list.
        ///
        /// One device per pedal rather than one with every channel: each pedal
        /// then carries its own ShakeIt profile and effect defaults, and the
        /// channel list stays short enough to read.
        /// </summary>
        /// <param name="discoveredPid">
        /// The PID SimHub should detect on. For a routed unit this is the host
        /// wheelbase's PID — the unit itself never enumerates, and the
        /// HardwareInterface block is only a detection anchor (the extension
        /// swaps our driver in, so nothing is written over HID). The wheel
        /// definition already binds the base's PID the same way. For a USB unit
        /// it is that unit's own PID.
        /// </param>
        public static bool DeployForPedalHaptics(string? discoveredPid)
        {
            RemoveLegacyPedalHapticsDefinition();

            bool any = false;
            for (byte pedal = MozaPedalHapticsProtocol.MinPedal;
                 pedal <= MozaPedalHapticsProtocol.MaxPedal; pedal++)
            {
                any |= DeployPedalHapticsDevice(pedal, discoveredPid);
            }
            return any;
        }

        private static bool DeployPedalHapticsDevice(byte pedal, string? discoveredPid)
        {
            var pid = discoveredPid ?? FallbackPid;
            var guid = PedalHapticsGuidFor(pedal);
            var deviceName = PedalHapticsDeviceName(pedal);

            try
            {
                var deviceDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "DevicesDefinitions", "User", deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");
                bool fileExists = File.Exists(deviceJsonPath);

                if (fileExists && !IsPedalHapticsDefinitionStale(deviceJsonPath, guid, pid))
                {
                    // Current, but the artwork may still be missing.
                    EnsureThumbnail(deviceDir, PedalHapticsThumbnailKey);
                    return false;
                }

                Directory.CreateDirectory(deviceDir);
                WriteAllTextAtomic(deviceJsonPath, GeneratePedalHapticsDeviceJson(guid, pedal, pid));
                EnsureThumbnail(deviceDir, PedalHapticsThumbnailKey);

                MozaLog.Info(
                    $"[AZOM] {(fileExists ? "Refreshed" : "Deployed")} pedal-haptics device definition: "
                    + $"{deviceName} (guid={guid}, "
                    + $"channels={MozaPedalHapticsProtocol.ChannelsPerPedal}, pid={pid}; "
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

        private static string GeneratePedalHapticsDeviceJson(string guid, byte pedal, string pid)
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
                    ["ProductName"] = "S12 " + MozaPedalHapticsProtocol.PedalLabel(pedal)
                },
                ["HapticsFeature"] = new JObject
                {
                    ["MotorsCount"] = MozaPedalHapticsProtocol.ChannelsPerPedal,
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
