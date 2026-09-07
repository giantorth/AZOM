using System;
using System.Runtime.InteropServices;

namespace MozaPlugin.Hardware
{
    /// <summary>
    /// Keeps the host process responsive while SimHub is the *background* app during
    /// fullscreen gameplay. Two Windows policies otherwise throttle us toward a stall —
    /// and a stalled write thread / engagement loop is why a mid-game control write (e.g.
    /// an RSF steering-lock change over the PitHouse UDP surface) reaches the base but
    /// isn't adopted live until the user alt-tabs and the foreground lifts the throttle:
    ///
    ///   1. <b>EcoQoS / power throttling</b> (Win10 1809+, aggressive on Win11) parks
    ///      background processes on efficiency cores at reduced clock. Opting out via
    ///      <c>PROCESS_POWER_THROTTLING_EXECUTION_SPEED</c> (control bit set, state bit
    ///      clear) keeps us at full speed in the background, as PitHouse does.
    ///   2. <b>Background timer-resolution clamp</b> (Win10 2004+): a foreground
    ///      <c>timeBeginPeriod(1)</c> is silently ignored once we lose focus unless we opt
    ///      OUT of <c>PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION</c> — control bit
    ///      set, state bit CLEAR ("always honor Timer Resolution Requests", Win11).
    ///
    /// Both throttling bits live in the same <c>ProcessPowerThrottling</c> word, so this
    /// class is their single owner — <see cref="SetExecutionThrottleOptOut"/> and the
    /// timer raise can't clobber each other's mask. Every native call is best-effort:
    /// under Wine / down-level Windows the API is absent or rejects the class, in which
    /// case we silently stay at OS defaults (the plugin still runs, just throttled).
    /// </summary>
    internal sealed class ProcessResponsivenessManager : IDisposable
    {
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess, int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation, int processInformationSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4; // Win11

        private const uint PeriodMs = 1;

        private readonly object _gate = new object();
        private bool _timerRaised;   // timeBeginPeriod(1) currently held
        private bool _execOptOut;    // EcoQoS execution-speed opt-out requested

        // Last power-throttling word *requested*, not the one the OS accepted: Win10
        // rejects IGNORE_TIMER_RESOLUTION and we fall back, so accepted != requested
        // forever and keying on accepted re-issues every reconcile (telemetry rate).
        private bool _ptAttempted;
        private uint _ptControl, _ptState;

        /// <summary>
        /// Hold (or release) the 1 ms multimedia timer and keep it honoured in the
        /// background. Idempotent; balances <c>timeBeginPeriod</c>/<c>timeEndPeriod</c>.
        /// </summary>
        public void SetTimerResolution(bool wanted)
        {
            lock (_gate)
            {
                if (wanted != _timerRaised)
                {
                    try
                    {
                        if (wanted) timeBeginPeriod(PeriodMs); else timeEndPeriod(PeriodMs);
                        _timerRaised = wanted;
                        MozaLog.Info(wanted
                            ? "Responsiveness: timer resolution raised to 1ms"
                            : "Responsiveness: timer resolution released");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"Responsiveness: time{(wanted ? "Begin" : "End")}Period failed: {ex.Message}");
                    }
                }
                ReconcilePowerThrottlingLocked();
            }
        }

        /// <summary>
        /// Opt the process out of EcoQoS execution-speed throttling so it keeps full
        /// clock in the background. Idempotent.
        /// </summary>
        public void SetExecutionThrottleOptOut(bool wanted)
        {
            lock (_gate)
            {
                _execOptOut = wanted;
                ReconcilePowerThrottlingLocked();
            }
        }

        // Recompute the desired ProcessPowerThrottling word from the two requests and
        // push it if it changed. Both mechanisms are opted OUT of whenever either request
        // is live. Must hold _gate.
        private void ReconcilePowerThrottlingLocked()
        {
            uint control, state;
            if (!_execOptOut && !_timerRaised)
            {
                // Nothing requested — hand all managed bits back to the OS default.
                control = 0;
                state = 0;
            }
            else
            {
                control = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
                // ControlMask selects the mechanism; StateMask turns it ON. We want BOTH
                // mechanisms OFF, so StateMask stays 0:
                //   EXECUTION_SPEED off        => EcoQoS disabled, full clock.
                //   IGNORE_TIMER_RESOLUTION off => "always honor Timer Resolution Requests".
                // Setting the IGNORE_TIMER_RESOLUTION bit means the opposite — Windows then
                // discards our own timeBeginPeriod(1), pinning every timer to the 15.625ms
                // default grid (a 20ms worker lands on 31.25ms => 50Hz runs at 32Hz). Only
                // Win11 accepts this bit, so the fault was invisible on Win10.
                state = 0;
            }

            if (_ptAttempted && control == _ptControl && state == _ptState) return;
            _ptAttempted = true; _ptControl = control; _ptState = state;

            // Win11 honours both bits; Win10 (no IGNORE_TIMER_RESOLUTION) rejects the
            // whole call, so fall back to EXECUTION_SPEED alone — the opt-out that
            // actually un-throttles us is the load-bearing half.
            if (!TrySetPowerThrottling(control, state)
                && control != 0
                && (control & PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION) != 0)
            {
                TrySetPowerThrottling(PROCESS_POWER_THROTTLING_EXECUTION_SPEED, 0);
            }
        }

        private static bool TrySetPowerThrottling(uint control, uint state)
        {
            try
            {
                var s = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = control,
                    StateMask = state,
                };
                bool ok = SetProcessInformation(
                    GetCurrentProcess(), ProcessPowerThrottling, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                if (ok)
                    MozaLog.Info($"Responsiveness: power-throttling control=0x{control:X} state=0x{state:X}");
                else
                    MozaLog.Debug($"Responsiveness: SetProcessInformation rejected (control=0x{control:X}, err {Marshal.GetLastWin32Error()})");
                return ok;
            }
            catch (Exception ex)
            {
                // EntryPointNotFound under Wine / pre-1809 Windows — best-effort.
                MozaLog.Debug($"Responsiveness: SetProcessInformation unavailable: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_timerRaised)
                {
                    try { timeEndPeriod(PeriodMs); } catch { /* best-effort on teardown */ }
                    _timerRaised = false;
                }
                _execOptOut = false;
                ReconcilePowerThrottlingLocked();
            }
        }
    }
}
