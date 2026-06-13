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
        /// <summary>True for a net-new synthetic split field (not in the static catalog).
        /// Such a row offers "Remove split" instead of "Reset field" and always persists a
        /// full mapping with an explicit byte span.</summary>
        public bool IsSynthetic { get; set; }
        public string RecordKey { get; set; } = "";
        public string FieldId { get; set; } = "";
        /// <summary>Human-readable field output capability, e.g. "0–255".</summary>
        public string CapabilityText { get; set; } = "";
        /// <summary>Record payload length (wire len byte) — caps the highest editable data
        /// byte at <c>PayloadLen-1</c>. Set by the FSR1 factory; 0 for non-FSR1 rows.</summary>
        public int PayloadLen { get; set; }

        // ── FSR1 boundary / scale editor state (coupled-divider model) ──────
        // An FSR1 record is a CONTIGUOUS, gapless partition of data bytes [5, PayloadLen-1]
        // (that is how PitHouse packs it — every byte belongs to exactly one field). So the
        // editor treats each field as separated from its neighbours by a shared divider:
        // stepping a boundary moves that one divider, reapportioning a single byte between
        // this field and the adjacent one. PrevField/NextField are wired by the FSR1 factory
        // in field order; null at the record's fixed edges (first field's left edge = 5,
        // last field's right edge = PayloadLen-1, neither of which can move). Span mutation
        // goes through SetSpan (raises UI only); the code-behind owns persistence + probe so
        // the per-property persist listener never double-fires on a coupled move.

        /// <summary>Adjacent field sharing this field's LEFT divider, or null at the record start.</summary>
        public ChannelMappingRow? PrevField { get; set; }
        /// <summary>Adjacent field sharing this field's RIGHT divider, or null at the record end.</summary>
        public ChannelMappingRow? NextField { get; set; }

        /// <summary>Payload-relative first data byte of the field (inclusive).</summary>
        public int Start { get; set; } = 5;
        /// <summary>Payload-relative last data byte of the field (inclusive).</summary>
        public int End { get; set; } = 5;

        private bool _littleEndian;
        /// <summary>Width-2 byte order. Ignored for width 1/3 (U8 / U24-BE are fixed). </summary>
        public bool LittleEndian
        {
            get => _littleEndian;
            set
            {
                if (_littleEndian == value) return;
                _littleEndian = value;
                // Raise LittleEndian itself so the persist listener fires, then the
                // layout-dependent properties (EncodingText etc.) for the UI.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LittleEndian)));
                RaiseLayoutChanged();
            }
        }

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

        /// <summary>Field width in bytes (1..3), derived from the current Start/End span.</summary>
        public int Width => End - Start + 1;

        /// <summary>The resolved encoding label shown next to the steppers, e.g. "U8", "U16 BE".</summary>
        public string EncodingText => Width switch
        {
            1 => "U8",
            2 => _littleEndian ? "U16 LE" : "U16 BE",
            _ => "U24 BE",
        };

        /// <summary>BE/LE toggle only applies to a 2-byte field; hidden otherwise.</summary>
        public bool EndianApplies => Width == 2;

        /// <summary>An FSR1 field ≥ 2 bytes wide can be split into two smaller fields.</summary>
        public bool CanSplit => IsFsr1 && Width >= 2;

        /// <summary>A catalog (non-synthetic) FSR1 field — offers "Reset field"; a synthetic split
        /// offers "Remove split" instead. Fixed per row lifetime (no notify).</summary>
        public bool IsCatalogFsr1 => IsFsr1 && !IsSynthetic;

        // Divider guards. A divider can move only if BOTH neighbours can absorb it: the field
        // gaining a byte stays ≤ 3 wide, the field losing a byte stays ≥ 1 wide. A boundary at
        // a record edge (Prev/Next == null) is fixed and never moves.
        public bool CanStartMinus => PrevField != null && PrevField.Width > 1 && Width < 3;
        public bool CanStartPlus => PrevField != null && Width > 1 && PrevField.Width < 3;
        public bool CanEndMinus => NextField != null && Width > 1 && NextField.Width < 3;
        public bool CanEndPlus => NextField != null && NextField.Width > 1 && Width < 3;

        /// <summary>Move the LEFT divider one byte toward the record start: this field grows
        /// left, the previous field shrinks from its right. Returns the neighbour that also
        /// changed (so the caller can persist it), or null if the move was not allowed.</summary>
        public ChannelMappingRow? StartMinus()
        {
            if (!CanStartMinus) return null;
            var p = PrevField!;
            p.SetSpan(p.Start, p.End - 1);
            SetSpan(Start - 1, End);
            return p;
        }

        /// <summary>Move the LEFT divider one byte toward the record end: this field shrinks
        /// from its left, the previous field grows right.</summary>
        public ChannelMappingRow? StartPlus()
        {
            if (!CanStartPlus) return null;
            var p = PrevField!;
            p.SetSpan(p.Start, p.End + 1);
            SetSpan(Start + 1, End);
            return p;
        }

        /// <summary>Move the RIGHT divider one byte toward the record start: this field shrinks
        /// from its right, the next field grows left.</summary>
        public ChannelMappingRow? EndMinus()
        {
            if (!CanEndMinus) return null;
            var n = NextField!;
            n.SetSpan(n.Start - 1, n.End);
            SetSpan(Start, End - 1);
            return n;
        }

        /// <summary>Move the RIGHT divider one byte toward the record end: this field grows
        /// right, the next field shrinks from its left.</summary>
        public ChannelMappingRow? EndPlus()
        {
            if (!CanEndPlus) return null;
            var n = NextField!;
            n.SetSpan(n.Start + 1, n.End);
            SetSpan(Start, End + 1);
            return n;
        }

        /// <summary>Set the span and refresh the dependent UI state. Does NOT route through the
        /// Start/End setters, so the per-property persist listener is not triggered — the
        /// code-behind persists both sides of a divider move explicitly.</summary>
        public void SetSpan(int start, int end)
        {
            Start = start;
            End = end;
            RaiseLayoutChanged();
        }

        // A span/endianness change can flip the encoding text, the BE/LE visibility, and every
        // stepper guard (a neighbour's guards depend on this field's width) — re-raise the lot.
        // Public so the code-behind can refresh sibling rows after a divider move ripples out.
        public void RaiseLayoutChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Start)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(End)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncodingText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndianApplies)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSplit)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartMinus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartPlus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEndMinus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEndPlus)));
        }

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
