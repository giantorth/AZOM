using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MozaPlugin.Diagnostics;
using MozaPlugin.Resources;
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

        private string? _sourcePath;
        private string? _convertedPath;

        /// <summary>Set once a conversion has written a dashboard, so the caller knows to
        /// rescan the library.</summary>
        public bool Converted { get; private set; }

        /// <param name="idealDeviceInfos">The connected display's descriptor from the
        /// wheel's own configJson, or null when no wheel is connected. Never substitute
        /// Studio's built-in literal — it describes one specific wheel.</param>
        public DjsonImportDialog(DashboardProfileStore store, JArray? idealDeviceInfos)
        {
            _store = store;
            _idealDeviceInfos = idealDeviceInfos;
            InitializeComponent();
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

            string? outputRoot = DashboardStudioLauncher.ResolveProjectRoot();
            if (string.IsNullOrEmpty(outputRoot))
            {
                // No PitHouse install: still convert, next to the source, so the file can
                // be picked up by the Files tab's local-file upload mode.
                outputRoot = Path.GetDirectoryName(_sourcePath);
            }
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
                // Size the canvas for the display actually connected. Sizes differ a
                // lot — a CM2 is 1280x720, the same 16:9 most SimHub dashboards are
                // authored at, so targeting it needs almost no rescale.
                converter.TargetDisplay(ProductTypeOf(_idealDeviceInfos));
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
            _convertedPath = result.MzdashPath;
            StudioButton.IsEnabled = true;

            string summary = result.Report.Summary();
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
            try
            {
                string? simhub = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetEntryAssembly()?.Location);
                if (string.IsNullOrEmpty(simhub)) return null;

                string templates = Path.Combine(simhub!, "DashTemplates");
                return Directory.Exists(templates) ? templates : null;
            }
            catch
            {
                return null;
            }
        }
    }

}
