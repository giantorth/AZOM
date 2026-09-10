using System;
using System.Timers;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>Which routine a run is executing.</summary>
    public enum MBoosterCalKind
    {
        None = 0,
        /// <summary>Pedal travel calibration — group 0x26 cmd 0x0D/0x11.</summary>
        Travel,
        /// <summary>Motor rotor-locate calibration — group 0x2A "MotorCtrl".</summary>
        Motor,
    }

    /// <summary>Where a run currently is. Exposed so the UI can label it.</summary>
    public enum MBoosterCalStep
    {
        Idle = 0,
        // Travel
        TravelSweeping,
        TravelSettling,
        // Motor
        MotorRebooting,
        MotorEnteringDebug,
        MotorLocateWait,
        MotorPolling,
        // Shared tail
        Rebooting,
        Verifying,
        Done,
        Failed,
    }

    /// <summary>
    /// Runs the mBooster's two hardware calibration routines, replicating real
    /// Pit House's flow frame for frame and beat for beat (captures dated
    /// 2026-09-08; see docs/protocol/devices/mbooster.md and the byte-exact
    /// expectations in tools/cmd-frame-mbooster-cal.txt).
    ///
    /// Lives on <see cref="MozaMBoosterRegistry"/>, NOT on
    /// <see cref="MBoosterDeviceController"/>, because both routines contain a
    /// soft reboot: the CDC pipe drops for ~4.6s and the registry's own sweep
    /// disposes and removes a controller whose port disappears. State on the
    /// controller would die mid-flow. The registry keys controllers by USB
    /// device-instance identity, which survives the re-enumeration, so a run
    /// holds the identity and re-resolves the controller (and its role→device
    /// mapping, which may have changed) on every step.
    ///
    /// Driven by one <see cref="System.Timers.Timer"/> rather than a
    /// DispatcherTimer in the settings panel: SettingsControl's
    /// RunCalibrationCountdown silently drops its stop action if the panel
    /// unloads mid-countdown, which here would leave the pedal mid-sweep with
    /// no stop frame and no reboot.
    ///
    /// PendingResponseTracker is deliberately not used for the motor status
    /// poll: its 60s sunset blacklist expires almost exactly when a ~59.5s
    /// rotor locate completes.
    /// </summary>
    internal sealed class MBoosterCalibrationRunner : IDisposable
    {
        // ---- Pit House's own timings, measured in both captures -------------
        // Travel: start → stop is exactly 20.0s (11.557→31.556 and
        // 101.835→121.831); the firmware itself is finished by ~+13s.
        private const double TravelSweepSeconds = 20.0;
        // stop → soft reboot (33.555 - 31.556 and 123.832 - 121.831).
        private const double TravelSettleSeconds = 2.0;
        // Motor: soft reboot → enter-debug is exactly 20.0s (16.83→36.83).
        private const double MotorRebootFloorSeconds = 20.0;
        // enter-debug → locate (36.83→41.83).
        private const double MotorEnterSeconds = 5.0;
        // locate → first status poll (41.83→86.83).
        private const double MotorFirstPollSeconds = 45.0;
        // subsequent poll cadence (86.83, 91.83, 96.83, 101.83).
        private const double MotorPollIntervalSeconds = 5.0;
        // Firmware took 59.5s in the capture. No failure state was ever
        // captured — only 1 (running) and 3 (complete) — so wall clock is the
        // only failure signal available.
        private const double MotorLocateTimeoutSeconds = 180.0;
        // How long to wait for the lane to come back after a soft reboot. The
        // outage itself was ~4.6s, but the registry only sweeps every 5s.
        private const double ReconnectTimeoutSeconds = 45.0;
        // Post-reboot settle before declaring the run finished.
        private const double VerifySeconds = 6.0;

        // ---- Wire values ----------------------------------------------------
        // Pit House sends param 0x0000 for both travel frames, where the
        // wheelbase pedals bus passes 1.
        private const int TravelParam = 0x0000;
        // MotorCtrl cmd 0x14, param_high 0 / param_low 1 → debug_mode enable.
        private const int MotorEnterParam = 0x0001;
        // MotorCtrl cmd 0x15, param_high 1 / param_low 3 → "Motor Locate Start".
        private const int MotorLocateParam = 0x0103;
        // MotorCtrl cmd 0x15, param 0 → status query.
        private const int MotorQueryParam = 0x0000;
        // Status echo's low byte.
        private const int MotorStateRunning = 1;
        private const int MotorStateComplete = 3;

        // Register 0xB4 is NOT a usable calibration-mode gate. On the USB unit
        // in the 2026-09-08 captures it went 2 → 0 on cal-start and back to 2
        // after the reboot, which looked like a state machine — but a routed
        // W17 lane (bug reports GVT5H8B8 / 34JAASN5) reads a constant 15
        // through an entire run, including while the firmware has accepted the
        // start frame. Gating entry on it made every travel calibration on
        // that hardware fail with "the pedal never entered calibration mode".
        // It is still read and reported in diagnostics, but nothing branches
        // on it. The firmware's own `Pedal Calib …` log lines are the real
        // progress signal — see _sawCalibProgress.

        private const double TickMs = 250.0;

        private readonly MozaMBoosterRegistry _registry;
        private readonly Timer _timer;
        private readonly object _lock = new object();

        // Run state, all under _lock.
        private string? _identity;
        private int _axisIndex;
        private string? _rolePrefix;
        private MBoosterCalKind _kind;
        private MBoosterCalStep _step;
        private string _message = string.Empty;
        private int _secondsRemaining;
        private DateTime _stepDueUtc;
        private DateTime _stepStartedUtc;
        private DateTime _locateStartedUtc;
        private DateTime _lastStateReadUtc;
        private bool _sawDisconnect;
        // Set once the firmware reports a real sweep on group 0x0E.
        private bool _sawCalibProgress;
        private string? _firmwareNote;

        public MBoosterCalibrationRunner(MozaMBoosterRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _timer = new Timer(TickMs) { AutoReset = true };
            _timer.Elapsed += OnTick;
        }

        /// <summary>Fired whenever <see cref="Snapshot"/> would return something
        /// new. Not marshalled — the UI must dispatch.</summary>
        public event Action? ProgressChanged;

        public readonly struct Status
        {
            public Status(MBoosterCalKind kind, MBoosterCalStep step, string message,
                          int secondsRemaining, string? identity, int axisIndex)
            {
                Kind = kind; Step = step; Message = message;
                SecondsRemaining = secondsRemaining; Identity = identity; AxisIndex = axisIndex;
            }

            public MBoosterCalKind Kind { get; }
            public MBoosterCalStep Step { get; }
            public string Message { get; }
            public int SecondsRemaining { get; }
            public string? Identity { get; }
            public int AxisIndex { get; }

            public bool IsRunning => Kind != MBoosterCalKind.None
                && Step != MBoosterCalStep.Idle
                && Step != MBoosterCalStep.Done
                && Step != MBoosterCalStep.Failed;
        }

        public Status Snapshot()
        {
            lock (_lock)
                return new Status(_kind, _step, _message, _secondsRemaining, _identity, _axisIndex);
        }

        /// <summary>True while a routine owns any lane. The UI disables both
        /// buttons on every pedal while this holds — the routines reboot the
        /// unit, so a second one cannot be allowed to interleave.</summary>
        public bool IsRunning => Snapshot().IsRunning;

        // ---- Entry points ---------------------------------------------------

        /// <summary>
        /// Begin a routine on ONE pedal of one lane. Refuses (with a reason)
        /// rather than throwing: the caller shows <paramref name="error"/> on
        /// that pedal's row.
        /// </summary>
        public bool StartCalibration(MBoosterCalKind kind, MBoosterDeviceController controller,
                                     int axisIndex, out string error)
        {
            error = string.Empty;
            if (controller == null) { error = "no device"; return false; }
            if (!controller.IsConnected) { error = "device not connected"; return false; }
            // A passive pedal has no motor: the travel routine's sweep is
            // motor-driven and the rotor locate is meaningless without one.
            if (!controller.IsAxisMotorized(axisIndex)) { error = "pedal has no motor"; return false; }
            string? prefix = controller.RolePrefixForAxis(axisIndex);
            if (string.IsNullOrEmpty(prefix)) { error = "pedal role unresolved"; return false; }

            lock (_lock)
            {
                if (Snapshot().IsRunning) { error = "a calibration is already running"; return false; }
                _identity = controller.Identity;
                _axisIndex = axisIndex;
                _rolePrefix = prefix;
                _kind = kind;
                _sawDisconnect = false;
                _sawCalibProgress = false;
                _firmwareNote = null;
                _locateStartedUtc = default;
                // default = "never read", so ReadCalibrationState's 1 Hz
                // throttle lets this run's first read through immediately.
                _lastStateReadUtc = default;
            }

            controller.SetEffectsSuspended(true);
            byte dev = controller.CalibDeviceForAxis(axisIndex);

            if (kind == MBoosterCalKind.Travel)
            {
                MozaLog.Info($"[AZOM/mBooster] {MBoosterDeviceController.ShortIdentity(controller.Identity)} travel calibration starting on {prefix} (dev 0x{dev:x2}) — motor drives its own sweep, hands off the pedal");
                controller.SendIntWrite($"mbooster-{prefix}-cal-start", TravelParam, dev);
                // Straight into Pit House's fixed 20.0s window — it polls
                // nothing here either. Whether the firmware actually started a
                // sweep is judged from its own 0x0E log at the end of the
                // window, not from a register (see the 0xB4 note above).
                Advance(MBoosterCalStep.TravelSweeping, TravelSweepSeconds, "calibrating");
            }
            else
            {
                MozaLog.Info($"[AZOM/mBooster] {MBoosterDeviceController.ShortIdentity(controller.Identity)} motor calibration starting on {prefix} (dev 0x{dev:x2}) — rebooting the pedal first, as Pit House does");
                controller.SendIntWrite("mbooster-soft-reboot", 0, dev);
                Advance(MBoosterCalStep.MotorRebooting, MotorRebootFloorSeconds, "rebooting");
            }

            _timer.Start();
            RaiseProgress();
            return true;
        }

        /// <summary>
        /// Abandon a run without leaving the pedal stuck.
        ///
        /// Sends the soft reboot and nothing else. Deliberately NOT the travel
        /// stop frame: `Pedal Calib End` is what COMMITS the measured angle and
        /// load-cell range, so stopping a sweep early would write whatever
        /// partial range the motor had reached over the pedal's good
        /// calibration. The reboot is what clears calibration mode
        /// (register 0xB4 returns to 2 only after it — never on the stop frame),
        /// and it is also the firmware's only exit from the motor routine's
        /// debug mode / MotorMode 8, so one frame covers both routines.
        /// </summary>
        public void Cancel()
        {
            MBoosterCalKind kind;
            lock (_lock) kind = _kind;
            if (kind == MBoosterCalKind.None) return;

            var controller = ResolveController();
            if (controller != null && controller.IsConnected)
                controller.SendIntWrite("mbooster-soft-reboot", 0,
                                        controller.CalibDeviceForAxis(_axisIndex));
            MozaLog.Info("[AZOM/mBooster] calibration cancelled — rebooting the pedal, nothing committed");
            Finish(MBoosterCalStep.Failed, "cancelled");
        }

        // ---- Registry hooks -------------------------------------------------

        /// <summary>Called by the registry when a lane's port disappears — the
        /// expected consequence of our own soft reboot.</summary>
        public void OnDeviceRemoved(MBoosterDeviceController controller)
        {
            lock (_lock)
            {
                if (_identity == null || controller == null) return;
                if (!string.Equals(controller.Identity, _identity, StringComparison.OrdinalIgnoreCase)) return;
                _sawDisconnect = true;
            }
        }

        /// <summary>Called by the registry once a lane is detected again. Both
        /// routines cross a reboot, so this is what lets a run continue.</summary>
        public void OnDeviceDetected(MBoosterDeviceController controller)
        {
            MBoosterCalStep step;
            lock (_lock)
            {
                if (_identity == null || controller == null) return;
                if (!string.Equals(controller.Identity, _identity, StringComparison.OrdinalIgnoreCase)) return;
                step = _step;
            }
            // The lane's reconnect re-runs ApplyMBoosterToHardware, which
            // pushes effect-adjacent config — re-arm the suspension so a
            // resumed motor calibration isn't fighting the effect workers.
            if (step == MBoosterCalStep.MotorRebooting || step == MBoosterCalStep.Rebooting)
                controller.SetEffectsSuspended(true);
        }

        /// <summary>Called from the controller's inbound path for the group-0x0E
        /// firmware log, so the UI can show what the firmware itself says
        /// instead of only a countdown.</summary>
        public void OnFirmwareLogLine(MBoosterDeviceController controller, string line)
        {
            if (controller == null || string.IsNullOrEmpty(line)) return;
            lock (_lock)
            {
                if (_identity == null) return;
                if (!string.Equals(controller.Identity, _identity, StringComparison.OrdinalIgnoreCase)) return;
            }
            // Only the lines the two captures actually produce for these
            // routines. Everything else on 0x0E is heartbeat noise.
            string? note = null;
            if (line.IndexOf("Pedal Calib", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("compen_theta_e", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Motor Locate", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Rotor Not Located", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Angle Excess", StringComparison.OrdinalIgnoreCase) >= 0)
                note = line.Trim();
            if (note == null) return;
            lock (_lock)
            {
                _firmwareNote = note;
                // "Pedal Calib Start/Backward/Forward/pressure Calculating/End"
                // — the only proof a travel sweep actually ran. A device that
                // acks the start frame and does nothing never prints these.
                if (note.IndexOf("Pedal Calib", StringComparison.OrdinalIgnoreCase) >= 0)
                    _sawCalibProgress = true;
            }
            MozaLog.Info($"[AZOM/mBooster] calibration firmware log: {note}");
            RaiseProgress();
        }

        /// <summary>Latest firmware-log line relevant to the running routine,
        /// or null.</summary>
        public string? FirmwareNote { get { lock (_lock) return _firmwareNote; } }

        // ---- State machine --------------------------------------------------

        private void OnTick(object sender, ElapsedEventArgs e)
        {
            try { Step(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] calibration tick: {ex.Message}"); }
        }

        private void Step()
        {
            MBoosterCalStep step;
            DateTime due;
            lock (_lock) { step = _step; due = _stepDueUtc; }
            if (step == MBoosterCalStep.Idle || step == MBoosterCalStep.Done || step == MBoosterCalStep.Failed)
                return;

            UpdateCountdown(due);

            var controller = ResolveController();
            bool connected = controller != null && controller.IsConnected;
            bool expired = DateTime.UtcNow >= due;

            switch (step)
            {
                case MBoosterCalStep.TravelSweeping:
                    if (!expired) break;
                    if (!connected) { Fail(controller, "the pedal disconnected mid-calibration"); break; }
                    controller!.SendIntWrite($"mbooster-{_rolePrefix}-cal-stop", TravelParam,
                                             controller.CalibDeviceForAxis(_axisIndex));
                    // The firmware narrates a real sweep on group 0x0E
                    // ("Pedal Calib Start/Backward/Forward/pressure
                    // Calculating"). Silence across the whole window means it
                    // ACKED the start frame and did nothing — which is what a
                    // routed W17 lane does for the throttle pair (GVT5H8B8).
                    // Still send stop + reboot so nothing is left half-open,
                    // but say so rather than reporting success.
                    if (!_sawCalibProgress)
                    {
                        MozaLog.Info($"[AZOM/mBooster] travel calibration on {_rolePrefix}: the device acked the start frame but never reported a sweep");
                        Reboot(controller, MBoosterCalStep.Failed);
                        Fail(controller, "this pedal did not start a calibration sweep");
                        break;
                    }
                    Advance(MBoosterCalStep.TravelSettling, TravelSettleSeconds, "saving");
                    break;

                case MBoosterCalStep.TravelSettling:
                    if (!expired) break;
                    Reboot(controller, MBoosterCalStep.Rebooting);
                    break;

                case MBoosterCalStep.Rebooting:
                    // Observing the port gone counts as the drop, not just the
                    // registry's DeviceRemoved: Refresh() only disposes a
                    // controller whose PORT disappeared, and reconnects one
                    // whose port merely wedged — that second path never fires
                    // DeviceRemoved, so waiting on it alone stalled the verify
                    // for the full reconnect timeout.
                    if (!connected)
                    {
                        lock (_lock) _sawDisconnect = true;
                        break;
                    }
                    // Lane is back; let its own read burst answer before
                    // judging 0xB4.
                    if (_sawDisconnect)
                    {
                        Advance(MBoosterCalStep.Verifying, VerifySeconds, "rebooting");
                        ReadCalibrationState(controller!);
                    }
                    else if (expired)
                    {
                        // Never saw the port drop. The commit already happened
                        // on the stop frame, so this is a warning, not a
                        // failed calibration — but the pedal may still be in
                        // calibration mode, which the verify step will say.
                        MozaLog.Info("[AZOM/mBooster] calibration: pedal did not re-enumerate after the reboot within "
                                     + $"{ReconnectTimeoutSeconds:0}s — verifying anyway");
                        Advance(MBoosterCalStep.Verifying, VerifySeconds, "rebooting");
                    }
                    break;

                case MBoosterCalStep.Verifying:
                    // Settle window only — the reboot has landed and the
                    // lane's own reconnect burst is re-reading everything.
                    // Nothing branches on 0xB4 here (see its note above).
                    if (!expired) break;
                    if (controller == null || !controller.IsConnected)
                        Fail(controller, "the pedal did not come back after the reboot");
                    else
                        Finish(MBoosterCalStep.Done, "done");
                    break;

                case MBoosterCalStep.MotorRebooting:
                    // Pit House waits exactly 20.0s from the reboot before
                    // enter-debug, so hold that floor even if the lane is back
                    // sooner — the firmware's own startup check has to finish
                    // and leave the motor at MotorMode 0.
                    if (!expired) break;
                    if (!connected)
                    {
                        if (ElapsedInStep() >= ReconnectTimeoutSeconds)
                            Fail(controller, "the pedal did not come back after the reboot");
                        break;
                    }
                    controller!.SendIntWrite("mbooster-motor-cal-enter", MotorEnterParam,
                                             controller.CalibDeviceForAxis(_axisIndex));
                    Advance(MBoosterCalStep.MotorEnteringDebug, MotorEnterSeconds, "calibrating");
                    break;

                case MBoosterCalStep.MotorEnteringDebug:
                    if (!expired) break;
                    if (!connected) { Fail(controller, "the pedal disconnected mid-calibration"); break; }
                    controller!.SendIntWrite("mbooster-motor-cal-locate", MotorLocateParam,
                                             controller.CalibDeviceForAxis(_axisIndex));
                    lock (_lock) _locateStartedUtc = DateTime.UtcNow;
                    Advance(MBoosterCalStep.MotorLocateWait, MotorFirstPollSeconds, "calibrating");
                    break;

                case MBoosterCalStep.MotorLocateWait:
                    if (!expired) break;
                    if (!connected) { Fail(controller, "the pedal disconnected mid-calibration"); break; }
                    PollMotor(controller!);
                    break;

                case MBoosterCalStep.MotorPolling:
                    if (MotorStateOf(controller) == MotorStateComplete)
                    {
                        // The firmware disables debug mode and returns to
                        // MotorMode 12 on this same poll, so no reboot is
                        // needed — Pit House sends none here either. Finish()
                        // lifts the effect suspension.
                        Finish(MBoosterCalStep.Done, "done");
                        break;
                    }
                    if (LocateElapsed() >= MotorLocateTimeoutSeconds)
                    {
                        // The only exit from debug mode / MotorMode 8 the
                        // firmware offers is the completing poll, so a stuck
                        // run has to be rebooted out.
                        MozaLog.Info($"[AZOM/mBooster] motor calibration did not complete within {MotorLocateTimeoutSeconds:0}s (last state={MotorStateOf(controller)}) — rebooting to leave debug mode");
                        Reboot(controller, MBoosterCalStep.Failed);
                        Fail(controller, "the motor calibration timed out");
                        break;
                    }
                    if (!expired) break;
                    if (!connected) { Fail(controller, "the pedal disconnected mid-calibration"); break; }
                    PollMotor(controller!);
                    break;
            }
        }

        /// <summary>
        /// Ask the target device for register 0xB4, at most once a second. The
        /// state machine ticks four times a second, which would otherwise put
        /// four reads on the wire per second for the whole confirm/verify
        /// window — the reply cannot even arrive that fast behind the lane's
        /// own read burst.
        /// </summary>
        private void ReadCalibrationState(MBoosterDeviceController controller)
        {
            lock (_lock)
            {
                if ((DateTime.UtcNow - _lastStateReadUtc).TotalSeconds < 1.0) return;
                _lastStateReadUtc = DateTime.UtcNow;
            }
            controller.SendRead("mbooster-calibration-state",
                                controller.CalibDeviceForAxis(_axisIndex));
        }

        /// <summary>Last motor-locate status this run's target device reported:
        /// <see cref="MotorStateRunning"/>, <see cref="MotorStateComplete"/>,
        /// or -1 if it has not answered.</summary>
        private int MotorStateOf(MBoosterDeviceController? controller)
            => controller == null ? -1 : controller.MotorCalStateFor(controller.CalibDeviceForAxis(_axisIndex));

        private void PollMotor(MBoosterDeviceController controller)
        {
            controller.SendIntWrite("mbooster-motor-cal-locate", MotorQueryParam,
                                    controller.CalibDeviceForAxis(_axisIndex));
            Advance(MBoosterCalStep.MotorPolling, MotorPollIntervalSeconds, "calibrating");
        }

        private void Reboot(MBoosterDeviceController? controller, MBoosterCalStep next)
        {
            if (controller != null && controller.IsConnected)
            {
                controller.SendIntWrite("mbooster-soft-reboot", 0,
                                        controller.CalibDeviceForAxis(_axisIndex));
                controller.SetEffectsSuspended(false);
            }
            if (next == MBoosterCalStep.Rebooting)
            {
                lock (_lock) _sawDisconnect = false;
                Advance(MBoosterCalStep.Rebooting, ReconnectTimeoutSeconds, "rebooting");
            }
        }

        private void Fail(MBoosterDeviceController? controller, string reason)
        {
            controller?.SetEffectsSuspended(false);
            MozaLog.Info($"[AZOM/mBooster] calibration failed: {reason}");
            Finish(MBoosterCalStep.Failed, reason);
        }

        private void Finish(MBoosterCalStep step, string message)
        {
            var controller = ResolveController();
            controller?.SetEffectsSuspended(false);
            lock (_lock)
            {
                _step = step;
                _message = message;
                _secondsRemaining = 0;
            }
            try { _timer.Stop(); } catch { }
            RaiseProgress();
        }

        private void Advance(MBoosterCalStep step, double seconds, string message)
        {
            lock (_lock)
            {
                _step = step;
                _message = message;
                _stepStartedUtc = DateTime.UtcNow;
                _stepDueUtc = _stepStartedUtc.AddSeconds(seconds);
                _secondsRemaining = (int)Math.Ceiling(seconds);
            }
            RaiseProgress();
        }

        private void UpdateCountdown(DateTime due)
        {
            int remaining = (int)Math.Max(0, Math.Ceiling((due - DateTime.UtcNow).TotalSeconds));
            bool changed;
            lock (_lock)
            {
                changed = remaining != _secondsRemaining;
                _secondsRemaining = remaining;
            }
            if (changed) RaiseProgress();
        }

        private double ElapsedInStep()
        {
            lock (_lock) return (DateTime.UtcNow - _stepStartedUtc).TotalSeconds;
        }

        private double LocateElapsed()
        {
            lock (_lock)
                return _locateStartedUtc == default
                    ? 0.0
                    : (DateTime.UtcNow - _locateStartedUtc).TotalSeconds;
        }

        private MBoosterDeviceController? ResolveController()
        {
            string? id;
            lock (_lock) id = _identity;
            if (id == null) return null;
            // Always go back through the registry: our own soft reboot can have
            // disposed and replaced the controller instance we started with.
            foreach (var c in _registry.Devices)
            {
                if (string.Equals(c.Identity, id, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        private void RaiseProgress()
        {
            try { ProgressChanged?.Invoke(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] calibration progress handler: {ex.Message}"); }
        }

        public void Dispose()
        {
            try { _timer.Stop(); _timer.Elapsed -= OnTick; _timer.Dispose(); } catch { }
        }
    }
}
