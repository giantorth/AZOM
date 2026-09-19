using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>What a legacy scan found, or empty fields when there is nothing to do.</summary>
    internal sealed class LegacyBaseScanResult
    {
        /// <summary>InstanceId of the orphaned pre-1.6 device, or "" when none was found.</summary>
        public string InstanceId = "";

        /// <summary>The old "Wheelbase LFE haptics" device's ShakeIt settings, ready to hand to the new Haptics sub-device.</summary>
        public JToken? Haptics;

        /// <summary>The legacy shared "MOZA Wheel Base" device's LED settings.</summary>
        public JToken? Leds;

        /// <summary>A per-model wheelbase device already exists with effects turned on — the user migrated by hand.</summary>
        public bool PerModelHapticsConfigured;

        public bool HasAnything => Haptics != null || Leds != null;
    }

    /// <summary>
    /// Finds the device instances the v1.6 wheelbase rework orphaned, so their
    /// settings can be carried into the per-model wheelbase device.
    ///
    /// Up to v1.5.7 a wheelbase was TWO SimHub devices:
    ///   • "MOZA / Wheelbase LFE haptics" — code-registered through
    ///     IDeviceDescriptorsRegistry, DeviceTypeID <see cref="LegacyShakeItDeviceTypeId"/>.
    ///     Its class was deleted in v1.6, so SimHub's registry scan no longer resolves
    ///     the id and the instance — with the user's whole ShakeIt effect tree —
    ///     disappears from the Devices page.
    ///   • "MOZA Wheel Base" — the shared 18-LED definition
    ///     (<see cref="MozaDeviceConstants.BaseAmbientGuid"/>). Its definition folder is
    ///     retired by DeviceDefinitionDeployer.RemoveLegacyBaseDefinition, driven from
    ///     MozaBaseDeviceExtension.TryRetireLegacyDefinition once this scan's settings
    ///     have been carried across — until then both wheelbases sit in SimHub's
    ///     Devices page and both drive the ambient strip.
    ///
    /// Both leave their settings behind: SimHub's own DeviceInstance.GetSettingsPath()
    /// is "PluginsData\Common\Devices\{InstanceId}", and it only removes that folder
    /// when the user deletes the device by hand. So the settings survive an orphaning
    /// and can be read back verbatim.
    ///
    /// Read-only and fail-soft by construction: this never writes, never deletes, and
    /// returns an empty result rather than throwing. Consumption is recorded in
    /// MozaPluginSettings, not on disk, so a failed import can be retried and the
    /// user's old data is never destroyed.
    /// </summary>
    internal static class LegacyBaseDeviceMigration
    {
        /// <summary>
        /// DeviceTypeID of the pre-1.6 code-registered haptics device. Permanent —
        /// it is the only way to recognise an instance whose owning class is gone.
        /// </summary>
        public const string LegacyShakeItDeviceTypeId = "F208F60B-0050-4E83-A874-AE28DD13F7AB";

        /// <summary>
        /// LogicalTelemetryLeds.LedCount the legacy shared "MOZA Wheel Base" definition
        /// always declared. Per-model definitions use the real geometry instead
        /// (BaseModelInfo.LedsPerStrip * 2 — 12 on an R16, 18 on R21/R25/R27), so an
        /// LED profile only transfers when the counts match.
        /// </summary>
        public const int LegacyBaseLedCount = 18;

        private static readonly object Gate = new object();
        private static LegacyBaseScanResult? _cached;

        /// <summary>
        /// Scan SimHub's device-instance store once per process. Callers may invoke
        /// this freely; the result is cached because nothing writes those files while
        /// SimHub is running except SimHub itself, at shutdown.
        ///
        /// Requires <see cref="MozaDeviceConstants.InitializeRegistry"/> to have run —
        /// the per-model check resolves DeviceTypeIDs through the model registry.
        /// </summary>
        public static LegacyBaseScanResult Scan()
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
                _cached = ScanInner();
                return _cached;
            }
        }

        private static LegacyBaseScanResult ScanInner()
        {
            var result = new LegacyBaseScanResult();
            try
            {
                var root = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "PluginsData", "Common", "Devices");
                if (!Directory.Exists(root)) return result;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    // SimHub keeps rolling copies of index.json under _Backups.
                    var folder = Path.GetFileName(dir);
                    if (folder.StartsWith("_", StringComparison.Ordinal)) continue;

                    var path = Path.Combine(dir, "settings.json");
                    if (!File.Exists(path)) continue;

                    JObject doc;
                    try { doc = JObject.Parse(File.ReadAllText(path)); }
                    catch (Exception ex)
                    {
                        MozaLog.Debug($"[AZOM] Skipping unreadable device settings '{folder}': {ex.Message}");
                        continue;
                    }

                    var typeId = doc["DeviceTypeID"]?.Value<string>() ?? "";
                    if (typeId.Length == 0) continue;

                    var settings = doc["Settings"];

                    if (Matches(typeId, LegacyShakeItDeviceTypeId))
                    {
                        result.Haptics = Section(settings, "Haptics", "Profiles");
                        if (result.Haptics != null && result.InstanceId.Length == 0)
                            result.InstanceId = folder;
                        continue;
                    }

                    if (MozaDeviceConstants.GetBaseModelPrefix(typeId) is string prefix)
                    {
                        // "" is the legacy shared identity; anything else is a
                        // per-model device this build wrote.
                        if (prefix.Length == 0)
                        {
                            // LedModuleDevice.SetSettings indexes "ledModuleSettings"
                            // unconditionally and throws KeyNotFoundException without it.
                            result.Leds = Section(settings, "LEDS", "ledModuleSettings");
                            if (result.Leds != null && result.InstanceId.Length == 0)
                                result.InstanceId = folder;
                        }
                        else if (AnyEffectEnabled(Section(settings, "Haptics", "Profiles")))
                        {
                            result.PerModelHapticsConfigured = true;
                        }
                    }
                }

                if (result.HasAnything)
                    MozaLog.Info(
                        "[AZOM] Found pre-1.6 wheelbase device settings to migrate "
                        + $"(haptics={result.Haptics != null}, leds={result.Leds != null})");
                if (result.PerModelHapticsConfigured)
                    MozaLog.Info(
                        "[AZOM] A per-model wheelbase device already has ShakeIt effects enabled "
                        + "— leaving the LFE source and that profile alone");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Could not scan for pre-1.6 wheelbase devices: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Pull one composite section out of a device's saved Settings node.
        ///
        /// CompositeDeviceInstance.GetSettings writes { "&lt;CompositeCode&gt;": … } per
        /// sub-device, but a device added as a standalone root serialises its own
        /// settings directly. Both shapes have existed for these devices, so probe for
        /// the named child and fall back to the node itself — validated by
        /// <paramref name="requiredChild"/> so a wrong-shaped blob is rejected rather
        /// than handed to SetSettings.
        /// </summary>
        private static JToken? Section(JToken? settings, string compositeCode, string? requiredChild)
        {
            if (!(settings is JObject obj)) return null;

            var section = obj[compositeCode] ?? settings;
            if (!(section is JObject sectionObj) || !sectionObj.HasValues) return null;
            if (requiredChild != null && sectionObj[requiredChild] == null) return null;
            return section;
        }

        /// <summary>
        /// True when any effect in any profile is switched on — the proxy for "the user
        /// has configured this device". The plugin's shipped defaults leave every stock
        /// effect disabled, and a profile with nothing enabled drives nothing, so an
        /// all-off profile is safe to replace and anything else is not.
        /// </summary>
        public static bool AnyEffectEnabled(JToken? shakeItSettings)
        {
            if (!(shakeItSettings?["Profiles"] is JArray profiles)) return false;
            if (profiles.Count > 1) return true;   // extra profiles are user work too

            return profiles.Any(p => ContainersHaveEnabled(p?["EffectsContainers"] as JArray));
        }

        // Groups nest their children under the same property name, so recurse.
        private static bool ContainersHaveEnabled(JArray? containers)
        {
            if (containers == null) return false;

            foreach (var container in containers)
            {
                if (!(container is JObject obj)) continue;
                // Only the container's OWN IsEnabled — the channel activations inside
                // SettingsStore carry IsEnabled too, and oscillator 1 is on by default.
                if (obj["IsEnabled"]?.Type == JTokenType.Boolean && obj["IsEnabled"]!.Value<bool>())
                    return true;
                if (ContainersHaveEnabled(obj["EffectsContainers"] as JArray))
                    return true;
            }
            return false;
        }

        private static bool Matches(string deviceTypeId, string id) =>
            deviceTypeId.Equals(id, StringComparison.OrdinalIgnoreCase)
            || deviceTypeId.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase);
    }
}
