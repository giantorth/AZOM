using System;
using MozaPlugin.UI.Import;

namespace MozaPlugin
{
    /// <summary>
    /// Partial-class continuation of <see cref="SettingsControl"/> that holds
    /// the import-apply path. The Import tab hosts a
    /// <see cref="PitHouseImportControl"/> which raises ApplyRequested with the
    /// chosen plan; <see cref="ApplyImportPlan"/> merges the PitHouse preset
    /// into the active <see cref="MozaProfile"/> and pushes to live hardware via
    /// the existing apply path.
    /// </summary>
    public partial class SettingsControl
    {
        private void ApplyImportPlan(ImportPlan plan)
        {
            // 1) Mutate the active profile + mBooster settings via the per-diff
            //    closures. Apply errors on individual rows shouldn't abort the
            //    rest — log and continue.
            foreach (var diff in plan.Diffs)
            {
                if (!diff.Changed) continue;
                try { diff.Apply(); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM/Import] apply '{diff.Label}': {ex.Message}");
                }
            }

            // 2) Push base / wheel / pedal / handbrake / dash / ambient / ab9
            //    settings to live hardware. _plugin.ApplyProfile internally
            //    routes through HardwareApplier.ApplyProfileHardware (per-
            //    subsystem detection-gated writes) and then PersistSettings —
            //    so this single call handles both the hardware push and the
            //    profile save.
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile != null)
            {
                try { _plugin.ApplyProfile(profile); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM/Import] ApplyProfile: {ex.Message}");
                }
            }

            // 3) mBooster lives outside ApplyProfileHardware. Push each touched
            //    controller explicitly so newly-imported calibration reaches
            //    the device without waiting for a reconnect edge.
            foreach (var controller in plan.TouchedMBoosters)
            {
                if (controller == null) continue;
                try
                {
                    var settings = controller.CurrentSettings;
                    if (settings != null)
                        _plugin.ApplyMBoosterToHardware(controller, settings);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM/Import] ApplyMBoosterToHardware: {ex.Message}");
                }
            }

            // 4) Trigger a UI refresh so the sliders snap to the imported
            //    values without waiting for the 500 ms tick.
            try { RefreshDisplay(this, EventArgs.Empty); }
            catch { /* refresh is cosmetic — never throw out of the import path */ }
        }
    }
}
