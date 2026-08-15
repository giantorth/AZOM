using System.Threading;

namespace MozaPlugin.Devices
{
    /// <summary>Positively-identified passive-shifter model. <c>Unknown</c> until a
    /// probe resolves it: the standalone-USB lane resolves it from the PID at connect
    /// (instant); a base/hub-relayed shifter resolves SGP from its LED-read answer and
    /// HGP from the generic device-type identity probe.</summary>
    public enum ShifterModelKind
    {
        Unknown = 0,
        Hgp,
        Sgp,
    }

    /// <summary>
    /// Mutable detection-state bag shared by serial-reader, poll timer, UI, and
    /// telemetry threads. All fields are <c>volatile</c> or accessed via
    /// <see cref="Interlocked"/>/<see cref="Volatile"/> for cross-thread visibility.
    /// </summary>
    internal sealed class DeviceDetectionState
    {
        public volatile bool BaseDetected;
        public volatile bool DashDetected;
        public volatile bool NewWheelDetected;
        public volatile bool OldWheelDetected;
        public volatile bool HandbrakeDetected;
        public volatile bool PedalsDetected;
        public volatile bool HubDetected;
        public volatile bool Ab9Detected;
        // HGP (H-pattern) and SGP (sequential) are INDEPENDENT devices — a user can
        // run both at once, each on its own USB port (own PID), so they get their own
        // detection flag + owner + settings. A base/hub relay carries at most one
        // (single 0x1A bus), resolved to whichever flag by a positive probe answer
        // (SGP: LED read; HGP: device-type identity probe) — never a timeout.
        public volatile bool HgpDetected;
        public volatile bool SgpDetected;

        // Which MozaDeviceManager owns each routable peripheral — i.e. the pipe
        // it was detected on. Null = no opinion → callers fall back to the
        // primary manager. Pedals/handbrake can live on the base pipe OR on a
        // dedicated Universal Hub pipe (a base model with no pedal port + a hub),
        // so settings reads AND calibration writes must target the owning pipe.
        // Set (owner first, then the *Detected flag) by DeviceProber.Mark*Detected;
        // read (flag first, then owner) by HardwareApplier. Volatile for
        // cross-thread visibility between the two serial read threads and the UI.
        public volatile MozaDeviceManager? PedalsOwner;
        public volatile MozaDeviceManager? HandbrakeOwner;
        public volatile MozaDeviceManager? HgpOwner;
        public volatile MozaDeviceManager? SgpOwner;

        /// <summary>Which shifter model was detected on the given pipe, so relayed
        /// shifter replies (shared <c>shifter-*</c> command names) route to the right
        /// device's settings. Unknown before the model resolves.</summary>
        public ShifterModelKind ShifterModelForOwner(MozaDeviceManager dm) =>
            ReferenceEquals(HgpOwner, dm) ? ShifterModelKind.Hgp :
            ReferenceEquals(SgpOwner, dm) ? ShifterModelKind.Sgp : ShifterModelKind.Unknown;

        // Which MozaDeviceManager owns the base (wheelbase main/motor controller)
        // — the pipe that answered the base-mcu-temp detection cascade. Normally
        // the primary pipe (base is the primary connection). After a base→hub
        // primary migration (broken base, wheel on hub) the base is detected on a
        // dedicated base-aux pipe instead, so its FFB/ambient WRITES must target
        // that pipe rather than the hub-bound primary. Null = no opinion →
        // HardwareApplier falls back to the primary manager (today's behavior).
        // Set owner-first (then BaseDetected) by DeviceProber; read flag-first by
        // HardwareApplier. Volatile for cross-thread visibility.
        public volatile MozaDeviceManager? BaseOwner;

        // Flips true on the first base-ambient-brightness response (R21/R25/R27 family).
        public volatile bool BaseAmbientLedSupported;
        // Edge guard: fire the ambient probe at most once per base detect.
        public volatile bool BaseAmbientProbed;
        // Edge guard: apply+read equalizer7-10 at most once per base detect
        // (deferred until the base-fw-version reply confirms 10-band support).
        public volatile bool BaseEq10Probed;
        // Edge guard: log the resolved base firmware once per base detect (three
        // probes race for the answer — see DeviceProber's base-fw-version case).
        public volatile bool BaseFwVersionLogged;

        public volatile bool Group3ColorsRead;
        public volatile string LastKnownWheelModel = "";
        public int WheelPollMisses;

        // Flips true when a wheel on a new-protocol-only id (0x17/0x15) ends up
        // classified old-protocol — a current-generation wheel answering like a
        // legacy one, which points at outdated firmware. Drives the
        // firmware-update banner (StatusHintKind.WheelFirmwareOutdated).
        public volatile bool NewWheelActingOldProtocol;
        // Model name behind the advisory once a valid group-0x07 reply names it
        // (e.g. "W13"). Empty until then; the banner falls back to generic wording.
        public volatile string NewWheelActingOldModel = "";

        // Bit g set => wheel LED group g present. Accessed via Interlocked.
        private int _wheelLedGroupMask;

        public int WheelLedGroupMask => Volatile.Read(ref _wheelLedGroupMask);

        public bool IsWheelLedGroupPresent(int group)
        {
            if (group < 2 || group > 4) return false;
            return (Volatile.Read(ref _wheelLedGroupMask) & (1 << group)) != 0;
        }

        /// <summary>
        /// Atomically set bit <paramref name="group"/>. Returns true if the bit
        /// transitioned 0→1 (caller may want to log the detection edge).
        /// </summary>
        public bool TrySetWheelLedGroupPresent(int group)
        {
            int bit = 1 << group;
            int prev;
            do
            {
                prev = _wheelLedGroupMask;
                if ((prev & bit) != 0) return false;
            } while (Interlocked.CompareExchange(ref _wheelLedGroupMask, prev | bit, prev) != prev);
            return true;
        }

        public void ResetWheelLedGroupMask() => Interlocked.Exchange(ref _wheelLedGroupMask, 0);

        /// <summary>
        /// Clear all device-detection flags. Called on plugin reload teardown so
        /// a load → unload → reload doesn't carry over stale detected state.
        /// </summary>
        public void ResetAll()
        {
            BaseDetected = false;
            DashDetected = false;
            BaseAmbientLedSupported = false;
            BaseAmbientProbed = false;
            BaseEq10Probed = false;
            NewWheelDetected = false;
            OldWheelDetected = false;
            HandbrakeDetected = false;
            PedalsDetected = false;
            HubDetected = false;
            Ab9Detected = false;
            HgpDetected = false;
            SgpDetected = false;
            NewWheelActingOldProtocol = false;
            NewWheelActingOldModel = "";
            PedalsOwner = null;
            HandbrakeOwner = null;
            HgpOwner = null;
            SgpOwner = null;
            BaseOwner = null;
        }

        /// <summary>
        /// Clear wheel-scoped flags for hot-swap recovery. Preserves
        /// base/hub/handbrake/pedals state.
        /// </summary>
        public void ResetWheel()
        {
            NewWheelDetected = false;
            OldWheelDetected = false;
            DashDetected = false;
            ResetWheelLedGroupMask();
            Group3ColorsRead = false;
            WheelPollMisses = 0;
            LastKnownWheelModel = "";
            NewWheelActingOldProtocol = false;
            NewWheelActingOldModel = "";
        }
    }
}
