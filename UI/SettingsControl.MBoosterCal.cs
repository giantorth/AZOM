using System;
using System.Windows;
using System.Windows.Controls;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;

namespace MozaPlugin.UI
{
    /// <summary>
    /// The mBooster tab's two real hardware calibration ROUTINES (travel and
    /// motor rotor-locate) plus the plain Virtual Damping pair (cmdId 0xAD)
    /// that Pit House pushes alongside Segmented Damping.
    ///
    /// The buttons live on each PEDAL's own row in the device list, not once
    /// per card: travel calibration carries the pedal's role in its cmd id
    /// (group 0x26 cmd 12/13/14), and a unit hosting two ACTIVE pedals answers
    /// both on a single device id — so one shared pair of buttons genuinely
    /// could not express which pedal was meant. Bug GVT5H8B8 was that
    /// confusion in the field.
    ///
    /// The routines themselves live in
    /// <see cref="MBoosterCalibrationRunner"/> on the registry, not here: both
    /// soft-reboot the pedal, so a run has to survive both the CDC outage and
    /// this settings panel being closed. This file is only the buttons, the
    /// per-row status text and the gating.
    /// </summary>
    public partial class SettingsControl
    {
        private MBoosterCalibrationRunner? _mboosterCalRunner;

        /// <summary>Subscribe to the runner's progress once, lazily — the
        /// registry creates it on first use.</summary>
        private MBoosterCalibrationRunner? EnsureMBoosterCalRunner(bool create)
        {
            var registry = _plugin?.MBoosterRegistry;
            if (registry == null) return null;
            var runner = create ? registry.CalibrationRunner : registry.CalibrationRunnerOrNull;
            if (runner == null || ReferenceEquals(runner, _mboosterCalRunner)) return runner;
            if (_mboosterCalRunner != null) _mboosterCalRunner.ProgressChanged -= OnMBoosterCalProgress;
            _mboosterCalRunner = runner;
            runner.ProgressChanged += OnMBoosterCalProgress;
            return runner;
        }

        /// <summary>Runner ticks on its own timer thread — marshal to the UI.</summary>
        private void OnMBoosterCalProgress()
        {
            try { Dispatcher.BeginInvoke((Action)RefreshMBoosterCalUi); }
            catch { }
        }

        private static MBoosterDeviceRow? RowOf(object sender)
            => (sender as FrameworkElement)?.DataContext as MBoosterDeviceRow;

        private void MBoosterRowTravelCal_Click(object sender, RoutedEventArgs e)
            => StartMBoosterCalibration(MBoosterCalKind.Travel, RowOf(sender));

        private void MBoosterRowMotorCal_Click(object sender, RoutedEventArgs e)
            => StartMBoosterCalibration(MBoosterCalKind.Motor, RowOf(sender));

        private void StartMBoosterCalibration(MBoosterCalKind kind, MBoosterDeviceRow? row)
        {
            if (row == null) return;
            var controller = _plugin?.MBoosterRegistry?.FindByIdentity(row.Identity);
            if (controller == null) return;
            var runner = EnsureMBoosterCalRunner(create: true);
            if (runner == null) return;

            // The running routine's own button doubles as Cancel — no third
            // control, and cancelling still reboots the pedal so it can never
            // be left mid-sweep or in the motor routine's debug mode.
            var running = runner.Snapshot();
            if (running.IsRunning)
            {
                if (running.Kind == kind
                    && string.Equals(running.Identity, row.Identity, StringComparison.OrdinalIgnoreCase)
                    && running.AxisIndex == row.AxisIndex)
                    runner.Cancel();
                return;
            }

            if (!runner.StartCalibration(kind, controller, row.AxisIndex, out string error))
            {
                row.CalStatus = string.Format(Strings.Status_CalibrationFailed, error);
                return;
            }
            RefreshMBoosterCalUi();
        }

        /// <summary>
        /// Push button enable/caption/status onto every pedal row. Called from
        /// RefreshMBoosterTab (via the passive-pedal gate) and on every runner
        /// progress event.
        /// </summary>
        private void RefreshMBoosterCalUi()
        {
            var rows = _mboosterDeviceRows;
            if (rows == null || rows.Count == 0) return;

            var runner = EnsureMBoosterCalRunner(create: false);
            var status = runner?.Snapshot() ?? default;
            bool anyRunning = status.IsRunning;
            string? note = runner?.FirmwareNote;

            foreach (var row in rows)
            {
                var controller = _plugin?.MBoosterRegistry?.FindByIdentity(row.Identity);
                // Both routines are motor-driven — the travel sweep IS the
                // motor moving the pedal, and a rotor locate needs a rotor.
                // A passive pedal has neither, and these are brake-named
                // singleton commands, so running one from a passive pedal
                // would act on the active pedal instead.
                bool motorized = controller?.IsAxisMotorized(row.AxisIndex) ?? false;
                row.CalVisible = controller == null || motorized;

                bool mine = anyRunning
                    && string.Equals(status.Identity, row.Identity, StringComparison.OrdinalIgnoreCase)
                    && status.AxisIndex == row.AxisIndex;
                bool usable = controller != null && controller.IsConnected && motorized;

                // A routine reboots the whole unit, so while one runs every
                // other button on every row is dead; the running one becomes
                // Cancel.
                bool travelRunning = mine && status.Kind == MBoosterCalKind.Travel;
                bool motorRunning = mine && status.Kind == MBoosterCalKind.Motor;
                row.TravelCalEnabled = travelRunning || (usable && !anyRunning);
                row.MotorCalEnabled = motorRunning || (usable && !anyRunning);
                row.TravelCalLabel = travelRunning ? Strings.Button_Stop : Strings.Button_TravelCalibration;
                row.MotorCalLabel = motorRunning ? Strings.Button_Stop : Strings.Button_MotorCalibration;

                if (mine)
                    row.CalStatus = DescribeMBoosterCal(status, note);
                else if (!anyRunning && IsCalOutcome(status) && OwnsStatus(status, row))
                    row.CalStatus = DescribeMBoosterCal(status, note);
                else
                    row.CalStatus = "";
            }
        }

        /// <summary>A finished run's Done/Failed line stays on the row it ran
        /// on, so the outcome doesn't vanish the instant the run ends.</summary>
        private static bool IsCalOutcome(MBoosterCalibrationRunner.Status s)
            => s.Step == MBoosterCalStep.Done || s.Step == MBoosterCalStep.Failed;

        private static bool OwnsStatus(MBoosterCalibrationRunner.Status s, MBoosterDeviceRow row)
            => string.Equals(s.Identity, row.Identity, StringComparison.OrdinalIgnoreCase)
               && s.AxisIndex == row.AxisIndex;

        private string DescribeMBoosterCal(MBoosterCalibrationRunner.Status status, string? note)
        {
            switch (status.Step)
            {
                case MBoosterCalStep.Done:
                    return Strings.Status_Done;
                case MBoosterCalStep.Failed:
                    return string.Format(Strings.Status_CalibrationFailed, status.Message);
                case MBoosterCalStep.MotorRebooting:
                case MBoosterCalStep.Rebooting:
                case MBoosterCalStep.Verifying:
                    return Strings.Hint_MBoosterCalRebooting;
                default:
                    break;
            }

            // The firmware narrates both routines on its own debug channel, so
            // show what it actually said rather than only a countdown.
            string format = status.Kind == MBoosterCalKind.Motor
                ? Strings.Hint_MBoosterMotorCal
                : Strings.Hint_MBoosterTravelCal;
            string text = string.Format(format, status.SecondsRemaining);
            return string.IsNullOrEmpty(note) ? text : text + "  " + note;
        }

        // ===== Plain Virtual Damping (cmdId 0xAD, press/release selectors) ===
        //
        // A register set of its own, NOT the 0xB7 per-segment fields: the
        // firmware log prints `virtual_damping_press` / `_release` for these
        // and `virtual_damping_press1..3` / `_release1..3` for the segments,
        // out of the same Pit House write burst (2026-09-08 captures). Both
        // selectors are independent values here, unlike Natural Friction's
        // two selectors which always carry the same number.
        //
        // One handler for both sliders — which one moved is read off the
        // sender, so the pair stays a single push key and a drag on either
        // coalesces the same way every other Pedal Feel write does.
        private void MBoosterDampingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            bool isPress = ReferenceEquals(sender, MBoosterDampingPressSlider);
            var box = isPress ? MBoosterDampingPressValue : MBoosterDampingReleaseValue;
            OnIntSliderChanged(e.NewValue, box, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                if (isPress) s.DampingPressPct = v;
                else s.DampingReleasePct = v;
                int raw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(v);
                string command = isPress ? "mbooster-brake-damping-press" : "mbooster-brake-damping-release";
                QueueMBoosterPedalFeelPush(isPress ? "damping-press" : "damping-release",
                    (c, dev) => c.SendIntWrite(command, raw, dev));
            });
        }
    }
}
