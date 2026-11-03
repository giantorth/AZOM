using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Devices.Extensions
{
    /// <summary>One device instance SimHub has saved settings for.</summary>
    internal sealed class SimHubDeviceEntry
    {
        public string InstanceId = "";
        public string DeviceTypeId = "";
        public JObject Document = new JObject();
    }

    /// <summary>
    /// Read-only walk of SimHub's saved device instances
    /// (<c>PluginsData\Common\Devices\{InstanceId}\settings.json</c>). Includes
    /// devices that are added but not connected, and orphaned ones whose definition
    /// is gone — SimHub only deletes the folder when the user removes the device.
    /// Never writes; unreadable entries are skipped.
    /// </summary>
    internal static class SimHubDeviceRoster
    {
        public static IEnumerable<SimHubDeviceEntry> Enumerate()
        {
            var root = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "PluginsData", "Common", "Devices");
            if (!Directory.Exists(root)) yield break;

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

                yield return new SimHubDeviceEntry { InstanceId = folder, DeviceTypeId = typeId, Document = doc };
            }
        }
    }
}
