using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MozaPlugin.Devices;
using MozaPlugin.Resources;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.UI;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;
using static MozaPlugin.UI.UiHelpers;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;
using CaptureRedactor = MozaPlugin.Diagnostics.CaptureRedactor;

namespace MozaPlugin
{
    public partial class SettingsControl : UserControl
    {

        // =====================================================================
        // mBooster tab — multi-device, multi-pedal. MBoosterDeviceRowsList (one
        // row per connected PEDAL, not per physical connection — see
        // MBoosterDeviceRow.cs) selects the active device+pedal; the settings
        // panel below populates from that selection's per-device/per-pedal
        // entry in MozaProfile.MBoosterSettings (lazily created).
        // =====================================================================

        private string? _mboosterSelectedIdentity;
        private bool _mboosterUiSeeded;
        // Separate one-shot gate for the no-device demo seed (Show-all-tabs).
        // Seeded once on entry so a demo drag isn't reset every 500 ms tick;
        // cleared when a real device appears so demo defaults re-apply if it
        // later disconnects.
        private bool _mboosterDemoSeeded;

        // One row per connected PEDAL (HID axis) across every connected
        // mBooster — see MBoosterDeviceRow.cs. Replaces the old trio of
        // "Device combo" + "per-axis Pedal Roles panel" + "Configure pedal
        // combo", which all overlapped the same selection/role concerns.
        // Rebuilt only when the signature (identity+axisCount+connected-mask
        // per device) changes, so a mid-click isn't disrupted by the 500ms
        // refresh tick; Role/IsSelected on existing rows are still resynced
        // every refresh (cheap — just property pushes, not a visual rebuild)
        // so an out-of-band profile switch or role edit is reflected promptly.
        private readonly System.Collections.ObjectModel.ObservableCollection<MBoosterDeviceRow> _mboosterDeviceRows =
            new System.Collections.ObjectModel.ObservableCollection<MBoosterDeviceRow>();
        private string? _mboosterDeviceRowsSignature;

        // Active-profile name + device identity the tab was last seeded for.
        // mBooster settings are per-profile (GetOrCreateMBoosterSettings reads
        // ProfileStore.CurrentProfile) and per-device; the seed-once gate below
        // must re-fire when either changes, or the tab keeps showing — and
        // edits keep writing against — the previously-seeded profile/device.
        private string? _mboosterSeededProfileName;
        private string? _mboosterSeededIdentity;

        // Custom Effects (Experimental) — dynamic per-device list, rebuilt
        // (not incrementally synced) on every seed/device-switch. See
        // PopulateMBoosterCustomEffectsList.
        private readonly System.Collections.ObjectModel.ObservableCollection<MBoosterCustomEffectRow> _mboosterCustomEffectRows =
            new System.Collections.ObjectModel.ObservableCollection<MBoosterCustomEffectRow>();

        private void RefreshMBoosterTab()
        {
            if (_plugin == null) { MBoosterTab.Visibility = Visibility.Collapsed; return; }
            var registry = _plugin.MBoosterRegistry;
            if (registry == null) { MBoosterTab.Visibility = Visibility.Collapsed; return; }
            var devices = registry.Devices;
            if (devices.Count == 0)
            {
                _mboosterUiSeeded = false;
                if (_plugin.Settings?.ShowAllTabs == true)
                {
                    // Demo mode: no mBooster is connected, but the tab was force-
                    // shown via Show-all-tabs. Surface the full per-pedal config UI
                    // so it can be demonstrated. With no device to drive, every
                    // control handler no-ops (each guards on a null effect target /
                    // null controller), so nothing reaches hardware. The tab's own
                    // visibility is owned by ApplyShowAllTabs.
                    ShowMBoosterDemoPanels();
                    return;
                }
                MBoosterTab.Visibility = Visibility.Collapsed;
                return;
            }
            MBoosterTab.Visibility = Visibility.Visible;
            // A real device is present — drop the demo-seed latch so demo
            // defaults re-apply if it later disconnects with the tab still shown.
            _mboosterDemoSeeded = false;

            // Rebuild the device/pedal row list if any device's identity, axis
            // count, or connectivity changed. One row per connected PEDAL — a
            // chained mBooster hosting several pedals on one connection gets
            // one row per detected axis, not one row for the whole connection
            // (see MBoosterDeviceRow.cs — this replaces the old trio of Device
            // combo + per-axis Pedal Roles panel + Configure-pedal combo).
            var sigBuilder = new System.Text.StringBuilder();
            foreach (var c in devices)
            {
                int sigAxisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                sigBuilder.Append(c.Identity).Append('|').Append(sigAxisCount).Append('|');
                var sigConnected = c.ConnectedAxes;
                if (sigConnected != null)
                    for (int k = 0; k < sigConnected.Length; k++) sigBuilder.Append(sigConnected[k] ? '1' : '0');
                sigBuilder.Append(';');
            }
            string rowsSignature = sigBuilder.ToString();
            bool rowsStale = !string.Equals(rowsSignature, _mboosterDeviceRowsSignature, StringComparison.Ordinal);

            using (_suppressor.Begin())
            {
                // Keep the existing (identity, axis) selection if it's still
                // valid (device still connected, axis still in range and
                // connected); otherwise fall back to axis 0 of the first device.
                bool selectionValid = false;
                bool selectedDeviceStillPresent = false;
                int sameDeviceRetargetAxis = 0;
                foreach (var c in devices)
                {
                    if (!string.Equals(c.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)) continue;
                    selectedDeviceStillPresent = true;
                    int axisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                    var connected = c.ConnectedAxes;
                    bool selAxisKnownConnected = connected != null && _mboosterEffectPedalIndex < connected.Length
                        ? connected[_mboosterEffectPedalIndex]
                        : _mboosterEffectPedalIndex == 0;
                    selectionValid = _mboosterEffectPedalIndex >= 0 && _mboosterEffectPedalIndex < axisCount && selAxisKnownConnected;
                    if (!selectionValid && connected != null)
                    {
                        // The device's real wired axis just became known (the
                        // "PD Linked" diagnostic landed) and it isn't the axis-0
                        // placeholder we'd optimistically selected before that —
                        // a standalone unit's sole pedal commonly reports on a
                        // non-zero axis regardless of chain status (see
                        // MBoosterDeviceController's ConnectedAxes doc comment).
                        // Follow the SAME physical device onto whichever axis is
                        // now known-connected instead of falling through to
                        // devices[0] below, which would silently reassign the
                        // user's selection to a different device a couple
                        // seconds after they picked one at startup.
                        for (int axis = 0; axis < connected.Length; axis++)
                        {
                            if (connected[axis]) { sameDeviceRetargetAxis = axis; break; }
                        }
                    }
                    break;
                }
                if (!selectionValid)
                {
                    if (selectedDeviceStillPresent)
                        _mboosterEffectPedalIndex = sameDeviceRetargetAxis;
                    else
                    {
                        _mboosterSelectedIdentity = devices[0].Identity;
                        _mboosterEffectPedalIndex = 0;
                    }
                }

                if (rowsStale)
                {
                    _mboosterDeviceRowsSignature = rowsSignature;
                    _mboosterDeviceRows.Clear();
                    foreach (var c in devices)
                    {
                        var rowSettings = _plugin.GetOrCreateMBoosterSettings(c.Identity);
                        int axisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                        string deviceLabel = BuildMBoosterComboLabel(c);
                        var connectedAxes = c.ConnectedAxisIndices();

                        // Only label rows "— Pedal N" when this device genuinely
                        // hosts more than one wired pedal — not just because its
                        // HID interface happens to expose 3 axes.
                        bool multiplePedals = connectedAxes.Count > 1;
                        int shown = 0;
                        foreach (int axis in connectedAxes)
                        {
                            ++shown;
                            string label = multiplePedals
                                ? $"{deviceLabel} — {string.Format(Strings.Label_PedalAxis, shown)}"
                                : deviceLabel;
                            // Resolve against the CONNECTED axis count, not the
                            // raw HID axis count — a chain-capable hub exposes
                            // all 3 GenericDesktop axes even with only one pedal
                            // plugged in, so raw axisCount would silently override
                            // that pedal's own Role with the axis-order default.
                            var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(rowSettings, axis, connectedAxes.Count);
                            bool isSelected = string.Equals(c.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                                && axis == _mboosterEffectPedalIndex;
                            _mboosterDeviceRows.Add(new MBoosterDeviceRow(c.Identity, axis, label, isSelected, role,
                                rowSettings.DisplayName, OnMBoosterDeviceRowRoleChanged, OnMBoosterDeviceRowDisplayNameChanged));
                        }
                    }
                    MBoosterDeviceRowsList.ItemsSource = _mboosterDeviceRows;
                }
                else
                {
                    foreach (var row in _mboosterDeviceRows)
                    {
                        var rowController = registry.FindByIdentity(row.Identity);
                        var rowSettings = _plugin.GetOrCreateMBoosterSettings(row.Identity);
                        int axisCount = rowController != null && rowController.AxisCount > 0 ? rowController.AxisCount : 1;
                        if (axisCount > MBoosterDeviceController.MaxAxes) axisCount = MBoosterDeviceController.MaxAxes;
                        int connectedAxisCount = 0;
                        if (rowController != null)
                            for (int axis = 0; axis < axisCount; axis++)
                                if (rowController.IsAxisConnected(axis)) connectedAxisCount++;
                        row.RoleIndex = (int)global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(rowSettings, row.AxisIndex, connectedAxisCount);
                        row.IsSelected = string.Equals(row.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                            && row.AxisIndex == _mboosterEffectPedalIndex;
                        // DisplayName is per-profile like every other mBooster
                        // setting, so a profile switch can change it without
                        // changing rowsSignature (which only tracks physical
                        // connectivity). The setter no-ops unless the value
                        // actually differs, and OnMBoosterDeviceRowDisplayNameChanged
                        // recomputes Label when it fires.
                        row.DisplayName = rowSettings.DisplayName;
                    }
                }
            }

            var selected = registry.FindByIdentity(_mboosterSelectedIdentity ?? "");
            if (selected == null)
            {
                MBoosterDevicePanel.Visibility = Visibility.Collapsed;
                return;
            }

            MBoosterDevicePanel.Visibility = Visibility.Visible;

            // Live position marker (on the curve editors) is updated at
            // 30 Hz from UpdateHidInputDisplays (UpdateMBoosterCurveMarkers)
            // instead of here — this 500ms pass felt sluggish for direct
            // pedal feedback.

            // Re-seed when the active profile or the selected device changed
            // since the last seed — otherwise the gate below keeps the
            // previously-seeded values on screen while edits write to the
            // now-current profile/device (mBooster settings are per-profile,
            // per-device).
            var currentProfileName = _plugin?.Settings?.ProfileStore?.CurrentProfile?.Name;
            if (!string.Equals(currentProfileName, _mboosterSeededProfileName, StringComparison.Ordinal)
                || !string.Equals(selected.Identity, _mboosterSeededIdentity, StringComparison.OrdinalIgnoreCase))
                _mboosterUiSeeded = false;

            if (_mboosterUiSeeded) return;
            // Seed slider/checkbox values from the profile entry. _plugin is
            // never null past Init (the constructor stores it); guard anyway.
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(selected.Identity);
            using (_suppressor.Begin())
            {
                // Role is seeded per-row by the device rows block above (each
                // MBoosterDeviceRow owns its own Role), not here.
                // _mboosterEffectPedalIndex is resolved/validated by the device
                // rows block above too — here we just seed whichever pedal it
                // settled on. (Test toggles are never persisted;
                // SeedMBoosterEffectControls always clears them.)
                SeedMBoosterEffectControls(PeekMBoosterEffectTarget());
                UpdateMBoosterEffectPassiveState();
                UpdateMBoosterConfigVisibilityForRole();
                MBoosterBrakeFadeEnable.IsChecked = s.BrakeFade?.Enabled ?? false;
                MBoosterBrakeFadeOnsetSlider.Value = s.BrakeFade?.BrakeFadeOnsetC ?? 550;
                SetValueText(MBoosterBrakeFadeOnsetValue, MBoosterBrakeFadeOnsetSlider.Value.ToString("F0"));
                // Never persisted — always starts off for a freshly-shown tab.
                MBoosterBrakeFadeTestToggle.IsChecked = false;
                SeedMBoosterConfigControls(PeekMBoosterEffectTarget());
            }
            PopulateMBoosterCustomEffectsList(PeekMBoosterEffectTarget());
            _mboosterUiSeeded = true;
            _mboosterSeededProfileName = currentProfileName;
            _mboosterSeededIdentity = selected.Identity;
        }

        /// <summary>Click handler for a pedal row's label Button (see
        /// MBoosterDeviceRow.cs's doc comment for why this is a plain Click
        /// event rather than RadioButton+GroupName+TwoWay binding). The row
        /// itself is the sender's DataContext, courtesy of the ItemsControl's
        /// DataTemplate.</summary>
        private void MBoosterDeviceRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not MBoosterDeviceRow row) return;
            OnMBoosterDeviceRowSelected(row.Identity, row.AxisIndex);
        }

        /// <summary>Row selection logic — fires when a pedal row's label Button
        /// is clicked. Selects BOTH the physical device AND the specific pedal
        /// (axis) on it in one step, replacing what the old MBoosterDeviceCombo_Changed
        /// (device only) + MBoosterEffectPedalCombo_SelectionChanged (pedal only)
        /// used to do separately.</summary>
        private void OnMBoosterDeviceRowSelected(string identity, int axisIndex)
        {
            if (_suppressEvents) return;
            if (string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                && axisIndex == _mboosterEffectPedalIndex)
                return;
            // Stop any sustained Engine/ABS/Traction Control/Wheel Spin/
            // Gear Shift/Road Texture/Lockup/Threshold/Brake Fade test on
            // the pedal we're navigating away from — otherwise it keeps
            // buzzing with no visible toggle left to turn it off (the new
            // pedal's tab reseeds its own, unrelated toggle state).
            if (MBoosterEngineTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetEngineTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterAbsTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetAbsTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterTcTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetTcTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterWheelSpinTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetWheelSpinTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterGearShiftTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetGearShiftTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterRoadTextureTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetRoadTextureTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterLockupTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetLockupTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterThresholdTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetThresholdTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterBrakeFadeTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetBrakeFadeTestActive(false);
            if (MBoosterGForceTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetGForceTestActive(false, _mboosterEffectPedalIndex);
            StopAllCustomEffectTests();
            _mboosterSelectedIdentity = identity;
            _mboosterEffectPedalIndex = axisIndex;
            _mboosterUiSeeded = false;
            RefreshMBoosterTab();
        }

        /// <summary>
        /// Write <paramref name="role"/> to wherever <see cref="MozaMBoosterRegistry.ResolveAxisRole"/>
        /// actually reads it for (<paramref name="axisCount"/>, <paramref name="axisIndex"/>)
        /// — <c>Role</c> for a standalone pedal, <c>AxisRoles[axisIndex]</c> for
        /// one pedal of a chain (seeding the array from the currently-resolved
        /// roles first, so unedited axes keep their effective role).
        /// </summary>
        private static void SetMBoosterPedalRole(MBoosterDeviceSettings s, int axisCount, int axisIndex, MBoosterRole role)
        {
            if (axisCount <= 1) { s.Role = role; return; }
            var roles = s.AxisRoles;
            if (roles == null || roles.Length != axisCount)
            {
                var seeded = new MBoosterRole[axisCount];
                for (int i = 0; i < axisCount; i++)
                    seeded[i] = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(s, i, axisCount);
                s.AxisRoles = roles = seeded;
            }
            if (axisIndex >= 0 && axisIndex < roles.Length)
                roles[axisIndex] = role;
        }

        /// <summary>Role-combo callback for <see cref="MBoosterDeviceRow"/> — edits
        /// THAT row's pedal Role regardless of which row is currently selected.
        /// Enforces "only one pedal may occupy a role": assigning a non-Disabled
        /// role clears it off any OTHER pedal that already held it (a physical
        /// pedal can only be one thing, so two rows both claiming Brake is
        /// always a mistake, not a valid state).</summary>
        private void OnMBoosterDeviceRowRoleChanged(string identity, int axisIndex, MBoosterRole role)
        {
            if (_suppressEvents) return;
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(identity);
            var controller = _plugin.MBoosterRegistry?.FindByIdentity(identity);
            int axisCount = controller != null && controller.AxisCount > 0 ? controller.AxisCount : 1;
            SetMBoosterPedalRole(s, axisCount, axisIndex, role);
            _plugin.SaveSettings();
            if (role != MBoosterRole.Disabled)
                ClearDuplicateMBoosterRoleAssignments(identity, axisIndex, role);
            if (string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase) && axisIndex == _mboosterEffectPedalIndex)
                UpdateMBoosterConfigVisibilityForRole();
        }

        /// <summary>Bumps every OTHER known pedal row currently showing
        /// <paramref name="role"/> to Disabled — setting a row's RoleIndex
        /// recurses into <see cref="OnMBoosterDeviceRowRoleChanged"/>, which
        /// persists it through the same Role/AxisRoles write path (and won't
        /// recurse further, since Disabled never triggers another clear
        /// pass). Only considers rows already built (<see cref="_mboosterDeviceRows"/>)
        /// — a pedal not yet known to the UI can't visibly collide with one
        /// that is.</summary>
        private void ClearDuplicateMBoosterRoleAssignments(string keepIdentity, int keepAxisIndex, MBoosterRole role)
        {
            foreach (var row in _mboosterDeviceRows)
            {
                if (string.Equals(row.Identity, keepIdentity, StringComparison.OrdinalIgnoreCase) && row.AxisIndex == keepAxisIndex)
                    continue;
                if (row.RoleIndex == (int)role)
                    row.RoleIndex = (int)MBoosterRole.Disabled;
            }
        }

        /// <summary>DisplayName edit callback — fires from MBoosterDeviceRow
        /// .DisplayName's setter (only when the value actually changes), both
        /// for a genuine user edit (the TwoWay-bound TextBox shown for the
        /// selected row) and RefreshMBoosterTab's per-tick resync (e.g. after
        /// a profile switch changes the saved name). Persists the value and
        /// recomputes every row sharing this identity's Label immediately —
        /// Label isn't part of rowsSignature, so RefreshMBoosterTab wouldn't
        /// otherwise notice a DisplayName change until some other signature
        /// field happened to change too.</summary>
        private void OnMBoosterDeviceRowDisplayNameChanged(string identity, string newDisplayName)
        {
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(identity);
            s.DisplayName = newDisplayName ?? "";
            _plugin.SaveSettings();

            var controller = _plugin.MBoosterRegistry?.FindByIdentity(identity);
            if (controller == null) return;
            string deviceLabel = BuildMBoosterComboLabel(controller);

            int matchCount = 0;
            foreach (var row in _mboosterDeviceRows)
                if (string.Equals(row.Identity, identity, StringComparison.OrdinalIgnoreCase)) matchCount++;
            bool multiplePedals = matchCount > 1;

            int shown = 0;
            foreach (var row in _mboosterDeviceRows)
            {
                if (!string.Equals(row.Identity, identity, StringComparison.OrdinalIgnoreCase)) continue;
                ++shown;
                row.Label = multiplePedals
                    ? $"{deviceLabel} — {string.Format(Strings.Label_PedalAxis, shown)}"
                    : deviceLabel;
            }
        }

        private MBoosterDeviceSettings? CurrentMBoosterSettings()
        {
            if (_mboosterSelectedIdentity == null) return null;
            return _plugin.GetOrCreateMBoosterSettings(_mboosterSelectedIdentity);
        }

        // Which pedal ALL the config sections below the role selector currently
        // edit (0 = master/host at device 0x12; 1/2 = chained pedals at
        // 0x1d/0x1e). Set by the per-pedal config combo; Pedal Feel, Sim Input
        // Mapping, Effects and Calibration are all stored (and effects sent) per
        // pedal. (Kept the "Effect" names to limit churn from the earlier
        // effects-only version — it now scopes every config section.)
        private int _mboosterEffectPedalIndex;

        /// <summary>The full per-pedal config the settings sections edit — the
        /// master's flat fields for pedal 0, else the chained pedal's per-pedal
        /// entry (created on demand so edits persist). A lane whose SOLE
        /// connected pedal is this (non-zero) axis with no per-pedal entry
        /// edits the flat fields instead (and never creates the entry): that's
        /// where the config landed while the UI still showed the axis-0 row,
        /// and creating an empty entry here would orphan it — see
        /// MBoosterDeviceController.SoleConnectedAxis. Null if no device
        /// selected. Covers effects + calibration + sim input + pedal feel.</summary>
        private IMBoosterPedalConfig? CurrentMBoosterEffectTarget() =>
            MozaMBoosterRegistry.GetOrCreatePedalConfig(
                CurrentMBoosterSettings(),
                _mboosterEffectPedalIndex,
                CurrentMBoosterController()?.SoleConnectedAxis() ?? -1);

        /// <summary>The per-pedal config for the selected pedal WITHOUT creating a
        /// missing entry — used when seeding controls so merely viewing a chained
        /// pedal doesn't persist an empty entry. Falls back to master defaults.
        /// Same sole-connected-pedal flat-fields fallback as
        /// <see cref="CurrentMBoosterEffectTarget"/> so seeding shows the config
        /// that pedal actually runs with.</summary>
        private IMBoosterPedalConfig? PeekMBoosterEffectTarget() =>
            MozaMBoosterRegistry.PeekPedalConfig(
                CurrentMBoosterSettings(),
                _mboosterEffectPedalIndex,
                CurrentMBoosterController()?.SoleConnectedAxis() ?? -1);

        /// <summary>Seed the eight vibration-effect cards' controls from one
        /// pedal's effect settings. Assumes the event suppressor is active. Brake
        /// Fade is seeded separately by <see cref="RefreshMBoosterTab"/> since it's
        /// per-lane (master pedal only).</summary>
        private void SeedMBoosterEffectControls(IMBoosterEffects? fx)
        {
            MBoosterTcEnable.IsChecked       = fx?.TractionControl?.Enabled          ?? false;
            MBoosterTcFrequencySlider.Value  = fx?.TractionControl?.FrequencyHz      ?? MBoosterUiConstants.TractionControlFreqMinHz;
            SetValueText(MBoosterTcFrequencyValue, MBoosterTcFrequencySlider.Value.ToString("F0"));
            MBoosterTcIntensity.Value        = fx?.TractionControl?.IntensityPct     ?? 50;
            SetValueText(MBoosterTcIntensityValue, (fx?.TractionControl?.IntensityPct ?? 50).ToString());
            MBoosterTcTestToggle.IsChecked = false;
            MBoosterWheelSpinEnable.IsChecked       = fx?.WheelSpin?.Enabled          ?? false;
            MBoosterWheelSpinFrequencySlider.Value  = fx?.WheelSpin?.FrequencyHz      ?? MBoosterUiConstants.WheelSpinFreqMinHz;
            SetValueText(MBoosterWheelSpinFrequencyValue, MBoosterWheelSpinFrequencySlider.Value.ToString("F0"));
            MBoosterWheelSpinIntensity.Value        = fx?.WheelSpin?.IntensityPct     ?? 50;
            SetValueText(MBoosterWheelSpinIntensityValue, (fx?.WheelSpin?.IntensityPct ?? 50).ToString());
            MBoosterWheelSpinTestToggle.IsChecked = false;
            MBoosterGearShiftEnable.IsChecked       = fx?.GearShift?.Enabled          ?? false;
            MBoosterGearShiftFrequencySlider.Value  = fx?.GearShift?.FrequencyHz      ?? MBoosterUiConstants.GearShiftFreqMinHz;
            SetValueText(MBoosterGearShiftFrequencyValue, MBoosterGearShiftFrequencySlider.Value.ToString("F0"));
            MBoosterGearShiftIntensity.Value        = fx?.GearShift?.IntensityPct     ?? 50;
            SetValueText(MBoosterGearShiftIntensityValue, (fx?.GearShift?.IntensityPct ?? 50).ToString());
            MBoosterGearShiftVibrateOnNeutralCheck.IsChecked = fx?.GearShift?.VibrateOnNeutral ?? false;
            int gearShiftDebounceMs = fx?.GearShift?.DebounceMs ?? 500;
            MBoosterGearShiftDebounceSlider.Value = gearShiftDebounceMs;
            MBoosterGearShiftDebounceValue.Text = $"{gearShiftDebounceMs} ms";
            MBoosterGearShiftTestToggle.IsChecked = false;
            MBoosterAbsEnable.IsChecked       = fx?.Abs?.Enabled          ?? false;
            MBoosterAbsFrequencySlider.Value  = fx?.Abs?.FrequencyHz      ?? MBoosterUiConstants.AbsFreqMinHz;
            SetValueText(MBoosterAbsFrequencyValue, MBoosterAbsFrequencySlider.Value.ToString("F0"));
            MBoosterAbsIntensity.Value        = fx?.Abs?.IntensityPct     ?? 50;
            SetValueText(MBoosterAbsIntensityValue, (fx?.Abs?.IntensityPct ?? 50).ToString());
            MBoosterAbsSmoothness.Value       = fx?.Abs?.SmoothnessPct    ?? 100;
            SetValueText(MBoosterAbsSmoothnessValue, (fx?.Abs?.SmoothnessPct ?? 100).ToString());
            MBoosterAbsTestToggle.IsChecked = false;
            MBoosterLockupEnable.IsChecked = fx?.Lockup?.Enabled ?? false;
            MBoosterLockupFrequencySlider.Value = fx?.Lockup?.FrequencyHz ?? MBoosterUiConstants.LockupFreqMinHz;
            SetValueText(MBoosterLockupFrequencyValue, MBoosterLockupFrequencySlider.Value.ToString("F0"));
            MBoosterLockupIntensity.Value = fx?.Lockup?.IntensityPct ?? 50;
            SetValueText(MBoosterLockupIntensityValue, (fx?.Lockup?.IntensityPct ?? 50).ToString());
            MBoosterLockupTestToggle.IsChecked = false;
            MBoosterThresholdEnable.IsChecked = fx?.Threshold?.Enabled ?? false;
            MBoosterThresholdTriggerLevel.Value = fx?.Threshold?.TriggerLevelPct ?? 60;
            SetValueText(MBoosterThresholdTriggerLevelValue, (fx?.Threshold?.TriggerLevelPct ?? 60).ToString());
            MBoosterThresholdFrequencySlider.Value = fx?.Threshold?.FrequencyHz ?? MBoosterUiConstants.ThresholdFreqMinHz;
            SetValueText(MBoosterThresholdFrequencyValue, MBoosterThresholdFrequencySlider.Value.ToString("F0"));
            MBoosterThresholdIntensity.Value = fx?.Threshold?.IntensityPct ?? 50;
            SetValueText(MBoosterThresholdIntensityValue, (fx?.Threshold?.IntensityPct ?? 50).ToString());
            MBoosterThresholdDecay.Value = fx?.Threshold?.DecayPct ?? 20;
            SetValueText(MBoosterThresholdDecayValue, (fx?.Threshold?.DecayPct ?? 20).ToString());
            MBoosterThresholdTestToggle.IsChecked = false;
            MBoosterEngineEnable.IsChecked    = fx?.Engine?.Enabled       ?? false;
            MBoosterEngineIntensity.Value     = fx?.Engine?.IntensityPct  ?? 50;
            SetValueText(MBoosterEngineIntensityValue, (fx?.Engine?.IntensityPct ?? 50).ToString());
            MBoosterEngineTestToggle.IsChecked = false;
            MBoosterRoadTextureEnable.IsChecked = fx?.RoadTexture?.Enabled ?? false;
            MBoosterRoadTextureIntensity.Value = fx?.RoadTexture?.IntensityPct ?? 50;
            SetValueText(MBoosterRoadTextureIntensityValue, (fx?.RoadTexture?.IntensityPct ?? 50).ToString());
            MBoosterRoadTextureSmoothness.Value = fx?.RoadTexture?.SmoothnessPct ?? 50;
            SetValueText(MBoosterRoadTextureSmoothnessValue, (fx?.RoadTexture?.SmoothnessPct ?? 50).ToString());
            MBoosterRoadTextureTestToggle.IsChecked = false;
            MBoosterGForceEnable.IsChecked = fx?.GForce?.Enabled ?? false;
            MBoosterGForceMaxTravel.Value = fx?.GForce?.MaxTravelMm ?? 10;
            SetValueText(MBoosterGForceMaxTravelValue, MBoosterGForceMaxTravel.Value.ToString("0.#"));
            MBoosterGForceResponseSpeed.Value = fx?.GForce?.ResponseSpeedPct ?? 50;
            SetValueText(MBoosterGForceResponseSpeedValue, (fx?.GForce?.ResponseSpeedPct ?? 50).ToString());
            MBoosterGForceTestToggle.IsChecked = false;
        }

        /// <summary>Seed the Calibration, Sim Input Mapping and Pedal Feel controls
        /// from one pedal's config (master flat fields or a chained pedal's entry;
        /// null = defaults). Assumes the event suppressor is active.</summary>
        private void SeedMBoosterConfigControls(IMBoosterPedalConfig? fx)
        {
            // Calibration
            MBoosterDirCheck.IsChecked = (fx?.Direction == 1);
            int min = fx?.Min ?? -1;
            MBoosterMinSlider.Value = min >= 0 ? min : 0;
            SetValueText(MBoosterMinValue, MBoosterMinSlider.Value.ToString("F0"));
            int max = fx?.Max ?? -1;
            MBoosterMaxSlider.Value = max >= 0 ? max : 0;
            SetValueText(MBoosterMaxValue, MBoosterMaxSlider.Value.ToString("F0"));
            var curve = (fx?.CurveY != null && fx.CurveY.Length == 5) ? fx.CurveY : MBoosterDefaultCurve;
            MBoosterY1Slider.Value = curve[0]; SetValueText(MBoosterY1Value, curve[0].ToString("F0"));
            MBoosterY2Slider.Value = curve[1]; SetValueText(MBoosterY2Value, curve[1].ToString("F0"));
            MBoosterY3Slider.Value = curve[2]; SetValueText(MBoosterY3Value, curve[2].ToString("F0"));
            MBoosterY4Slider.Value = curve[3]; SetValueText(MBoosterY4Value, curve[3].ToString("F0"));
            MBoosterY5Slider.Value = curve[4]; SetValueText(MBoosterY5Value, curve[4].ToString("F0"));
            var curveX = (fx?.CurveX != null && fx.CurveX.Length == 5) ? fx.CurveX : MBoosterDefaultCurve;
            MBoosterX1Slider.Value = curveX[0]; SetValueText(MBoosterX1Value, curveX[0].ToString("F0"));
            MBoosterX2Slider.Value = curveX[1]; SetValueText(MBoosterX2Value, curveX[1].ToString("F0"));
            MBoosterX3Slider.Value = curveX[2]; SetValueText(MBoosterX3Value, curveX[2].ToString("F0"));
            MBoosterX4Slider.Value = curveX[3]; SetValueText(MBoosterX4Value, curveX[3].ToString("F0"));
            MBoosterX5Slider.Value = curveX[4]; SetValueText(MBoosterX5Value, curveX[4].ToString("F0"));
            // Sim Input Mapping
            float ratio = fx?.SensorOutputRatioPct ?? -1;
            MBoosterRatioSlider.Value = ratio >= 0 ? ratio : 0;
            SetValueText(MBoosterRatioValue, $"{MBoosterRatioSlider.Value:F0}%");
            float thr = fx?.MaxThresholdKg ?? -1;
            MBoosterMaxThresholdSlider.Value = thr >= 0 ? thr : 100;
            SetValueText(MBoosterMaxThresholdValue, MBoosterMaxThresholdSlider.Value.ToString("F0"));
            // Pedal Feel
            var inputCurve = (fx?.InputCurveY != null && fx.InputCurveY.Length == 5) ? fx.InputCurveY : MBoosterDefaultCurve;
            MBoosterInputY1Slider.Value = inputCurve[0]; SetValueText(MBoosterInputY1Value, inputCurve[0].ToString("F0"));
            MBoosterInputY2Slider.Value = inputCurve[1]; SetValueText(MBoosterInputY2Value, inputCurve[1].ToString("F0"));
            MBoosterInputY3Slider.Value = inputCurve[2]; SetValueText(MBoosterInputY3Value, inputCurve[2].ToString("F0"));
            MBoosterInputY4Slider.Value = inputCurve[3]; SetValueText(MBoosterInputY4Value, inputCurve[3].ToString("F0"));
            MBoosterInputY5Slider.Value = inputCurve[4]; SetValueText(MBoosterInputY5Value, inputCurve[4].ToString("F0"));
            float ts = fx?.TravelStartMm ?? -1;
            MBoosterTravelRangeSlider.LowValue = ts >= 0 ? ts : MBoosterUiConstants.TravelMinMm;
            float te = fx?.TravelEndMm ?? -1;
            MBoosterTravelRangeSlider.HighValue = te >= 0 ? te : MBoosterUiConstants.TravelMinMm + MBoosterUiConstants.TravelMaxGapMm;
            float ef = fx?.EndstopFrontStiffness ?? -1;
            MBoosterEndstopFrontSlider.Value = ef >= 0 ? ef : 1;
            SetValueText(MBoosterEndstopFrontValue, MBoosterEndstopFrontSlider.Value.ToString("F0"));
            float ee = fx?.EndstopEndStiffness ?? -1;
            MBoosterEndstopEndSlider.Value = ee >= 0 ? ee : 1;
            SetValueText(MBoosterEndstopEndValue, MBoosterEndstopEndSlider.Value.ToString("F0"));
            MBoosterDeadzoneSlider.Value = fx?.DeadzoneKg ?? 0;
            SetValueText(MBoosterDeadzoneValue, (fx?.DeadzoneKg ?? 0).ToString("F1"));
            ApplyMBoosterMaxForceCeiling(fx);
            MBoosterMaxForceSlider.Value = Math.Min(fx?.MaxForceKg ?? 200, MBoosterMaxForceSlider.Maximum);
            SetValueText(MBoosterMaxForceValue, MBoosterMaxForceSlider.Value.ToString("F0"));
            float nf = fx?.NaturalFrictionPct ?? -1;
            MBoosterNaturalFrictionSlider.Value = nf >= 0 ? nf : 0;
            SetValueText(MBoosterNaturalFrictionValue, MBoosterNaturalFrictionSlider.Value.ToString("F0"));

            var sd = fx?.SegmentedDamping;
            MBoosterSegDampPressedPlot.Divider1 = (sd?.Divider1Pressed ?? -1) >= 0 ? sd!.Divider1Pressed : MBoosterUiConstants.SegDampDivider1PressedDefaultPct;
            MBoosterSegDampPressedPlot.Divider2 = (sd?.Divider2Pressed ?? -1) >= 0 ? sd!.Divider2Pressed : MBoosterUiConstants.SegDampDivider2PressedDefaultPct;
            MBoosterSegDampPressedPlot.Seg1Value = (sd?.Seg1Pressed ?? -1) >= 0 ? sd!.Seg1Pressed : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampPressedPlot.Seg2Value = (sd?.Seg2Pressed ?? -1) >= 0 ? sd!.Seg2Pressed : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampPressedPlot.Seg3Value = (sd?.Seg3Pressed ?? -1) >= 0 ? sd!.Seg3Pressed : MBoosterUiConstants.SegDampSegDefaultPct;

            MBoosterSegDampReleasedPlot.Divider1 = (sd?.Divider1Released ?? -1) >= 0 ? sd!.Divider1Released : MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct;
            MBoosterSegDampReleasedPlot.Divider2 = (sd?.Divider2Released ?? -1) >= 0 ? sd!.Divider2Released : MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct;
            MBoosterSegDampReleasedPlot.Seg1Value = (sd?.Seg1Released ?? -1) >= 0 ? sd!.Seg1Released : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampReleasedPlot.Seg2Value = (sd?.Seg2Released ?? -1) >= 0 ? sd!.Seg2Released : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampReleasedPlot.Seg3Value = (sd?.Seg3Released ?? -1) >= 0 ? sd!.Seg3Released : MBoosterUiConstants.SegDampSegDefaultPct;
        }

        /// <summary>
        /// Cap the Max Force slider at the force the pedal's raw HID axis
        /// actually reaches 100% at (<see cref="MozaMBoosterRegistry.ResolveFullScaleKg"/>).
        /// Above that point the device has already pegged its own output, so
        /// there is no resolution left for software to require more force —
        /// every position past it was silently inert, which with Max Threshold
        /// at 140kg left the whole top 30% of a 0-200 slider doing nothing
        /// (bundle KY3HK4QP). The XAML's static "200" end label would then be
        /// wrong, so it is rewritten to match.
        /// </summary>
        private void ApplyMBoosterMaxForceCeiling(IMBoosterPedalConfig? fx)
        {
            double ceiling = MozaMBoosterRegistry.ResolveFullScaleKg(fx, CurrentMBoosterController());
            if (ceiling <= 0) ceiling = 200;
            MBoosterMaxForceSlider.Maximum = ceiling;
            MBoosterMaxForceRangeEndLabel.Text = ceiling.ToString("F0");
            if (MBoosterMaxForceSlider.Value > ceiling)
            {
                MBoosterMaxForceSlider.Value = ceiling;
                SetValueText(MBoosterMaxForceValue, ceiling.ToString("F0"));
            }
        }

        private MBoosterDeviceController? CurrentMBoosterController()
        {
            return _plugin?.MBoosterRegistry?.FindByIdentity(_mboosterSelectedIdentity ?? "");
        }

        /// <summary>
        /// Device combo label: port/identity, prefixed with the user's
        /// DisplayName when set — the whole point of that field is telling
        /// two same-role mBoosters apart in this exact list. See
        /// MBoosterDeviceSettings.DisplayName.
        /// </summary>
        private string BuildMBoosterComboLabel(MBoosterDeviceController c)
        {
            string baseLabel = $"{MBoosterDeviceController.ShortIdentity(c.Identity)} ({c.PortName})";
            var name = _plugin?.GetOrCreateMBoosterSettings(c.Identity)?.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? baseLabel : $"{name} — {baseLabel}";
        }

    }
}
