using System.Threading;

namespace MozaPlugin.Devices
{
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

        public volatile bool Group3ColorsRead;
        public volatile string LastKnownWheelModel = "";
        public int WheelPollMisses;

        // One-shot guard for old-protocol wheel device-definition deployment. Set
        // once a definition has been deployed for this old wheel — either the
        // model-specific one (e.g. "MOZA ES", deployed from the es-wheel-model-name
        // case once id 0x18 answers) or the generic old-proto fallback (deployed in
        // PollStatus when no model-specific deploy happened within a grace window).
        // Gating the PollStatus fallback on this flag stops an ES wheel from also
        // getting the generic device, and stops re-deploying every tick.
        public volatile bool OldProtoFallbackDeployed;

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
            NewWheelDetected = false;
            OldWheelDetected = false;
            HandbrakeDetected = false;
            PedalsDetected = false;
            HubDetected = false;
            Ab9Detected = false;
            PedalsOwner = null;
            HandbrakeOwner = null;
            BaseOwner = null;
            OldProtoFallbackDeployed = false;
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
            OldProtoFallbackDeployed = false;
        }
    }
}
