using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MozaPlugin.Devices;
using MozaPlugin.Resources;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Embeddable version of the PitHouse preset import wizard. Hosts the same
    /// two-phase picker → confirm state machine the old
    /// <c>PitHouseImportDialog</c> Window did, but lives inline inside the
    /// settings "Import" tab. The host (<see cref="SettingsControl"/>) calls
    /// <see cref="Initialize"/> with the plugin instance after its own
    /// InitializeComponent, and subscribes to <see cref="ApplyRequested"/> to
    /// run the existing apply path when the user clicks Apply.
    /// </summary>
    public partial class PitHouseImportControl : UserControl
    {
        private MozaPlugin? _plugin;
        private string? _customPathOverride;

        // Pedals path only: the attached pedals the preset can be applied to,
        // snapshotted when the preset is loaded so a device arriving mid-confirm
        // can't shift the combo out from under the user's selection.
        private IReadOnlyList<MBoosterDeviceController> _pedalControllers = Array.Empty<MBoosterDeviceController>();
        private bool _suppressTargetChange;

        // Selected preset + built plan, populated when Next is clicked.
        public PitHousePreset? SelectedPreset { get; private set; }
        public ImportPlan? Plan { get; private set; }

        /// <summary>
        /// Raised when the user clicks Apply on a valid <see cref="ImportPlan"/>.
        /// The host routes this to its ApplyImportPlan(ImportPlan) which mutates
        /// the active profile, pushes to hardware, and refreshes the UI.
        /// </summary>
        public event Action<ImportPlan>? ApplyRequested;

        // Parameterless ctor required for XAML instantiation. Does NOT touch
        // _plugin — the host calls Initialize once the plugin is available.
        public PitHouseImportControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Wires up the plugin reference and populates the preset lists. Safe to
        /// call right after the host's InitializeComponent — the visual tree is
        /// already built by then.
        /// </summary>
        public void Initialize(MozaPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _customPathOverride = plugin.Settings?.PitHousePresetsPathOverride;
            // Scan on first Loaded, not here: SimHub constructs the settings pane
            // on every game switch, and the scan reads + parses every preset file.
            Loaded += OnFirstLoaded;
        }

        private bool _listsLoaded;

        private void OnFirstLoaded(object sender, RoutedEventArgs e)
        {
            if (_listsLoaded) return;
            _listsLoaded = true;
            RefreshLists();
        }

        // ------------------------------------------------------------
        //  Picker phase
        // ------------------------------------------------------------

        // Bumped per refresh so a slow scan can't publish over a newer one.
        private int _refreshGeneration;

        private async void RefreshLists()
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

            int gen = ++_refreshGeneration;
            List<PitHouseFolderScanner.PresetHeader> motors, pedals;
            try
            {
                // File reads + JSON/zip parsing per preset — off the dispatcher.
                (motors, pedals) = await System.Threading.Tasks.Task.Run(() =>
                    (PitHouseFolderScanner.ListCategory(root, PitHouseFolderScanner.Category.Motor),
                     PitHouseFolderScanner.ListCategory(root, PitHouseFolderScanner.Category.Pedals)));
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] PitHouse preset scan failed: {ex.Message}");
                return;
            }
            if (gen != _refreshGeneration) return;

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
                if (_plugin?.Settings != null)
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
                Filter = "PitHouse preset (*.mzpreset;*.json)|*.mzpreset;*.json|All files (*.*)|*.*",
                Title = Strings.Import_DialogTitle,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

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
                    Window.GetWindow(this), error, Strings.Import_DialogTitle,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.Equals(preset.DeviceType, "Motor", StringComparison.OrdinalIgnoreCase))
            {
                var profile = _plugin?.Settings?.ProfileStore?.CurrentProfile;
                if (profile == null)
                {
                    System.Windows.MessageBox.Show(Window.GetWindow(this),
                        Strings.Import_NoActiveProfile,
                        Strings.Import_DialogTitle,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                SelectedPreset = preset;
                Plan = PitHouseMotorMapper.BuildPlan(preset, profile);
                _pedalControllers = Array.Empty<MBoosterDeviceController>();
                PopulatePedalTargets(null);
            }
            else if (string.Equals(preset.DeviceType, "Pedals", StringComparison.OrdinalIgnoreCase))
            {
                if (RoutesToCrpPedals(preset))
                {
                    // CRP/CRP2/SRP: calibration lives on the profile, and the one
                    // device carries all three pedals — no target to pick.
                    var pedalProfile = _plugin?.Settings?.ProfileStore?.CurrentProfile;
                    if (pedalProfile == null)
                    {
                        System.Windows.MessageBox.Show(Window.GetWindow(this),
                            Strings.Import_NoActiveProfile,
                            Strings.Import_DialogTitle,
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    SelectedPreset = preset;
                    _pedalControllers = Array.Empty<MBoosterDeviceController>();
                    Plan = PitHouseCrpPedalsMapper.BuildPlan(preset, pedalProfile);
                    PopulatePedalTargets(Plan);
                }
                else
                {
                    SelectedPreset = preset;
                    _pedalControllers = _plugin?.MBoosterRegistry?.Devices
                                        ?? (IReadOnlyList<MBoosterDeviceController>)Array.Empty<MBoosterDeviceController>();
                    // First build with no override so the mapper picks the pedal
                    // carrying the preset's subject role; the combo then preselects
                    // whatever it resolved to.
                    Plan = PitHousePedalsMapper.BuildPlan(preset, _pedalControllers);
                    PopulatePedalTargets(Plan);
                }
            }
            else
            {
                System.Windows.MessageBox.Show(Window.GetWindow(this),
                    string.Format(Strings.Import_Error_UnsupportedType, preset.DeviceType),
                    Strings.Import_DialogTitle,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            LogPlan(preset, Plan);
            ShowConfirmPanel();
        }

        /// <summary>
        /// Whether a Pedals preset should go to the CRP/CRP2/SRP surface rather than
        /// the mBooster one. Two gates, both conservative:
        ///
        /// <para>An attached mBooster always keeps the mBooster path — its plugin
        /// fields are raw sensor counts where the CRP's are percent, so mis-routing
        /// would write a 99 % range as ~0.15 % of full scale. A routed mBooster (RJ45
        /// into a base pedal port) also sets <c>PedalsDetected</c>, so the flag alone
        /// can't tell the families apart.</para>
        ///
        /// <para>With no mBooster attached, the preset's own <c>devices</c> list
        /// decides: mBooster presets name "mBooster" (docs/protocol/devices/
        /// mbooster.md § PitHouse Pedals preset format), so one that doesn't goes to
        /// the passive surface. No CRP-family pedals detected either → stay on the
        /// mBooster path so its "no mBooster pedal attached" note is what shows.</para>
        /// </summary>
        private bool RoutesToCrpPedals(PitHousePreset preset)
        {
            if (_plugin?.IsPedalsDetected != true) return false;
            if (_plugin?.MBoosterRegistry?.Devices.Count > 0) return false;
            var devs = preset.Devices;
            if (devs != null)
            {
                foreach (var d in devs)
                    if (!string.IsNullOrEmpty(d)
                        && d.IndexOf("mBooster", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
            }
            return true;
        }

        private static void LogPlan(PitHousePreset preset, ImportPlan plan)
        {
            // Debug — surface what BuildPlan produced so we can diagnose the
            // "empty Changes container" case from logs. Logs to SimHub.txt.
            int changedCount = 0;
            foreach (var d in plan.Diffs) if (d.Changed) changedCount++;
            MozaLog.Info(
                $"[AZOM/Import] BuildPlan '{preset.Name}' type={preset.DeviceType}: " +
                $"dp.Count={preset.DeviceParams.Count} " +
                $"subject='{plan.SubjectRoleDisplay ?? "-"}' target='{plan.ResolvedTarget?.Label ?? "-"}' " +
                $"diffs={plan.Diffs.Count} changed={changedCount} " +
                $"notImported={plan.NotImported.Count} " +
                $"fatal='{plan.FatalError ?? ""}'");
            for (int i = 0; i < Math.Min(plan.Diffs.Count, 5); i++)
            {
                var d = plan.Diffs[i];
                MozaLog.Info($"[AZOM/Import]   diff[{i}] {d.Label}: '{d.OldDisplay}' -> '{d.NewDisplay}' changed={d.Changed}");
            }
        }

        /// <summary>
        /// Fill the "Apply to" combo with every attached pedal and preselect the
        /// one <paramref name="plan"/> resolved to. Passing null (Motor path)
        /// clears and hides the row. Runs under <see cref="_suppressTargetChange"/>
        /// so repopulating never re-enters the rebuild.
        /// </summary>
        private void PopulatePedalTargets(ImportPlan? plan)
        {
            _suppressTargetChange = true;
            try
            {
                if (plan == null)
                {
                    TargetPedalCombo.ItemsSource = null;
                    return;
                }

                var targets = PitHousePedalsMapper.EnumerateTargets(_pedalControllers);
                TargetPedalCombo.ItemsSource = targets;
                // BuildPlan enumerated its own targets, so the plan's instance
                // is never one of these — match on (controller, axis) instead.
                var resolved = plan.ResolvedTarget;
                if (resolved != null)
                {
                    TargetPedalCombo.SelectedItem = targets.FirstOrDefault(t =>
                        ReferenceEquals(t.Controller, resolved.Controller) && t.AxisIndex == resolved.AxisIndex);
                }
                // No resolved target (no attached pedal carries the subject
                // role) leaves the combo unselected so the user picks one.
            }
            finally { _suppressTargetChange = false; }
        }

        private void TargetPedalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTargetChange) return;
            if (SelectedPreset == null) return;
            if (!(TargetPedalCombo.SelectedItem is MBoosterImportTarget target)) return;

            Plan = PitHousePedalsMapper.BuildPlan(SelectedPreset, _pedalControllers, target);
            LogPlan(SelectedPreset, Plan);
            ShowConfirmPanel();
        }

        private void ShowConfirmPanel()
        {
            if (SelectedPreset == null || Plan == null) return;

            string profileName = _plugin?.Settings?.ProfileStore?.CurrentProfile?.Name
                                 ?? "(unknown)";

            // Header card key/value rows.
            ConfirmPresetText.Text = SelectedPreset.Name;
            ConfirmProfileText.Text = profileName;

            // Pedals only: which role the preset configures, and where it lands.
            // A PitHouse preset carries all three role sections but fills in
            // only its own — the other two are the device-wide snapshot, so the
            // header has to say which one is actually being imported.
            bool hasSubject = !string.IsNullOrEmpty(Plan.SubjectRoleDisplay);
            SubjectRoleLabel.Visibility = hasSubject ? Visibility.Visible : Visibility.Collapsed;
            SubjectRoleText.Visibility = hasSubject ? Visibility.Visible : Visibility.Collapsed;
            SubjectRoleText.Text = Plan.SubjectRoleDisplay ?? "";

            // Retargeting only means something when one section drives one
            // pedal — a calibration-only preset already covers every role.
            bool canRetarget = hasSubject && !Plan.AutoMatchedPerRole
                               && TargetPedalCombo.Items.Count > 0;
            ApplyToLabel.Visibility = canRetarget ? Visibility.Visible : Visibility.Collapsed;
            TargetPedalCombo.Visibility = canRetarget ? Visibility.Visible : Visibility.Collapsed;

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
                FooterStatusText.Text = Strings.Import_Footer_NoMappable;
            else if (changedCount == 0)
                FooterStatusText.Text = string.Format(Strings.Import_Footer_AllMatch, totalMapped);
            else
                FooterStatusText.Text = string.Format(Strings.Import_Footer_WillChange, changedCount, totalMapped);

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
            _pedalControllers = Array.Empty<MBoosterDeviceController>();
            PopulatePedalTargets(null);
            ConfirmPanel.Visibility = Visibility.Collapsed;
            PickerPanel.Visibility = Visibility.Visible;

            // Restore footer to picker phase.
            NextButton.Visibility = Visibility.Visible;
            ApplyButton.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            FooterStatusText.Text = "";
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (Plan == null) return;

            string applied = SelectedPreset?.Name ?? "";

            // Hand the plan to the host, which runs the mutate/push/refresh path.
            ApplyRequested?.Invoke(Plan);

            // Return to the picker phase and surface a confirmation caption.
            // Back_Click clears SelectedPreset/Plan and resets the footer, so
            // capture the name first and set the caption afterwards.
            Back_Click(sender, e);
            RefreshLists();
            FooterStatusText.Text = $"applied “{applied}” to the active profile";
        }
    }
}
