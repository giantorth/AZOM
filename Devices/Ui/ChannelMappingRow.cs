using System;
using System.Collections.Generic;
using System.ComponentModel;
using MozaPlugin.Telemetry;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;

namespace MozaPlugin.Devices.Ui
{
    /// <summary>
    /// Row backing the per-wheel telemetry channel-mapping list. Holds the
    /// channel metadata (name/url/package level/compression) read from the
    /// wheel's catalog, the user-edited SimHub property/formula mapping, and the
    /// inline-editor state. Two edit modes share one stored string
    /// (<see cref="SimHubProperty"/>): the <b>simple</b> inline property list
    /// (pencil) and the <b>advanced</b> SimHub formula dialog (ƒₓ button, edits
    /// the bound <see cref="Expression"/>).
    /// </summary>
    internal sealed class ChannelMappingRow : INotifyPropertyChanged
    {
        // Cap simple-editor filtered results — protects against substrings like
        // "data" matching half the property universe. The ListBox virtualizes,
        // but an unfiltered first-keystroke render would still cost layout time.
        private const int MaxFilteredResults = 500;

        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public int PackageLevel { get; set; }
        public string Compression { get; set; } = "";

        // ── FSR V1 dashboard-field rows (group-0x42) ────────────────────
        // Set by ChannelMappingRowFactory.BuildFromFsr1Catalog. When IsFsr1 is
        // true the row maps a fixed dashboard FIELD (RecordKey + FieldId) rather
        // than a tier-def channel URL, and carries a scale (InMin..InMax mapped to
        // the field's full-scale capability shown by CapabilityText).
        public bool IsFsr1 { get; set; }
        /// <summary>True for a CM1 base-bridged dash field (group-0x35). Flat — uses
        /// FieldId only (no RecordKey); the row maps the field to a SimHub property.</summary>
        public bool IsCm1 { get; set; }
        public string RecordKey { get; set; } = "";
        public string FieldId { get; set; } = "";
        /// <summary>Human-readable field output capability, e.g. "0–255".</summary>
        public string CapabilityText { get; set; } = "";

        private double _scale = 1;
        /// <summary>Per-field gain: emitted value = <c>raw·Scale + Bias</c>. 1 = no gain.</summary>
        public double Scale
        {
            get => _scale;
            set
            {
                if (_scale.Equals(value)) return;
                _scale = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
            }
        }

        private double _bias;
        /// <summary>Per-field offset added after Scale. 0 = none. (FSR1 only; CM1 uses Scale alone.)</summary>
        public double Bias
        {
            get => _bias;
            set
            {
                if (_bias.Equals(value)) return;
                _bias = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bias)));
            }
        }

        // The field's catalog gain, so the row can show the gain actually applied (lap
        // times ×1000, tyre temps +300, fuel laps ×100) instead of a flat 1/0, and so a
        // save can tell "still at default" from "user override" — persisting Scale=1 over
        // a ×1000 default silently drops the unit conversion.
        /// <summary>Catalog default for <see cref="Scale"/> (1 = none).</summary>
        public double DefaultScale { get; set; } = 1.0;
        /// <summary>Catalog default for <see cref="Bias"/> (0 = none).</summary>
        public double DefaultBias { get; set; }


        private double _inMin;
        public double InMin
        {
            get => _inMin;
            set
            {
                if (_inMin.Equals(value)) return;
                _inMin = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InMin)));
            }
        }

        private double _inMax = 1;
        public double InMax
        {
            get => _inMax;
            set
            {
                if (_inMax.Equals(value)) return;
                _inMax = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InMax)));
            }
        }

        private string _simHubProperty = "";
        /// <summary>
        /// The persisted mapping string — a plain SimHub property path
        /// (<c>DataCorePlugin.GameData.Rpms</c>) or a SimHub formula
        /// (<c>[SpeedKmh] * 0.621</c>, or a <c>js:</c> JavaScript expression).
        /// Source of truth; <see cref="Expression"/> is kept in sync both ways.
        /// </summary>
        public string SimHubProperty
        {
            get => _simHubProperty;
            set
            {
                var v = (value ?? "").Trim();
                if (_simHubProperty == v) return;
                _simHubProperty = v;
                // Keep the bound ExpressionValue in sync without re-firing back
                // into us (the FormulaPicker mutates Expression; this is the
                // reverse direction — Reset, repopulate, programmatic set).
                if (_expression != null && !_syncingExpression)
                {
                    _syncingExpression = true;
                    try { ApplyStoredToExpression(_expression, v); }
                    finally { _syncingExpression = false; }
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SimHubProperty)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOverridden)));
                // Clear the live value so the next refresh repopulates from the
                // new path/formula and the user doesn't see a stale value matched
                // against an unrelated source.
                CurrentValueText = "";
            }
        }

        /// <summary>The pristine <c>Data/Telemetry.json</c> default for this channel —
        /// what a reset returns to. Master channel mapper only; left empty on the
        /// device-page rows, which never bind <see cref="IsOverridden"/>.</summary>
        public string DefaultProperty { get; set; } = "";

        /// <summary>True when <see cref="SimHubProperty"/> deviates from
        /// <see cref="DefaultProperty"/>. Master channel mapper only.</summary>
        public bool IsOverridden
            => !string.Equals(SimHubProperty, DefaultProperty, StringComparison.Ordinal);

        // ── Advanced editing (SimHub formula dialog) ───────────────────────
        // The ƒₓ button opens SimHub's BindingEditor against Engine + a working
        // copy of Expression; on OK the code-behind calls ApplyEditedFormula,
        // which serializes the result back into SimHubProperty (the persisted
        // form) and fires the per-row persist listener. SimHubProperty stays the
        // source of truth so persistence/back-compat are unchanged.

        /// <summary>Shared SimHub formula engine (set by the row factory). Null if
        /// engine construction failed; the ƒₓ button is then disabled.</summary>
        public NCalcEngineBase? Engine { get; set; }

        private ExpressionValue? _expression;
        /// <summary>The mapping as a SimHub <see cref="ExpressionValue"/>, kept in
        /// sync with <see cref="SimHubProperty"/>. A bare property path is wrapped
        /// as <c>[path]</c> so the formula dialog opens on valid NCalc.</summary>
        public ExpressionValue Expression
        {
            get
            {
                if (_expression == null) _expression = MakeExpression(_simHubProperty);
                return _expression;
            }
        }

        // Sync direction string -> Expression only; the dialog never mutates the
        // row's Expression directly (it works on a clone), so no reverse listener
        // is needed — the code-behind calls ApplyEditedFormula on OK.
        private bool _syncingExpression;

        /// <summary>Apply a formula chosen in the advanced dialog: set the bound
        /// Expression in place (so the live object the next ƒₓ open reads is
        /// current) and serialize it once into SimHubProperty (firing persistence).
        /// A sole <c>[property]</c> is unwrapped to a bare path; JavaScript is
        /// <c>js:</c>-prefixed.</summary>
        public void ApplyEditedFormula(string? expression, bool useJavascript)
        {
            var ev = Expression;
            _syncingExpression = true;
            try
            {
                ev.UseJavascript = useJavascript;
                ev.Expression = expression ?? "";
            }
            finally { _syncingExpression = false; }
            SimHubProperty = SerializeExpression(ev);
        }

        // Build a fresh ExpressionValue from a stored mapping string.
        private static ExpressionValue MakeExpression(string? stored)
        {
            var ev = new ExpressionValue();
            ApplyStoredToExpression(ev, stored);
            return ev;
        }

        // Mutate an existing ExpressionValue in place to match a stored string
        // (the FormulaPicker holds a reference to this object, so we must not swap
        // it). A bare property path is WRAPPED as [path] so the NCalc editor sees
        // a valid single-property formula — existing mappings persisted before
        // this feature are bare paths and would otherwise be invalid NCalc. A
        // string that already looks like a formula (brackets/operators/js:) is
        // used verbatim. UseJavascript's setter flips the interpreter for us.
        private static void ApplyStoredToExpression(ExpressionValue ev, string? stored)
        {
            var s = (stored ?? "").Trim();
            if (s.Length == 0) { ev.UseJavascript = false; ev.Expression = ""; return; }
            if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
            {
                ev.UseJavascript = true;
                ev.Expression = s.Substring(3);
                return;
            }
            ev.UseJavascript = false;
            ev.Expression = NCalcExpressionEvaluator.LooksLikeExpression(s) ? s : "[" + s + "]";
        }

        // Serialize an ExpressionValue back to the persisted string form. A sole
        // [property] reference is UNWRAPPED to its bare path so existing mappings
        // keep their plain stored form (and the resolver's fast GetPropertyValue
        // path); a real formula ([a]+[b], functions, js:, …) is stored verbatim
        // (js:-prefixed for JavaScript so MakeExpression restores the interpreter).
        private static string SerializeExpression(ExpressionValue ev)
        {
            var expr = (ev.Expression ?? "").Trim();
            if (expr.Length == 0) return "";
            if (ev.UseJavascript) return "js:" + expr;
            if (expr.Length >= 2 && expr[0] == '[' && expr[expr.Length - 1] == ']')
            {
                var inner = expr.Substring(1, expr.Length - 2);
                if (inner.IndexOf('[') < 0 && inner.IndexOf(']') < 0
                    && !NCalcExpressionEvaluator.LooksLikeExpression(inner))
                    return inner;
            }
            return expr;
        }

        // ── Simple editor: property list (pencil) ──────────────────────────

        /// <summary>Master snapshot of every SimHub property name (set once by the
        /// row factory). The inline ListBox binds to <see cref="FilteredProperties"/>,
        /// filtered from this on each <see cref="EditFilter"/> keystroke.</summary>
        public IReadOnlyList<string> AllProperties { get; set; } = KnownSimHubProperties.Paths;

        private IReadOnlyList<string> _filteredProperties = Array.Empty<string>();
        /// <summary>Live filtered subset of <see cref="AllProperties"/>, bound to the
        /// simple editor's ListBox. Immutable-list swap per keystroke.</summary>
        public IReadOnlyList<string> FilteredProperties
        {
            get => _filteredProperties;
            private set
            {
                _filteredProperties = value ?? Array.Empty<string>();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredProperties)));
            }
        }

        private string _currentValueText = "";
        public string CurrentValueText
        {
            get => _currentValueText;
            set
            {
                if (_currentValueText == value) return;
                _currentValueText = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValueText)));
            }
        }

        // ── Inline simple-editor state ─────────────────────────────────────

        private bool _isEditing;
        /// <summary>Drives the inline simple-editor panel's visibility: a searchable
        /// property list (all rows) plus the FSR1/CM1 boundary/scale/bias steppers.
        /// Toggled by the row's pencil via <see cref="BeginEdit"/> /
        /// <see cref="CommitEdit"/> / <see cref="CancelEdit"/>.</summary>
        public bool IsEditing
        {
            get => _isEditing;
            private set
            {
                if (_isEditing == value) return;
                _isEditing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
            }
        }

        private string _editFilter = "";
        /// <summary>Filter text inside the simple editor; substring, ordinal-ignore-case,
        /// capped at <c>MaxFilteredResults</c>. Empty = full list.</summary>
        public string EditFilter
        {
            get => _editFilter;
            set
            {
                var v = value ?? "";
                if (_editFilter == v) return;
                _editFilter = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditFilter)));
                UpdateFilteredProperties();
            }
        }

        private string _pendingProperty = "";
        /// <summary>ListBox selection inside the simple editor. Applied to
        /// <see cref="SimHubProperty"/> on <see cref="CommitEdit"/>.</summary>
        public string PendingProperty
        {
            get => _pendingProperty;
            set
            {
                var v = value ?? "";
                if (_pendingProperty == v) return;
                _pendingProperty = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingProperty)));
            }
        }

        /// <summary>Open the simple editor: empty filter (full list) + pending seeded
        /// to the current mapping so the user's choice is highlighted.</summary>
        public void BeginEdit()
        {
            _editFilter = "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditFilter)));
            PendingProperty = SimHubProperty;
            UpdateFilteredProperties();
            IsEditing = true;
        }

        /// <summary>Apply the picked property to <see cref="SimHubProperty"/> (firing
        /// persistence) and collapse the editor.</summary>
        public void CommitEdit()
        {
            SimHubProperty = PendingProperty;
            IsEditing = false;
        }

        /// <summary>Discard the pending selection and collapse the editor.</summary>
        public void CancelEdit()
        {
            PendingProperty = SimHubProperty;
            IsEditing = false;
        }

        private void UpdateFilteredProperties()
        {
            string query = _editFilter;
            var src = AllProperties;
            if (src == null || src.Count == 0)
            {
                FilteredProperties = Array.Empty<string>();
                return;
            }

            bool noFilter = string.IsNullOrEmpty(query);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(Math.Min(src.Count, MaxFilteredResults));
            for (int i = 0; i < src.Count; i++)
            {
                var p = src[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (!noFilter && p.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!seen.Add(p)) continue;
                list.Add(p);
                if (list.Count >= MaxFilteredResults) break;
            }
            FilteredProperties = list;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
