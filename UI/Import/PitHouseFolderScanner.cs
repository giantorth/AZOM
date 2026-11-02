using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Discovers a PitHouse Presets folder on disk and lists its presets
    /// without fully parsing each one (we only need name + devices + id +
    /// path for the picker list). Parse errors on individual files are
    /// swallowed silently so one bad preset doesn't hide the rest.
    /// </summary>
    public static class PitHouseFolderScanner
    {
        /// <summary>
        /// Resolve the active presets root: explicit override (when valid) first,
        /// then the OS Documents/MOZA Pit House/Presets default. Returns null if
        /// neither exists.
        /// </summary>
        public static string? ResolvePresetsRoot(string? overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
                return overridePath;

            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(docs))
                {
                    var standard = Path.Combine(docs, "MOZA Pit House", "Presets");
                    if (Directory.Exists(standard)) return standard;
                }
            }
            catch
            {
                // Documents resolution can fail in restricted environments —
                // fall through to returning null so the dialog can fall back
                // to the Browse... path.
            }

            return null;
        }

        /// <summary>
        /// Subfolder for one PitHouse preset category. Names mirror the
        /// on-disk folder names PitHouse creates.
        /// </summary>
        public enum Category
        {
            Motor,
            Pedals,
        }

        public static string FolderNameFor(Category cat)
        {
            switch (cat)
            {
                case Category.Motor: return "Motor";
                case Category.Pedals: return "Pedals";
                default: return cat.ToString();
            }
        }

        /// <summary>
        /// Lightweight header for a discovered preset — enough to populate
        /// the picker list without holding the full <c>deviceParams</c> blob
        /// in memory for every file in the folder.
        /// </summary>
        public sealed class PresetHeader
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public List<string> Devices { get; set; } = new List<string>();

            public string DisplayLabel
            {
                get
                {
                    var devicesLabel = (Devices != null && Devices.Count > 0)
                        ? " (" + string.Join(", ", Devices) + ")"
                        : "";
                    return (string.IsNullOrEmpty(Name) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Name)
                           + devicesLabel;
                }
            }
        }

        /// <summary>
        /// Enumerate the preset files under <paramref name="presetsRoot"/>/<paramref name="cat"/>,
        /// reading just the header fields. Covers both legacy raw-JSON presets
        /// (<c>*.json</c>) and the ZIP-wrapped <c>*.mzpreset</c> container.
        /// Returns alphabetised by display label. Returns an empty list if the
        /// subfolder doesn't exist.
        /// </summary>
        public static List<PresetHeader> ListCategory(string presetsRoot, Category cat)
        {
            var result = new List<PresetHeader>();
            if (string.IsNullOrEmpty(presetsRoot) || !Directory.Exists(presetsRoot)) return result;

            var subPath = Path.Combine(presetsRoot, FolderNameFor(cat));
            if (!Directory.Exists(subPath)) return result;

            string[] files;
            try
            {
                files = Directory.GetFiles(subPath, "*.json", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(subPath, "*.mzpreset", SearchOption.TopDirectoryOnly))
                    .ToArray();
            }
            catch
            {
                return result;
            }

            foreach (var file in files)
            {
                var header = TryReadHeader(file);
                if (header != null) result.Add(header);
            }

            result.Sort((a, b) => string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        private static PresetHeader? TryReadHeader(string file)
        {
            try
            {
                // Only need top-level fields, but the presets are <100KB each,
                // so a full parse is simpler and reliable. LoadRoot unwraps the
                // .mzpreset ZIP container or reads legacy raw JSON transparently.
                var (root, _) = PitHousePresetArchive.LoadRoot(file);
                if (root == null) return null;
                // Malformed id/deviceType stays out of the picker.
                if (root["id"] is JContainer || root["deviceType"] is JContainer) return null;
                var header = new PresetHeader
                {
                    Path = file,
                    Name = (string?)root["name"] ?? "",
                };
                if (root["devices"] is JArray arr)
                {
                    foreach (var d in arr)
                    {
                        var s = (string?)d;
                        if (!string.IsNullOrEmpty(s)) header.Devices.Add(s!);
                    }
                }
                return header;
            }
            catch
            {
                return null;
            }
        }
    }
}
