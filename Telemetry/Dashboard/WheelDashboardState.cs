using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Snapshot of what the wheel reports via session 0x09 configJson RPC:
    /// which dashboards are loaded, which are disabled, canonical library
    /// names PitHouse offered. Schema matches the 2025-11 firmware capture
    /// (usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng)
    /// and retains compatibility with 2026-04 firmware via field aliasing.
    ///
    /// All 11 top-level fields from the 2025-11 state blob are captured —
    /// previously the parser only pulled 6 and PitHouse downgraded the wheel
    /// to "invalid state" when any were missing. Keep this in sync with
    /// <c>sim/wheel_sim.py build_configjson_state</c>.
    /// </summary>
    public sealed class WheelDashboardState
    {
        public int TitleId { get; set; }
        public int DisplayVersion { get; set; }
        public int ResetVersion { get; set; }
        public int SortTag { get; set; }
        public string RootDirPath { get; set; } = "";
        /// <summary>Names exposed by wheel firmware for library selection UI.</summary>
        public IReadOnlyList<string> ConfigJsonList { get; set; } = Array.Empty<string>();
        /// <summary>Active / installed dashboards (2025-11: enableManager.dashboards; 2026-04: enabledManager.updateDashboards).</summary>
        public IReadOnlyList<WheelDashboardEntry> EnabledDashboards { get; set; } = Array.Empty<WheelDashboardEntry>();
        /// <summary>Explicitly disabled dashboards.</summary>
        public IReadOnlyList<WheelDashboardEntry> DisabledDashboards { get; set; } = Array.Empty<WheelDashboardEntry>();
        /// <summary>Shared image refcount map — <c>MD5/&lt;hash&gt;.png → refcount</c>. Top-level view (union of managers).</summary>
        public IReadOnlyDictionary<string, int> ImageRefMap { get; set; } = new Dictionary<string, int>();
        /// <summary>Image asset catalog: md5, modify, url per shared image.</summary>
        public IReadOnlyList<WheelImagePathEntry> ImagePath { get; set; } = Array.Empty<WheelImagePathEntry>();
        /// <summary>Font refcount map (schema analogous to ImageRefMap; typically empty).</summary>
        public IReadOnlyDictionary<string, int> FontRefMap { get; set; } = new Dictionary<string, int>();
        /// <summary>rootPath observed inside enableManager/disableManager (dashboard storage root).</summary>
        public string RootPath { get; set; } = "";
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class WheelDashboardEntry
    {
        public string Title { get; set; } = "";
        public string DirName { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Id { get; set; } = "";
        public string CreateTime { get; set; } = "";
        public string LastModified { get; set; } = "";
        public IReadOnlyList<string> PreviewImageFilePaths { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> ResourceImageFilePaths { get; set; } = Array.Empty<string>();
        public IReadOnlyList<WheelDashboardDeviceInfo> IdealDeviceInfos { get; set; } = Array.Empty<WheelDashboardDeviceInfo>();
    }

    public sealed class WheelDashboardDeviceInfo
    {
        public int DeviceId { get; set; }
        public string HardwareVersion { get; set; } = "";
        public int NetworkId { get; set; }
        public string ProductType { get; set; } = "";
    }

    public sealed class WheelImagePathEntry
    {
        public string Md5 { get; set; } = "";
        public string Modify { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>
    /// Parses the device→host configJson state JSON. Handles BOTH firmware
    /// schemas so plugin versions work across a wheel firmware rollout:
    ///
    ///   2025-11: enableManager.dashboards[] + configJsonList + displayVersion
    ///   2026-04: enabledManager.updateDashboards[] + imagePath (top-level)
    /// </summary>
    public static class WheelStateParser
    {
        /// <summary>Top-level field names the 2025-11 firmware emits. If any
        /// are missing from a parsed blob, logged as a drift warning so
        /// firmware-schema changes get flagged early.</summary>
        public static readonly string[] ExpectedTopLevelFields2025_11 = new[]
        {
            "TitleId", "configJsonList", "disableManager", "displayVersion",
            "enableManager", "fontRefMap", "imagePath", "imageRefMap",
            "resetVersion", "rootDirPath", "sortTag",
        };

        /// <summary>
        /// Parse a decompressed configJson state blob.
        /// </summary>
        /// <param name="jsonBytes">Decompressed JSON bytes.</param>
        /// <param name="missingFields">Top-level fields from
        /// <see cref="ExpectedTopLevelFields2025_11"/> that were absent —
        /// useful for diagnostic logging.</param>
        public static WheelDashboardState? Parse(byte[] jsonBytes, out IReadOnlyList<string> missingFields)
        {
            missingFields = Array.Empty<string>();
            try
            {
                string text = Encoding.UTF8.GetString(jsonBytes);
                var root = JObject.Parse(text);

                // Reject RPC replies. Session 0x09 carries both device→host
                // state pushes AND device→host RPC replies (e.g. the wheel's
                // ack of our configJson() reply: `{"configJson()":"","id":11}`).
                // RPC replies parse as a "valid" empty WheelDashboardState
                // and overwrite the real state. Heuristic: any top-level key
                // matching `<name>()` is an RPC reply, not a state blob.
                foreach (var prop in root.Properties())
                {
                    if (prop.Name.EndsWith("()", StringComparison.Ordinal))
                        return null;
                }

                // Collect missing expected fields for drift diagnostics. 2026-04
                // firmware uses different keys (enabledManager vs enableManager
                // etc.); don't report those as missing if a legacy name exists.
                var missing = new List<string>();
                foreach (string field in ExpectedTopLevelFields2025_11)
                {
                    if (root[field] != null) continue;
                    if (field == "enableManager" && root["enabledManager"] != null) continue;
                    if (field == "disableManager" && root["disabledManager"] != null) continue;
                    missing.Add(field);
                }
                missingFields = missing;

                var state = new WheelDashboardState
                {
                    TitleId = root.Value<int?>("TitleId") ?? 0,
                    DisplayVersion = root.Value<int?>("displayVersion") ?? 0,
                    ResetVersion = root.Value<int?>("resetVersion") ?? 0,
                    SortTag = root.Value<int?>("sortTag") ?? 0,
                    RootDirPath = root.Value<string>("rootDirPath") ?? "",
                };
                if (root["configJsonList"] is JArray cjl)
                {
                    var list = new List<string>();
                    foreach (var item in cjl) list.Add(item.Value<string>() ?? "");
                    state.ConfigJsonList = list;
                }
                state.EnabledDashboards = ReadDashboards(root, "enableManager", "enabledManager");
                state.DisabledDashboards = ReadDashboards(root, "disableManager", "disabledManager");
                state.RootPath = ReadRootPath(root) ?? "";
                state.ImageRefMap = ReadIntMap(root["imageRefMap"] as JObject);
                state.FontRefMap = ReadIntMap(root["fontRefMap"] as JObject);
                state.ImagePath = ReadImagePath(root["imagePath"] as JArray);
                return state;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Overload without missing-fields output, kept for existing callers.</summary>
        public static WheelDashboardState? Parse(byte[] jsonBytes) => Parse(jsonBytes, out _);

        private static IReadOnlyList<WheelDashboardEntry> ReadDashboards(
            JObject root, string newKey, string oldKey)
        {
            JToken? mgr = root[newKey] ?? root[oldKey];
            if (!(mgr is JObject mgrObj)) return Array.Empty<WheelDashboardEntry>();
            JToken? arr = mgrObj["dashboards"] ?? mgrObj["updateDashboards"];
            if (!(arr is JArray jarr)) return Array.Empty<WheelDashboardEntry>();
            var items = new List<WheelDashboardEntry>();
            foreach (var d in jarr)
            {
                if (!(d is JObject o)) continue;
                items.Add(new WheelDashboardEntry
                {
                    Title = o.Value<string>("title") ?? "",
                    DirName = o.Value<string>("dirName") ?? "",
                    Hash = o.Value<string>("hash") ?? "",
                    Id = o.Value<string>("id") ?? "",
                    CreateTime = o.Value<string>("createTime") ?? "",
                    LastModified = o.Value<string>("lastModified") ?? "",
                    PreviewImageFilePaths = ReadStringArray(o["previewImageFilePaths"] as JArray),
                    ResourceImageFilePaths = ReadStringArray(o["resouceImageFilePaths"] as JArray),
                    IdealDeviceInfos = ReadDeviceInfos(o["idealDeviceInfos"] as JArray),
                });
            }
            return items;
        }

        private static string? ReadRootPath(JObject root)
        {
            foreach (var key in new[] { "enableManager", "disableManager", "enabledManager", "disabledManager" })
            {
                if (root[key] is JObject mgr && mgr.Value<string>("rootPath") is string rp)
                    return rp;
            }
            return null;
        }

        private static IReadOnlyList<string> ReadStringArray(JArray? arr)
        {
            if (arr == null) return Array.Empty<string>();
            var list = new List<string>(arr.Count);
            foreach (var t in arr) list.Add(t.Value<string>() ?? "");
            return list;
        }

        private static IReadOnlyList<WheelDashboardDeviceInfo> ReadDeviceInfos(JArray? arr)
        {
            if (arr == null) return Array.Empty<WheelDashboardDeviceInfo>();
            var list = new List<WheelDashboardDeviceInfo>(arr.Count);
            foreach (var t in arr)
            {
                if (!(t is JObject o)) continue;
                list.Add(new WheelDashboardDeviceInfo
                {
                    DeviceId = o.Value<int?>("deviceId") ?? 0,
                    HardwareVersion = o.Value<string>("hardwareVersion") ?? "",
                    NetworkId = o.Value<int?>("networkId") ?? 0,
                    ProductType = o.Value<string>("productType") ?? "",
                });
            }
            return list;
        }

        private static IReadOnlyDictionary<string, int> ReadIntMap(JObject? obj)
        {
            if (obj == null) return new Dictionary<string, int>();
            var dict = new Dictionary<string, int>(obj.Count);
            foreach (var prop in obj.Properties())
                dict[prop.Name] = prop.Value.Value<int?>() ?? 0;
            return dict;
        }

        private static IReadOnlyList<WheelImagePathEntry> ReadImagePath(JArray? arr)
        {
            if (arr == null) return Array.Empty<WheelImagePathEntry>();
            var list = new List<WheelImagePathEntry>(arr.Count);
            foreach (var t in arr)
            {
                if (!(t is JObject o)) continue;
                list.Add(new WheelImagePathEntry
                {
                    Md5 = o.Value<string>("md5") ?? "",
                    Modify = o.Value<string>("modify") ?? "",
                    Url = o.Value<string>("url") ?? "",
                });
            }
            return list;
        }
    }
}
