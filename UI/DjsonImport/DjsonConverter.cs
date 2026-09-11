using System;
using System.IO;
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

        /// <summary>Dashboard Studio's shared image pool. When set, extracted images are
        /// copied there too so the editor can render them.</summary>
        public string? StudioImageRoot { get; set; }

        /// <summary>The connected display's <c>idealDeviceInfos</c>, taken from the wheel's
        /// own configJson. Left null when no wheel is connected — Studio's built-in literal
        /// describes one specific wheel and must never be used as a stand-in.</summary>
        public JArray? IdealDeviceInfos { get; set; }

        /// <summary>Also write the conversion report beside the dashboard.</summary>
        public bool WriteReportFile { get; set; } = true;

        /// <summary>
        /// Convert one dashboard into <c>&lt;outputRoot&gt;/&lt;name&gt;/&lt;name&gt;.mzdash</c>,
        /// with its images under that folder's <c>Resource/MD5/</c>.
        /// </summary>
        public ConversionResult Convert(string djsonPath, string outputRoot)
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
                var images = ResourceExtractor.Extract(djsonPath, outputDir, StudioImageRoot, report);

                var translator = new BindingTranslator(_channels, new NCalcToJs(_channels), report);
                var mapper = new DjsonToIr(translator, _fonts, report, images.ByName, sourceDir);

                var dashboard = mapper.Convert(root, name);
                dashboard.ImageResources.AddRange(images.Resources);
                report.ScreenCount = dashboard.Screens.Count;

                if (dashboard.Screens.Count == 0)
                {
                    result.Error = "no convertible screens";
                    return result;
                }

                CanvasFitter.Fit(dashboard, CanvasWidth, CanvasHeight, report);

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
