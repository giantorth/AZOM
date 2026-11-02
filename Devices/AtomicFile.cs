using System.IO;
using System.Text;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Whole-file writes staged to a sibling <c>.tmp</c> and swapped into place, so a
    /// crash mid-write never leaves the target truncated or missing.
    /// </summary>
    internal static class AtomicFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteAllText(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            Commit(tmp, path);
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            Commit(tmp, path);
        }

        private static void Commit(string tmp, string path)
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
