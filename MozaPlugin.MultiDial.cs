using System.Collections.Generic;
using System.Text.RegularExpressions;
using MozaPlugin.Settings;

namespace MozaPlugin
{
    // Multi-mode thumb dial (wheel cmd 0x28 + session FF kind=18 labels). Settings
    // live per wheel page in WheelMultiDialSettings; the wire config is applied by
    // HardwareApplier, the labels ride the dashboard session.
    public partial class MozaPlugin
    {
        // Wheel heartbeat line carrying Table 2 Param 62. Anchored on the line end
        // so a line split across log frames can't yield a truncated number.
        private static readonly Regex s_rotaryMultiFunctionRe =
            new Regex(@"RotaryMultiFunction\s+(\d+)\s*[\r\n]", RegexOptions.Compiled);

        // A label push that couldn't go yet (session not Active). Retried on the poll
        // tick; also re-armed by the wheel's own report at every session init.
        private volatile bool _multiDialPushPending;

        /// <summary>Wheel-reported label table (b2h kind=18). Serial read thread, so
        /// it only arms the push: it arrives during init, before the session is Active.</summary>
        private void OnWheelMultiDialLabels(List<(string Left, string Right)> wheel)
        {
            if (_profileCoordinator.SeedMultiDialLabelsFromWheel(wheel, WheelModelInfo))
                _multiDialPushPending = true;
        }

        /// <summary>Send the saved label table now, or as soon as the session is Active.
        /// Not for the serial read thread.</summary>
        internal void RequestMultiDialLabelPush()
        {
            _multiDialPushPending = true;
            TryPushMultiDialLabels();
        }

        private void TickMultiDialLabelPush()
        {
            if (_multiDialPushPending) TryPushMultiDialLabels();
        }

        private void TryPushMultiDialLabels()
        {
            var model = WheelModelInfo;
            var bundle = ActiveWheelMultiDial;
            if (model?.MultiDialKnobOrder == null || model.HasDisplay != true || bundle?.Labels == null)
            {
                _multiDialPushPending = false;   // nothing this wheel can take
                return;
            }
            var sender = _telemetrySender;
            if (sender != null && sender.SendMultiDialLabels(ProfileCoordinator.MultiDialWireLabels(bundle, model)))
                _multiDialPushPending = false;
        }

        /// <summary>Seed the dial enable + knob from the wheel heartbeat when unset.
        /// Sides aren't seeded: their bits in the packed word aren't decoded.</summary>
        private void TryFollowMultiDialHeartbeat(string text)
        {
            if (text.IndexOf("RotaryMultiFunction", System.StringComparison.Ordinal) < 0) return;
            var m = s_rotaryMultiFunctionRe.Match(text);
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out long packed)) return;
            var model = WheelModelInfo;
            if (model?.MultiDialKnobOrder == null) return;
            var bundle = _profileCoordinator.GetOrCreateActiveWheelMultiDial();
            if (bundle == null) return;
            bool changed = false;
            if (bundle.Enabled < 0)
            {
                bundle.Enabled = (int)(packed & 1);
                changed = true;
            }
            if (bundle.Knob < 0)
            {
                int knob = model.MultiDialLogicalKnob((int)((packed >> 4) & 0x0F));
                if (model.IsMultiDialKnob(knob))
                {
                    bundle.Knob = knob;
                    changed = true;
                }
            }
            if (changed) _profileCoordinator.PersistSettings();
        }

        /// <summary>Seed an unset right thumb wheel from the reply to the telemetry
        /// start's <c>28 02 01</c> read. The reply (<c>[v][v]</c>, 0x0F = none) doesn't
        /// echo the side; that read is the only <c>28 02</c> the plugin sends.</summary>
        private void TryFollowMultiDialRightRead(byte a, byte b)
        {
            var wheels = WheelModelInfo?.MultiDialThumbWheels;
            if (wheels == null || a != b) return;
            int wire = a == 0x0F ? 0xFF : a;
            if (wire != 0xFF && wire >= wheels.Length) return;
            var bundle = _profileCoordinator.GetOrCreateActiveWheelMultiDial();
            if (bundle == null || bundle.RightWheel >= 0 || bundle.Right >= 0) return;
            bundle.RightWheel = wire;
            _profileCoordinator.PersistSettings();
        }
    }
}
