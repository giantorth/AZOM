using System;
using System.Collections.Generic;
using System.ComponentModel;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Devices.WheelUi
{
    /// <summary>
    /// Row backing the per-wheel telemetry channel-mapping list. Holds the
    /// channel metadata (name/url/package level/compression) read from the
    /// wheel's catalog, the user-edited SimHub property mapping, and the
    /// inline-editor state (filter text + pending selection + visibility flag).
    /// </summary>
    internal sealed class ChannelMappingRow : INotifyPropertyChanged
    {
        // Cap filtered results — protects against substrings like "data"
        // matching half the universe. The ListBox virtualizes, but a 1500-item
        // unfiltered render with no virtualization at all (just-typed first
        // char) would still cost layout time; this keeps the inline editor
        // snappy.
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
        public string SimHubProperty
        {
            get => _simHubProperty;
            set
            {
                var v = (value ?? "").Trim();
                if (_simHubProperty == v) return;
                _simHubProperty = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SimHubProperty)));
                // Clear the live value so the next refresh repopulates from the
                // new path and the user doesn't see a stale value matched
                // against an unrelated property name.
                CurrentValueText = "";
            }
        }

        /// <summary>
        /// Master snapshot of every SimHub property name (set once by the populator
        /// from <see cref="MozaPlugin.GetAllSimHubPropertyNames"/>). The inline
        /// editor's ListBox does NOT bind to this directly — filter into
        /// <see cref="FilteredProperties"/> on each EditFilter keystroke.
        /// </summary>
        public IReadOnlyList<string> AllProperties { get; set; } = KnownSimHubProperties.Paths;

        private IReadOnlyList<string> _filteredProperties = Array.Empty<string>();
        /// <summary>
        /// Live filtered subset of <see cref="AllProperties"/>. Bound to the
        /// inline editor's ListBox.ItemsSource. Immutable-list swap on each
        /// keystroke — Clear+Add would race a mid-click on a list item.
        /// </summary>
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

        // ── Inline-editor state ────────────────────────────────────────

        private bool _isEditing;
        /// <summary>
        /// Drives the inline editor panel's visibility. Set to true via
        /// <see cref="BeginEdit"/> (user clicked the pencil icon), back to
        /// false via <see cref="CommitEdit"/> / <see cref="CancelEdit"/>.
        /// </summary>
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
        /// <summary>
        /// TextBox-backed filter inside the inline editor. Empty string = show
        /// the full property list (the user has explicitly opened the editor,
        /// so there's no "min 3 chars" gate). Substring + ordinal-ignore-case
        /// match against <see cref="AllProperties"/>, capped at
        /// <c>MaxFilteredResults</c>.
        /// </summary>
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
        /// <summary>
        /// ListBox-backed selection inside the inline editor. Two-way bound
        /// to ListBox.SelectedItem. Applied to <see cref="SimHubProperty"/>
        /// on <see cref="CommitEdit"/>; discarded on <see cref="CancelEdit"/>.
        /// </summary>
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

        /// <summary>
        /// Open the inline editor: seed the filter empty (full list visible)
        /// and the pending selection to the row's current SimHub property so
        /// the user sees their current choice highlighted in the list.
        /// </summary>
        public void BeginEdit()
        {
            _editFilter = "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditFilter)));
            PendingProperty = SimHubProperty;
            UpdateFilteredProperties();
            IsEditing = true;
        }

        /// <summary>
        /// Apply the pending selection to <see cref="SimHubProperty"/> and
        /// collapse the editor. The SimHubProperty setter raises
        /// PropertyChanged, which the populator's listener wires through to
        /// <see cref="MozaPlugin.SetChannelMapping"/> for persistence.
        /// </summary>
        public void CommitEdit()
        {
            SimHubProperty = PendingProperty;
            IsEditing = false;
        }

        /// <summary>
        /// Discard the pending selection and collapse the editor.
        /// </summary>
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

            // Empty filter = full list (capped). The user explicitly opened
            // the editor so a flood of options is the expected starting point.
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
