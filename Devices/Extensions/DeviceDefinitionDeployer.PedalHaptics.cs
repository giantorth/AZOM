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
        private const string PedalHapticsDeviceName = "MOZA S12 Pedal Vibration";

        /// <summary>Product-render key under DeviceTemplates/Thumbnails, deployed as a thumbnail.png sidecar.</summary>
        private const string PedalHapticsThumbnailKey = "S12";

        /// <summary>
        /// Content version of the generated pedal-haptics device.json. Bump when
        /// the generated body changes in a way that must re-deploy over an
        /// otherwise-unchanged file. v2: all 27 channels (3 pedals x 9 effect
        /// slots) instead of 3, and the 10-100 Hz band.
        /// </summary>
        private const int GeneratedPedalHapticsSchemaVersion = 2;

        /// <summary>
        /// Write (or refresh) the definition once a unit is actually present.
        /// Deploying it unconditionally would put a permanently-disconnected
        /// device in every user's SimHub device list.
        /// </summary>
        /// <param name="discoveredPid">
        /// The PID SimHub should detect on. For a routed unit this is the host
        /// wheelbase's PID — the unit itself never enumerates, and the
        /// HardwareInterface block is only a detection anchor (the extension
        /// swaps our driver in, so nothing is written over HID). The wheel
        /// definition already binds the base's PID the same way. For a USB unit
        /// it is that unit's own PID, which is also how the PID first becomes
        /// known at all.
        /// </param>
        public static bool DeployForPedalHaptics(string? discoveredPid)
        {
            var pid = discoveredPid ?? FallbackPid;
            var guid = MozaDeviceConstants.PedalHapticsGuid;

            try
            {
                var deviceDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "DevicesDefinitions", "User", PedalHapticsDeviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");
                bool fileExists = File.Exists(deviceJsonPath);

                if (fileExists && !IsPedalHapticsDefinitionStale(deviceJsonPath, guid, pid))
                {
                    // Current, but the artwork may still be missing.
                    EnsureThumbnail(deviceDir, PedalHapticsThumbnailKey);
                    return false;
                }

                Directory.CreateDirectory(deviceDir);
                WriteAllTextAtomic(deviceJsonPath, GeneratePedalHapticsDeviceJson(guid, pid));
                EnsureThumbnail(deviceDir, PedalHapticsThumbnailKey);

                MozaLog.Info(
                    $"[AZOM] {(fileExists ? "Refreshed" : "Deployed")} pedal-haptics device definition: "
                    + $"{PedalHapticsDeviceName} (guid={guid}, "
                    + $"motors={MozaPedalHapticsProtocol.ChannelCount}, pid={pid}; "
                    + "restart SimHub to pick it up)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error deploying the pedal-haptics device definition: {ex.Message}");
                return false;
            }
        }

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

        private static string GeneratePedalHapticsDeviceJson(string guid, string pid)
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
                    ["ProductName"] = "S12 Pedal Vibration"
                },
                ["HapticsFeature"] = new JObject
                {
                    ["MotorsCount"] = MozaPedalHapticsProtocol.ChannelCount,
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
