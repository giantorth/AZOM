using System;
using System.Collections.Generic;
using MozaPlugin.Devices;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// One row in the import-confirmation diff. The <see cref="Apply"/>
    /// closure encapsulates the actual write site so heterogeneous fields
    /// (profile ints, mBooster nested objects, multi-field FFB curve writes)
    /// share the same apply loop without a giant switch.
    /// </summary>
    public sealed class FieldDiff
    {
        public string Label { get; }
        public string OldDisplay { get; }
        public string NewDisplay { get; }
        public Action Apply { get; }

        /// <summary>True when old and new differ — used by the dialog to
        /// hide no-op rows so the user only sees real changes.</summary>
        public bool Changed => !string.Equals(OldDisplay, NewDisplay, StringComparison.Ordinal);

        public FieldDiff(string label, string oldDisplay, string newDisplay, Action apply)
        {
            Label = label ?? "";
            OldDisplay = oldDisplay ?? "";
            NewDisplay = newDisplay ?? "";
            Apply = apply ?? (() => { });
        }
    }

    /// <summary>
    /// Result of mapping a PitHouse preset onto the current profile/hardware
    /// state. Carries the per-field diffs that <em>do</em> apply, the list of
    /// preset keys that have no plugin equivalent (surfaced as "Not imported"),
    /// and an optional fatal error (e.g. "no mBooster with this role attached")
    /// which short-circuits the apply path.
    /// </summary>
    public sealed class ImportPlan
    {
        public List<FieldDiff> Diffs { get; } = new List<FieldDiff>();
        public List<string> NotImported { get; } = new List<string>();
        public string? FatalError { get; set; }

        /// <summary>
        /// mBooster controllers whose settings were touched by one or more
        /// diffs in this plan. After all diffs have been applied, the caller
        /// must invoke <c>MozaPlugin.ApplyMBoosterToHardware(controller,
        /// controller.CurrentSettings)</c> on each to push the new
        /// calibration to the device — the standard
        /// <c>ApplyProfileHardware</c> path does not cover mBooster.
        /// </summary>
        public HashSet<MBoosterDeviceController> TouchedMBoosters { get; }
            = new HashSet<MBoosterDeviceController>();

        /// <summary>
        /// PitHouse deviceParams keys the mapper has already considered (either
        /// mapped to a profile field or explicitly noted as "not imported").
        /// At the end of BuildPlan the mapper sweeps the preset's deviceParams
        /// and adds any remaining unhandled keys to <see cref="NotImported"/>
        /// — so a new PitHouse field can never silently disappear.
        /// </summary>
        public HashSet<string> ConsideredKeys { get; }
            = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True when there is at least one row whose old != new.</summary>
        public bool HasChanges
        {
            get
            {
                foreach (var d in Diffs) if (d.Changed) return true;
                return false;
            }
        }
    }
}
