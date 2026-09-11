using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MozaPlugin.UI.DjsonImport;
using Newtonsoft.Json.Linq;

// Drives the plugin's SimHub .djson -> MOZA .mzdash converter from the command line.
//
// The pipeline in UI/DjsonImport is linked into this project rather than duplicated, so
// what runs here is exactly what runs inside SimHub. Only the channel catalog differs:
// the plugin reads it through DashboardProfileStore, and this tool reads the same
// Data/Telemetry.json directly.
//
//   usage: djson-convert <input> <output-dir> [--telemetry <T.json>] [--display <t>] [--quiet]
//
// --display sizes the canvas for a specific MOZA screen by its reported productType
// ("W17 Display", "S09 Display", ...). Worth using: a CM2 is 1280x720, the same 16:9 most
// SimHub dashboards are authored at, where the 780x248 wheel screens can only fill ~57%.
//
// <input> is a .djson file or a directory searched recursively. Converting a whole
// directory is the corpus smoke test: it is what catches the Newtonsoft "$values" wrapper,
// the WhereEnumerableIterator $type leak, and missing .ressources archives, none of which
// show up on a single hand-picked dashboard.
//
// Exit code 0 when every file converted, 1 when any failed — so it works as a CI gate.

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: djson-convert <input.djson|dir> <output-dir> "
                + "[--telemetry <Telemetry.json>] [--display <productType>] [--quiet]");
            return 2;
        }

        string input = args[0];
        string outputRoot = args[1];
        string telemetryPath = Path.Combine(RepoRoot(), "Data", "Telemetry.json");
        bool quiet = false;
        string? display = null;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--telemetry" when i + 1 < args.Length: telemetryPath = args[++i]; break;
                case "--display" when i + 1 < args.Length: display = args[++i]; break;
                case "--quiet": quiet = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return 2;
            }
        }

        if (!File.Exists(telemetryPath))
        {
            Console.Error.WriteLine($"Telemetry.json not found at '{telemetryPath}' (use --telemetry)");
            return 2;
        }

        var catalog = ReadCatalog(telemetryPath);
        Console.WriteLine($"catalog: {catalog.Count} channels from {telemetryPath}");

        var converter = new DjsonConverter(new ChannelResolver(catalog));
        if (display != null && !converter.TargetDisplay(display))
        {
            Console.Error.WriteLine($"unknown display '{display}'; known: "
                + string.Join(", ", DisplayCanvasMap.All().Keys));
            return 2;
        }
        Console.WriteLine($"canvas : {converter.CanvasWidth}x{converter.CanvasHeight}"
            + (display == null ? " (default)" : $" for {display}"));

        var inputs = new List<string>();
        if (Directory.Exists(input))
            inputs.AddRange(Directory.GetFiles(input, "*.djson", SearchOption.AllDirectories).OrderBy(p => p));
        else
            inputs.Add(input);

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine($"no .djson files under '{input}'");
            return 2;
        }

        int ok = 0, failed = 0;
        int converted = 0, substituted = 0, dropped = 0;
        var unresolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fonts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var heavyLosses = new List<string>();

        foreach (var path in inputs)
        {
            var result = converter.Convert(path, outputRoot);
            string name = Path.GetFileName(path);

            if (!result.Ok)
            {
                failed++;
                Console.WriteLine($"FAIL  {name,-52} {result.Error}");
                continue;
            }

            ok++;
            var r = result.Report;
            converted += r.ConvertedCount;
            substituted += r.SubstitutedCount;
            dropped += r.DroppedCount;
            Merge(unresolved, r.UnresolvedProperties);
            Merge(fonts, r.UnmappedFonts);

            // The plan's rule: past a quarter of the widgets lost, the output is
            // misleading rather than useful, and the user should be told so.
            if (r.DropRatio > 0.25 && r.TotalCount > 0)
                heavyLosses.Add($"{name} ({r.DropRatio * 100:0}% dropped)");

            if (!quiet)
            {
                Console.WriteLine($"ok    {name,-52} {r.ConvertedCount,4}c "
                                + $"{r.SubstitutedCount,3}s {r.DroppedCount,3}d  "
                                + $"{r.Channels.Count,3}ch");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"files      : {ok} converted, {failed} failed, {inputs.Count} total");
        Console.WriteLine($"items      : {converted} converted, {substituted} substituted, {dropped} dropped");

        Report("Top unresolved properties", unresolved, 20);
        Report("Unmapped fonts", fonts, 20);

        if (heavyLosses.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Lost more than 25% of their widgets ({heavyLosses.Count}):");
            foreach (var h in heavyLosses.Take(25)) Console.WriteLine($"  {h}");
        }

        return failed == 0 ? 0 : 1;
    }

    private static void Merge(Dictionary<string, int> into, IReadOnlyDictionary<string, int> from)
    {
        foreach (var kv in from)
        {
            into.TryGetValue(kv.Key, out int n);
            into[kv.Key] = n + kv.Value;
        }
    }

    private static void Report(string title, Dictionary<string, int> map, int top)
    {
        if (map.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine($"{title} ({map.Count} distinct):");
        foreach (var kv in map.OrderByDescending(k => k.Value).Take(top))
            Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
    }

    /// <summary>Channel rows straight out of Telemetry.json's <c>sectors</c> array — the
    /// same two fields <c>DashboardProfileStore</c> would hand the resolver in-plugin.</summary>
    private static List<ChannelRow> ReadCatalog(string path)
    {
        var rows = new List<ChannelRow>();
        var root = JObject.Parse(File.ReadAllText(path));
        if (root["sectors"] is not JArray sectors) return rows;

        foreach (var s in sectors)
        {
            if (s is not JObject o) continue;
            string url = (string?)o["url"] ?? "";
            if (url.Length == 0) continue;
            rows.Add(new ChannelRow(url, (string?)o["simhub_property"] ?? ""));
        }
        return rows;
    }

    /// <summary>Walk up from the binary to the repo root, so the default Telemetry.json
    /// path works without arguments from any working directory.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MozaPlugin.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}

namespace MozaPlugin.Diagnostics
{
    /// <summary>Console stand-in for the plugin's logger. The linked pipeline sources log
    /// through <c>MozaLog</c>; in-plugin that is the real ring-buffer logger, here it is
    /// just stderr so warnings from the tables still surface.</summary>
    internal static class MozaLog
    {
        public static void Info(string message) { }
        public static void Debug(string message) { }
        public static void Warn(string message) => Console.Error.WriteLine("WARN  " + message);
        public static void Error(string message) => Console.Error.WriteLine("ERROR " + message);
    }
}
