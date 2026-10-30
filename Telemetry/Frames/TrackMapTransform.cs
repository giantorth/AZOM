using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Per-track world→field transform for the track-map <c>patch/Location*</c>
    /// (location_t) channels.
    ///
    /// Reverse-engineered from PitHouse captures on Imola, Spa and Zandvoort
    /// (player slot of the low-rate 48-byte track-map tier, fit corr ~1.0). The
    /// key finding: PitHouse sends MAP-PIXEL coordinates, not bounding-box-
    /// normalised values — so the scale is keyed to the map's pixel scale
    /// (<c>map.ini SCALE_FACTOR</c>), NOT the track extent:
    /// <code>
    ///   scale_X = PixelK / SCALE_FACTOR     (PixelK ≈ 669; scale_X·SCALE was
    ///                                         672/661/673 across the 3 tracks)
    ///   scale_Z = 16 · scale_X              (Z carries 16× the X resolution)
    ///   field   = scale · world + CENTER    (CENTER ~universal: measured X-center
    ///                                         4,721,866 / 4,716,730 on Imola/Spa)
    /// </code>
    /// The Y (elevation) field is 16-bit and secondary to the 2-D map.
    /// Games without a map.ini use <see cref="FromBounds"/> (SimHub's recorded map), else <see cref="Fallback"/>.
    /// </summary>
    internal sealed class TrackMapTransform
    {
        public float ScaleX { get; private set; }
        public float ScaleZ { get; private set; }
        public float ScaleY { get; private set; }
        public int CenterX { get; private set; }
        public int CenterZ { get; private set; }
        public int CenterY { get; private set; }
        public string Source { get; private set; } = "fallback";

        // PitHouse map constants (calibrated on Imola/Spa/Zandvoort, corr ~1.0).
        // X is the map-pixel coordinate (scale = PixelK / SCALE_FACTOR). Z is
        // normalised so the track's LARGER map dimension fills the 24-bit field:
        //   scale_Z = 2^24 / (SCALE_FACTOR · max(WIDTH, HEIGHT))
        private const double PixelK = 669.0;       // field counts per map pixel (X)
        private const double FieldFull = 16777216.0; // 2^24, the Z field span
        private const double ZRatio = 16.0;        // fallback Z/X when dims unknown
        // X centre scales with the map's larger dimension (fits all 3 tracks:
        // (center_X − 4,500,000)·max(W,H) ≈ 343M — Imola 342.5M, Spa 345.4M,
        // Zandvoort 340.3M). Same max(W,H) that drives the Z scale.
        private const double CenterXBase = 4360000.0;
        private const double CenterXK = 558200000.0; // center_X = Base + K/max(W,H)
        private const int CenterXConst = 4719000;  // fallback X center (no dims)
        private const int CenterZConst = 8300000;  // field at world_z = 0
        private const int CenterYConst = 32768;    // mid 16-bit; elevation secondary

        public static TrackMapTransform Fallback() => FromMapIni(1.2, 0.0, 0.0, "fallback(SCALE=1.2)");

        // The wheel normalises X by its low 20 bits (centre 0x480000, PitHouse's top
        // nibble is always 4) and Z by the full 24 bits, so one scale S puts both
        // axes on the same footing: X = 0x480000 + S·dx, Z = 2^23 + 16·S·dz. PitHouse
        // spans the outline's larger dimension E with 10^6 counts: S·E = 999,900 /
        // 999,875 / 1,000,246 on Imola / Spa / Zandvoort (E from the .mztrack bounds).
        private const double TrackCounts = 1000000.0;
        private const double FieldY = 65536.0;     // 2^16, Y field span
        private const double FillY = 0.9;
        private const int BoundsCenterX = 0x480000;
        private const int BoundsCenterZ = 0x800000;

        /// <summary>Fit a track bounding box (the wheel outline's, else SimHub's recorded
        /// map) into the wheel's window, centred. For tracks without an AC map.ini.
        /// <paramref name="invertZ"/>: SimHub's lap-relative (iRacing) maps run Z
        /// opposite to the outline.</summary>
        public static TrackMapTransform FromBounds(double minX, double maxX, double minY, double maxY,
            double minZ, double maxZ, bool invertZ, string source)
        {
            double ext = Math.Max(maxX - minX, maxZ - minZ);
            double s = TrackCounts / ext;
            double sz = invertZ ? -16.0 * s : 16.0 * s;
            // Elevation shares S unless the track's height range overflows the 16-bit
            // field (Bathurst: 191 m × 442/m), which would clamp the low end to 0.
            double sy = maxY - minY > 0.0 ? Math.Min(s, FillY * FieldY / (maxY - minY)) : s;
            return new TrackMapTransform
            {
                ScaleX = (float)s,
                ScaleZ = (float)sz,
                ScaleY = (float)sy,
                CenterX = (int)Math.Round(BoundsCenterX - s * (minX + maxX) / 2.0),
                CenterZ = (int)Math.Round(BoundsCenterZ - sz * (minZ + maxZ) / 2.0),
                CenterY = (int)Math.Round(CenterYConst - sy * (minY + maxY) / 2.0),
                Source = $"{source}:{(int)(maxX - minX)}x{(int)(maxZ - minZ)}m{(invertZ ? ",-Z" : "")}",
            };
        }

        private static TrackMapTransform FromMapIni(double scaleFactor, double width, double height, string src)
        {
            double sx = PixelK / scaleFactor;
            double maxDim = Math.Max(width, height);
            double sz = maxDim > 1.0 ? FieldFull / (scaleFactor * maxDim) : ZRatio * sx;
            int cx = maxDim > 1.0
                ? (int)Math.Round(CenterXBase + CenterXK / maxDim)
                : CenterXConst;
            return new TrackMapTransform
            {
                ScaleX = (float)sx,
                ScaleZ = (float)sz,
                ScaleY = (float)sx,
                CenterX = cx,
                CenterZ = CenterZConst,
                CenterY = CenterYConst,
                Source = src,
            };
        }

        // Per-track result of ResolveMapIniUncached, null included. Every track-map
        // builder resolves on its first frame and on track change, so without this
        // each one re-reads map.ini on the tick thread. Transforms are immutable.
        private static readonly ConcurrentDictionary<string, TrackMapTransform?> s_mapIniByTrack =
            new ConcurrentDictionary<string, TrackMapTransform?>(StringComparer.Ordinal);

        /// <summary>Resolve the transform for an AC track folder (e.g. "ks_zandvoort")
        /// from its map.ini; null when there is none (other games).</summary>
        public static TrackMapTransform? ResolveMapIni(string? trackFolder)
        {
            if (string.IsNullOrEmpty(trackFolder)) return null;
            return s_mapIniByTrack.GetOrAdd(trackFolder!, ResolveMapIniUncached);
        }

        private static TrackMapTransform? ResolveMapIniUncached(string trackFolder)
        {
            // TEST (temporary): exact PitHouse-fitted transform for ks_zandvoort
            // (R^2 ~1.0 against the radar6 capture). Confirms whether the radar's
            // static is the Location transform before generalising the formula.
            if (trackFolder == "ks_zandvoort")
            {
                MozaLog.Info("[AZOM] track transform 'ks_zandvoort': using PitHouse-fitted test values");
                return new TrackMapTransform
                {
                    ScaleX = 1201.95f, ScaleZ = 19220.4f, ScaleY = 1204.35f,
                    CenterX = 4884363, CenterZ = 8540005, CenterY = 34180,
                    Source = "pithouse-fit:ks_zandvoort",
                };
            }
            try
            {
                string? ini = FindMapIni(trackFolder);
                if (ini == null)
                {
                    MozaLog.Info($"[AZOM] track transform: no map.ini for '{trackFolder}'");
                    return null;
                }
                double? scale = null, width = null, height = null;
                foreach (var line in File.ReadAllLines(ini))
                {
                    var m = Regex.Match(line, @"^\s*([A-Za-z_]+)\s*=\s*([-0-9.]+)");
                    if (!m.Success) continue;
                    if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double v)) continue;
                    switch (m.Groups[1].Value.ToUpperInvariant())
                    {
                        case "SCALE_FACTOR": scale = v; break;
                        case "WIDTH": width = v; break;
                        case "HEIGHT": height = v; break;
                    }
                }
                if (scale.HasValue && scale.Value > 0.0)
                {
                    var t = FromMapIni(scale.Value, width ?? 0.0, height ?? 0.0, $"map.ini:{trackFolder}");
                    MozaLog.Info($"[AZOM] track transform '{trackFolder}': SCALE_FACTOR={scale.Value} " +
                        $"WIDTH={width ?? 0} HEIGHT={height ?? 0} " +
                        $"-> X_scale={t.ScaleX:F1} X_center={t.CenterX} Z_scale={t.ScaleZ:F0} (from {ini})");
                    return t;
                }
                MozaLog.Info($"[AZOM] track transform: map.ini for '{trackFolder}' has no SCALE_FACTOR");
                return null;
            }
            catch (Exception e)
            {
                MozaLog.Info($"[AZOM] track transform resolve failed for '{trackFolder}': {e.Message}");
                return null;
            }
        }

        private static string? FindMapIni(string trackFolder)
        {
            string? content = FindAcContentPath();
            if (content == null) return null;
            foreach (var p in new[]
            {
                Path.Combine(content, "tracks", trackFolder, "data", "map.ini"),
                Path.Combine(content, "tracks", trackFolder, "map.ini"),
            })
                if (File.Exists(p)) return p;
            return null;
        }

        // Only a found path is cached; a miss is retried on the next track lookup.
        private static volatile string? _acContentCache;

        // Locate AC's content/ directory: a running AC process, else Steam libraries.
        private static string? FindAcContentPath()
        {
            var cached = _acContentCache;
            if (cached != null) return cached;
            foreach (var name in new[] { "acs", "AssettoCorsa" })
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }
                string? found = null;
                foreach (var proc in procs)
                {
                    // Each process gets its own try (MainModule throws across bitness)
                    // and is always disposed, including the ones after a match.
                    try
                    {
                        if (found != null) continue;
                        string? exe = proc.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exe)) continue;
                        string c = Path.Combine(Path.GetDirectoryName(exe)!, "content");
                        if (Directory.Exists(c)) found = c;
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
                if (found != null) return _acContentCache = found;
            }
            try
            {
                string? steam =
                    Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(steam))
                {
                    var libs = new System.Collections.Generic.List<string> { steam! };
                    string vdf = Path.Combine(steam!, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    foreach (var lib in libs)
                    {
                        string c = Path.Combine(lib, "steamapps", "common", "assettocorsa", "content");
                        if (Directory.Exists(c)) return _acContentCache = c;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
