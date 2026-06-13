using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Media;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Devices.WheelUi
{
    /// <summary>
    /// Bindable view-model for one streamed FSR1 record in the live byte-strip
    /// visualization. Wraps an immutable <see cref="Fsr1VizRecord"/> snapshot:
    /// the structure (label, field spans) is fixed for the VM's lifetime
    /// (<see cref="StructKey"/> drives rebuild-vs-in-place), while each field's
    /// raw bytes + scaled value update in place each 2 Hz tick to avoid flicker.
    /// </summary>
    internal sealed class Fsr1VizRowVm
    {
        public string Label { get; }
        public ObservableCollection<Fsr1VizFieldVm> Fields { get; } = new ObservableCollection<Fsr1VizFieldVm>();

        /// <summary>Identity of the record's field LAYOUT — recompute and compare against a
        /// fresh snapshot to decide whether the strip can update in place or must rebuild
        /// (a split/merge/edit changes the layout, a value change does not).</summary>
        public string StructKey { get; }

        public Fsr1VizRowVm(Fsr1VizRecord rec)
        {
            Label = rec.Label;
            StructKey = BuildKey(rec);
            foreach (var f in rec.Fields)
                Fields.Add(new Fsr1VizFieldVm(f));
        }

        /// <summary>In-place value refresh — caller guarantees the layout matches
        /// (same <see cref="StructKey"/>), so field count and order are unchanged.</summary>
        public void Update(Fsr1VizRecord rec)
        {
            int n = rec.Fields.Length;
            for (int i = 0; i < n && i < Fields.Count; i++)
                Fields[i].Update(rec.Fields[i]);
        }

        public static string BuildKey(Fsr1VizRecord rec)
        {
            var sb = new StringBuilder();
            sb.Append(rec.Type).Append(':');
            foreach (var f in rec.Fields)
                sb.Append(f.Label).Append('[').Append(f.Start).Append('-').Append(f.End)
                  .Append(f.IsSynthetic ? "s" : "").Append(']');
            return sb.ToString();
        }
    }

    /// <summary>Bindable view-model for one field box: fixed label/span/encoding,
    /// live raw-hex + scaled-value text.</summary>
    internal sealed class Fsr1VizFieldVm : INotifyPropertyChanged
    {
        // Synthetic split boxes are tinted so they stand out from catalog fields.
        private static readonly Brush SyntheticBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0x26, 0xC6, 0xDA)));
        private static readonly Brush CatalogBrush = Brushes.Transparent;

        public string Label { get; }
        public string RangeText { get; }
        public Brush BoxBrush { get; }

        private string _hexText = "";
        public string HexText
        {
            get => _hexText;
            private set { if (_hexText == value) return; _hexText = value; Raise(nameof(HexText)); }
        }

        private string _valueText = "";
        public string ValueText
        {
            get => _valueText;
            private set { if (_valueText == value) return; _valueText = value; Raise(nameof(ValueText)); }
        }

        public Fsr1VizFieldVm(Fsr1VizField f)
        {
            Label = f.Label;
            RangeText = $"[{f.Start}..{f.End}] {f.Encoding.Replace('_', ' ')}";
            BoxBrush = f.IsSynthetic ? SyntheticBrush : CatalogBrush;
            Update(f);
        }

        public void Update(Fsr1VizField f)
        {
            HexText = ToHex(f.Bytes);
            ValueText = f.Value.ToString();
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            var sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static Brush Freeze(Brush b) { if (b.CanFreeze) b.Freeze(); return b; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
