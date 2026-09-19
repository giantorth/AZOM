using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MozaPlugin.Diagnostics;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry.Dashboard;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Converts a SimHub <c>.djson</c> dashboard to a MOZA <c>.mzdash</c>.
    ///
    /// <para>Output lands in Dashboard Studio's project root, so the converted dashboard
    /// is picked up by the existing library scan (<c>ReloadDashboardLibrary</c> →
    /// <c>DashboardCache.LoadFromFolders</c>) and appears in the Files tab's upload list
    /// with no extra plumbing.</para>
    ///
    /// <para>The report is shown before anything is uploaded: conversion is lossy for some
    /// dashboards (maps, radars and multi-opponent leaderboards have no wheel equivalent),
    /// and a file that quietly lost half its widgets is worse than one that says so.</para>
    /// </summary>
    public partial class DjsonImportDialog : Window
    {
        /// <summary>Past this share of dropped widgets the output is misleading rather
        /// than useful, and the summary says so.</summary>
        private const double HeavyLossRatio = 0.25;

        private readonly DashboardProfileStore _store;
        private readonly JArray? _idealDeviceInfos;
        private readonly string? _libraryFolder;

        private string? _sourcePath;
        private string? _convertedPath;

        /// <summary>Set once a conversion has written a dashboard, so the caller knows to
        /// rescan the library.</summary>
        public bool Converted { get; private set; }

        /// <summary>The written dashboard, once <see cref="Converted"/>.</summary>
        public string? ConvertedPath => _convertedPath;

        /// <summary>The last successful conversion, so the caller can publish its
        /// <see cref="ConversionReport.ChannelOverrides"/>.</summary>
        public ConversionResult? Result { get; private set; }

        /// <param name="libraryFolder">The configured dashboard library, or null. When set
        /// the converted dashboard lands there, so it appears in the upload list and gets
        /// picked up by the folder scan on the next connect.</param>
        /// <param name="idealDeviceInfos">The connected display's descriptor from the
        /// wheel's own configJson, or null when no wheel is connected. Never substitute
        /// Studio's built-in literal — it describes one specific wheel.</param>
        /// <param name="settings">Plugin settings, for the persisted node ceiling. The
        /// caller saves after the dialog closes.</param>
        public DjsonImportDialog(DashboardProfileStore store, JArray? idealDeviceInfos,
                                 string? libraryFolder, MozaPluginSettings? settings)
        {
            _store = store;
            _idealDeviceInfos = idealDeviceInfos;
            _libraryFolder = libraryFolder;
            _settings = settings;
            InitializeComponent();

            int limit = settings != null && settings.DjsonImportMaxNodes > 0
                ? settings.DjsonImportMaxNodes
                : DjsonConverter.DefaultMaxNodes;
            NodeLimitBox.Text = limit.ToString();
        }

        private readonly MozaPluginSettings? _settings;

        /// <summary>The node ceiling typed into the dialog, clamped to the schema's hard
        /// cap. Falls back to the proven default when the field isn't a number.</summary>
        private int ReadNodeLimit()
        {
            if (!int.TryParse(NodeLimitBox.Text.Trim(), out int n) || n <= 0)
                n = DjsonConverter.DefaultMaxNodes;
            n = Math.Min(n, MzdashWriter.MaxElementId + 1);
            NodeLimitBox.Text = n.ToString();
            return n;
        }

        private void PickFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = Strings.FileFilter_Djson,
                CheckFileExists = true,
                Multiselect = false,
            };

            string? start = FindSimHubTemplates();
            if (start != null) dialog.InitialDirectory = start;

            if (dialog.ShowDialog(this) != true) return;

            _sourcePath = dialog.FileName;
            _convertedPath = null;
            SourceText.Text = _sourcePath;
            ConvertButton.IsEnabled = true;
            StudioButton.IsEnabled = false;
            SummaryText.Visibility = Visibility.Collapsed;
            ReportText.Text = "";
        }

        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_sourcePath)) return;

            string? outputRoot = ResolveOutputRoot();
            if (string.IsNullOrEmpty(outputRoot))
            {
                ShowSummary(string.Format(Strings.Status_DjsonConvertFailed,
                    "no output directory"), error: true);
                return;
            }

            ConversionResult result;
            var previousCursor = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var converter = PluginChannelCatalog.CreateConverter(_store);
                converter.StudioImageRoot = DashboardStudioLauncher.ResolveImageRoot();
                converter.IdealDeviceInfos = _idealDeviceInfos;
                converter.SimHubRoot = FindSimHubRoot();
                // Size the canvas for the display actually connected. Sizes differ a
                // lot — a CM2 is 1280x720, the same 16:9 most SimHub dashboards are
                // authored at, so targeting it needs almost no rescale.
                converter.TargetDisplay(ProductTypeOf(_idealDeviceInfos));
                converter.MaxNodes = ReadNodeLimit();
                if (_settings != null) _settings.DjsonImportMaxNodes = converter.MaxNodes;
                result = converter.Convert(_sourcePath!, outputRoot!);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] DjsonImportDialog: conversion threw: {ex}");
                ShowSummary(string.Format(Strings.Status_DjsonConvertFailed, ex.Message), error: true);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = previousCursor;
            }

            ReportText.Text = result.Report.ToText();

            if (!result.Ok)
            {
                ShowSummary(string.Format(Strings.Status_DjsonConvertFailed, result.Error), error: true);
                return;
            }

            Converted = true;
            Result = result;
            _convertedPath = result.MzdashPath;
            StudioButton.IsEnabled = true;

            string summary = result.Report.Summary();
            summary += $"  |  {result.Report.Channels.Count} channels";
            if (result.Report.ChannelOverrides.Count > 0)
                summary += $" ({result.Report.ChannelOverrides.Count} via SimHub)";
            bool heavy = result.Report.DropRatio > HeavyLossRatio;
            if (heavy)
            {
                summary += "  |  " + string.Format(Strings.Status_DjsonHeavyLoss,
                    (int)Math.Round(result.Report.DropRatio * 100));
            }
            ShowSummary(summary, error: heavy);
        }

        private void OpenStudio_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_convertedPath)) return;

            var launch = DashboardStudioLauncher.LaunchEdit(_convertedPath!);
            if (launch.Outcome != DashboardStudioLauncher.LaunchOutcome.Started)
                ShowSummary(launch.Error ?? launch.Outcome.ToString(), error: true);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowSummary(string text, bool error)
        {
            SummaryText.Text = text;
            SummaryText.Foreground = (Brush)FindResource(error ? "AmberBrush" : "TextBrush");
            SummaryText.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Where the converted dashboard goes.
        ///
        /// <para>The configured dashboard library wins: that folder is what the plugin
        /// scans into <c>DashboardCache</c>, so a dashboard written there shows up in the
        /// upload list and survives a restart. Studio's project root is the fallback so
        /// the result is still somewhere the editor lists, and only failing both does it
        /// land beside the source file.</para>
        /// </summary>
        private string? ResolveOutputRoot()
        {
            if (!string.IsNullOrWhiteSpace(_libraryFolder) && Directory.Exists(_libraryFolder))
                return _libraryFolder;

            string? studio = DashboardStudioLauncher.ResolveProjectRoot();
            if (!string.IsNullOrEmpty(studio)) return studio;

            return Path.GetDirectoryName(_sourcePath);
        }

        /// <summary>The connected display's <c>productType</c> from its own descriptor,
        /// which is what <see cref="DisplayCanvasMap"/> keys on. Null when no wheel is
        /// connected, leaving the converter on its 780x248 default.</summary>
        private static string? ProductTypeOf(JArray? idealDeviceInfos)
        {
            if (idealDeviceInfos == null) return null;
            foreach (var entry in idealDeviceInfos)
            {
                string? type = (string?)entry?["productType"];
                if (!string.IsNullOrWhiteSpace(type)) return type;
            }
            return null;
        }

        /// <summary>SimHub's stock template folder, so the picker opens somewhere useful.
        /// Null when SimHub isn't installed where we expect — the picker just opens at its
        /// own default then.</summary>
        private static string? FindSimHubTemplates()
        {
            string? simhub = FindSimHubRoot();
            if (simhub == null) return null;
            string templates = Path.Combine(simhub, "DashTemplates");
            return Directory.Exists(templates) ? templates : null;
        }

        /// <summary>The running SimHub's install directory — the plugin is loaded into
        /// SimHub's process, so the entry assembly is SimHub itself.</summary>
        private static string? FindSimHubRoot()
        {
            try
            {
                string? dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetEntryAssembly()?.Location);
                return string.IsNullOrEmpty(dir) || !Directory.Exists(dir) ? null : dir;
            }
            catch
            {
                return null;
            }
        }
    }

}
