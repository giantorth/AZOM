using System;
using System.Collections.Generic;
using System.ComponentModel;
using MozaPlugin.Devices;
using MozaPlugin.Telemetry;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.UI
{
    /// <summary>
    /// UI-layer wrapper around one <see cref="MBoosterCustomEffect"/> for the
    /// mBooster tab's dynamic "Custom Effects (Experimental)" list —
    /// <see cref="MBoosterCustomEffect"/> itself stays a plain POCO (matching
    /// every other mBooster settings type, persisted via Newtonsoft.Json), so
    /// this row supplies the <see cref="INotifyPropertyChanged"/> binding
    /// surface an <c>ItemsControl</c> template needs, plus a callback to
    /// persist on every edit. Mirrors <c>Devices/Ui/ChannelMappingRow.cs</c>'s
    /// role for the (unrelated) channel-mapping list.
    /// </summary>
    internal sealed class MBoosterCustomEffectRow : INotifyPropertyChanged
    {
        public MBoosterCustomEffect Model { get; }
        private readonly Action _onChanged;
        private readonly Action<string, bool> _onTestToggle;

        public MBoosterCustomEffectRow(MBoosterCustomEffect model, Action onChanged, Action<string, bool> onTestToggle)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
            _onTestToggle = onTestToggle ?? throw new ArgumentNullException(nameof(onTestToggle));
        }

        // Sustained Test toggle — never persisted (mirrors the five built-in
        // effects' Test toggles), always starts unchecked because a fresh row
        // instance is created every time the list is repopulated. Forwards
        // to the controller/worker rather than the model — there is nothing
        // to save here.
        private bool _testActive;
        public bool TestActive
        {
            get => _testActive;
            set
            {
                if (_testActive == value) return;
                _testActive = value;
                Raise(nameof(TestActive));
                _onTestToggle(Id, value);
            }
        }

        public string Id => Model.Id;

        public string Name
        {
            get => Model.Name;
            set
            {
                var v = value ?? "";
                if (Model.Name == v) return;
                Model.Name = v;
                Raise(nameof(Name));
                _onChanged();
            }
        }

        public bool Enabled
        {
            get => Model.Enabled;
            set
            {
                if (Model.Enabled == value) return;
                Model.Enabled = value;
                Raise(nameof(Enabled));
                _onChanged();
            }
        }

        // ── Formula editing — reuses SimHub's own property picker / NCalc
        // formula dialog, same dual-mode (pencil + ƒₓ) surface the telemetry
        // channel-mapper's ChannelMappingRow.cs already offers. Formula stays
        // the single persisted string (a bare property path or a full
        // NCalc/js: formula); Expression is a bound ExpressionValue kept in
        // sync with it both ways so SimHub's BindingEditor dialog always
        // opens on valid NCalc.

        public string Formula
        {
            get => Model.Formula;
            set
            {
                var v = (value ?? "").Trim();
                if (Model.Formula == v) return;
                Model.Formula = v;
                // Keep the bound ExpressionValue in sync without re-firing
                // back into us — this is the reverse direction from
                // ApplyEditedFormula (dialog OK) and CommitEdit (simple
                // picker), both of which set Formula themselves.
                if (_expression != null && !_syncingExpression)
                {
                    _syncingExpression = true;
                    try { ApplyStoredToExpression(_expression, v); }
                    finally { _syncingExpression = false; }
                }
                Raise(nameof(Formula));
                _onChanged();
            }
        }

        /// <summary>Shared SimHub formula engine, set by the code-behind
        /// (<c>MozaPlugin.ChannelFormulaEngine</c>). Null if engine
        /// construction failed; the ƒₓ button is then disabled.</summary>
        public NCalcEngineBase? Engine { get; set; }

        /// <summary>Master snapshot of every SimHub property name, set once
        /// by the code-behind when the list is (re)populated. The simple
        /// editor's ListBox binds to <see cref="FilteredProperties"/>,
        /// filtered from this on each <see cref="EditFilter"/> keystroke.</summary>
        public IReadOnlyList<string> AllProperties { get; set; } = Array.Empty<string>();

        private ExpressionValue? _expression;
        /// <summary>Formula as a SimHub <see cref="ExpressionValue"/>, kept in
        /// sync with <see cref="Formula"/>. A bare property path is wrapped
        /// as <c>[path]</c> so the formula dialog opens on valid NCalc.</summary>
        public ExpressionValue Expression
        {
            get
            {
                if (_expression == null) _expression = MakeExpression(Model.Formula);
                return _expression;
            }
        }

        private bool _syncingExpression;

        /// <summary>Apply a formula chosen in the advanced (ƒₓ) dialog: set the
        /// bound Expression in place (so the live object the next ƒₓ open reads
        /// is current) and serialize it once into Formula (firing persistence).
        /// A sole <c>[property]</c> is unwrapped to a bare path; JavaScript is
        /// <c>js:</c>-prefixed. Mirrors ChannelMappingRow.ApplyEditedFormula.</summary>
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
            Formula = SerializeExpression(ev);
        }

        private static ExpressionValue MakeExpression(string? stored)
        {
            var ev = new ExpressionValue();
            ApplyStoredToExpression(ev, stored);
            return ev;
        }

        // Mutate an existing ExpressionValue in place to match a stored string
        // (the BindingEditor dialog holds a reference to this object, so it
        // must not be swapped). A bare property path is WRAPPED as [path] so
        // the NCalc editor sees a valid single-property formula.
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

        // Serialize an ExpressionValue back to the persisted string form. A
        // sole [property] reference is UNWRAPPED to its bare path so a plain
        // property pick keeps the simple stored form; a real formula
        // ([a]+[b], functions, js:, …) is stored verbatim.
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

        // ── Simple editor: inline searchable property list (pencil) ────────
        private const int MaxFilteredResults = 500;

        private IReadOnlyList<string> _filteredProperties = Array.Empty<string>();
        /// <summary>Live filtered subset of <see cref="AllProperties"/>, bound to the
        /// simple editor's ListBox. Immutable-list swap per keystroke.</summary>
        public IReadOnlyList<string> FilteredProperties
        {
            get => _filteredProperties;
            private set
            {
                _filteredProperties = value ?? Array.Empty<string>();
                Raise(nameof(FilteredProperties));
            }
        }

        private bool _isEditing;
        /// <summary>Drives the inline simple-editor panel's visibility.</summary>
        public bool IsEditing
        {
            get => _isEditing;
            private set
            {
                if (_isEditing == value) return;
                _isEditing = value;
                Raise(nameof(IsEditing));
            }
        }

        private string _editFilter = "";
        /// <summary>Filter text inside the simple editor; substring, ordinal-ignore-case,
        /// capped at <see cref="MaxFilteredResults"/>. Empty = full list.</summary>
        public string EditFilter
        {
            get => _editFilter;
            set
            {
                var v = value ?? "";
                if (_editFilter == v) return;
                _editFilter = v;
                Raise(nameof(EditFilter));
                UpdateFilteredProperties();
            }
        }

        private string _pendingProperty = "";
        /// <summary>ListBox selection inside the simple editor. Applied to
        /// <see cref="Formula"/> on <see cref="CommitEdit"/>.</summary>
        public string PendingProperty
        {
            get => _pendingProperty;
            set
            {
                var v = value ?? "";
                if (_pendingProperty == v) return;
                _pendingProperty = v;
                Raise(nameof(PendingProperty));
            }
        }

        /// <summary>Open the simple editor: empty filter (full list) + pending seeded
        /// to the current formula so the user's choice is highlighted.</summary>
        public void BeginEdit()
        {
            _editFilter = "";
            Raise(nameof(EditFilter));
            PendingProperty = Formula;
            UpdateFilteredProperties();
            IsEditing = true;
        }

        /// <summary>Apply the picked property to <see cref="Formula"/> (firing
        /// persistence) and collapse the editor.</summary>
        public void CommitEdit()
        {
            Formula = PendingProperty;
            IsEditing = false;
        }

        /// <summary>Discard the pending selection and collapse the editor.</summary>
        public void CancelEdit()
        {
            PendingProperty = Formula;
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

        public bool ThresholdEnabled
        {
            get => Model.ThresholdEnabled;
            set
            {
                if (Model.ThresholdEnabled == value) return;
                Model.ThresholdEnabled = value;
                Raise(nameof(ThresholdEnabled));
                _onChanged();
            }
        }

        public double Threshold
        {
            get => Model.Threshold;
            set
            {
                if (Model.Threshold.Equals(value)) return;
                Model.Threshold = value;
                Raise(nameof(Threshold));
                Raise(nameof(ThresholdDisplay));
                _onChanged();
            }
        }

        public double FrequencyHz
        {
            get => Model.FrequencyHz;
            set
            {
                float v = (float)value;
                if (Model.FrequencyHz.Equals(v)) return;
                Model.FrequencyHz = v;
                Raise(nameof(FrequencyHz));
                Raise(nameof(FrequencyDisplay));
                _onChanged();
            }
        }

        public double IntensityPct
        {
            get => Model.IntensityPct;
            set
            {
                int v = (int)Math.Round(value);
                if (Model.IntensityPct == v) return;
                Model.IntensityPct = v;
                Raise(nameof(IntensityPct));
                Raise(nameof(IntensityDisplay));
                _onChanged();
            }
        }

        public string FrequencyDisplay => $"{FrequencyHz:F0} Hz";
        public string IntensityDisplay => $"{IntensityPct:F0}%";
        public string ThresholdDisplay => Threshold.ToString("F1");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
