using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MozaPlugin.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>One display's dashboard canvas.</summary>
    public readonly struct DisplayCanvas
    {
        public int Width { get; }
        public int Height { get; }
        /// <summary>MOZA's own shape hint — <c>Rectangle</c>, <c>Round</c>,
        /// <c>CircularTrapezoid</c>. Informational: the fitter works on the bounding box
        /// either way, but a round display crops the corners of what it is given.</summary>
        public string Shape { get; }
        /// <summary>Human name for the report, e.g. "CS Pro".</summary>
        public string Note { get; }

        public DisplayCanvas(int width, int height, string shape, string note)
        {
            Width = width;
            Height = height;
            Shape = shape ?? "Rectangle";
            Note = note ?? "";
        }

        public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;
    }

    /// <summary>
    /// Canvas geometry per MOZA display, from <c>Data/DjsonDisplayMap.json</c>.
    ///
    /// <para>Keyed on the <c>productType</c> the wheel reports in its own configJson
    /// (<c>window.idealDeviceInfos[].productType</c>), which the plugin already parses
    /// into <c>WheelDashboardDeviceInfo</c>. Sizes vary far more than the 780x248 the
    /// wheel displays use — CM2 is 1280x720, which happens to be exactly what a typical
    /// SimHub dashboard is authored at.</para>
    /// </summary>
    public static class DisplayCanvasMap
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, DisplayCanvas>? s_displays;
        private static DisplayCanvas s_default =
            new DisplayCanvas(CanvasFitter.WheelWidth, CanvasFitter.WheelHeight, "Rectangle", "");

        /// <summary>The canvas for a reported <c>productType</c>, or the default
        /// (780x248) when it isn't a display this table knows.</summary>
        public static DisplayCanvas Resolve(string? productType, out bool known)
        {
            EnsureLoaded();
            known = false;
            if (string.IsNullOrWhiteSpace(productType)) return s_default;

            lock (Gate)
            {
                if (s_displays != null && s_displays.TryGetValue(productType!.Trim(), out var hit))
                {
                    known = true;
                    return hit;
                }
            }
            return s_default;
        }

        /// <summary>Every display in the table, for diagnostics.</summary>
        public static IReadOnlyDictionary<string, DisplayCanvas> All()
        {
            EnsureLoaded();
            lock (Gate)
                return s_displays ?? new Dictionary<string, DisplayCanvas>();
        }

        private static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (s_displays != null) return;
                s_displays = new Dictionary<string, DisplayCanvas>(StringComparer.OrdinalIgnoreCase);

                var root = LoadEmbedded("MozaPlugin.Data.DjsonDisplayMap.json");
                if (root == null) return;

                if (root["default"] is JObject dflt)
                {
                    int w = (int?)dflt["width"] ?? CanvasFitter.WheelWidth;
                    int h = (int?)dflt["height"] ?? CanvasFitter.WheelHeight;
                    if (w > 0 && h > 0) s_default = new DisplayCanvas(w, h, "Rectangle", "");
                }

                if (root["displays"] is not JObject displays) return;
                foreach (var prop in displays.Properties())
                {
                    if (prop.Value is not JObject entry) continue;
                    int w = (int?)entry["width"] ?? 0;
                    int h = (int?)entry["height"] ?? 0;
                    if (w <= 0 || h <= 0)
                    {
                        MozaLog.Warn($"[AZOM] DjsonDisplayMap: '{prop.Name}' has no usable size — ignored");
                        continue;
                    }
                    s_displays[prop.Name] = new DisplayCanvas(
                        w, h, (string?)entry["shape"] ?? "Rectangle", (string?)entry["note"] ?? "");
                }
            }
        }

        private static JObject? LoadEmbedded(string logicalName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(logicalName);
                if (stream == null)
                {
                    MozaLog.Warn($"[AZOM] DjsonImport: embedded resource '{logicalName}' not found");
                    return null;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return JObject.Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] DjsonImport: failed to load '{logicalName}': {ex.Message}");
                return null;
            }
        }
    }
}
