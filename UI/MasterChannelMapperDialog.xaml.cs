using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.Devices.Ui;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry.Dashboard;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Master channel mapper — edits the DEFAULT mapping for every channel declared
    /// in <c>Data/Telemetry.json</c>. These defaults apply to every dashboard on every
    /// wheel/dash surface; a per-dashboard override set from a device page's
    /// channel-mapping list still wins over them.
    ///
    /// Resolution order per channel URL:
    ///   per-dashboard override  >  THIS  >  Telemetry.json simhub_property
    ///                                       >  StringChannelDefaults
    ///
    /// <para>The set lives on <see cref="MozaChannelDefaultsProfile"/>, in a profile
    /// store of its OWN (<see cref="MozaChannelDefaultsStore"/>) — unrelated to the
    /// device profiles the Options tab manages. The <c>ProfileList</c> at the top is
    /// SimHub's control driving that store, so these sets are created, named, cloned,
    /// switched and exported here, and picking one has no effect on the device profile
    /// (no ApplyProfile, no hardware writes). SimHub still switches the set for the
    /// running game on its own; either source raises <c>CurrentProfileChanged</c> and
    /// rebuilds the rows.</para>
    ///
    /// Rows reuse <see cref="ChannelMappingRow"/>, so the pencil (simple property
    /// list) and ƒ(x) (SimHub NCalc / js: formula dialog) editors behave exactly as
    /// they do on the device page; the FSR1/CM1 stepper surface is not applicable
    /// here and is omitted.
    /// </summary>
    public partial class MasterChannelMapperDialog : Window
    {
        private readonly MozaPlugin _plugin;

        // Master list (every eligible channel) + the filtered view bound to the
        // ItemsControl. Filtering swaps the observable collection's contents; the row
        // objects themselves are stable so an open inline editor survives a re-filter.
        private readonly List<ChannelMappingRow> _allRows = new List<ChannelMappingRow>();
        private readonly ObservableCollection<ChannelMappingRow> _rows
            = new ObservableCollection<ChannelMappingRow>();

        // A default change rebinds the live profiles (BuildProfileFromCatalog + the
        // per-dashboard overlay). Debounced so editing several rows in a row costs one
        // rebuild instead of one per commit. Flushed on close.
        private static readonly TimeSpan ReResolveDebounce = TimeSpan.FromMilliseconds(400);
        private DispatcherTimer? _reResolveDebounce;

        // The control that opened us — still live in SimHub's visual tree, so it is
        // the ground truth for what colour SimHub paints behind the plugin page.
        private readonly DependencyObject? _backdropSource;

        public MasterChannelMapperDialog(MozaPlugin plugin, DependencyObject? backdropSource = null)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _backdropSource = backdropSource;
            InitializeComponent();
            ChannelList.ItemsSource = _rows;

            // ProfileList wires its inner combobox to whatever DataContext it gets —
            // here the channel-defaults store, NOT the device-profile store the
            // Options tab drives.
            _profileStore = _plugin.Settings?.ChannelDefaultsStore;
            if (_profileStore != null)
                ProfileSelector.DataContext = _profileStore;

            BuildRows();
            ApplyFilter();

            // Subscribe only after the first build: binding the combo above can settle
            // its SelectedItem back onto the store, and a CurrentProfileChanged landing
            // mid-construction would run RebuildRows against a half-built list — then
            // BuildRows here would append a second copy of all ~450 rows. Follows the
            // active profile from both directions afterwards: this combo, and a game
            // switch behind our back.
            if (_profileStore != null)
                _profileStore.CurrentProfileChanged += OnCurrentProfileChanged;

            // Owner isn't assigned until after the ctor returns, so resolve the
            // backdrop once the window is realized but before it first renders.
            SourceInitialized += (_, __) => ApplyHostBackdrop();
            Closed += OnClosed;
        }

        // ── Active profile ─────────────────────────────────────────────────

        // Held so the Closed handler detaches from the SAME store instance we
        // subscribed on (ClearSettings can swap the store while we're open).
        private readonly MozaChannelDefaultsStore? _profileStore;

        /// <summary>The channel-defaults profile the rows are editing.</summary>
        private MozaChannelDefaultsProfile? ActiveProfile => _profileStore?.CurrentProfile;

        /// <summary>Rebuild the rows against the newly-active profile. Fires from this
        /// dialog's own selector AND from SimHub switching the set for a running game,
        /// on whichever thread it raised the event, so it marshals to the dispatcher.
        /// ProfileCoordinator subscribed at Init and so runs first — the new set is
        /// already published by the time we repaint.</summary>
        private void OnCurrentProfileChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RebuildRows));
                return;
            }
            RebuildRows();
        }

        /// <summary>Re-seed every row from the active profile. Any pending edit belongs
        /// to the profile we're leaving, so it is cancelled rather than carried over.</summary>
        private void RebuildRows()
        {
            // The edit itself was already persisted synchronously by SetProfileDefault;
            // only the re-resolve is pending, and ApplyProfile has just done one for the
            // profile we're switching TO. Drop the timer rather than firing a redundant
            // pass over the senders.
            _reResolveDebounce?.Stop();

            foreach (var r in _allRows)
            {
                r.PropertyChanged -= OnRowPropertyChanged;
                if (r.IsEditing) r.CancelEdit();
            }
            _allRows.Clear();
            _rows.Clear();
            BuildRows();
            ApplyFilter();
        }

        // ── Host backdrop ──────────────────────────────────────────────────

        /// <summary>
        /// Repaint the window with the opaque colour SimHub itself is painting behind
        /// the plugin page.
        ///
        /// Every <c>Bg*</c> token in MozaTheme is a TRANSLUCENT overlay — it tints
        /// whatever the host already drew. A Window is its own HWND with nothing
        /// underneath, so those tokens composite against the system brush instead and
        /// the window never matches. Hardcoding a hex only approximates one SimHub
        /// theme and drifts when SimHub restyles (it ships AvalonDock/VS2013 theming),
        /// so instead walk up from the control that opened us to the nearest ancestor
        /// that actually paints something opaque and reuse that exact brush. The XAML's
        /// SurfaceOpaqueBrush stays as the fallback for when nothing in the chain is
        /// opaque.
        /// </summary>
        private void ApplyHostBackdrop()
        {
            try
            {
                var found = FindOpaqueBackground(_backdropSource) ?? FindOpaqueBackground(Owner);
                if (found.HasValue)
                {
                    Background = found.Value.Brush;
                    MozaLog.Debug(
                        $"[AZOM] master mapper backdrop: {found.Value.Brush.Color} from {found.Value.Source}");
                }
                else
                {
                    MozaLog.Debug(
                        "[AZOM] master mapper backdrop: no opaque ancestor found, "
                        + "keeping the SurfaceOpaque theme fallback");
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] master mapper backdrop resolve failed: " + ex.Message);
            }
        }

        private static (SolidColorBrush Brush, string Source)? FindOpaqueBackground(DependencyObject? node)
        {
            // Bounded so a malformed tree can't spin; real depth is well under this.
            for (int guard = 0; node != null && guard < 200; guard++)
            {
                Brush? b = node switch
                {
                    Control c => c.Background,
                    Panel p => p.Background,
                    Border bd => bd.Background,
                    _ => null,
                };
                if (b is SolidColorBrush s && s.Color.A == 0xFF)
                    return (s, node.GetType().Name);

                // Visual-tree first; fall back to the logical parent for nodes the
                // visual tree doesn't chain (and never call VisualTreeHelper on a
                // non-Visual — it throws).
                DependencyObject? parent = null;
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                    parent = VisualTreeHelper.GetParent(node);
                node = parent ?? LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        // ── Row construction ───────────────────────────────────────────────

        private void BuildRows()
        {
            // Idempotent: RebuildRows re-enters this on every profile switch, and a
            // stale row surviving here would show the previous game's mapping.
            _allRows.Clear();

            var props = _plugin.GetAllSimHubPropertyNames();
            var engine = _plugin.ChannelFormulaEngine;

            // Normalise the stored overrides once — Newtonsoft hands the settings dict
            // back with the default (ordinal) comparer, so a case difference in a URL
            // would otherwise read as "not overridden".
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stored = ActiveProfile?.Mappings;
            if (stored != null)
                foreach (var kv in stored)
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        overrides[kv.Key] = kv.Value.Trim();

            foreach (var entry in _plugin.DashProfileStore.EnumerateTelemetryChannels())
            {
                // Plugin-locked channels (@internal/ sentinels) resolve to a computed
                // value, radar/track-map channels are driven by the plugin's own
                // position pipeline, and the whole preset/ namespace is dropped from
                // every subscription (the wheel fills those itself). None is
                // user-mappable, so none belongs in the defaults editor.
                if (DashboardProfileStore.IsInternalChannel(entry.DefaultProperty)) continue;
                if (DashboardProfileStore.IsRadarTrackMapChannel(entry.Url)) continue;
                if (DashboardProfileStore.IsWheelInternalPresetChannel(entry.Url)) continue;

                string effective = overrides.TryGetValue(entry.Url, out var ov)
                    ? ov : entry.DefaultProperty;

                _allRows.Add(new ChannelMappingRow
                {
                    AllProperties = props,
                    Engine = engine,
                    Name = entry.Name,
                    Url = entry.Url,
                    PackageLevel = entry.PackageLevel,
                    Compression = entry.Compression,
                    DefaultProperty = entry.DefaultProperty,
                    SimHubProperty = effective,
                });
            }

            // Subscribe after seeding so the constructor's SimHubProperty assignment
            // doesn't persist a redundant entry for every channel.
            foreach (var r in _allRows) r.PropertyChanged += OnRowPropertyChanged;
        }

        // ── Filtering ──────────────────────────────────────────────────────

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            string query = FilterBox?.Text ?? "";
            bool noFilter = query.Length == 0;

            _rows.Clear();
            int overridden = 0;
            foreach (var r in _allRows)
            {
                if (r.IsOverridden) overridden++;
                if (!noFilter && !Matches(r, query)) continue;
                _rows.Add(r);
            }

            OverriddenCountText.Text = string.Format(
                Strings.Status_OverriddenCountFormat, overridden, _allRows.Count);
        }

        private static bool Matches(ChannelMappingRow row, string query)
            => row.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
               || row.Url.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
               || row.SimHubProperty.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        // ── Persistence ────────────────────────────────────────────────────

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)) return;
            if (sender is not ChannelMappingRow row || string.IsNullOrEmpty(row.Url)) return;

            // Setting a row back to its Telemetry.json value prunes the entry rather
            // than persisting a redundant override.
            string value = string.Equals(row.SimHubProperty, row.DefaultProperty, StringComparison.Ordinal)
                ? ""
                : row.SimHubProperty;
            _plugin.ChannelMapping.SetProfileDefault(row.Url, value);
            ScheduleReResolve();
            RefreshOverriddenCount();
        }

        private void RefreshOverriddenCount()
        {
            int overridden = 0;
            foreach (var r in _allRows) if (r.IsOverridden) overridden++;
            OverriddenCountText.Text = string.Format(
                Strings.Status_OverriddenCountFormat, overridden, _allRows.Count);
        }

        private void ScheduleReResolve()
        {
            if (_reResolveDebounce == null)
            {
                _reResolveDebounce = new DispatcherTimer { Interval = ReResolveDebounce };
                _reResolveDebounce.Tick += (_, __) => FlushReResolve();
            }
            _reResolveDebounce.Stop();
            _reResolveDebounce.Start();
        }

        private void FlushReResolve()
        {
            _reResolveDebounce?.Stop();
            try { _plugin.ChannelMapping.ReResolveAll(); }
            catch (Exception ex) { MozaLog.Warn("[AZOM] master mapper re-resolve failed: " + ex.Message); }
        }

        // ── Row editors (mirrors DashboardManagementControl) ────────────────

        private void EditMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            // Only one inline editor open at a time to keep the list scannable.
            foreach (var r in _allRows)
                if (!ReferenceEquals(r, row) && r.IsEditing) r.CancelEdit();
            row.BeginEdit();
            Dispatcher.BeginInvoke(new Action(() => FocusInlineFilter(row)), DispatcherPriority.Render);
        }

        private void CommitMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            row.CommitEdit();
        }

        private void CancelMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            row.CancelEdit();
        }

        // Advanced edit: SimHub's own BindingEditor against the shared engine and a
        // throwaway copy of the row's formula; copied back through the row on OK so it
        // serializes into SimHubProperty and fires the persist listener.
        private async void AdvancedEditMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            var engine = row.Engine;
            if (engine == null) return;   // engine failed to construct — ƒ(x) is a no-op

            var src = row.Expression;
            var working = new ExpressionValue
            {
                UseJavascript = src.UseJavascript,
                Expression = src.Expression,
                PreExpression = src.PreExpression,
            };

            var data = new DashboardBindingData
            {
                Formula = working,
                Mode = string.IsNullOrWhiteSpace(working.Expression) ? BindingMode.None : BindingMode.Formula,
                TargetPropertyName = row.Name,
                TargetType = typeof(double),
            };

            try
            {
                var editor = new BindingEditor(engine) { DataContext = data };
                var result = await editor.ShowDialogWindowAsync(this);
                if ((int)result != 1) return; // not OK
                if (data.Mode == BindingMode.Formula)
                    row.ApplyEditedFormula(data.Formula?.Expression, data.Formula?.UseJavascript ?? false);
                else
                    row.ApplyEditedFormula("", false);
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] master mapper formula editor failed: " + ex.Message);
            }
        }

        private void RevertMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            if (row.IsEditing) row.CancelEdit();
            // Fires OnRowPropertyChanged, which prunes the stored entry and refreshes
            // the count. No re-filter: IsOverridden's PropertyChanged already hides the
            // revert button + default column, and rebuilding the list here would yank
            // the scroll position back to the top under the user's click.
            row.SimHubProperty = row.DefaultProperty;
        }

        // Resets the ACTIVE profile's defaults only — other games keep theirs.
        private void ResetAll_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ChannelMapping.ClearProfileDefaults();
            // Re-seed the rows from their pristine defaults without re-firing the
            // per-row persist path 450 times — the store is already cleared.
            foreach (var r in _allRows)
            {
                r.PropertyChanged -= OnRowPropertyChanged;
                if (r.IsEditing) r.CancelEdit();
                r.SimHubProperty = r.DefaultProperty;
                r.PropertyChanged += OnRowPropertyChanged;
            }
            ScheduleReResolve();
            ApplyFilter();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_profileStore != null)
                _profileStore.CurrentProfileChanged -= OnCurrentProfileChanged;
            foreach (var r in _allRows) r.PropertyChanged -= OnRowPropertyChanged;
            // Land any edit made inside the debounce window before the dialog goes away.
            if (_reResolveDebounce != null && _reResolveDebounce.IsEnabled) FlushReResolve();
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private void FocusInlineFilter(ChannelMappingRow row)
        {
            if (ChannelList == null) return;
            var container = ChannelList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
            if (container == null) return;
            var tb = FindDescendant<TextBox>(container, "EditFilterBox");
            tb?.Focus();
            tb?.SelectAll();
        }

        private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T fe && fe.Name == name) return fe;
                var nested = FindDescendant<T>(child, name);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
