using System;
using System.IO;
using System.Reflection;
using MozaPlugin.Protocol;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Writes SimHub device definitions (<c>device.json</c>) into
    /// <c>DevicesDefinitions/User/&lt;DeviceName&gt;/</c> at runtime when a
    /// MOZA device is detected. Two sources: embedded resources (dashboard,
    /// old-protocol wheel) and a generated JSON tree (new-protocol wheels,
    /// where the LED/button layout depends on the detected model).
    ///
    /// Each method returns <c>true</c> when a file was actually written
    /// (fresh deploy or stale-rewrite); the caller is expected to flip its
    /// "restart SimHub" flag on a true result.
    /// </summary>
    internal static class DeviceDefinitionDeployer
    {
        private const string DashResource = "MozaPlugin.Devices.Dash.device.json";
        private const string DashCm2Resource = "MozaPlugin.Devices.DashCm2.device.json";
        private const string OldProtoResource = "MozaPlugin.Devices.WheelOldProto.device.json";
        private const string BaseAmbientResource = "MozaPlugin.Devices.WheelBase.device.json";
        private const string DashDeviceName = "MOZA Dashboard";
        private const string DashCm2DeviceName = "MOZA CM2 Racing Dash";
        private const string DashCm2ProductName = "CM2 Racing Dash";
        private const string OldProtoDeviceName = "MOZA Old Protocol Wheel";
        private const string BaseAmbientDeviceName = "MOZA Wheel Base";

        // 0x0006 (R9 wheelbase) is the most common documented PID. The prior
        // 0x0004 placeholder doesn't match any known device. Used only when
        // registry discovery returns no PID (probe path under Wine).
        private const string FallbackPid = "0x0006";

        /// <summary>
        /// Deploy a dynamically generated device definition for a new-protocol wheel.
        /// Uses WheelModelInfo for button count (defaults for unknown models) and
        /// deterministic GUIDs for device identity.
        /// Called once when the wheel model name is first received from firmware.
        /// </summary>
        public static bool DeployForModel(string modelName, string? discoveredPid)
        {
            var prefix = WheelModelInfo.ExtractPrefix(modelName);
            var friendlyName = WheelModelInfo.GetFriendlyName(prefix);
            var guid = MozaDeviceConstants.ResolveWheelGuid(prefix);
            var modelInfo = WheelModelInfo.FromModelName(modelName);
            var deviceName = "MOZA " + friendlyName;

            return DeployGeneratedWheelDefinition(
                deviceName, guid, friendlyName,
                modelInfo.RpmLedCount, modelInfo.HasFlagLeds,
                modelInfo.ButtonLedCount, modelInfo.KnobCount,
                modelInfo.BrowSegmentSize,
                discoveredPid);
        }

        /// <summary>
        /// Deploy the embedded dashboard device definition. Routes by PID:
        /// CM2 standalone (PID 0x0025) gets the 16-RPM-LED CM2 template;
        /// every other dashboard PID gets the legacy SHDP template.
        /// </summary>
        public static bool DeployDashboard(string? discoveredPid)
            => string.Equals(discoveredPid, MozaUsbIds.PidDashboardCm2, StringComparison.OrdinalIgnoreCase)
                ? DeployFromResource(DashCm2DeviceName, DashCm2Resource, discoveredPid, MozaDeviceConstants.DashCm2Guid)
                : DeployFromResource(DashDeviceName, DashResource, discoveredPid, MozaDeviceConstants.DashGuid);

        /// <summary>
        /// Deploy the embedded "Wheel Base" device definition exposing the
        /// 18-LED ambient telemetry strip. Caller must have already verified
        /// the connected base actually has the strip (via the
        /// base-ambient-brightness read probe in MozaPlugin) — bases without
        /// it (R9/R12) should never see this file deployed.
        /// </summary>
        public static bool DeployBaseAmbient(string? discoveredPid)
            => DeployFromResource(BaseAmbientDeviceName, BaseAmbientResource, discoveredPid, MozaDeviceConstants.BaseAmbientGuid);

        /// <summary>
        /// Deploy the old-protocol wheel device definition.
        /// Called once when an ES wheel is detected.
        /// </summary>
        public static bool DeployOldProtoWheel(string? discoveredPid)
            => DeployFromResource(OldProtoDeviceName, OldProtoResource, discoveredPid, MozaDeviceConstants.WheelOldProtoGuid);

        private static bool DeployGeneratedWheelDefinition(string deviceName, string guid, string productName,
            int rpmCount, bool hasFlagLeds, int buttonCount, int knobCount, int browSegmentSize, string? discoveredPid)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                int expectedTelemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
                bool fileExists = File.Exists(deviceJsonPath);
                bool stale = false;

                if (fileExists)
                {
                    // Compare existing LogicalTelemetryLeds.LedCount + LogicalButtonsSection.Items
                    // against expected. Mismatch = layout changed in a plugin update; rewrite.
                    try
                    {
                        var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));
                        int existingLed = existing.SelectToken("LedsFeature.LogicalTelemetryLeds.LedCount")?.Value<int>() ?? -1;
                        int existingButtons = (existing.SelectToken("LedsFeature.LogicalButtonsSection.Items") as JArray)?.Count ?? -1;
                        int existingExtra = existing.SelectToken("LedsFeature.LogicalExtraSection.LedCount")?.Value<int>() ?? 0;
                        stale = existingLed != expectedTelemetryCount || existingButtons != buttonCount || existingExtra != knobCount;
                    }
                    catch (Exception parseEx)
                    {
                        MozaLog.Warn(
                            $"[Moza] Could not parse existing device.json for '{deviceName}', rewriting: {parseEx.Message}");
                        stale = true;
                    }

                    if (!stale)
                        return false;
                }

                Directory.CreateDirectory(deviceDir);

                // Registry-based discovery always populates DiscoveredPid when
                // we successfully connected. Fallback only matters if the file
                // is generated before the first connect.
                var pid = discoveredPid ?? FallbackPid;
                var json = GenerateWheelDeviceJson(guid, productName, rpmCount, hasFlagLeds, buttonCount, knobCount, browSegmentSize, pid);
                File.WriteAllText(deviceJsonPath, json);

                string action = stale ? "Refreshed" : "Deployed";
                MozaLog.Debug(
                    $"[Moza] {action} device definition: {deviceName} " +
                    $"(guid={guid}, telemetryLeds={expectedTelemetryCount}, rpm={rpmCount}, flags={hasFlagLeds}, " +
                    $"buttons={buttonCount}, knobs={knobCount}, brow={browSegmentSize}, pid={pid}, restart SimHub to pick up changes)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Error deploying device definition '{deviceName}': {ex.Message}");
                return false;
            }
        }

        private static string GenerateWheelDeviceJson(string guid, string productName, int rpmCount, bool hasFlagLeds, int buttonCount, int knobCount, int browSegmentSize, string pid)
        {
            var physItems = new JArray();

            // Telemetry LEDs: single contiguous sequence. When the wheel has flag LEDs
            // they are 3-on-each-side of the RPM strip, so SimHub sees (rpmCount + 6)
            // LEDs as one logical run: [flag 1..3][rpm 1..N][flag 4..6].
            int telemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
            physItems.Add(new JObject
            {
                ["SourceRole"] = 1,
                ["SourceIndex"] = 0,
                ["RepeatCount"] = telemetryCount,
                ["RepeatMode"] = 1
            });
            for (int i = 1; i < telemetryCount; i++)
                physItems.Add(new JObject());

            // Button LEDs: buttonCount slots
            physItems.Add(new JObject
            {
                ["SourceRole"] = 2,
                ["SourceIndex"] = 0,
                ["RepeatCount"] = buttonCount,
                ["RepeatMode"] = 1
            });
            for (int i = 1; i < buttonCount; i++)
                physItems.Add(new JObject());

            // Knob indicator LEDs (Extra/encoders channel): one per rotary knob
            if (knobCount > 0)
            {
                physItems.Add(new JObject
                {
                    ["SourceRole"] = 3,
                    ["SourceIndex"] = 0,
                    ["RepeatCount"] = knobCount,
                    ["RepeatMode"] = 1
                });
                for (int i = 1; i < knobCount; i++)
                    physItems.Add(new JObject());
            }

            var buttonItems = new JArray();
            for (int i = 0; i < buttonCount; i++)
            {
                buttonItems.Add(new JObject
                {
                    ["Left"] = 20,
                    ["Top"] = 20,
                    ["Width"] = 40
                });
            }

            var device = new JObject
            {
                ["DescriptorUniqueId"] = guid,
                ["SchemaVersion"] = 1,
                ["MinimumSimHubVersion"] = "9.11.8",
                ["DeviceDescription"] = new JObject
                {
                    ["BrandName"] = "MOZA",
                    ["ProductName"] = productName
                },
                ["LedsFeature"] = new JObject
                {
                    ["IsIndividualLedsSectionEnabled"] = true,
                    ["PhysicalLedsMappings"] = new JObject { ["Items"] = physItems },
                    ["LogicalTelemetryLeds"] = new JObject
                    {
                        ["LedCount"] = telemetryCount,
                        ["Segments"] = BuildTelemetrySegments(hasFlagLeds, browSegmentSize),
                        ["IsEnabled"] = true
                    },
                    ["LogicalButtonsSection"] = new JObject
                    {
                        ["IsButtonEditorEnabled"] = false,
                        ["Items"] = buttonItems,
                        ["IsEnabled"] = true
                    },
                    ["LogicalExtraSection"] = knobCount > 0
                        ? new JObject
                        {
                            ["LedCount"] = knobCount,
                            ["TitleOverride"] = "Knob Indicators",
                            ["IsEnabled"] = true
                        }
                        : new JObject { ["IsEnabled"] = false },
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

        // SimHub interprets a single Segments entry of Size N as a 3-LED brow
        // region carved from the LogicalTelemetryLeds strip. Both the legacy
        // hasFlagLeds path (extra 6 LEDs total, 3 per side) and the in-band
        // brow path (segment carved from the existing strip) use the same
        // single-entry representation; they only differ in whether the LED
        // count includes the segment. Brow size wins when both are present.
        private static JArray BuildTelemetrySegments(bool hasFlagLeds, int browSegmentSize)
        {
            int size = browSegmentSize > 0 ? browSegmentSize : (hasFlagLeds ? 3 : 0);
            if (size <= 0)
                return new JArray();
            return new JArray(new JObject { ["Size"] = size });
        }

        private static bool DeployFromResource(string deviceName, string resourceName, string? discoveredPid, string expectedDescriptorId)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                bool fileExists = File.Exists(deviceJsonPath);
                bool stale = false;

                if (fileExists)
                {
                    // Compare existing PID + DescriptorUniqueId against expected.
                    // PID mismatch covers user moving between hardware variants;
                    // DescriptorUniqueId mismatch covers the plugin shipping a new
                    // template for the same PID (e.g. previously-deployed SHDP
                    // template with PID 0x0025 must be replaced with the CM2
                    // template that uses a different GUID).
                    try
                    {
                        var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));
                        string? existingPid = existing
                            .SelectToken("HardwareInterface.HardwareInterface.DeviceDetection.Pid")
                            ?.Value<string>();
                        string? existingDescriptorId = existing
                            .SelectToken("DescriptorUniqueId")
                            ?.Value<string>();
                        string expectedPid = discoveredPid ?? FallbackPid;
                        stale =
                            !string.Equals(existingPid, expectedPid, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(existingDescriptorId, expectedDescriptorId, StringComparison.OrdinalIgnoreCase);

                        // CM2-specific guard: a user-renamed JSON (ProductName
                        // changed from "CM2 Racing Dash") signals manual
                        // intervention; rewrite to the shipped template.
                        if (!stale
                            && string.Equals(deviceName, DashCm2DeviceName, StringComparison.Ordinal))
                        {
                            string? productName = existing
                                .SelectToken("DeviceDescription.ProductName")
                                ?.Value<string>();
                            if (!string.Equals(productName, DashCm2ProductName, StringComparison.Ordinal))
                                stale = true;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        MozaLog.Warn(
                            $"[Moza] Could not parse existing device.json for '{deviceName}', rewriting: {parseEx.Message}");
                        stale = true;
                    }

                    if (!stale)
                        return false;
                }

                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        MozaLog.Warn($"[Moza] Embedded resource not found: {resourceName}");
                        return false;
                    }

                    Directory.CreateDirectory(deviceDir);

                    // Read the template JSON and patch the PID if we discovered one
                    string json;
                    using (var reader = new StreamReader(stream))
                    {
                        json = reader.ReadToEnd();
                    }

                    if (discoveredPid != null)
                    {
                        json = json.Replace("__DETECT_PID__", discoveredPid);
                        MozaLog.Debug($"[Moza] Patched device PID to {discoveredPid} for {deviceName}");
                    }
                    else
                    {
                        json = json.Replace("__DETECT_PID__", FallbackPid);
                        MozaLog.Debug($"[Moza] No PID discovered, using fallback {FallbackPid} for {deviceName}");
                    }

                    File.WriteAllText(deviceJsonPath, json);
                }

                string action = stale ? "Refreshed" : "Deployed";
                MozaLog.Info($"[Moza] {action} device definition: {deviceName} (restart SimHub to add it)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Error deploying device definition '{deviceName}': {ex.Message}");
                return false;
            }
        }
    }
}
