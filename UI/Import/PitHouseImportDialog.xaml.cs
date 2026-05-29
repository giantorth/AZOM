using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MozaPlugin.Devices;
using MozaPlugin.Resources;

namespace MozaPlugin.UI.Import
{
    public partial class PitHouseImportDialog : Window
    {
        private readonly MozaPlugin _plugin;
        private string? _customPathOverride;

        // Selected preset + built plan, populated when Next is clicked. The
        // caller pulls these via SelectedPreset / Plan once DialogResult is true.
        public PitHousePreset? SelectedPreset { get; private set; }
        public ImportPlan? Plan { get; private set; }

        public PitHouseImportDialog(MozaPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            _customPathOverride = plugin.Settings?.PitHousePresetsPathOverride;
            RefreshLists();
        }

        // ------------------------------------------------------------
        //  Picker phase
        // ------------------------------------------------------------

        private void RefreshLists()
        {
            var root = PitHouseFolderScanner.ResolvePresetsRoot(_customPathOverride);
            if (root == null)
            {
                FolderHintText.Text = Strings.Import_NoFolderFound;
                MotorList.ItemsSource = null;
                PedalsList.ItemsSource = null;
                MotorEmptyText.Visibility = Visibility.Visible;
                PedalsEmptyText.Visibility = Visibility.Visible;
                return;
            }

            FolderHintText.Text = root;

            var motors = PitHouseFolderScanner.ListCategory(root, PitHouseFolderScanner.Category.Motor);
            var pedals = PitHouseFolderScanner.ListCategory(root, PitHouseFolderScanner.Category.Pedals);

            MotorList.ItemsSource = motors;
            PedalsList.ItemsSource = pedals;
            MotorEmptyText.Visibility = motors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PedalsEmptyText.Visibility = pedals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetFolderButton_Click(object sender, RoutedEventArgs e)
        {
            // Use Windows Forms folder browser (System.Windows.Forms is already
            // referenced in MozaPlugin.csproj). WPF lacks a native folder picker
            // on net48 and pulling Microsoft.Win32.OpenFolderDialog requires .NET 8.
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = Strings.Import_SetCustomFolder;
                if (!string.IsNullOrEmpty(_customPathOverride))
                    fbd.SelectedPath = _customPathOverride;

                var result = fbd.ShowDialog();
                if (result != System.Windows.Forms.DialogResult.OK) return;

                string picked = fbd.SelectedPath ?? "";
                _customPathOverride = picked;
                if (_plugin.Settings != null)
                {
                    _plugin.Settings.PitHousePresetsPathOverride = picked;
                    try { _plugin.SaveSettings(); } catch { /* persistence is best-effort */ }
                }
                RefreshLists();
            }
        }

        private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The TabControl bubbles its own SelectionChanged through this same
            // handler — guard against firing while the inner ListBox isn't the
            // event's source by always re-querying from the active tab.
            NextButton.IsEnabled = GetSelectedHeader() != null;
        }

        private void CategoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Tab switches don't fire ListBox.SelectionChanged on their own — keep
            // the Next button enabled state in sync with whichever tab is now
            // visible. The Browse tab has no selection, so Next stays disabled
            // there (user clicks the in-tab Browse button instead).
            if (e.OriginalSource is TabControl)
                NextButton.IsEnabled = GetSelectedHeader() != null;
        }

        private void PresetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GetSelectedHeader() != null) Next_Click(sender, e);
        }

        private PitHouseFolderScanner.PresetHeader? GetSelectedHeader()
        {
            // Whichever tab is active gives us the selection.
            if (CategoryTabs.SelectedItem == MotorTab)
                return MotorList.SelectedItem as PitHouseFolderScanner.PresetHeader;
            if (CategoryTabs.SelectedItem == PedalsTab)
                return PedalsList.SelectedItem as PitHouseFolderScanner.PresetHeader;
            return null;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PitHouse preset (*.json)|*.json|All files (*.*)|*.*",
                Title = Strings.Import_DialogTitle,
            };
            if (dlg.ShowDialog(this) != true) return;

            LoadPresetAndConfirm(dlg.FileName);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            var header = GetSelectedHeader();
            if (header == null) return;
            LoadPresetAndConfirm(header.Path);
        }

        // ------------------------------------------------------------
        //  Confirm phase
        // ------------------------------------------------------------

        private void LoadPresetAndConfirm(string path)
        {
            var (preset, error) = PitHousePresetReader.Read(path);
            if (preset == null)
            {
                System.Windows.MessageBox.Show(
                    this, error, Strings.Import_DialogTitle,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ImportPlan plan;
            if (string.Equals(preset.DeviceType, "Motor", StringComparison.OrdinalIgnoreCase))
            {
                var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
                if (profile == null)
                {
                    System.Windows.MessageBox.Show(this,
                        "No active SimHub profile.",
                        Strings.Import_DialogTitle,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                plan = PitHouseMotorMapper.BuildPlan(preset, profile);
            }
            else if (string.Equals(preset.DeviceType, "Pedals", StringComparison.OrdinalIgnoreCase))
            {
                var registry = _plugin.MBoosterRegistry;
                IReadOnlyList<MBoosterDeviceController> controllers =
                    registry?.Devices ?? Array.Empty<MBoosterDeviceController>();
                plan = PitHousePedalsMapper.BuildPlan(preset, controllers);
            }
            else
            {
                System.Windows.MessageBox.Show(this,
                    string.Format(Strings.Import_Error_UnsupportedType, preset.DeviceType),
                    Strings.Import_DialogTitle,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SelectedPreset = preset;
            Plan = plan;

            // Debug — surface what BuildPlan produced so we can diagnose the
            // "empty Changes container" case from logs. Logs to SimHub.txt.
            int changedCount = 0;
            foreach (var d in plan.Diffs) if (d.Changed) changedCount++;
            MozaLog.Info(
                $"[Moza/Import] BuildPlan '{preset.Name}' type={preset.DeviceType}: " +
                $"dp.Count={preset.DeviceParams.Count} " +
                $"diffs={plan.Diffs.Count} changed={changedCount} " +
                $"notImported={plan.NotImported.Count} " +
                $"fatal='{plan.FatalError ?? ""}'");
            for (int i = 0; i < Math.Min(plan.Diffs.Count, 5); i++)
            {
                var d = plan.Diffs[i];
                MozaLog.Info($"[Moza/Import]   diff[{i}] {d.Label}: '{d.OldDisplay}' -> '{d.NewDisplay}' changed={d.Changed}");
            }

            ShowConfirmPanel();
        }

        private void ShowConfirmPanel()
        {
            if (SelectedPreset == null || Plan == null) return;

            string profileName = _plugin.Settings?.ProfileStore?.CurrentProfile?.Name
                                 ?? "(unknown)";

            // Header card key/value rows.
            ConfirmPresetText.Text = SelectedPreset.Name;
            ConfirmProfileText.Text = profileName;

            // Show the full diff list (changed AND unchanged) so the user can
            // see the complete mapping. The DataTemplate dims unchanged rows
            // so the actual changes still stand out. Counts feed the footer
            // caption and the Apply-button enable below.
            DiffList.ItemsSource = Plan.Diffs;

            int changedCount = 0;
            foreach (var d in Plan.Diffs) if (d.Changed) changedCount++;

            // The "no-op" empty state only fires when the preset produced zero
            // mappable rows at all — e.g. a preset whose deviceParams had no
            // recognisable keys. With at least one row we always show the list.
            NoChangesText.Visibility = Plan.Diffs.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (Plan.NotImported.Count > 0)
            {
                NotImportedList.ItemsSource = Plan.NotImported;
                NotImportedCard.Visibility = Visibility.Visible;
            }
            else
            {
                NotImportedCard.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrEmpty(Plan.FatalError))
            {
                FatalErrorText.Text = Plan.FatalError;
                FatalErrorBanner.Visibility = Visibility.Visible;
                ApplyButton.IsEnabled = false;
            }
            else
            {
                FatalErrorBanner.Visibility = Visibility.Collapsed;
                ApplyButton.IsEnabled = changedCount > 0;
            }

            // Footer status caption summarises the diff count so the user
            // sees the impact without having to scan the list.
            int totalMapped = Plan.Diffs.Count;
            if (totalMapped == 0)
                FooterStatusText.Text = "no mappable settings in this preset";
            else if (changedCount == 0)
                FooterStatusText.Text = $"all {totalMapped} settings already match — nothing to apply";
            else
                FooterStatusText.Text = $"{changedCount} of {totalMapped} setting{(totalMapped == 1 ? "" : "s")} will change";

            // Header bar reflects the active phase.
            TopBarSubtitle.Text = $"// Applying “{SelectedPreset.Name}” to “{profileName}”";

            PickerPanel.Visibility = Visibility.Collapsed;
            ConfirmPanel.Visibility = Visibility.Visible;

            // Swap footer button visibility: Next → Apply, show Back.
            NextButton.Visibility = Visibility.Collapsed;
            ApplyButton.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            SelectedPreset = null;
            Plan = null;
            ConfirmPanel.Visibility = Visibility.Collapsed;
            PickerPanel.Visibility = Visibility.Visible;

            // Restore footer to picker phase.
            NextButton.Visibility = Visibility.Visible;
            ApplyButton.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            FooterStatusText.Text = "";
            TopBarSubtitle.Text = "// Apply a MOZA Pit House preset to the active profile and hardware";
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
