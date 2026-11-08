using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Picks the HID axis the clutch bite point reads (MozaPlugin.BitePointHidAxis):
    /// device and axis dropdowns, Auto-detect (move the pedal; the axis that moves
    /// furthest wins), a live readout, and Set range to capture the pedal's travel.
    /// Shared by the mBooster Clutch Bite Point card and the ShakeIt effect editor.
    /// </summary>
    internal sealed class HidAxisPicker : UserControl
    {
        // Fraction of its range an axis must travel for Auto-detect to pick it.
        private const double DetectMinTravel = 0.3;
        private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(15);

        private readonly ComboBox _device = new ComboBox { MinWidth = 220, Margin = new Thickness(8, 0, 0, 0) };
        private readonly Button _refresh = new Button { Content = Strings.Button_Refresh, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        private readonly ComboBox _axis = new ComboBox { MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        private readonly Button _detect = new Button { Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock _value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly Button _range = new Button { Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock _rangeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

        private List<HidAxisDevice> _devices = new List<HidAxisDevice>();
        private HidAxisDetector? _detector;
        private DateTime _detectUntil;
        private bool _populating;
        private bool _enumerating;

        public HidAxisPicker()
        {
            var root = new StackPanel();
            root.Children.Add(Row(Strings.Label_HidDevice, _device, _refresh));
            root.Children.Add(Row(Strings.Option_Axis, _axis, _detect, _value));
            root.Children.Add(Row(Strings.SliderLabel_Range, _range, _rangeText));
            Content = root;

            _refresh.Click += (s, e) => RefreshDevices();
            _device.SelectionChanged += (s, e) => { if (!_populating) OnDeviceChosen(); };
            _axis.SelectionChanged += (s, e) => { if (!_populating) CommitAxis(); };
            _detect.Click += (s, e) => ToggleDetect();
            _range.Click += (s, e) => ToggleRange();
            _timer.Tick += (s, e) => Tick();
            // Scan only once shown: hosts create this before the HID source is picked.
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible && _devices.Count == 0) RefreshDevices();
            };
            Loaded += (s, e) =>
            {
                if (IsVisible && _devices.Count == 0) RefreshDevices();
                Tick();
                _timer.Start();
            };
            Unloaded += (s, e) =>
            {
                _timer.Stop();
                StopDetect();
                var plugin = MozaPlugin.Instance;
                if (plugin != null && plugin.IsBitePointHidRangeCapturing) plugin.CancelBitePointHidRangeCapture();
            };
        }

        private static StackPanel Row(string label, params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center });
            foreach (var item in items) row.Children.Add(item);
            return row;
        }

        private async void RefreshDevices()
        {
            if (_enumerating) return;
            _enumerating = true;
            _refresh.IsEnabled = false;
            try
            {
                _devices = await Task.Run(HidAxisCatalog.Enumerate);
            }
            finally
            {
                _enumerating = false;
                _refresh.IsEnabled = true;
            }
            PopulateDevices();
        }

        // Fill the device list and select the saved axis' device and axis.
        private void PopulateDevices()
        {
            var sel = MozaPlugin.Instance?.BitePointHidAxis;
            _populating = true;
            try
            {
                _device.Items.Clear();
                int selected = -1;
                for (int i = 0; i < _devices.Count; i++)
                {
                    var d = _devices[i];
                    _device.Items.Add(new ComboBoxItem { Content = $"{d.Name} ({d.VendorId:X4}:{d.ProductId:X4})", Tag = d });
                    if (sel == null || !d.Matches(sel)) continue;
                    if (selected < 0 || string.Equals(d.Path, sel.DevicePath, StringComparison.OrdinalIgnoreCase)) selected = i;
                }
                _device.SelectedIndex = selected;
                PopulateAxes(selected >= 0 ? _devices[selected] : null, sel?.Usage);
            }
            finally
            {
                _populating = false;
            }
        }

        private void PopulateAxes(HidAxisDevice? device, uint? usage)
        {
            _axis.Items.Clear();
            if (device == null) return;
            int selected = -1;
            var names = new Dictionary<string, int>();
            for (int i = 0; i < device.Axes.Count; i++)
            {
                var a = device.Axes[i];
                string name = AxisName(a.Usage);
                names[name] = names.TryGetValue(name, out int n) ? n + 1 : 1;
                if (names[name] > 1) name += " " + names[name];
                _axis.Items.Add(new ComboBoxItem { Content = name, Tag = a });
                if (usage == a.Usage) selected = i;
            }
            _axis.SelectedIndex = selected;
        }

        private HidAxisDevice? SelectedDevice => (_device.SelectedItem as ComboBoxItem)?.Tag as HidAxisDevice;

        private void OnDeviceChosen()
        {
            var device = SelectedDevice;
            uint? keep = MozaPlugin.Instance?.BitePointHidAxis?.Usage;
            _populating = true;
            try { PopulateAxes(device, keep); }
            finally { _populating = false; }
            if (_axis.SelectedIndex < 0 && _axis.Items.Count > 0) _axis.SelectedIndex = 0; // commits via SelectionChanged
            else CommitAxis();
        }

        // Store the chosen device + axis; a different axis starts with no captured travel.
        private void CommitAxis()
        {
            var plugin = MozaPlugin.Instance;
            var device = SelectedDevice;
            if (plugin == null || device == null || !((_axis.SelectedItem as ComboBoxItem)?.Tag is HidAxisInfo axis)) return;
            var next = new HidAxisSelection
            {
                VendorId = device.VendorId,
                ProductId = device.ProductId,
                DeviceName = device.Name,
                DevicePath = device.Path,
                Usage = axis.Usage,
                LogicalMin = axis.LogicalMin,
                LogicalMax = axis.LogicalMax,
            };
            var cur = plugin.BitePointHidAxis;
            if (next.SameAxis(cur))
            {
                next.RawReleased = cur!.RawReleased;
                next.RawPressed = cur.RawPressed;
            }
            plugin.BitePointHidAxis = next;
        }

        private void ToggleDetect()
        {
            if (_detector != null)
            {
                StopDetect();
                return;
            }
            if (_devices.Count == 0) return;
            _detector = new HidAxisDetector(_devices);
            _detectUntil = DateTime.UtcNow + DetectTimeout;
        }

        private void StopDetect()
        {
            _detector?.Dispose();
            _detector = null;
        }

        private void PollDetect()
        {
            if (_detector == null) return;
            if (_detector.TryGetBest(DetectMinTravel, out var device, out var axis) && device != null)
            {
                StopDetect();
                _populating = true;
                try
                {
                    _device.SelectedIndex = _devices.IndexOf(device);
                    PopulateAxes(device, axis.Usage);
                }
                finally
                {
                    _populating = false;
                }
                CommitAxis();
            }
            else if (DateTime.UtcNow > _detectUntil)
            {
                StopDetect();
            }
        }

        private void ToggleRange()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return;
            if (plugin.IsBitePointHidRangeCapturing) plugin.EndBitePointHidRangeCapture();
            else plugin.BeginBitePointHidRangeCapture();
        }

        private void Tick()
        {
            PollDetect();
            var plugin = MozaPlugin.Instance;
            var sel = plugin?.BitePointHidAxis;
            _detect.Content = _detector != null ? Strings.Button_Cancel : Strings.Button_AutoDetect;
            _detect.IsEnabled = _devices.Count > 0;
            bool capturing = plugin?.IsBitePointHidRangeCapturing ?? false;
            _range.Content = capturing ? Strings.Button_RangeDone : Strings.Button_SetRange;
            _range.IsEnabled = sel != null;

            if (plugin != null && sel != null && plugin.TryGetBitePointHidRaw(out int raw))
            {
                double pos = sel.Normalize(raw);
                _value.Text = double.IsNaN(pos) ? raw.ToString() : $"{raw} · {Math.Round(pos * 100)}%";
            }
            else
            {
                _value.Text = "—";
            }
            _rangeText.Text = sel == null ? ""
                : $"{sel.RawReleased ?? sel.LogicalMin} → {sel.RawPressed ?? sel.LogicalMax}";
        }

        private static string AxisName(uint fullUsage)
        {
            ushort page = (ushort)(fullUsage >> 16);
            ushort usage = (ushort)fullUsage;
            if (page == 0x01)
            {
                switch (usage)
                {
                    case 0x30: return "X";
                    case 0x31: return "Y";
                    case 0x32: return "Z";
                    case 0x33: return "Rx";
                    case 0x34: return "Ry";
                    case 0x35: return "Rz";
                    case 0x36: return Strings.HidAxis_Slider;
                    case 0x37: return Strings.HidAxis_Dial;
                    case 0x38: return Strings.HidAxis_Wheel;
                }
            }
            else if (page == 0x02)
            {
                switch (usage)
                {
                    case 0xBB:
                    case 0xC4: return Strings.Option_Throttle;
                    case 0xC5: return Strings.Option_Brake;
                    case 0xC6: return Strings.Option_Clutch;
                }
            }
            return $"0x{page:X2}:0x{usage:X2}";
        }
    }
}
