using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MozaPlugin.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>Outcome of one conversion.</summary>
    public sealed class ConversionResult
    {
        public bool Ok { get; set; }
        /// <summary>Why the conversion could not run at all. Empty on success — a
        /// successful conversion can still have dropped content, which is in the report.</summary>
        public string Error { get; set; } = "";
        /// <summary>The written <c>.mzdash</c>, once <see cref="Ok"/>.</summary>
        public string MzdashPath { get; set; } = "";
        public ConversionReport Report { get; set; } = new ConversionReport();
    }

    /// <summary>
    /// Runs the SimHub <c>.djson</c> → MOZA <c>.mzdash</c> pipeline.
    ///
    /// <para>Construct once and reuse: the channel reverse index and the font table are
    /// parsed from embedded resources in the constructor, which is worth doing once when
    /// converting a whole folder.</para>
    ///
    /// <para>Deliberately free of WPF and of any live-wheel dependency, so it can be driven
    /// from a test over the whole stock template corpus.</para>
    /// </summary>
    public sealed class DjsonConverter
    {
        private readonly ChannelResolver _channels;
        private readonly FontMap _fonts;

        public DjsonConverter(ChannelResolver channels)
        {
            _channels = channels;
            _fonts = new FontMap();
        }

        /// <summary>Target canvas. Defaults to the 780x248 of the W17/W18/W20 wheel
        /// displays; use <see cref="TargetDisplay"/> to take it from the connected
        /// hardware instead, which matters because the sizes differ a lot (CM2 is
        /// 1280x720, FSR V2 847x480, VGS 480x480).</summary>
        public int CanvasWidth { get; set; } = CanvasFitter.WheelWidth;
        public int CanvasHeight { get; set; } = CanvasFitter.WheelHeight;

        /// <summary>Set the canvas from a display's reported <c>productType</c>
        /// (<c>"W17 Display"</c>, <c>"S09 Display"</c>, …). Returns false and leaves the
        /// canvas alone when the type isn't in <c>Data/DjsonDisplayMap.json</c>.</summary>
        public bool TargetDisplay(string? productType)
        {
            var canvas = DisplayCanvasMap.Resolve(productType, out bool known);
            if (!known) return false;
            CanvasWidth = canvas.Width;
            CanvasHeight = canvas.Height;
            _displayNote = string.IsNullOrEmpty(canvas.Note)
                ? productType ?? "" : $"{canvas.Note} ({productType})";
            return true;
        }

        private string _displayNote = "";

        /// <summary>
        /// Decide which paged widgets get every page inlined: fewest pages first, while the
        /// running total stays under <see cref="MaxNodes"/>. A 4-page fuel module is cheap
        /// and high-payoff and gets in; a 29-page settings card placed 25 times would on its
        /// own push the dashboard past anything the wheel has been seen to load, and is
        /// held at its static page instead. Each decision is written to the report.
        /// </summary>
        private HashSet<string> ChoosePageExpansions(int baseline,
            Dictionary<string, PageExpansionCandidate> candidates, ConversionReport report,
            Func<HashSet<string>, int> measure)
        {
            var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 0) return admitted;

            // The estimate only orders the attempts; every admission is measured.
            int current = baseline;
            foreach (var c in candidates.Values.OrderBy(c => c.Pages).ThenBy(c => c.ExtraNodes))
            {
                admitted.Add(c.File);
                int actual = measure(admitted);
                if (actual <= MaxNodes)
                {
                    current = actual;
                    report.Notes.Add($"paged widget '{c.File}' ({c.Pages} pages): expanded — "
                                   + $"dashboard now {actual} nodes");
                }
                else
                {
                    admitted.Remove(c.File);
                    report.Notes.Add($"paged widget '{c.File}' ({c.Pages} pages): held at one page — "
                                   + $"expanding it brings the dashboard to {actual} nodes, budget {MaxNodes}");
                }
            }
            report.Notes.Add($"node budget {MaxNodes}: {baseline} baseline, {admitted.Count} of "
                           + $"{candidates.Count} paged widgets expanded, {current} nodes");
            return admitted;
        }

        /// <summary>Bindings the wheel evaluates every frame. Grows with page expansion
        /// alongside the node count, and is the other plausible axis for the firmware
        /// lock-up — reported so a bisect can watch both.</summary>
        private static int CountBindings(IrDashboard dashboard)
        {
            int n = 0;
            void Walk(IrNode node)
            {
                n += node.Bindings.Count;
                foreach (var c in node.Children) Walk(c);
            }
            foreach (var s in dashboard.Screens) Walk(s);
            return n;
        }

        private static int CountNodes(IrDashboard dashboard)
        {
            int n = 1; // the Window
            void Walk(IrNode node)
            {
                n++;
                foreach (var c in node.Children) Walk(c);
            }
            foreach (var s in dashboard.Screens) Walk(s);
            return n;
        }

        /// <summary>Dashboard Studio's shared image pool. When set, extracted images are
        /// copied there too so the editor can render them.</summary>
        public string? StudioImageRoot { get; set; }

        /// <summary>The SimHub install directory, for images a dashboard takes from
        /// SimHub's shared library (<c>library:Icons\ABS.png</c> → <c>ImageLibrary/Icons/ABS.png</c>).
        /// In-plugin this is where SimHub is running from; the CLI derives it from the
        /// input path or takes <c>--simhub</c>.</summary>
        public string? SimHubRoot { get; set; }

        /// <summary>The connected display's <c>idealDeviceInfos</c>, taken from the wheel's
        /// own configJson. Left null when no wheel is connected — Studio's built-in literal
        /// describes one specific wheel and must never be used as a stand-in.</summary>
        public JArray? IdealDeviceInfos { get; set; }

        /// <summary>Also write the conversion report beside the dashboard.</summary>
        public bool WriteReportFile { get; set; } = true;

        /// <summary>
        /// Let properties with no MOZA channel borrow a spare one, with SimHub publishing
        /// the value. On by default: SimHub has the data for almost every property a
        /// dashboard reads, so dropping those widgets loses content for no reason.
        ///
        /// <para>The caller must apply <see cref="ConversionReport.ChannelOverrides"/> as
        /// per-dashboard channel mappings — without that the borrowed channels carry their
        /// factory meaning and the widgets read the wrong thing.</para>
        /// </summary>
        public bool AllowChannelAllocation { get; set; } = true;

        /// <summary>
        /// The largest dashboard a wheel has been shown to load: a converted community
        /// dashboard at 817 nodes / 3.4 MB. The same dashboard fully expanded to 8006
        /// nodes / 41 MB uploaded fine and then locked the firmware — the display froze
        /// and the wheel stopped answering its own dashboard-switch button combos until
        /// the file was deleted. The real ceiling is somewhere between; nothing narrower
        /// has been measured.
        /// </summary>
        public const int DefaultMaxNodes = 817;

        /// <summary>
        /// Ceiling on emitted nodes that page expansion may grow the dashboard to.
        ///
        /// <para>Defaults to <see cref="DefaultMaxNodes"/>, the proven size — so a
        /// sub-dashboard's pages are only expanded when the result stays within what a
        /// wheel has actually been seen to run. Raise it to probe: the report gives the
        /// measured size at which each sub-dashboard was admitted or held, so a bisect
        /// against the hardware takes a handful of uploads. The Dashboard Studio schema
        /// cap (ids below 10000) is the hard upper bound.</para>
        /// </summary>
        public int MaxNodes { get; set; } = DefaultMaxNodes;

        /// <summary>
        /// Convert one dashboard into <c>&lt;outputRoot&gt;/&lt;name&gt;/&lt;name&gt;.mzdash</c>,
        /// with its images under that folder's <c>Resource/MD5/</c>.
        ///
        /// <para>Accepts either a bare <c>.djson</c> or a <c>.simhubdash</c> bundle; the
        /// bundle is expanded to a temporary folder and converted from there, so widget
        /// includes and <c>.ressources</c> archives resolve as siblings exactly as they
        /// would in a SimHub install.</para>
        /// </summary>
        public ConversionResult Convert(string sourcePath, string outputRoot)
        {
            if (!SimhubDashBundle.IsBundle(sourcePath)) return ConvertDjson(sourcePath, outputRoot);

            var bundleResult = new ConversionResult();
            SimhubDashBundle? bundle = null;
            try
            {
                bundle = SimhubDashBundle.Open(sourcePath, bundleResult.Report);
            }
            catch (Exception ex)
            {
                bundleResult.Error = $"could not open bundle: {ex.Message}";
                MozaLog.Warn($"[AZOM] DjsonImport: bundle '{Path.GetFileName(sourcePath)}' failed: {ex}");
                return bundleResult;
            }

            using (bundle)
            {
                var result = ConvertDjson(bundle.MainDjsonPath, outputRoot);
                // Carry the bundle's own notes across and report the real source, not the
                // temporary folder the conversion actually read.
                result.Report.Notes.InsertRange(0, bundleResult.Report.Notes);
                result.Report.SourcePath = sourcePath;
                return result;
            }
        }

        private ConversionResult ConvertDjson(string djsonPath, string outputRoot)
        {
            var result = new ConversionResult();
            var report = result.Report;

            if (!File.Exists(djsonPath))
            {
                result.Error = $"'{djsonPath}' does not exist";
                return result;
            }

            string sourceDir = Path.GetDirectoryName(djsonPath) ?? ".";
            string rawName = Path.GetFileNameWithoutExtension(djsonPath);
            string name = MzdashWriter.SafeName(rawName);

            report.DashboardName = name;
            report.SourcePath = djsonPath;
            report.CanvasWidth = CanvasWidth;
            report.CanvasHeight = CanvasHeight;
            if (_displayNote.Length > 0) report.Notes.Add($"target display: {_displayNote}");
            if (!string.Equals(name, rawName, StringComparison.Ordinal))
                report.Notes.Add($"name '{rawName}' sanitised to '{name}' for the wheel's file paths");

            var (root, error) = DjsonReader.Load(djsonPath);
            if (root == null)
            {
                result.Error = $"could not parse: {error}";
                return result;
            }

            report.SourceWidth = DjsonReader.Num(root, "BaseWidth", 0);
            report.SourceHeight = DjsonReader.Num(root, "BaseHeight", 0);

            string outputDir = Path.Combine(outputRoot, name);

            try
            {
                // Each conversion gets a clean allocation pool — borrowings are recorded
                // per dashboard, so one dashboard's must not leak into the next.
                _channels.ResetAllocations();

                var images = ResourceExtractor.Extract(djsonPath, outputDir, StudioImageRoot, report, SimHubRoot);

                // Pass 1 — the probe. Allocation off, no page expansion. It settles two
                // things the real pass needs first: which channels the dashboard reads for
                // their own meaning (reserved, so a borrowed channel can never collide with
                // one), and what each paged widget would cost to expand. Mapping is pure
                // apart from the images already extracted, so extra passes cost parse time.
                _channels.AllocationEnabled = false;
                var probeReport = new ConversionReport();
                var probe = new DjsonToIr(
                    new BindingTranslator(_channels, new NCalcToJs(_channels), images,probeReport),
                    _fonts, probeReport, images, sourceDir);
                var baseline = probe.Convert(root, name);
                _channels.ReserveDirect(probeReport.Channels);

                // Admit page expansions against the node budget, each one MEASURED by a dry
                // run rather than estimated: a paged widget nested inside another only
                // becomes real once its parent expands, so no static count sees the
                // interaction. Allocation has to be on for the measurement — a page selector
                // rides a borrowed channel, and without one the widget stays at its static
                // page — and is reset to the probe's reservations after each.
                int Measure(HashSet<string> set)
                {
                    _channels.AllocationEnabled = true;
                    var scratch = new ConversionReport();

                    // Same shape as the real pipeline, or the measurement lies: without the
                    // containers-first pass, leaf offloads drain the pool before any page
                    // selector is reached, nothing expands, and the count is the static tree.
                    new DjsonToIr(
                        new BindingTranslator(_channels, new NCalcToJs(_channels), images,scratch)
                            { ContainerBindingsOnly = true },
                        _fonts, scratch, images, sourceDir)
                        { ExpandedWidgets = set }
                        .Convert(root, name);

                    var dry = new DjsonToIr(
                        new BindingTranslator(_channels, new NCalcToJs(_channels), images,scratch),
                        _fonts, scratch, images, sourceDir)
                        { ExpandedWidgets = set };
                    int n = CountNodes(dry.Convert(root, name));

                    _channels.ResetAllocations();
                    _channels.ReserveDirect(probeReport.Channels);
                    return n;
                }

                var expanded = AllowChannelAllocation
                    ? ChoosePageExpansions(CountNodes(baseline), probe.PagedWidgets, report, Measure)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!AllowChannelAllocation && probe.PagedWidgets.Count > 0)
                    report.Notes.Add("paged widgets held at one page — a page selector needs channel allocation");

                _channels.AllocationEnabled = AllowChannelAllocation;

                // Pass 2 — priority. Let the bindings that gate whole subtrees borrow their
                // channels before any leaf readout can. Allocations are cached by source
                // expression, so the real pass below reuses them rather than paying twice.
                if (AllowChannelAllocation)
                {
                    var priorityReport = new ConversionReport();
                    var priority = new DjsonToIr(
                        new BindingTranslator(_channels, new NCalcToJs(_channels), images,priorityReport)
                            { ContainerBindingsOnly = true },
                        _fonts, priorityReport, images, sourceDir)
                        { ExpandedWidgets = expanded };
                    priority.Convert(root, name);
                }

                var translator = new BindingTranslator(_channels, new NCalcToJs(_channels), images,report);
                var mapper = new DjsonToIr(translator, _fonts, report, images, sourceDir)
                    { ExpandedWidgets = expanded };

                var dashboard = mapper.Convert(root, name);
                dashboard.ImageResources.AddRange(images.Resources);
                report.ScreenCount = dashboard.Screens.Count;
                report.NodeCount = CountNodes(dashboard);
                report.BindingCount = CountBindings(dashboard);

                if (dashboard.Screens.Count == 0)
                {
                    result.Error = "no convertible screens";
                    return result;
                }

                report.ChannelOverrides.AddRange(_channels.Allocations);
                if (_channels.AllocationEnabled && _channels.AvailableChannels == 0)
                {
                    int starved = 0;
                    foreach (var n in report.Notes)
                        if (n.IndexOf("no spare channel", StringComparison.Ordinal) >= 0) starved++;
                    if (starved > 0 || report.UnresolvedProperties.Count > 0)
                    {
                        report.Notes.Insert(0, $"every spare channel is in use — {starved} "
                                             + "formula(s) and the remaining unresolved properties "
                                             + "were dropped");
                    }
                }

                CanvasFitter.Fit(dashboard, CanvasWidth, CanvasHeight,
                                 report.SourceWidth, report.SourceHeight, report);

                var writer = new MzdashWriter();
                var document = writer.Build(
                    dashboard, CanvasWidth, CanvasHeight, IdealDeviceInfos);
                if (writer.OverflowedNodes > 0)
                {
                    report.Notes.Add($"{writer.OverflowedNodes} node(s) dropped — a dashboard "
                                   + $"may hold at most {MzdashWriter.MaxElementId} elements");
                }

                string mzdashPath = Path.Combine(outputDir, name + ".mzdash");
                MzdashWriter.Save(document, mzdashPath);

                if (WriteReportFile)
                {
                    try { File.WriteAllText(Path.Combine(outputDir, name + ".conversion.txt"), report.ToText()); }
                    catch (Exception ex) { report.Notes.Add($"report file not written: {ex.Message}"); }
                }

                result.Ok = true;
                result.MzdashPath = mzdashPath;
                MozaLog.Info($"[AZOM] DjsonImport: converted '{rawName}' -> {mzdashPath} ({report.Summary()})");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                MozaLog.Warn($"[AZOM] DjsonImport: '{rawName}' failed: {ex}");
            }

            return result;
        }
    }
}
