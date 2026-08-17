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

        /// <summary>How a manager's dashboard list arrived in this blob. The
        /// wheel self-describes: <c>dashboards</c> = full authoritative list
        /// (2025-11 schema / TitleId=1 pushes), <c>updateDashboards</c> = delta
        /// upserts (2026-04 TitleId=4 pushes), absent manager = no change.</summary>
        public enum ManagerListKind { Absent, Full, Delta }

        public ManagerListKind EnabledListKind { get; set; } = ManagerListKind.Absent;
        public ManagerListKind DisabledListKind { get; set; } = ManagerListKind.Absent;
        /// <summary>dirNames whose entries the wheel CONFIRMED deleted (left
        /// both managers via delta pushes) since the last full push. The
        /// wheel's live slot table keeps them as dead slots until a
        /// port-level reconnect compacts the table (order-preserving) — the
        /// host compacts its cached copy at that same moment
        /// (<c>ConfigJsonClient.CompactConfirmedDeletes</c>).</summary>
        public IReadOnlyList<string> ConfirmedRemovedNames { get; set; } = Array.Empty<string>();
        /// <summary>Ids from <c>enabledManager.deletedDashboards</c> — entries the
        /// wheel removed entirely.</summary>
        public IReadOnlyList<string> EnabledDeletedIds { get; set; } = Array.Empty<string>();
        /// <summary>Ids from <c>disabledManager.deletedDashboards</c>.</summary>
        public IReadOnlyList<string> DisabledDeletedIds { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Merge a freshly-parsed state push into the previously-known state.
        /// The wheel's TitleId=4 pushes are DELTAS (<c>updateDashboards</c> +
        /// <c>deletedDashboards</c>); treating each blob as the full state
        /// collapsed the cached inventory to just the last delta (post-upload
        /// symptom: a single "disabled" row). Full lists (<c>dashboards</c>
        /// key) replace their manager; deltas upsert; deletions remove from
        /// both managers; an entry upserted into one manager leaves the other.
        /// </summary>
        public static WheelDashboardState Merge(WheelDashboardState? prev, WheelDashboardState next)
        {
            if (prev == null) return next;

            static string KeyOf(WheelDashboardEntry e)
                => !string.IsNullOrEmpty(e.Id) ? e.Id
                 : !string.IsNullOrEmpty(e.DirName) ? "dir:" + e.DirName
                 : "title:" + e.Title;

            // Ordered dictionaries: preserve prior ordering, append new entries.
            var enabled = new List<WheelDashboardEntry>(prev.EnabledDashboards);
            var disabled = new List<WheelDashboardEntry>(prev.DisabledDashboards);

            static void Upsert(List<WheelDashboardEntry> list, WheelDashboardEntry e, Func<WheelDashboardEntry, string> key)
            {
                string k = key(e);
                for (int i = 0; i < list.Count; i++)
                    if (key(list[i]) == k) { list[i] = e; return; }
                list.Add(e);
            }

            static void RemoveKey(List<WheelDashboardEntry> list, string k, Func<WheelDashboardEntry, string> key)
                => list.RemoveAll(x => key(x) == k);

            if (next.EnabledListKind == ManagerListKind.Full)
                enabled = new List<WheelDashboardEntry>(next.EnabledDashboards);
            if (next.DisabledListKind == ManagerListKind.Full)
                disabled = new List<WheelDashboardEntry>(next.DisabledDashboards);

            if (next.EnabledListKind == ManagerListKind.Delta)
                foreach (var e in next.EnabledDashboards) Upsert(enabled, e, KeyOf);
            if (next.DisabledListKind == ManagerListKind.Delta)
                foreach (var e in next.DisabledDashboards) Upsert(disabled, e, KeyOf);

            // Cross-manager consistency: whatever manager NEXT placed an entry
            // in wins; drop the same key from the other side.
            if (next.EnabledListKind != ManagerListKind.Absent)
                foreach (var e in next.EnabledDashboards) RemoveKey(disabled, KeyOf(e), KeyOf);
            if (next.DisabledListKind != ManagerListKind.Absent)
                foreach (var e in next.DisabledDashboards) RemoveKey(enabled, KeyOf(e), KeyOf);

            // Explicit deletions remove the entry everywhere.
            foreach (var id in next.EnabledDeletedIds)
            {
                enabled.RemoveAll(x => x.Id == id);
                disabled.RemoveAll(x => x.Id == id);
            }
            foreach (var id in next.DisabledDeletedIds)
            {
                enabled.RemoveAll(x => x.Id == id);
                disabled.RemoveAll(x => x.Id == id);
            }

            // Slot-table maintenance. Full pushes (connect-time TitleId=1)
            // carry the authoritative ordinal-sorted configJsonList. Delta
            // pushes don't, and the wheel never re-pushes the table
            // mid-session — so mirror its live allocator from the
            // wheel-CONFIRMED deltas only (host intent desyncs the table when
            // an operation silently fails to take effect — confirm deltas can
            // be lost on the un-acked config session).
            IReadOnlyList<string> slotList;
            IReadOnlyList<string> confirmedRemoved;
            if (next.ConfigJsonList.Count > 0)
            {
                slotList = next.ConfigJsonList;
                // Full push = fresh table; prior deletions are baked in.
                confirmedRemoved = Array.Empty<string>();
            }
            else
            {
                // The wheel's live slot table is ordinal-sorted over its
                // registry entries INCLUDING dead ones: an accepted delete
                // keeps the entry as a dead slot (its own cycling shows 'dash
                // load error' there) and a (re-)enabled name takes its ordinal
                // position — behavior-verified 2026-08-16: a re-uploaded dash
                // re-occupied its dead hole ahead of "Grids" rather than
                // appending. So: never remove, insert new enabled names at
                // their ordinal position.
                var table = new List<string>(prev.ConfigJsonList);
                foreach (var e in enabled)
                {
                    if (string.IsNullOrEmpty(e.DirName)) continue;
                    if (table.FindIndex(n => string.Equals(n, e.DirName, StringComparison.Ordinal)) >= 0)
                        continue;
                    int at = 0;
                    while (at < table.Count && string.CompareOrdinal(table[at], e.DirName) < 0) at++;
                    table.Insert(at, e.DirName);
                }
                slotList = table;

                // Accumulate wheel-confirmed deletions: names present in the
                // managers before this merge and gone after (their entries
                // were removed via deletedDashboards ids).
                var removed = new List<string>(prev.ConfirmedRemovedNames);
                var survivorNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in enabled)
                    if (!string.IsNullOrEmpty(e.DirName)) survivorNames.Add(e.DirName);
                foreach (var e in disabled)
                    if (!string.IsNullOrEmpty(e.DirName)) survivorNames.Add(e.DirName);
                foreach (var e in prev.EnabledDashboards)
                    if (!string.IsNullOrEmpty(e.DirName) && !survivorNames.Contains(e.DirName)
                        && !removed.Contains(e.DirName))
                        removed.Add(e.DirName);
                foreach (var e in prev.DisabledDashboards)
                    if (!string.IsNullOrEmpty(e.DirName) && !survivorNames.Contains(e.DirName)
                        && !removed.Contains(e.DirName))
                        removed.Add(e.DirName);
                confirmedRemoved = removed;
            }

            return new WheelDashboardState
            {
                TitleId = next.TitleId,
                DisplayVersion = next.DisplayVersion != 0 ? next.DisplayVersion : prev.DisplayVersion,
                ResetVersion = next.ResetVersion != 0 ? next.ResetVersion : prev.ResetVersion,
                SortTag = next.SortTag,
                RootDirPath = !string.IsNullOrEmpty(next.RootDirPath) ? next.RootDirPath : prev.RootDirPath,
                ConfigJsonList = slotList,
                EnabledDashboards = enabled,
                DisabledDashboards = disabled,
                ImageRefMap = next.ImageRefMap.Count > 0 ? next.ImageRefMap : prev.ImageRefMap,
                ImagePath = next.ImagePath.Count > 0 ? next.ImagePath : prev.ImagePath,
                FontRefMap = next.FontRefMap.Count > 0 ? next.FontRefMap : prev.FontRefMap,
                RootPath = !string.IsNullOrEmpty(next.RootPath) ? next.RootPath : prev.RootPath,
                CapturedAt = next.CapturedAt,
                EnabledListKind = next.EnabledListKind,
                DisabledListKind = next.DisabledListKind,
                EnabledDeletedIds = next.EnabledDeletedIds,
                DisabledDeletedIds = next.DisabledDeletedIds,
                ConfirmedRemovedNames = confirmedRemoved,
            };
        }
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
            // Cheap structural pre-check: skip JObject.Parse entirely when
            // the buffer is obviously not a complete root JSON object yet.
            // ConfigJsonClient.OnChunk calls us after every wire chunk —
            // _deviceInbox.TryDecompress returns whatever DeflateStream has
            // inflated so far, which is partial JSON (truncated mid-string)
            // until the final chunk lands. Without this check JObject.Parse
            // throws JsonReaderException(Unterminated string) once per
            // chunk, the catch below absorbs it, but SimHub's
            // AppDomain.FirstChanceException listener still logs every
            // throw — spamming SimHub.txt during every configJson reload.
            // The try/catch below stays as the backstop for edge cases
            // where this heuristic passes but the inner JSON is malformed.
            if (!LooksStructurallyComplete(jsonBytes)) return null;
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
                state.EnabledDashboards = ReadDashboards(root, "enableManager", "enabledManager",
                    out var enabledKind, out var enabledDeleted);
                state.EnabledListKind = enabledKind;
                state.EnabledDeletedIds = enabledDeleted;
                state.DisabledDashboards = ReadDashboards(root, "disableManager", "disabledManager",
                    out var disabledKind, out var disabledDeleted);
                state.DisabledListKind = disabledKind;
                state.DisabledDeletedIds = disabledDeleted;
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

        /// <summary>
        /// One-pass byte scan that returns true only when <paramref name="jsonBytes"/>
        /// holds a balanced root JSON object: starts with '{', ends with the
        /// matching '}' at depth 0, and is not mid-string at end. Pure byte
        /// scan — safe on UTF-8 because the structural characters '{', '}',
        /// '"', '\\' are all ASCII (&lt; 0x80) and UTF-8 continuation bytes
        /// never collide with ASCII codepoints. Used as a guard before
        /// JObject.Parse to avoid throwing JsonReaderException on partial
        /// chunk-assembly buffers.
        /// </summary>
        private static bool LooksStructurallyComplete(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            int i = 0;
            // Skip leading whitespace and NUL padding so a buffer that starts
            // with stray nulls still resolves to its real first token.
            while (i < bytes.Length && IsSkippableByte(bytes[i])) i++;
            if (i >= bytes.Length || bytes[i] != (byte)'{') return false;

            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (escape) { escape = false; continue; }
                if (inString)
                {
                    if (b == (byte)'\\') { escape = true; continue; }
                    if (b == (byte)'"') inString = false;
                    continue;
                }
                switch (b)
                {
                    case (byte)'"':
                        inString = true;
                        break;
                    case (byte)'{':
                        depth++;
                        break;
                    case (byte)'}':
                        depth--;
                        if (depth < 0) return false;
                        if (depth == 0)
                        {
                            // Root closed — any remaining bytes must be
                            // skippable (trailing whitespace or NUL padding).
                            for (int j = i + 1; j < bytes.Length; j++)
                                if (!IsSkippableByte(bytes[j])) return false;
                            return true;
                        }
                        break;
                }
            }
            // Reached end of buffer without root closing → incomplete.
            return false;
        }

        private static bool IsSkippableByte(byte b)
            => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' || b == 0;

        private static IReadOnlyList<WheelDashboardEntry> ReadDashboards(
            JObject root, string newKey, string oldKey,
            out WheelDashboardState.ManagerListKind kind,
            out IReadOnlyList<string> deletedIds)
        {
            kind = WheelDashboardState.ManagerListKind.Absent;
            deletedIds = Array.Empty<string>();
            JToken? mgr = root[newKey] ?? root[oldKey];
            if (!(mgr is JObject mgrObj)) return Array.Empty<WheelDashboardEntry>();
            if (mgrObj["deletedDashboards"] is JArray del && del.Count > 0)
            {
                var ids = new List<string>(del.Count);
                foreach (var t in del)
                {
                    string? s = t.Value<string>();
                    if (!string.IsNullOrEmpty(s)) ids.Add(s!);
                }
                deletedIds = ids;
                // Deletions-only manager is still a delta push.
                kind = WheelDashboardState.ManagerListKind.Delta;
            }
            JToken? arr = mgrObj["dashboards"];
            if (arr is JArray)
                kind = WheelDashboardState.ManagerListKind.Full;
            else
            {
                arr = mgrObj["updateDashboards"];
                if (arr is JArray)
                    kind = WheelDashboardState.ManagerListKind.Delta;
            }
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
