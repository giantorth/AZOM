using System;
using System.Collections.Generic;
using System.ComponentModel;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Resources;

namespace MozaPlugin.UI
{
    /// <summary>
    /// One selectable assignment in the Stalks truck-sim button-map editor: an
    /// action kind plus stage (for the stage kinds). Key-bearing kinds take
    /// their key from the row's capture box, not from the option.
    /// </summary>
    internal sealed class StalkOptionItem
    {
        public StalkOptionItem(StalkActionKind kind, int stage, string label)
        {
            Kind = kind;
            Stage = stage;
            Label = label;
        }

        public StalkActionKind Kind { get; }
        public int Stage { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    /// <summary>
    /// One row in the Stalks truck-sim button-map editor: a stalk button, its
    /// assigned action kind, and (for key-bearing kinds) the captured key.
    /// </summary>
    internal sealed class StalkRow : INotifyPropertyChanged
    {
        private readonly Action<StalkRow> _onChanged;

        // Stored key string kept verbatim (legacy friendly name or sc: literal);
        // only a real capture rewrites it.
        private string _keyStored;
        private int _keyCode;

        public StalkRow(int buttonIndex, StalkAction? action, IReadOnlyList<StalkOptionItem> options, Action<StalkRow> onChanged)
        {
            ButtonIndex = buttonIndex;
            Options = options;
            _onChanged = onChanged;
            _keyStored = action?.Key ?? "";
            _keyCode = KeyCodes.Parse(_keyStored);
            _selected = OptionForAction(action, options);
        }

        public int ButtonIndex { get; }
        public string Label => "Btn " + (ButtonIndex + 1);

        /// <summary>The shared option list (same instance for every row).</summary>
        public IReadOnlyList<StalkOptionItem> Options { get; }

        private StalkOptionItem _selected;
        public StalkOptionItem Selected
        {
            get => _selected;
            set
            {
                var v = value ?? Options[0];
                if (ReferenceEquals(_selected, v)) return;
                _selected = v;
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(ShowsKeyCapture));
                try { _onChanged?.Invoke(this); } catch { }
            }
        }

        /// <summary>Captured scan code for key-bearing kinds (0 = not set).</summary>
        public int KeyCode
        {
            get => _keyCode;
            set
            {
                if (_keyCode == value) return;
                _keyCode = value;
                _keyStored = KeyCodes.Encode((ushort)value);
                OnPropertyChanged(nameof(KeyCode));
                try { _onChanged?.Invoke(this); } catch { }
            }
        }

        public bool ShowsKeyCapture =>
            _selected.Kind == StalkActionKind.Momentary ||
            _selected.Kind == StalkActionKind.HeldKey ||
            _selected.Kind == StalkActionKind.LatchKey;

        private bool _isPressed;
        public bool IsPressed
        {
            get => _isPressed;
            set { if (_isPressed != value) { _isPressed = value; OnPropertyChanged(nameof(IsPressed)); } }
        }

        /// <summary>The <see cref="StalkAction"/> for this row's current selection.</summary>
        public StalkAction ToAction() => new StalkAction
        {
            Kind = _selected.Kind,
            Stage = _selected.Stage,
            Key = ShowsKeyCapture ? _keyStored : "",
        };

        public static List<StalkOptionItem> BuildOptions(int wiperStageCount, int lightStageCount)
        {
            var list = new List<StalkOptionItem>
            {
                new StalkOptionItem(StalkActionKind.None, 0, Strings.StalkKind_None),
                new StalkOptionItem(StalkActionKind.Momentary, 0, Strings.StalkKind_KeyTap),
                new StalkOptionItem(StalkActionKind.HeldKey, 0, Strings.StalkKind_HeldKey),
                new StalkOptionItem(StalkActionKind.LatchKey, 0, Strings.StalkKind_LatchKey),
                new StalkOptionItem(StalkActionKind.IndicatorLeft, 0, Strings.StalkKind_IndicatorLeft),
                new StalkOptionItem(StalkActionKind.IndicatorRight, 0, Strings.StalkKind_IndicatorRight),
                new StalkOptionItem(StalkActionKind.IndicatorCancel, 0, Strings.StalkKind_IndicatorCancel),
                new StalkOptionItem(StalkActionKind.WiperSingleSwipe, 0, Strings.StalkKind_WiperSwipe),
            };
            for (int i = 0; i < Math.Max(1, wiperStageCount); i++)
                list.Add(new StalkOptionItem(StalkActionKind.WiperStage, i, string.Format(Strings.StalkKind_WiperStage, i)));
            for (int i = 0; i < Math.Max(1, lightStageCount); i++)
                list.Add(new StalkOptionItem(StalkActionKind.LightStage, i, string.Format(Strings.StalkKind_LightStage, i)));
            list.Add(new StalkOptionItem(StalkActionKind.ReleaseHeld, 0, Strings.StalkKind_ReleaseHeld));
            return list;
        }

        public static StalkOptionItem OptionForAction(StalkAction? a, IReadOnlyList<StalkOptionItem> options)
        {
            var kind = a?.Kind ?? StalkActionKind.None;
            int stage = a?.Stage ?? 0;
            bool staged = kind == StalkActionKind.WiperStage || kind == StalkActionKind.LightStage;
            foreach (var o in options)
                if (o.Kind == kind && (!staged || o.Stage == stage)) return o;
            return options[0];
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
