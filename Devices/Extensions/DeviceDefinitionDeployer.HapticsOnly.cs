using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>
    /// Shared writer for definitions that declare HapticsFeature and nothing else
    /// (S12 pedal haptics, mBooster pedals). The HardwareInterface block is only a
    /// detection anchor: the device extension swaps our driver in, so nothing is
    /// ever written over HID.
    /// </summary>
    internal static partial class DeviceDefinitionDeployer
    {
        /// <summary>Everything that differs between haptics-only definitions.</summary>
        private readonly struct HapticsOnlyDefinition
        {
            public readonly string DeviceName;
            public readonly string Guid;
            public readonly string ProductName;
            public readonly int MotorsCount;
            public readonly int MinFrequencyHz;
            public readonly int MaxFrequencyHz;
            public readonly int SchemaVersion;
            public readonly string ThumbnailKey;
            public readonly string LogLabel;

            public HapticsOnlyDefinition(string deviceName, string guid, string productName, int motorsCount,
                int minFrequencyHz, int maxFrequencyHz, int schemaVersion, string thumbnailKey, string logLabel)
            {
                DeviceName = deviceName;
                Guid = guid;
                ProductName = productName;
                MotorsCount = motorsCount;
                MinFrequencyHz = minFrequencyHz;
                MaxFrequencyHz = maxFrequencyHz;
                SchemaVersion = schemaVersion;
                ThumbnailKey = thumbnailKey;
                LogLabel = logLabel;
            }
        }

        /// <returns>True when device.json was written and SimHub needs a restart.</returns>
        private static bool DeployHapticsOnlyDefinition(in HapticsOnlyDefinition def, string? discoveredPid, bool force)
        {
            var pid = discoveredPid ?? FallbackPid;

            try
            {
                var deviceDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "DevicesDefinitions", "User", def.DeviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");
                bool fileExists = File.Exists(deviceJsonPath);

                if (fileExists && !force && !IsHapticsOnlyDefinitionStale(deviceJsonPath, def, pid))
                {
                    // Current, but the artwork may still be missing.
                    EnsureThumbnail(deviceDir, def.ThumbnailKey);
                    return false;
                }

                Directory.CreateDirectory(deviceDir);
                AtomicFile.WriteAllText(deviceJsonPath, GenerateHapticsOnlyDeviceJson(def, pid));
                EnsureThumbnail(deviceDir, def.ThumbnailKey);

                MozaLog.Info(
                    $"[AZOM] {(fileExists ? "Refreshed" : "Deployed")} {def.LogLabel} device definition: "
                    + $"{def.DeviceName} (guid={def.Guid}, "
                    + $"channels={def.MotorsCount}, pid={pid}; "
                    + "restart SimHub to pick it up)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Error deploying the {def.DeviceName} device definition: {ex.Message}");
                return false;
            }
        }

        /// <summary>Rewrite when identity, content version, haptics shape or PID drift.</summary>
        private static bool IsHapticsOnlyDefinitionStale(string deviceJsonPath, in HapticsOnlyDefinition def, string pid)
        {
            try
            {
                var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));

                if (!string.Equals(existing["DescriptorUniqueId"]?.Value<string>(), def.Guid, StringComparison.OrdinalIgnoreCase))
                    return true;
                if ((existing["SchemaVersion"]?.Value<int>() ?? 0) != def.SchemaVersion)
                    return true;
                var haptics = existing["HapticsFeature"];
                if (haptics == null
                    || (haptics["MotorsCount"]?.Value<int>() ?? -1) != def.MotorsCount
                    || (haptics["MinimumFrequency"]?.Value<int>() ?? -1) != def.MinFrequencyHz
                    || (haptics["MaximumFrequency"]?.Value<int>() ?? -1) != def.MaxFrequencyHz)
                    return true;
                if (!PidEquals(
                        existing.SelectToken("HardwareInterface.HardwareInterface.DeviceDetection.Pid")?.Value<string>(),
                        pid))
                    return true;

                return false;
            }
            catch
            {
                // Unparseable — rewrite it.
                return true;
            }
        }

        private static string GenerateHapticsOnlyDeviceJson(in HapticsOnlyDefinition def, string pid)
        {
            var device = new JObject
            {
                ["DescriptorUniqueId"] = def.Guid,
                ["SchemaVersion"] = def.SchemaVersion,
                // HapticsFeature and the MotorsWithFrequency provider types are 9.12+.
                ["MinimumSimHubVersion"] = "9.12.0",
                ["DeviceDescription"] = new JObject
                {
                    ["BrandName"] = "MOZA",
                    ["ProductName"] = def.ProductName
                },
                ["HapticsFeature"] = new JObject
                {
                    ["MotorsCount"] = def.MotorsCount,
                    ["HasFrequency"] = true,
                    ["MinimumFrequency"] = def.MinFrequencyHz,
                    ["MaximumFrequency"] = def.MaxFrequencyHz,
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
