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
using MozaPlugin.Settings;

namespace MozaPlugin.UI
{
    public partial class SettingsControl : UserControl
    {

        // Build a filesystem-safe slug from the active wheel's firmware model name
        // for use as a filename prefix on diagnostics bundles. Prefers the curated
        // friendly name (e.g. "CS Pro" for firmware "W17"); falls back to the raw
        // prefix for unknown wheels. Returns "" when no model is known yet so the
        // caller can omit the prefix entirely rather than emit a leading dash.
        
        // ===== AB9 Active Shifter Tab =====

        // Tracks whether the slider/combo values have been seeded from the profile
        // — without this, the first refresh tick would race the user's drag and
        // immediately overwrite an in-flight slider value with the saved one.
        private void RefreshAb9Tab()
        {
            if (_plugin?.Ab9Manager == null) { Ab9Tab.Visibility = Visibility.Collapsed; return; }

            bool connected = _plugin.Ab9Manager.IsConnected;
            bool detected  = _plugin.IsAb9Detected;

            Ab9Tab.Visibility = (connected || detected)
                ? Visibility.Visible : Visibility.Collapsed;

            // AB9 and AB6 share this lane, so the header names whichever answered.
            // Only derive it while the lane is live: DiscoveredPid is never cleared
            // on Disconnect, so an unplugged AB6 would otherwise keep labelling the
            // tab after an AB9 is plugged in. The x:Static default resolves once, so
            // the neutral branch has to restore it explicitly.
            Ab9Tab.Header = (connected || detected)
                ? global::MozaPlugin.Protocol.MozaUsbIds.ActiveShifterShortName(
                      _plugin.Ab9Manager.Connection.DiscoveredPid)
                : (object)Strings.TabHeader_Ab9Shifter;

            if (!connected && !detected) return;

            // Re-seed the controls from the active profile every refresh tick so
            // the tab follows per-game profile switches (matching the other
            // tabs). Events are suppressed (RefreshDisplay holds the suppressor;
            // the nested Begin below is depth-counted). A missing Ab9 block
            // shows defaults. The slider handlers write profile.Ab9 synchronously
            // so re-seeding can't fight a live drag.
            var ab9 = _plugin.Settings?.ProfileStore?.CurrentProfile?.Ab9 ?? new Ab9Settings();
            using (_suppressor.Begin())
            {
                SetAb9InputModeCombo(ab9.InputMode);
                SetAb9ModeCombo(ab9.Mode);
                SetAb9Slider(Ab9MechResistanceSlider,    Ab9MechResistanceValue,    ab9.MechanicalResistance);
                SetAb9Slider(Ab9SpringSlider,            Ab9SpringValue,            ab9.Spring);
                SetAb9Slider(Ab9DampingSlider,           Ab9DampingValue,           ab9.NaturalDamping);
                SetAb9Slider(Ab9FrictionSlider,          Ab9FrictionValue,          ab9.NaturalFriction);
                SetAb9Slider(Ab9MaxTorqueSlider,         Ab9MaxTorqueValue,         ab9.MaxTorqueLimit);
                SetAb9Slider(Ab9EngineVibIntensitySlider, Ab9EngineVibIntensityValue, ab9.EngineVibrationIntensity);
                Ab9EngineVibFreqSlider.Value = ab9.EngineVibrationFrequency;
                SetValueText(Ab9EngineVibFreqValue, ab9.EngineVibrationFrequency + " Hz");
                SetAb9Slider(Ab9GearShiftVibSlider,       Ab9GearShiftVibValue,       ab9.GearShiftVibrationIntensity);
                Ab9GearShiftVibrateOnNeutralCheck.IsChecked = ab9.GearShiftVibrateOnNeutral;
                int ab9DbMs = ab9.GearShiftDebounceMs;
                if (ab9DbMs < 0) ab9DbMs = 0;
                if (ab9DbMs > 1000) ab9DbMs = 1000;
                // Snap to 50 ms grid in case a persisted value came from a
                // manual edit / older build before the slider enforced ticks.
                ab9DbMs = ((ab9DbMs + 25) / 50) * 50;
                Ab9GearShiftDebounceSlider.Value = ab9DbMs;
                Ab9GearShiftDebounceValue.Text = $"{ab9DbMs} ms";
            }

            RefreshAb9StatusProbe();
        }

        // ===== AB9 status probe (diagnostic) =====

        // Environment.TickCount of the last polled probe; the panel refresh runs at
        // 500 ms and the probe is 13 frames, so it is throttled to ~1 Hz.
        private int _ab9ProbeLastTickMs;
        private const int Ab9ProbeIntervalMs = 1000;

        private void Ab9StatusProbeButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.Ab9Manager?.RequestStatusProbe();
            _ab9ProbeLastTickMs = Environment.TickCount;
        }

        // Renders the last probe result and, while the toggle is on, re-issues the
        // reads. Called from RefreshAb9Tab, so it only runs with the panel open and
        // the AB9 tab present. Send() just enqueues on the write lane — no blocking
        // work lands on the dispatcher.
        private void RefreshAb9StatusProbe()
        {
            var data = _data;
            if (data == null) return;

            if (Ab9StatusPollCheck.IsChecked == true)
            {
                int now = Environment.TickCount;
                if (unchecked(now - _ab9ProbeLastTickMs) >= Ab9ProbeIntervalMs)
                {
                    _ab9ProbeLastTickMs = now;
                    _plugin?.Ab9Manager?.RequestStatusProbe();
                }
            }

            Ab9State2bValue.Text     = FormatAb9Raw(data.Ab9State2b);
            Ab9StateErr2bValue.Text  = FormatAb9Raw(data.Ab9StateErr2b);
            Ab9Mcu2bValue.Text       = FormatAb9Temp(data.Ab9McuTemp2b);

            int mode = data.Ab9ModeReadback;
            Ab9ModeReadbackValue.Text = mode == MozaData.NoAb9Reading
                ? "—" : $"0x{mode:X2} ({mode})";
        }

        // Em dash = the register never answered, which is the probe's whole point,
        // so no reading can be confused with "absent".
        private static string FormatAb9Raw(int raw)
            => raw == MozaData.NoAb9Reading ? "—" : $"{raw} (0x{raw:X4})";

        // raw/100 degrees C, the same scaling the wheelbase uses (a live AB9 MCU
        // reads 0x0ED8 = 38.0 C, where the protocol notes' x0.1 would claim 380 C).
        // A raw 0 is an unpopulated register, not 0.00 C on a powered device, so it
        // prints bare — deriving a temperature from it would invent a reading.
        private static string FormatAb9Temp(int raw)
        {
            if (raw == MozaData.NoAb9Reading) return "—";
            if (raw == 0) return "0 (0x0000)";
            return $"{raw} (0x{raw:X4}) · {raw / 100.0:F1} °C";
        }

        private void SetAb9Slider(Slider slider, TextBox value, byte v)
        {
            slider.Value = v;
            value.Text = v.ToString();
        }

        private void SetAb9ModeCombo(Ab9Mode mode)
        {
            for (int i = 0; i < Ab9ModeCombo.Items.Count; i++)
            {
                var item = Ab9ModeCombo.Items[i] as ComboBoxItem;
                if (item?.Tag is string tag && byte.TryParse(tag, out byte val) && val == (byte)mode)
                {
                    Ab9ModeCombo.SelectedIndex = i;
                    return;
                }
            }
            Ab9ModeCombo.SelectedIndex = -1;
        }

        private void SetAb9InputModeCombo(Ab9InputMode mode)
        {
            for (int i = 0; i < Ab9InputModeCombo.Items.Count; i++)
            {
                var item = Ab9InputModeCombo.Items[i] as ComboBoxItem;
                if (item?.Tag is string tag && byte.TryParse(tag, out byte val) && val == (byte)mode)
                {
                    Ab9InputModeCombo.SelectedIndex = i;
                    return;
                }
            }
            Ab9InputModeCombo.SelectedIndex = -1;
        }

        private void Ab9InputModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (Ab9InputModeCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag || !byte.TryParse(tag, out byte val)) return;

            var mode = (Ab9InputMode)val;
            GetOrCreateAb9Profile().InputMode = mode;
            _plugin.Ab9Manager?.SendInputMode(mode);
            _plugin.SaveSettings();
        }

        private Ab9Settings GetOrCreateAb9Profile()
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return new Ab9Settings();
            if (profile.Ab9 == null) profile.Ab9 = new Ab9Settings();
            return profile.Ab9;
        }

        private void Ab9ModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (Ab9ModeCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag || !byte.TryParse(tag, out byte val)) return;

            var mode = (Ab9Mode)val;
            GetOrCreateAb9Profile().Mode = mode;
            _plugin.Ab9Manager?.SendMode(mode);
            _plugin.SaveSettings();
        }

        private void Ab9MechResistanceSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9MechResistanceSlider, Ab9MechResistanceValue, Ab9Slider.MechanicalResistance, e.NewValue);

        private void Ab9SpringSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9SpringSlider, Ab9SpringValue, Ab9Slider.Spring, e.NewValue);

        private void Ab9DampingSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9DampingSlider, Ab9DampingValue, Ab9Slider.NaturalDamping, e.NewValue);

        private void Ab9FrictionSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9FrictionSlider, Ab9FrictionValue, Ab9Slider.NaturalFriction, e.NewValue);

        private void Ab9MaxTorqueSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9MaxTorqueSlider, Ab9MaxTorqueValue, Ab9Slider.MaxTorqueLimit, e.NewValue);

        private void HandleAb9SliderChanged(Slider slider, TextBox label, Ab9Slider which, double newValue)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(newValue)));
            label.Text = v.ToString();

            var ab9 = GetOrCreateAb9Profile();
            switch (which)
            {
                case Ab9Slider.MechanicalResistance: ab9.MechanicalResistance = v; break;
                case Ab9Slider.Spring:               ab9.Spring = v;               break;
                case Ab9Slider.NaturalDamping:       ab9.NaturalDamping = v;       break;
                case Ab9Slider.NaturalFriction:      ab9.NaturalFriction = v;      break;
                case Ab9Slider.MaxTorqueLimit:       ab9.MaxTorqueLimit = v;       break;
            }
            _plugin.Ab9Manager?.SendSlider(which, v);
            _plugin.SaveSettings();
        }

        // Engine Vibration intensity (host-rendered). The 91 Hz worker thread
        // reads the new value from the profile on its next tick — no device
        // command is sent.
        private void Ab9EngineVibIntensitySlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            Ab9EngineVibIntensityValue.Text = v.ToString();
            GetOrCreateAb9Profile().EngineVibrationIntensity = v;
            _plugin.SaveSettings();
        }

        // Engine Vibration frequency slider — literal target Hz (0..200) of
        // the AB9 oscillator. Host-rendered, no device-side write; the worker
        // thread picks up the new value on its next tick.
        private void Ab9EngineVibFreqSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            ushort v = (ushort)Math.Max(0, Math.Min(200, (int)Math.Round(e.NewValue)));
            Ab9EngineVibFreqValue.Text = v + " Hz";
            GetOrCreateAb9Profile().EngineVibrationFrequency = v;
            _plugin.SaveSettings();
        }

        // Gear-shift vibration intensity. Fires one 0x0A 0x01 config write per
        // change so the AB9 firmware persists the new stored intensity for its
        // autonomous shift-rumble.
        private void Ab9GearShiftVibSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            Ab9GearShiftVibValue.Text = v.ToString();
            GetOrCreateAb9Profile().GearShiftVibrationIntensity = v;
            _plugin.Ab9Manager?.SendGearShiftVibrationIntensity(v);
            _plugin.SaveSettings();
        }

        // AB9-only "vibrate on neutral" — gates whether the per-shift
        // 0x0D 0x06 (Disengage) trigger fires on any-gear→neutral transitions.
        // Independent from the wheelbase GearshiftVibrateOnNeutralCheck so the
        // AB9 can pulse for downshifts into N while the wheelbase stays quiet.
        private void Ab9GearShiftVibrateOnNeutralCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = Ab9GearShiftVibrateOnNeutralCheck.IsChecked == true;
            GetOrCreateAb9Profile().GearShiftVibrateOnNeutral = on;
            _plugin.SaveSettings();
        }

        // AB9-only shift debounce. Same 0..1000 ms range and 50 ms grid as
        // the wheelbase slider, but stored on Ab9Settings so the two devices
        // can be tuned independently.
        private void Ab9GearShiftDebounceSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            val = ((val + 25) / 50) * 50;
            if (val < 0) val = 0;
            if (val > 1000) val = 1000;
            Ab9GearShiftDebounceValue.Text = $"{val} ms";
            GetOrCreateAb9Profile().GearShiftDebounceMs = val;
            _plugin.SaveSettings();
        }

    }
}
