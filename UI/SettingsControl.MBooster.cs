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
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.UI
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
        // Pedal (axis) last seeded. The selection moves on its own when a
        // passive pedal's type lands after connect; the page must reseed then
        // or it shows that pedal's values while edits go to the new one.
        private int _mboosterSeededPedalIndex = -1;

        // The exact MBoosterDeviceSettings INSTANCE last seeded from — not
        // just a string identity check, because MozaPlugin
        // .GetOrCreateMBoosterSettings can return a genuinely DIFFERENT
        // object for the SAME identity string across two calls: it first
        // hands back a fresh, all-defaults placeholder keyed by the raw
        // transport identity (before the device's serial has been read
        // back), then — once OnMBoosterSerialResolved fires, asynchronously,
        // on the connection thread — silently swaps in the real, saved
        // profile object under the resolved serial key. If this tab's first
        // seed pass raced that swap, the string-only checks above never
        // noticed the object underneath had changed, so the tab kept
        // displaying the placeholder's all-defaults values forever instead
        // of the actual saved profile (the values were never lost — this
        // was a display bug, not a persistence one). Comparing the object
        // reference catches that swap and forces a proper reseed.
        private MBoosterDeviceSettings? _mboosterSeededSettings;

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
            FollowMBoosterRoleSwap(registry);
            var devices = registry.Devices;
            // Role-follow, selection validation and the device panel's
            // Visibility (which gates the 30 Hz trace feed) run every tick; the
            // rows, role/passive visibility and seeding only while the tab is
            // shown (MainTabs_SelectionChanged repaints it on selection).
            bool tabShown = MBoosterTab.IsSelected;
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
                    if (tabShown) ShowMBoosterDemoPanels();
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
            string? rowsSignature = null;
            bool rowsStale = false;
            if (tabShown)
            {
                var sigBuilder = new System.Text.StringBuilder();
                foreach (var c in devices)
                {
                    int sigAxisCount = c.AxisSlotCount;
                    sigBuilder.Append(c.Identity).Append('|').Append(sigAxisCount).Append('|');
                    var sigConnected = c.ConnectedAxes;
                    if (sigConnected != null)
                        for (int k = 0; k < sigConnected.Length; k++) sigBuilder.Append(sigConnected[k] ? '1' : '0');
                    sigBuilder.Append('|').Append(string.Join(",", MBoosterTabAxes(registry, c)));
                    sigBuilder.Append(';');
                }
                rowsSignature = sigBuilder.ToString();
                rowsStale = !string.Equals(rowsSignature, _mboosterDeviceRowsSignature, StringComparison.Ordinal);
            }

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
                    var listed = MBoosterTabAxes(registry, c);
                    selectedDeviceStillPresent = listed.Count > 0;
                    selectionValid = listed.Contains(_mboosterEffectPedalIndex);
                    // The device's real wired axis just became known (the "PD
                    // Linked" diagnostic landed) and it isn't the axis-0
                    // placeholder we'd optimistically selected before that — a
                    // standalone unit's sole pedal commonly reports on a
                    // non-zero axis regardless of chain status (see
                    // MBoosterDeviceController's ConnectedAxes doc comment) —
                    // or the selected pedal moved to the Pedals tab once its
                    // passive type landed. Follow the SAME physical device onto
                    // its first listed pedal instead of falling through to
                    // devices[0] below, which would silently reassign the
                    // user's selection to a different device.
                    if (!selectionValid && listed.Count > 0) sameDeviceRetargetAxis = listed[0];
                    break;
                }
                if (!selectionValid)
                {
                    StopMBoosterPedalTests();
                    if (selectedDeviceStillPresent)
                        _mboosterEffectPedalIndex = sameDeviceRetargetAxis;
                    else
                    {
                        // First-ever selection (a brand-new SettingsControl, or
                        // the previously-selected device vanished entirely) —
                        // this used to always land on axis 0, even when axis 0
                        // isn't actually wired (a standalone unit's sole pedal
                        // commonly reports on a non-zero axis — see the
                        // ConnectedAxes-based retarget above, and
                        // MBoosterDeviceController's own ConnectedAxes doc
                        // comment). Connectivity is frequently ALREADY known at
                        // this point from the persisted cache (seeded well
                        // before the live "PD Linked" diagnostic confirms it —
                        // see MozaPlugin.LookupMBoosterKnownPedals), so defaulting
                        // to axis 0 blindly showed this device's (often
                        // long-stale/orphaned) axis-0 flat-field data — e.g. a
                        // Sim Input Mapping/Pedal Feel curve nobody's touched in
                        // ages — until a later refresh tick corrected the axis
                        // once the live diagnostic caught up. Pick the first
                        // known-connected axis instead, same as the retarget
                        // logic above; fall back to axis 0 only when
                        // connectivity isn't known yet at all.
                        _mboosterSelectedIdentity = devices[0].Identity;
                        int initialAxis = 0;
                        foreach (var c in devices)
                        {
                            var listed = MBoosterTabAxes(registry, c);
                            if (listed.Count == 0) continue;
                            _mboosterSelectedIdentity = c.Identity;
                            initialAxis = listed[0];
                            break;
                        }
                        _mboosterEffectPedalIndex = initialAxis;
                    }
                }

                // Only ever stale while the tab is shown (see above).
                if (rowsStale)
                {
                    _mboosterDeviceRowsSignature = rowsSignature;
                    _mboosterDeviceRows.Clear();
                    foreach (var c in devices)
                    {
                        var rowSettings = _plugin.GetOrCreateMBoosterSettings(c.Identity);
                        string deviceLabel = BuildMBoosterComboLabel(c);
                        var listedAxes = MBoosterTabAxes(registry, c);

                        // Only label rows "— Pedal N" when this device genuinely
                        // lists more than one pedal — not just because its HID
                        // interface happens to expose 3 axes.
                        bool multiplePedals = listedAxes.Count > 1;
                        int shown = 0;
                        foreach (int axis in listedAxes)
                        {
                            ++shown;
                            string label = multiplePedals
                                ? $"{deviceLabel} — {string.Format(Strings.Label_PedalAxis, shown)}"
                                : deviceLabel;
                            var role = c.AxisRole(axis, rowSettings);
                            bool isSelected = string.Equals(c.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                                && axis == _mboosterEffectPedalIndex;
                            _mboosterDeviceRows.Add(new MBoosterDeviceRow(c.Identity, axis, label, isSelected, role,
                                OnMBoosterDeviceRowRoleChanged));
                        }
                    }
                    MBoosterDeviceRowsList.ItemsSource = _mboosterDeviceRows;
                }
                else if (tabShown)
                {
                    foreach (var row in _mboosterDeviceRows)
                    {
                        var rowController = registry.FindByIdentity(row.Identity);
                        var rowSettings = _plugin.GetOrCreateMBoosterSettings(row.Identity);
                        row.RoleIndex = rowController != null
                            ? (int)rowController.AxisRole(row.AxisIndex, rowSettings)
                            : (int)global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.ResolveAxisRole(rowSettings, row.AxisIndex, 1);
                        row.IsSelected = string.Equals(row.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                            && row.AxisIndex == _mboosterEffectPedalIndex;
                    }
                }
            }

            var selected = registry.FindByIdentity(_mboosterSelectedIdentity ?? "");
            if (selected == null || MBoosterTabAxes(registry, selected).Count == 0)
            {
                MBoosterDevicePanel.Visibility = Visibility.Collapsed;
                return;
            }

            MBoosterDevicePanel.Visibility = Visibility.Visible;
            if (!tabShown) return;

            // Live position marker (on the curve editors) is updated at
            // 30 Hz from UpdateHidInputDisplays (UpdateMBoosterCurveMarkers)
            // instead of here — this 500ms pass felt sluggish for direct
            // pedal feedback.

            // Resynced on EVERY pass, not gated by the seed-once latch below —
            // both depend on state that can resolve strictly AFTER the tab's
            // first seed: the pedal's Role may still be sitting on a fresh,
            // Disabled-default MBoosterDeviceSettings if this first seed raced
            // OnMBoosterSerialResolved (which migrates the real saved settings
            // in under the device's serial key, asynchronously, once the
            // serial has actually been read back over the wire — see
            // MozaPlugin.GetOrCreateMBoosterSettings/OnMBoosterSerialResolved);
            // AxisTypes (passive-pedal detection) similarly only populates once
            // the 0x0E diagnostic arrives. Neither call bumps _mboosterUiSeeded
            // above, and re-selecting the identity string never changes once
            // that race resolves, so gating these behind the seed-once latch
            // left a pedal that's genuinely Brake (or genuinely active)
            // permanently showing as if it weren't, from first tab-open until
            // the user forced a reseed some other way (switching pedals/
            // profiles). Cheap, idempotent Visibility pushes — safe every tick,
            // same reasoning as the per-row Role/IsSelected resync above.
            UpdateMBoosterEffectPassiveState();
            UpdateMBoosterConfigVisibilityForRole();

            // Read-only until the lane's settings entry is final — before the
            // serial resolves, GetOrCreateMBoosterSettings hands out a
            // transport-keyed placeholder, and edits made to it only survive
            // the re-key as merge conflicts. Not seeded either, so the
            // placeholder's defaults never show as if they were saved values.
            bool settingsResolved = _plugin != null && _plugin.IsMBoosterSettingsResolved(selected);
            MBoosterDevicePanel.IsEnabled = settingsResolved;
            MBoosterDeviceRowsList.IsEnabled = settingsResolved;
            if (!settingsResolved) return;

            // Re-seed when the active profile or the selected device changed
            // since the last seed, OR the settings object itself is a
            // different instance than last time (see _mboosterSeededSettings)
            // — otherwise the gate below keeps the previously-seeded values
            // on screen while edits write to the now-current profile/device
            // (mBooster settings are per-profile, per-device).
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(selected.Identity);
            var currentProfileName = _plugin.Settings?.ProfileStore?.CurrentProfile?.Name;
            if (!string.Equals(currentProfileName, _mboosterSeededProfileName, StringComparison.Ordinal)
                || !string.Equals(selected.Identity, _mboosterSeededIdentity, StringComparison.OrdinalIgnoreCase)
                || _mboosterEffectPedalIndex != _mboosterSeededPedalIndex
                || !ReferenceEquals(s, _mboosterSeededSettings))
                _mboosterUiSeeded = false;

            if (_mboosterUiSeeded)
            {
                // The host's 0xB4 read-back can land after the seed.
                if (s.SleepMinutes < 0) SeedMBoosterSleep(s, selected);
                // So can the Pedal Feel read-backs.
                using (_suppressor.Begin())
                    SeedMBoosterPedalFeelRegisters(PeekMBoosterEffectTarget(), unsetOnly: true);
                return;
            }
            // Diagnostic trail for the "curve values wrong until profile
            // reload" bug — logs exactly what this seed pass is about to push
            // into the curve editors, timestamped, so it can be correlated
            // against GetOrCreateMBoosterSettings's "NEW placeholder" log and
            // OnMBoosterSerialResolved's re-key log from the same session.
            {
                var fxLog = PeekMBoosterEffectTarget();
                string Fmt(float[]? a) => a == null ? "null" : "[" + string.Join(",", a) + "]";
                MozaLog.Info($"[AZOM\\mBooster] RefreshMBoosterTab seeding: profile='{currentProfileName}' identity='{selected.Identity}' pedalIdx={_mboosterEffectPedalIndex} "
                    + $"curve={Fmt(fxLog?.HardwareCurveY)} min={fxLog?.Min} max={fxLog?.Max} InputCurveY={Fmt(fxLog?.InputCurveY)} InputCurveX={Fmt(fxLog?.InputCurveX)}");
            }
            // The seed below shows every Test toggle off (custom-effect rows
            // are rebuilt off too), so nothing may keep running behind them —
            // a profile switch, settings re-key, import or role move reseeds
            // without a selection change.
            StopMBoosterPedalTests();
            using (_suppressor.Begin())
            {
                // Role is seeded per-row by the device rows block above (each
                // MBoosterDeviceRow owns its own Role), not here.
                // _mboosterEffectPedalIndex is resolved/validated by the device
                // rows block above too — here we just seed whichever pedal it
                // settled on.
                SeedMBoosterEffectControls(PeekMBoosterEffectTarget());
                MBoosterBrakeFadeEnable.IsChecked = s.BrakeFade?.Enabled ?? false;
                MBoosterBrakeFadeOnsetSlider.Value = s.BrakeFade?.BrakeFadeOnsetC ?? 550;
                SetValueText(MBoosterBrakeFadeOnsetValue, MBoosterBrakeFadeOnsetSlider.Value.ToString("F0"));
                SeedMBoosterConfigControls(PeekMBoosterEffectTarget());
            }
            SeedMBoosterSleep(s, selected);
            PopulateMBoosterCustomEffectsList(PeekMBoosterEffectTarget());
            _mboosterUiSeeded = true;
            _mboosterSeededProfileName = currentProfileName;
            _mboosterSeededIdentity = selected.Identity;
            _mboosterSeededPedalIndex = _mboosterEffectPedalIndex;
            _mboosterSeededSettings = s;
        }

        /// <summary>The pedals this tab lists for a device: every connected
        /// one except the passive pedals the Pedals tab configures (see
        /// MozaMBoosterRegistry.IsPedalsTabPassive).</summary>
        private static List<int> MBoosterTabAxes(MozaMBoosterRegistry registry, MBoosterDeviceController c)
        {
            var axes = c.ConnectedAxisIndices();
            axes.RemoveAll(a => registry.IsPedalsTabPassive(c, a));
            return axes;
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

        /// <summary>Stop every sustained test on the selected lane and show
        /// every Test toggle off. Call before the selection moves off the
        /// pedal, before a reseed (it clears the toggles) and on unload —
        /// otherwise a test keeps running with no visible toggle left to turn
        /// it off. Must run while <see cref="CurrentMBoosterController"/> is
        /// still the lane that ran it.</summary>
        private void StopMBoosterPedalTests()
        {
            CurrentMBoosterController()?.StopAllTests();
            using (_suppressor.Begin())
                ClearMBoosterTestToggles();
            StopAllCustomEffectTests();
        }

        /// <summary>Show every built-in Test toggle off. Never persisted, so
        /// every seed starts from here too.</summary>
        private void ClearMBoosterTestToggles()
        {
            MBoosterTcTestToggle.IsChecked = false;
            MBoosterWheelSpinTestToggle.IsChecked = false;
            MBoosterGearShiftTestToggle.IsChecked = false;
            MBoosterAbsTestToggle.IsChecked = false;
            MBoosterLockupTestToggle.IsChecked = false;
            MBoosterThresholdTestToggle.IsChecked = false;
            MBoosterBitePointTestToggle.IsChecked = false;
            MBoosterEngineTestToggle.IsChecked = false;
            MBoosterRoadTextureTestToggle.IsChecked = false;
            MBoosterGForceTestToggle.IsChecked = false;
            MBoosterBrakeFadeTestToggle.IsChecked = false;
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
            StopMBoosterPedalTests();
            _mboosterSelectedIdentity = identity;
            _mboosterEffectPedalIndex = axisIndex;
            _mboosterUiSeeded = false;
            RefreshMBoosterTab();
        }

        /// <summary>
        /// Persist one pedal's role. <paramref name="rawAxisCount"/> sizes the
        /// <see cref="MBoosterDeviceSettings.AxisRoles"/> array (it's indexed by
        /// HID axis index, so it must cover every axis, wired or not), while
        /// <paramref name="connectedAxisCount"/> is what role RESOLUTION uses —
        /// the same split every other caller makes (see
        /// <see cref="MBoosterDeviceController.ConnectedAxisCount"/>). Passing
        /// the raw count to both, as this used to, seeded the array with
        /// axis-order defaults instead of each pedal's own resolved role,
        /// baking in a wrong (and sometimes duplicated) set the moment the user
        /// touched any one dropdown.
        /// </summary>
        private static void SetMBoosterPedalRole(
            MBoosterDeviceSettings s, int connectedAxisCount, int rawAxisCount, int axisIndex, MBoosterRole role)
        {
            if (connectedAxisCount <= 1) { s.Role = role; return; }
            int length = Math.Max(rawAxisCount, axisIndex + 1);
            var roles = s.AxisRoles;
            if (roles == null || roles.Length != length)
            {
                var seeded = new MBoosterRole[length];
                for (int i = 0; i < length; i++)
                    seeded[i] = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.ResolveAxisRole(s, i, connectedAxisCount);
                s.AxisRoles = roles = seeded;
            }
            if (axisIndex >= 0 && axisIndex < roles.Length)
                roles[axisIndex] = role;
        }

        /// <summary>
        /// The other axis of the lane that already holds <paramref name="role"/>,
        /// or -1. It takes the edited axis's old role, or the first role no axis
        /// holds if that was Disabled, so a lane keeps distinct roles (Pit House's
        /// unit map goes 1/2/3 → 2/1/3 the same way).
        /// </summary>
        private static int MBoosterRoleSwapPartner(
            MBoosterDeviceSettings s, int connectedAxisCount, int rawAxisCount, int axisIndex,
            MBoosterRole role, out MBoosterRole partnerRole)
        {
            partnerRole = MBoosterRole.Disabled;
            if (connectedAxisCount <= 1 || role == MBoosterRole.Disabled) return -1;
            int length = Math.Max(rawAxisCount, axisIndex + 1);
            var current = new MBoosterRole[length];
            for (int i = 0; i < length; i++)
                current[i] = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.ResolveAxisRole(s, i, connectedAxisCount);
            int partner = Array.FindIndex(current, (r) => r == role);
            if (partner == axisIndex) partner = Array.FindIndex(current, axisIndex + 1, (r) => r == role);
            if (partner < 0) return -1;
            partnerRole = current[axisIndex];
            if (partnerRole == MBoosterRole.Disabled)
            {
                current[axisIndex] = role;
                foreach (var r in new[] { MBoosterRole.Throttle, MBoosterRole.Brake, MBoosterRole.Clutch })
                    if (Array.IndexOf(current, r) < 0) { partnerRole = r; break; }
            }
            return partner;
        }

        /// <summary>Role-combo callback for <see cref="MBoosterDeviceRow"/> — edits
        /// THAT row's pedal Role regardless of which row is currently selected.
        /// A pedal already holding the new role swaps to the edited pedal's old
        /// one (see <see cref="MBoosterRoleSwapPartner"/>).</summary>
        private void OnMBoosterDeviceRowRoleChanged(string identity, int axisIndex, MBoosterRole role)
        {
            if (_suppressEvents) return;
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(identity);
            var controller = _plugin.MBoosterRegistry?.FindByIdentity(identity);
            int axisCount = controller?.AxisSlotCount ?? 1;
            int connectedAxisCount = controller?.ConnectedAxisCount ?? 1;
            if (controller != null && controller.DeviceRolesKnown)
            {
                ChangeMBoosterDeviceRole(identity, controller, s, axisIndex, role);
                return;
            }
            int partner = MBoosterRoleSwapPartner(s, connectedAxisCount, axisCount, axisIndex, role, out var partnerRole);
            // A motor pedal's role lives on its unit — write it there too, as
            // Pit House does. Both pedals of a swap are resolved before either
            // is written; a passive pedal keeps the profile-only role.
            if (controller != null)
            {
                var writes = new List<(int Axis, int Role)>();
                if (role != MBoosterRole.Disabled) writes.Add((axisIndex, MBoosterDeviceController.RoleIndexOf(role)));
                if (partner >= 0 && partnerRole != MBoosterRole.Disabled) writes.Add((partner, MBoosterDeviceController.RoleIndexOf(partnerRole)));
                if (writes.Count > 0) controller.WriteAxisRoles(writes);
            }
            SetMBoosterPedalRole(s, connectedAxisCount, axisCount, axisIndex, role);
            if (partner >= 0)
            {
                SetMBoosterPedalRole(s, connectedAxisCount, axisCount, partner, partnerRole);
                MozaLog.Info($"[AZOM/mBooster] {identity}: axis {axisIndex} -> {role}, axis {partner} swapped to {partnerRole}");
            }
            _plugin.SaveSettings();
            // Max Force/Deadzone bounds follow the pedal's hardware (active
            // 0-200kg / 0-37kg, passive 4-20kg / 0-8kg) — a pedal whose type
            // verdict changed may still carry a stored value outside its
            // range. Clamp it into range and re-push immediately, targeting
            // THIS specific axis
            // (not necessarily the one selected in the UI — see
            // PushMBoosterFeelCurve's axis-explicit overload). Left alone,
            // the stored value and the slider disagree until the user happens
            // to drag it, and the Pedal Feel curve plots against a span the
            // hardware isn't using.
            if (role != MBoosterRole.Disabled && controller != null)
            {
                var cfg = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.GetOrCreatePedalConfig(s, axisIndex, controller.SoleConnectedAxis());
                if (cfg != null)
                {
                    // Hardware, not role — reassigning an ACTIVE pedal from
                    // brake to throttle must not clamp its load-cell value
                    // down to a spring pedal's ceiling (that clamp, plus the
                    // slider's own coercion, is what walked Max Force to 24kg
                    // in bug reports ARE6993X / QQS3MVDS).
                    MBoosterUiConstants.ForceRanges(controller.IsAxisMotorized(axisIndex),
                        out float mfMin, out float mfMax, out float dzMin, out float dzMax);
                    bool clamped = false;
                    if (cfg.MaxForceKg >= 0)
                    {
                        float mf = Math.Max(mfMin, Math.Min(mfMax, cfg.MaxForceKg));
                        if (Math.Abs(mf - cfg.MaxForceKg) > 0.0001f) { cfg.MaxForceKg = mf; clamped = true; }
                    }
                    if (cfg.DeadzoneKg >= 0)
                    {
                        float dz = Math.Max(dzMin, Math.Min(dzMax, cfg.DeadzoneKg));
                        if (Math.Abs(dz - cfg.DeadzoneKg) > 0.0001f) { cfg.DeadzoneKg = dz; clamped = true; }
                    }
                    if (clamped)
                    {
                        _plugin.SaveSettings();
                        PushMBoosterFeelCurve(cfg, controller, axisIndex);
                    }
                }
            }
            if (string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                && (axisIndex == _mboosterEffectPedalIndex || partner == _mboosterEffectPedalIndex))
            {
                UpdateMBoosterConfigVisibilityForRole();
                // Bounds just changed above — re-seed so a value that was
                // just clamped (or simply out of the new role's range on
                // screen) doesn't keep showing a stale number.
                using (_suppressor.Begin())
                    SeedMBoosterConfigControls(PeekMBoosterEffectTarget());
            }
        }

        /// <summary>
        /// Role change on a lane whose roles the hardware holds
        /// (<see cref="MBoosterDeviceController.DeviceRolesKnown"/>): swap it
        /// on the units — the pedal moves to the new role's HID axis and the
        /// pedal holding that role takes the old one. Disabled is not a role the
        /// hardware has, so it is refused. A lone pedal's role is also kept on
        /// the profile (see <see cref="MBoosterDeviceController.SyncPedalRole"/>).
        /// </summary>
        private void ChangeMBoosterDeviceRole(string identity, MBoosterDeviceController controller,
            MBoosterDeviceSettings s, int axisIndex, MBoosterRole role)
        {
            int roleIdx = MBoosterDeviceController.RoleIndexOf(role);
            bool motorized = controller.IsAxisMotorized(axisIndex);
            bool lone = controller.ConnectedAxisCount <= 1;
            if (roleIdx < 0 || !controller.AssignAxisRole(axisIndex, roleIdx))
            {
                RefreshMBoosterTab();
                return;
            }
            if (lone) s.Role = role;
            int newAxis = controller.AxisAfterRoleChange(axisIndex, roleIdx);
            if (newAxis != axisIndex && string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase))
                _mboosterRoleFollow = (identity, axisIndex, newAxis, controller.RoleMapVersion, DateTime.UtcNow + TimeSpan.FromSeconds(3));

            // The pedal now reports on newAxis, so its config is that axis's.
            // Clamp it to this pedal's hardware range; the re-apply that
            // follows the role-map read-back pushes it to the right unit.
            var cfg = global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.GetOrCreatePedalConfig(
                s, newAxis, lone ? newAxis : controller.SoleConnectedAxis());
            if (cfg != null)
            {
                MBoosterUiConstants.ForceRanges(motorized, out float mfMin, out float mfMax, out float dzMin, out float dzMax);
                if (cfg.MaxForceKg >= 0) cfg.MaxForceKg = Math.Max(mfMin, Math.Min(mfMax, cfg.MaxForceKg));
                if (cfg.DeadzoneKg >= 0) cfg.DeadzoneKg = Math.Max(dzMin, Math.Min(dzMax, cfg.DeadzoneKg));
            }
            _plugin!.SaveSettings();
        }

        // A role swap the selection follows once the host's role-map read-back
        // moves the pedals: (lane, axis A, axis B, map version at the write,
        // give-up time).
        private (string Identity, int A, int B, int Version, DateTime Until)? _mboosterRoleFollow;

        private void FollowMBoosterRoleSwap(MozaMBoosterRegistry registry)
        {
            var follow = _mboosterRoleFollow;
            if (follow == null) return;
            var (identity, a, b, version, until) = follow.Value;
            var c = registry.FindByIdentity(identity);
            if (c == null || DateTime.UtcNow > until) { _mboosterRoleFollow = null; return; }
            if (c.RoleMapVersion == version) return;
            _mboosterRoleFollow = null;
            if (!string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)) return;
            if (_mboosterEffectPedalIndex != a && _mboosterEffectPedalIndex != b) return;
            StopMBoosterPedalTests();
            _mboosterEffectPedalIndex = _mboosterEffectPedalIndex == a ? b : a;
            _mboosterUiSeeded = false;
        }

        // Auto-sleep timeout, 0 = off. Lane-wide: stored on the device
        // settings and written to the host unit only.
        private void MBoosterSleepSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            SetMBoosterSleepText(v);
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            s.SleepMinutes = v;
            var c = CurrentMBoosterController();
            if (c != null && c.IsConnected)
            {
                byte dev = c.HostDeviceId;
                c.QueueCalibWrite($"{dev:x2}:mbooster-sleep-minutes",
                    () => c.SendIntWrite("mbooster-sleep-minutes", v, dev));
            }
            _plugin.SaveSettings();
        }

        private void SetMBoosterSleepText(int minutes) =>
            SetValueText(MBoosterSleepValue, minutes == 0 ? Strings.Option_Off : minutes.ToString());

        /// <summary>Show the saved sleep timeout, else the host unit's own
        /// read-back (0 until it has answered).</summary>
        private void SeedMBoosterSleep(MBoosterDeviceSettings s, MBoosterDeviceController? c)
        {
            int v = s.SleepMinutes >= 0 ? s.SleepMinutes : c?.SleepMinutesReadback ?? -1;
            v = Math.Max(0, Math.Min((int)MBoosterSleepSlider.Maximum, v));
            using (_suppressor.Begin()) MBoosterSleepSlider.Value = v;
            SetMBoosterSleepText(v);
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
            ClearMBoosterTestToggles();
            MBoosterTcEnable.IsChecked       = fx?.TractionControl?.Enabled          ?? false;
            MBoosterTcFrequencySlider.Value  = fx?.TractionControl?.FrequencyHz      ?? MBoosterUiConstants.TractionControlFreqMinHz;
            SetValueText(MBoosterTcFrequencyValue, MBoosterTcFrequencySlider.Value.ToString("F0"));
            MBoosterTcIntensity.Value        = fx?.TractionControl?.IntensityPct     ?? 50;
            SetValueText(MBoosterTcIntensityValue, (fx?.TractionControl?.IntensityPct ?? 50).ToString());
            MBoosterWheelSpinEnable.IsChecked       = fx?.WheelSpin?.Enabled          ?? false;
            MBoosterWheelSpinFrequencySlider.Value  = fx?.WheelSpin?.FrequencyHz      ?? MBoosterUiConstants.WheelSpinFreqMinHz;
            SetValueText(MBoosterWheelSpinFrequencyValue, MBoosterWheelSpinFrequencySlider.Value.ToString("F0"));
            MBoosterWheelSpinIntensity.Value        = fx?.WheelSpin?.IntensityPct     ?? 50;
            SetValueText(MBoosterWheelSpinIntensityValue, (fx?.WheelSpin?.IntensityPct ?? 50).ToString());
            MBoosterGearShiftEnable.IsChecked       = fx?.GearShift?.Enabled          ?? false;
            MBoosterGearShiftFrequencySlider.Value  = fx?.GearShift?.FrequencyHz      ?? MBoosterUiConstants.GearShiftFreqMinHz;
            SetValueText(MBoosterGearShiftFrequencyValue, MBoosterGearShiftFrequencySlider.Value.ToString("F0"));
            MBoosterGearShiftIntensity.Value        = fx?.GearShift?.IntensityPct     ?? 50;
            SetValueText(MBoosterGearShiftIntensityValue, (fx?.GearShift?.IntensityPct ?? 50).ToString());
            MBoosterGearShiftVibrateOnNeutralCheck.IsChecked = fx?.GearShift?.VibrateOnNeutral ?? false;
            int gearShiftDebounceMs = fx?.GearShift?.DebounceMs ?? 500;
            MBoosterGearShiftDebounceSlider.Value = gearShiftDebounceMs;
            MBoosterGearShiftDebounceValue.Text = $"{gearShiftDebounceMs} ms";
            MBoosterAbsEnable.IsChecked       = fx?.Abs?.Enabled          ?? false;
            MBoosterAbsFrequencySlider.Value  = fx?.Abs?.FrequencyHz      ?? MBoosterUiConstants.AbsFreqMinHz;
            SetValueText(MBoosterAbsFrequencyValue, MBoosterAbsFrequencySlider.Value.ToString("F0"));
            MBoosterAbsIntensity.Value        = fx?.Abs?.IntensityPct     ?? 50;
            SetValueText(MBoosterAbsIntensityValue, (fx?.Abs?.IntensityPct ?? 50).ToString());
            MBoosterAbsSmoothness.Value       = fx?.Abs?.SmoothnessPct    ?? 100;
            SetValueText(MBoosterAbsSmoothnessValue, (fx?.Abs?.SmoothnessPct ?? 100).ToString());
            MBoosterLockupEnable.IsChecked = fx?.Lockup?.Enabled ?? false;
            MBoosterLockupFrequencySlider.Value = fx?.Lockup?.FrequencyHz ?? MBoosterUiConstants.LockupFreqMinHz;
            SetValueText(MBoosterLockupFrequencyValue, MBoosterLockupFrequencySlider.Value.ToString("F0"));
            MBoosterLockupIntensity.Value = fx?.Lockup?.IntensityPct ?? 50;
            SetValueText(MBoosterLockupIntensityValue, (fx?.Lockup?.IntensityPct ?? 50).ToString());
            MBoosterThresholdEnable.IsChecked = fx?.Threshold?.Enabled ?? false;
            MBoosterThresholdTriggerLevel.Value = fx?.Threshold?.TriggerLevelPct ?? 60;
            SetValueText(MBoosterThresholdTriggerLevelValue, (fx?.Threshold?.TriggerLevelPct ?? 60).ToString());
            MBoosterThresholdFrequencySlider.Value = fx?.Threshold?.FrequencyHz ?? MBoosterUiConstants.ThresholdFreqMinHz;
            SetValueText(MBoosterThresholdFrequencyValue, MBoosterThresholdFrequencySlider.Value.ToString("F0"));
            MBoosterThresholdIntensity.Value = fx?.Threshold?.IntensityPct ?? 50;
            SetValueText(MBoosterThresholdIntensityValue, (fx?.Threshold?.IntensityPct ?? 50).ToString());
            MBoosterThresholdDecay.Value = fx?.Threshold?.DecayPct ?? 20;
            SetValueText(MBoosterThresholdDecayValue, (fx?.Threshold?.DecayPct ?? 20).ToString());
            MBoosterBitePointEnable.IsChecked = fx?.BitePoint?.Enabled ?? false;
            MBoosterBitePointTriggerLevel.Value = fx?.BitePoint?.TriggerLevelPct ?? 50;
            SetValueText(MBoosterBitePointTriggerLevelValue, (fx?.BitePoint?.TriggerLevelPct ?? 50).ToString());
            MBoosterBitePointFrequencySlider.Value = fx?.BitePoint?.FrequencyHz ?? MBoosterUiConstants.BitePointFreqMinHz;
            SetValueText(MBoosterBitePointFrequencyValue, MBoosterBitePointFrequencySlider.Value.ToString("F0"));
            MBoosterBitePointSmoothness.Value = fx?.BitePoint?.SmoothnessPct ?? 100;
            SetValueText(MBoosterBitePointSmoothnessValue, (fx?.BitePoint?.SmoothnessPct ?? 100).ToString());
            MBoosterBitePointIntensity.Value = fx?.BitePoint?.IntensityPct ?? 50;
            SetValueText(MBoosterBitePointIntensityValue, (fx?.BitePoint?.IntensityPct ?? 50).ToString());
            MBoosterEngineEnable.IsChecked    = fx?.Engine?.Enabled       ?? false;
            MBoosterEngineIntensity.Value     = fx?.Engine?.IntensityPct  ?? 50;
            SetValueText(MBoosterEngineIntensityValue, (fx?.Engine?.IntensityPct ?? 50).ToString());
            MBoosterRoadTextureEnable.IsChecked = fx?.RoadTexture?.Enabled ?? false;
            MBoosterRoadTextureIntensity.Value = fx?.RoadTexture?.IntensityPct ?? 50;
            SetValueText(MBoosterRoadTextureIntensityValue, (fx?.RoadTexture?.IntensityPct ?? 50).ToString());
            MBoosterRoadTextureSmoothness.Value = fx?.RoadTexture?.SmoothnessPct ?? 50;
            SetValueText(MBoosterRoadTextureSmoothnessValue, (fx?.RoadTexture?.SmoothnessPct ?? 50).ToString());
            MBoosterRoadTextureGain.Value = fx?.RoadTexture?.GainPct ?? 100;
            SetValueText(MBoosterRoadTextureGainValue, (fx?.RoadTexture?.GainPct ?? 100).ToString());
            MBoosterGForceEnable.IsChecked = fx?.GForce?.Enabled ?? false;
            MBoosterGForceMaxTravel.Value = fx?.GForce?.MaxTravelMm ?? 10;
            SetValueText(MBoosterGForceMaxTravelValue, MBoosterGForceMaxTravel.Value.ToString("0.#"));
            MBoosterGForceResponseSpeed.Value = fx?.GForce?.ResponseSpeedPct ?? 50;
            SetValueText(MBoosterGForceResponseSpeedValue, (fx?.GForce?.ResponseSpeedPct ?? 50).ToString());
        }

        /// <summary>Seed the output curve, Sim Input Mapping and Pedal Feel controls
        /// from one pedal's config (master flat fields or a chained pedal's entry;
        /// null = defaults). Assumes the event suppressor is active.</summary>
        private void SeedMBoosterConfigControls(IMBoosterPedalConfig? fx)
        {
            SeedMBoosterOutputCurve(fx);
            // Sim Input Mapping
            float ratio = fx?.SensorOutputRatioPct ?? -1;
            MBoosterRatioSlider.Value = ratio >= 0 ? ratio : 0;
            SetValueText(MBoosterRatioValue, $"{MBoosterRatioSlider.Value:F0}%");
            float thr = fx?.MaxThresholdKg ?? -1;
            MBoosterMaxThresholdSlider.Value = thr >= 0 ? thr : 100;
            SetValueText(MBoosterMaxThresholdValue, MBoosterMaxThresholdSlider.Value.ToString("F0"));
            // Pedal Feel
            var inputCurve = (fx?.InputCurveY != null && fx.InputCurveY.Length == MBoosterUiConstants.PedalFeelNodeCount)
                ? fx.InputCurveY : MBoosterInputCurveDefault;
            MBoosterInputY1Slider.Value = inputCurve[0];
            MBoosterInputY2Slider.Value = inputCurve[1];
            MBoosterInputY3Slider.Value = inputCurve[2];
            MBoosterInputY4Slider.Value = inputCurve[3];
            MBoosterInputY5Slider.Value = inputCurve[4];
            MBoosterInputY6Slider.Value = inputCurve[5];
            var inputCurveX = (fx?.InputCurveX != null && fx.InputCurveX.Length == MBoosterUiConstants.PedalFeelNodeCount)
                ? fx.InputCurveX : MBoosterInputCurveDefault;
            MBoosterInputX1Slider.Value = inputCurveX[0];
            MBoosterInputX2Slider.Value = inputCurveX[1];
            MBoosterInputX3Slider.Value = inputCurveX[2];
            MBoosterInputX4Slider.Value = inputCurveX[3];
            MBoosterInputX5Slider.Value = inputCurveX[4];
            MBoosterInputX6Slider.Value = inputCurveX[5];
            float mf = fx?.MaxForceKg ?? -1;
            MBoosterMaxForceSlider.Value = mf >= 0 ? mf : 200;
            SetValueText(MBoosterMaxForceValue, MBoosterMaxForceSlider.Value.ToString("F0"));
            bool frictionEnabled = fx?.NaturalFrictionEnabled ?? true;
            MBoosterNaturalFrictionEnable.IsChecked = frictionEnabled;
            MBoosterNaturalFrictionSlider.IsEnabled = frictionEnabled;
            SeedMBoosterPedalFeelRegisters(fx, unsetOnly: false);

            var sd = fx?.SegmentedDamping;
            bool dampingEnabled = sd?.DampingEnabled ?? true;
            MBoosterSegDampEnable.IsChecked = dampingEnabled;
            ShowMBoosterDampingMode(dampingEnabled);
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

        /// <summary>Seed the Pedal Feel controls the unit reports back: the
        /// profile's value, else the read-back, else the default.
        /// <paramref name="unsetOnly"/> skips profile-set fields, so the
        /// per-tick refresh can pick up late read-backs without touching a
        /// control the user is editing.</summary>
        private void SeedMBoosterPedalFeelRegisters(IMBoosterPedalConfig? fx, bool unsetOnly)
        {
            var reported = MBoosterReportedPedalFeel();
            double Pick(float stored, string name, Func<int, double> decode, double fallback)
            {
                if (stored >= 0) return stored;
                int raw = reported(name);
                return raw >= 0 ? decode(raw) : fallback;
            }

            float ts = fx?.TravelStartMm ?? -1, te = fx?.TravelEndMm ?? -1;
            if (!unsetOnly || ts < 0 || te < 0)
            {
                MBoosterTravelRangeSlider.LowValue = Math.Round(Pick(ts, "mbooster-brake-travel-start",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeTravelMm, MBoosterUiConstants.TravelMinMm), 1);
                MBoosterTravelRangeSlider.HighValue = Math.Round(Pick(te, "mbooster-brake-travel-end",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeTravelMm, MBoosterUiConstants.TravelDefaultEndMm), 1);
            }

            void Int(Slider slider, TextBox box, float stored, string name, Func<int, double> decode, double fallback)
            {
                if (unsetOnly && stored >= 0) return;
                slider.Value = Math.Round(Pick(stored, name, decode, fallback));
                SetValueText(box, slider.Value.ToString("F0"));
            }
            Int(MBoosterEndstopFrontSlider, MBoosterEndstopFrontValue, fx?.EndstopFrontStiffness ?? -1,
                "mbooster-brake-endstop-front", global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeEndstopStiffness, 1);
            Int(MBoosterEndstopEndSlider, MBoosterEndstopEndValue, fx?.EndstopEndStiffness ?? -1,
                "mbooster-brake-endstop-end", global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeEndstopStiffness, 1);
            Int(MBoosterNaturalFrictionSlider, MBoosterNaturalFrictionValue, fx?.NaturalFrictionPct ?? -1,
                "mbooster-brake-friction-0", global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeFrictionPct, 0);
            // Plain Virtual Damping (0xAD) — separate registers from the 0xB7
            // segments; see MBoosterDeviceSettings.DampingPressPct.
            Int(MBoosterDampingPressSlider, MBoosterDampingPressValue, fx?.DampingPressPct ?? -1,
                "mbooster-brake-damping-press", global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeFrictionPct, 0);
            Int(MBoosterDampingReleaseSlider, MBoosterDampingReleaseValue, fx?.DampingReleasePct ?? -1,
                "mbooster-brake-damping-release", global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeFrictionPct, 0);

            float dz = fx?.DeadzoneKg ?? -1;
            if (!unsetOnly || dz < 0)
            {
                MBoosterDeadzoneSlider.Value = Math.Round(Pick(dz, "mbooster-brake-deadzone",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.DecodeThresholdKg, 0), 1);
                SetValueText(MBoosterDeadzoneValue, MBoosterDeadzoneSlider.Value.ToString("F1"));
            }
        }

        private MBoosterDeviceController? CurrentMBoosterController()
        {
            return _plugin?.MBoosterRegistry?.FindByIdentity(_mboosterSelectedIdentity ?? "");
        }

        /// <summary>Device row label: short identity and port.</summary>
        private static string BuildMBoosterComboLabel(MBoosterDeviceController c)
            => $"{MBoosterDeviceController.ShortIdentity(c.Identity)} ({c.PortName})";

    }
}
