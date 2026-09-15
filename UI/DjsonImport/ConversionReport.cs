using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>What happened to one source item.</summary>
    public enum ItemOutcome
    {
        /// <summary>Emitted with full fidelity.</summary>
        Converted,
        /// <summary>Emitted, but something was approximated or lost.</summary>
        Substituted,
        /// <summary>Not emitted.</summary>
        Dropped,
    }

    public sealed class ItemRecord
    {
        public string SourceType { get; set; } = "";
        public string Name { get; set; } = "";
        public ItemOutcome Outcome { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// The per-conversion ledger. Every approximation and every drop is recorded with a
    /// reason, so a converted dashboard is never quietly wrong — the dialog shows this
    /// before the user uploads anything.
    /// </summary>
    public sealed class ConversionReport
    {
        public string DashboardName { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public double SourceWidth { get; set; }
        public double SourceHeight { get; set; }
        public int CanvasWidth { get; set; }
        public int CanvasHeight { get; set; }
        public int ScreenCount { get; set; }
        /// <summary>Uniform scale applied to fit the wheel canvas, per screen.</summary>
        public List<double> ScreenFitScales { get; } = new List<double>();

        public List<ItemRecord> Items { get; } = new List<ItemRecord>();
        public List<string> Notes { get; } = new List<string>();

        /// <summary>SimHub property → how many bindings wanted it but could not get it.</summary>
        public Dictionary<string, int> UnresolvedProperties { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Source font family → how many text items used it with no mapping entry.</summary>
        public Dictionary<string, int> UnmappedFonts { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Channel URLs the converted dashboard reads.</summary>
        public SortedSet<string> Channels { get; } =
            new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Channels borrowed to carry a SimHub value that has none of its own.
        /// The host must publish these as per-dashboard channel mappings, or the widgets
        /// bound to them read nothing.</summary>
        public List<ChannelAllocation> ChannelOverrides { get; } = new List<ChannelAllocation>();

        public int ConvertedCount => Items.Count(i => i.Outcome == ItemOutcome.Converted);
        public int SubstitutedCount => Items.Count(i => i.Outcome == ItemOutcome.Substituted);
        public int DroppedCount => Items.Count(i => i.Outcome == ItemOutcome.Dropped);
        public int TotalCount => Items.Count;

        /// <summary>Share of source items that produced nothing. The plan's guidance is to
        /// treat a dashboard losing more than a quarter of its items as unconvertible
        /// rather than shipping a misleadingly clean result.</summary>
        public double DropRatio => TotalCount == 0 ? 0 : (double)DroppedCount / TotalCount;

        public void Record(string sourceType, string name, ItemOutcome outcome, string detail = "")
            => Items.Add(new ItemRecord
            {
                SourceType = sourceType,
                Name = name,
                Outcome = outcome,
                Detail = detail,
            });

        public void NoteUnresolvedProperty(string property)
        {
            if (string.IsNullOrWhiteSpace(property)) return;
            UnresolvedProperties.TryGetValue(property, out int n);
            UnresolvedProperties[property] = n + 1;
        }

        public void NoteUnmappedFont(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return;
            UnmappedFonts.TryGetValue(family, out int n);
            UnmappedFonts[family] = n + 1;
        }

        /// <summary>A one-line summary for the dialog header.</summary>
        public string Summary()
            => string.Format(CultureInfo.InvariantCulture,
                "{0} items: {1} converted, {2} substituted, {3} dropped",
                TotalCount, ConvertedCount, SubstitutedCount, DroppedCount);

        /// <summary>The full report, for the dialog body and the sidecar text file.</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;

            sb.AppendLine($"SimHub dashboard conversion — {DashboardName}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine($"source     : {SourcePath}");
            sb.AppendLine($"canvas     : {SourceWidth.ToString("0.#", inv)}x{SourceHeight.ToString("0.#", inv)}"
                        + $" -> {CanvasWidth}x{CanvasHeight}");
            if (ScreenFitScales.Count > 0)
            {
                sb.AppendLine($"screens    : {ScreenCount} (fit "
                            + string.Join(", ", ScreenFitScales.Select(s => s.ToString("0.###", inv)))
                            + ")");
            }
            sb.AppendLine($"items      : {Summary()}");
            sb.AppendLine($"channels   : {Channels.Count}"
                        + (ChannelOverrides.Count > 0
                            ? $" ({ChannelOverrides.Count} fed from SimHub)" : ""));
            sb.AppendLine();

            if (Channels.Count == 0 && TotalCount > 0)
            {
                sb.AppendLine("This dashboard reads NO telemetry — every widget renders its");
                sb.AppendLine("design-time value. See the dropped formulas below.");
                sb.AppendLine();
            }

            if (ChannelOverrides.Count > 0)
            {
                sb.AppendLine("Channels fed from SimHub");
                sb.AppendLine(new string('-', 60));
                sb.AppendLine("  These properties have no MOZA channel, so a spare channel is");
                sb.AppendLine("  borrowed and the plugin publishes SimHub's value on it.");
                foreach (var o in ChannelOverrides)
                    sb.AppendLine($"  {o.Url}  @{o.PackageLevel}ms  <-  {Truncate(o.Source)}");
                sb.AppendLine();
            }

            if (Notes.Count > 0)
            {
                // Collapse repeats: a dashboard built on a SimHub plugin can emit the same
                // note hundreds of times, burying the one-off notes that actually matter.
                sb.AppendLine("Notes");
                sb.AppendLine(new string('-', 60));
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (var n in Notes)
                {
                    if (!seen.ContainsKey(n)) { seen[n] = 0; order.Add(n); }
                    seen[n]++;
                }
                foreach (var n in order)
                    sb.AppendLine(seen[n] > 1 ? $"  {seen[n]}x  {n}" : $"  {n}");
                sb.AppendLine();
            }

            AppendCounts(sb, "Properties with no MOZA channel (binding dropped)", UnresolvedProperties);
            AppendCounts(sb, "Fonts with no mapping (fell back to the default)", UnmappedFonts);

            var dropped = Items.Where(i => i.Outcome == ItemOutcome.Dropped).ToList();
            if (dropped.Count > 0)
            {
                sb.AppendLine("Dropped items");
                sb.AppendLine(new string('-', 60));
                foreach (var g in dropped.GroupBy(i => i.SourceType + " — " + i.Detail)
                                         .OrderByDescending(g => g.Count()))
                    sb.AppendLine($"  {g.Count(),4}  {g.Key}");
                sb.AppendLine();
            }

            var subst = Items.Where(i => i.Outcome == ItemOutcome.Substituted).ToList();
            if (subst.Count > 0)
            {
                sb.AppendLine("Substituted items");
                sb.AppendLine(new string('-', 60));
                foreach (var g in subst.GroupBy(i => i.SourceType + " — " + i.Detail)
                                       .OrderByDescending(g => g.Count()))
                    sb.AppendLine($"  {g.Count(),4}  {g.Key}");
                sb.AppendLine();
            }

            if (Channels.Count > 0)
            {
                sb.AppendLine("Channels used");
                sb.AppendLine(new string('-', 60));
                foreach (var c in Channels) sb.AppendLine($"  {c}");
            }

            return sb.ToString();
        }

        private static string Truncate(string s)
            => s.Length <= 90 ? s.Replace("\n", " ") : s.Substring(0, 87).Replace("\n", " ") + "...";

        private static void AppendCounts(StringBuilder sb, string title, Dictionary<string, int> map)
        {
            if (map.Count == 0) return;
            sb.AppendLine(title);
            sb.AppendLine(new string('-', 60));
            foreach (var kv in map.OrderByDescending(k => k.Value))
                sb.AppendLine($"  {kv.Value,4}  {kv.Key}");
            sb.AppendLine();
        }
    }
}
