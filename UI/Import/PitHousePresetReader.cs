using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MozaPlugin.Resources;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Reads + validates a PitHouse preset JSON file from disk. Returns a
    /// typed <see cref="PitHousePreset"/> on success; on failure returns a
    /// localised error string that the dialog can surface to the user.
    /// </summary>
    public static class PitHousePresetReader
    {
        /// <summary>
        /// Supported deviceType values. Steering-wheel presets exist but are
        /// out of scope for this importer (the LED-effects DSL doesn't map to
        /// the plugin's color arrays).
        /// </summary>
        public static bool IsSupportedDeviceType(string deviceType)
        {
            if (string.IsNullOrEmpty(deviceType)) return false;
            return string.Equals(deviceType, "Motor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deviceType, "Pedals", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Read + validate. On success returns a populated preset and empty
        /// error. On failure returns null preset and a localised message.
        /// </summary>
        public static (PitHousePreset? preset, string error) Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return (null, string.Format(Strings.Import_Error_InvalidJson, path ?? ""));

            JObject root;
            try
            {
                var text = File.ReadAllText(path);
                root = JObject.Parse(text);
            }
            catch (JsonException ex)
            {
                return (null, string.Format(Strings.Import_Error_InvalidJson, ex.Message));
            }
            catch (IOException ex)
            {
                return (null, string.Format(Strings.Import_Error_InvalidJson, ex.Message));
            }

            var deviceType = (string?)root["deviceType"] ?? "";
            if (!IsSupportedDeviceType(deviceType))
                return (null, string.Format(Strings.Import_Error_UnsupportedType, deviceType));

            var deviceParams = root["deviceParams"] as JObject;
            if (deviceParams == null)
                return (null, string.Format(Strings.Import_Error_InvalidJson, "missing 'deviceParams' object"));

            var preset = new PitHousePreset
            {
                Id = (string?)root["id"] ?? "",
                Name = (string?)root["name"] ?? Path.GetFileNameWithoutExtension(path),
                DeviceType = deviceType,
                DeviceParams = deviceParams,
                SourcePath = path,
            };

            var devicesArr = root["devices"] as JArray;
            if (devicesArr != null)
            {
                foreach (var d in devicesArr)
                {
                    var s = (string?)d;
                    if (!string.IsNullOrEmpty(s)) preset.Devices.Add(s!);
                }
            }

            return (preset, "");
        }
    }
}
