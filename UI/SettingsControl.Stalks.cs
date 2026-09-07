using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using MozaPlugin.Settings;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Stalks settings tab: mode selector (Button box / Truck sim), the interactive
    /// 28-button key-map editor, and the wiper/light stage-sync config.
    /// </summary>
    public partial class SettingsControl
    {
        private readonly ObservableCollection<StalkRow> _stalkRows = new ObservableCollection<StalkRow>();
        private List<StalkOptionItem>? _stalkOptions;
        private bool _stalksWired;
        private MozaHidReader? _stalksReader;

        private void RefreshStalksTab()
        {
            bool connected = _data?.IsStalksConnected ?? false;

            // Demo mode: no stalks attached, but the tab is force-shown via
            // Show-all-tabs. The whole tab is settings-driven — the button map,
            // mode, and stage counts all come from StalksTruckSim settings, not
            // hardware — so wire it up so the mapping list binds and the truck-sim
            // panel seeds. Without this the early return below leaves the list
            // unbound and the panel collapsed. The tab's own visibility is owned
            // by ApplyShowAllTabs; the status dot stays on its "searching" default
            // and the live-press highlight stays dark, both correct with no device.
            if (!connected && _plugin?.Settings?.ShowAllTabs == true)
            {
                if (!_stalksWired) WireStalksTab();
                return;
            }

            StalksTab.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            if (!connected) return;

            if (!_stalksWired) WireStalksTab();
        }

        private void WireStalksTab()
        {
            _stalksWired = true;
            BuildStalkRows();
            StalksButtonList.ItemsSource = _stalkRows;

            // Detach from a reader replaced by a reconnect before re-wiring.
            if (_stalksReader != null)
                _stalksReader.StalksButtonChanged -= OnStalksButtonChangedUi;
            _stalksReader = _plugin?.HidReader;
            if (_stalksReader != null)
                _stalksReader.StalksButtonChanged += OnStalksButtonChangedUi;

            SeedStalksControls();
        }

        private void BuildStalkRows()
        {
            var cfg = _plugin.Settings.StalksTruckSim ?? new StalkTruckSimSettings();
            _stalkOptions = StalkRow.BuildOptions(cfg.WiperStageCount, cfg.LightStageCount);
            _stalkRows.Clear();
            int max = Math.Min(MozaData.MaxStalksButtons, 28);
            for (int i = 0; i < max; i++)
            {
                cfg.ButtonActions.TryGetValue(i, out var act);
                _stalkRows.Add(new StalkRow(i, act, _stalkOptions, OnStalkRowChanged));
            }
        }

        private void SeedStalksControls()
        {
            using (_suppressor.Begin())
            {
                var s = _plugin.Settings;
                StalksModeCombo.SelectedIndex = s.StalksMode == StalkMode.TruckSim ? 1 : 0;
                StalksTruckSimPanel.Visibility =
                    s.StalksMode == StalkMode.TruckSim ? Visibility.Visible : Visibility.Collapsed;

                var cfg = s.StalksTruckSim;
                StalksWiperStageSlider.Value = cfg.WiperStageCount;
                StalksWiperStageValue.Text = cfg.WiperStageCount.ToString();
                StalksLightStageSlider.Value = cfg.LightStageCount;
                StalksLightStageValue.Text = cfg.LightStageCount.ToString();
                StalksWiperWrapToggle.IsChecked = cfg.WiperForwardWraps;
                StalksIndMinBlinkSlider.Value = cfg.IndicatorMinBlinkSeconds;
                StalksIndMinBlinkValue.Text = cfg.IndicatorMinBlinkSeconds.ToString();

                StalksWiperFwdKeyBox.KeyCode = KeyCodes.Parse(cfg.WiperForwardKey);
                StalksWiperBackKeyBox.KeyCode = KeyCodes.Parse(cfg.WiperBackKey);
                StalksLightCycleKeyBox.KeyCode = KeyCodes.Parse(cfg.LightCycleKey);
                StalksIndLeftKeyBox.KeyCode = KeyCodes.Parse(cfg.IndicatorLeftKey);
                StalksIndRightKeyBox.KeyCode = KeyCodes.Parse(cfg.IndicatorRightKey);
            }
        }

        private void StalksGameKey_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (!(sender is MozaControls.KeyCaptureBox box)) return;
            var cfg = _plugin.Settings.StalksTruckSim;
            string key = KeyCodes.Encode((ushort)box.KeyCode);
            if (ReferenceEquals(box, StalksWiperFwdKeyBox)) cfg.WiperForwardKey = key;
            else if (ReferenceEquals(box, StalksWiperBackKeyBox)) cfg.WiperBackKey = key;
            else if (ReferenceEquals(box, StalksLightCycleKeyBox)) cfg.LightCycleKey = key;
            else if (ReferenceEquals(box, StalksIndLeftKeyBox)) cfg.IndicatorLeftKey = key;
            else if (ReferenceEquals(box, StalksIndRightKeyBox)) cfg.IndicatorRightKey = key;
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        private void OnStalkRowChanged(StalkRow row)
        {
            if (_suppressEvents) return;
            var cfg = _plugin.Settings.StalksTruckSim;
            var action = row.ToAction();
            if (action.Kind == StalkActionKind.None)
                cfg.ButtonActions.Remove(row.ButtonIndex);
            else
                cfg.ButtonActions[row.ButtonIndex] = action;
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        private void StalksModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var mode = StalksModeCombo.SelectedIndex == 1 ? StalkMode.TruckSim : StalkMode.ButtonBox;
            _plugin.Settings.StalksMode = mode;
            StalksTruckSimPanel.Visibility =
                mode == StalkMode.TruckSim ? Visibility.Visible : Visibility.Collapsed;
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        private void StalksWiperStageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int n = (int)Math.Round(e.NewValue);
            StalksWiperStageValue.Text = n.ToString();
            _plugin.Settings.StalksTruckSim.WiperStageCount = n;
            RebuildStalkOptionsPreservingSelection();
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        private void StalksLightStageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int n = (int)Math.Round(e.NewValue);
            StalksLightStageValue.Text = n.ToString();
            _plugin.Settings.StalksTruckSim.LightStageCount = n;
            RebuildStalkOptionsPreservingSelection();
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        private void StalksIndMinBlinkSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int n = (int)Math.Round(e.NewValue);
            StalksIndMinBlinkValue.Text = n.ToString();
            _plugin.Settings.StalksTruckSim.IndicatorMinBlinkSeconds = n;
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        private void StalksWiperWrapToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.StalksTruckSim.WiperForwardWraps = StalksWiperWrapToggle.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        private void StalksResyncButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.StalksController?.ResyncWipers();
        }

        private void StalksLoadDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.StalksTruckSim.ApplyEts2Defaults();
            BuildStalkRows();       // reflect the new map + stage counts
            SeedStalksControls();   // reseed sliders / wrap toggle
            _plugin.SaveSettings();
            _plugin.ApplyStalksSettings();
        }

        // Rebuild the shared option list after a stage-count change; re-create the
        // rows (preserving each button's current assignment where still valid).
        private void RebuildStalkOptionsPreservingSelection()
        {
            BuildStalkRows();
        }

        // HID read thread → marshal to the UI thread for the live-press highlight.
        private void OnStalksButtonChangedUi(int index, bool pressed)
        {
            var disp = Dispatcher;
            if (disp == null) return;
            try
            {
                disp.BeginInvoke((Action)(() =>
                {
                    if (index >= 0 && index < _stalkRows.Count)
                        _stalkRows[index].IsPressed = pressed;
                    if (pressed && index >= 0)
                        StalksLastPressedLabel.Text = string.Format(Strings.Label_StalksLastPressed, index + 1);
                }));
            }
            catch { }
        }

        private void UnsubscribeStalks()
        {
            try
            {
                if (_stalksReader != null)
                    _stalksReader.StalksButtonChanged -= OnStalksButtonChangedUi;
            }
            catch { }
            _stalksReader = null;
            // Allow a subsequent Loaded to re-wire (re-subscribe + rebuild rows).
            _stalksWired = false;
        }
    }
}
