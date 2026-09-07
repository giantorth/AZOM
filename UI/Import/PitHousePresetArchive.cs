using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Loads the preset JSON document from either a legacy raw-JSON preset file
    /// or the ZIP-wrapped <c>.mzpreset</c> container PitHouse 1.4+ writes (a ZIP
    /// holding <c>preset.json</c> + <c>metadata.json</c>). Detection is by
    /// content (ZIP local-file-header magic), not extension — the exported
    /// samples show the extension is unreliable (a <c>.mzpreset</c> may be raw
    /// JSON, and a zipped preset can arrive as <c>.mzpreset.zip</c>). The inner
    /// <c>preset.json</c> is byte-for-byte the same schema as the legacy file,
    /// so both read paths funnel through here and everything downstream stays
    /// format-agnostic.
    /// </summary>
    public static class PitHousePresetArchive
    {
        // Entry PitHouse stores the profile payload under inside the container.
        private const string PresetEntryName = "preset.json";

        /// <summary>
        /// Read + parse the preset document at <paramref name="path"/>, unwrapping
        /// the ZIP container when present. Returns the parsed root on success, or
        /// <c>(null, message)</c> on failure — the message is raw error text the
        /// caller formats into <c>Strings.Import_Error_InvalidJson</c>.
        /// </summary>
        public static (JObject? root, string error) LoadRoot(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return (null, path ?? "");

            try
            {
                string? text = IsZip(path) ? ReadPresetEntry(path) : File.ReadAllText(path);
                if (text == null)
                    return (null, $"'{PresetEntryName}' not found in archive");
                return (JObject.Parse(text), "");
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        /// <summary>
        /// True when the file begins with the ZIP local-file-header magic
        /// (<c>PK\x03\x04</c>).
        /// </summary>
        public static bool IsZip(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    return fs.ReadByte() == 0x50 && fs.ReadByte() == 0x4B
                        && fs.ReadByte() == 0x03 && fs.ReadByte() == 0x04;
                }
            }
            catch
            {
                return false;
            }
        }

        // Extract the preset.json text from the container. Returns null when the
        // entry is absent so the caller can surface a clear error.
        private static string? ReadPresetEntry(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = zip.GetEntry(PresetEntryName)
                    ?? zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, PresetEntryName, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                using (var s = entry.Open())
                using (var reader = new StreamReader(s))
                {
                    // User-chosen file, but the zip entry declares its own size —
                    // bound the inflate so a hostile preset can't exhaust the x86 heap.
                    var sb = new System.Text.StringBuilder();
                    var buf = new char[8192];
                    int n;
                    while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (sb.Length + n > MaxPresetChars)
                            throw new InvalidDataException($"{PresetEntryName} exceeds {MaxPresetChars / (1024 * 1024)} MB");
                        sb.Append(buf, 0, n);
                    }
                    return sb.ToString();
                }
            }
        }

        private const int MaxPresetChars = 16 * 1024 * 1024;
    }
}
