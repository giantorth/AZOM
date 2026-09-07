using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using HidSharp;
using MozaPlugin.Diagnostics;


namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Latest-wins coalescing slot identifier; one slot per kind. Use Send() for
    /// one-shot/session traffic instead.
    /// </summary>
    public enum StreamKind
    {
        TierDash0 = 0,
        TierDash1 = 1,
        TierDash2 = 2,
        TierDash3 = 3,
        TierDash4 = 4,
        TierDash5 = 5,
        TierDash6 = 6,
        TierDash7 = 7,
        Enable = 8,
        Sequence = 9,
        Mode = 10,
        // AB9 host-rendered engine vibration (0x20/0x0A 05) at ~91 Hz.
        Ab9EngineVibration = 11,
        // AB9 secondary FFB sub-streams (latest-wins per lane).
        Ab9EnginePulse = 12,
        Ab9TriggerA = 13,        // 0x0D start of both engine-pulse dampers (flat ~9 Hz)
        Ab9TriggerRpm = 14,      // 0x0D start of the engine-vib sine (RPM-tracked)
        Ab9TriggerExtra = 15,    // spare 0x0D lane
        Ab9LowRate = 16,         // unused — 0x08 shift constant-force rides the one-shot FIFO
        // mBooster motor-write lane for pedal axis 0. Each axis runs its own
        // worker and needs its OWN latest-wins lane — see MBoosterEffectAxis1/2
        // at 44+ for the chained axes.
        MBoosterEffect = 17,

        // ── LED lanes (absolute slots 29+) ───────────────────────────────────
        // Latest-wins LED writes ride the coalescing stream lane instead of the
        // paced/throttled one-shot FIFO, so a co-resident value stream (a CM2
        // sharing the wheelbase bus) can never starve them. These MUST start at
        // 29 — absolute slots 18..28 are the CM2 second tier-def pipeline
        // (StreamSlotBase 18 + logical 0..10, written via int-cast, not named
        // members), so a named member at 18..28 would alias a CM2 value slot.
        // See the slot-layout comment + StreamSlotCount below.
        //
        // WHEEL live LED writes (RPM/button/knob colours + bitmasks) are NOT here:
        // they stay on the paced one-shot FIFO. Streaming them emitted the whole set
        // in <1 ms host-side with nothing discarded (measured), but the rim drops
        // unpaced bursts — losing frames, which reads as laggy / off-time animation
        // and knob rings reverting to stored colours. The FIFO's 4 ms spacing is what
        // the rim needs, and its ordering keeps each colour ahead of its bitmask.
        // Only the CM2 dash (its own USB pipe, or the bus lane) streams.
        //
        // CM2 per-LED RPM indicator colours (0B 00) — up to 10 discrete writes per
        // frame (the SyncRpmColors amplifier); one slot each bounds them. Ordered
        // BEFORE DashRpmBitmask so the write loop (drains slots in index order)
        // emits colours before the lit-mask in the same pass — matching the
        // manager's "never light an LED before its colour lands" sequencing.
        DashRpmColor0 = 29,
        DashRpmColor1 = 30,
        DashRpmColor2 = 31,
        DashRpmColor3 = 32,
        DashRpmColor4 = 33,
        DashRpmColor5 = 34,
        DashRpmColor6 = 35,
        DashRpmColor7 = 36,
        DashRpmColor8 = 37,
        DashRpmColor9 = 38,
        DashRpmBitmask = 39,      // CM2 dash-send-telemetry (41 FD DE) — after colours
        DashFlagColors = 40,      // CM2 dash-flag-colors (32 08 00, 18-byte array)

        // ── Wheelbase LFE lanes (absolute slots 41+) ─────────────────────────
        // Host-rendered wheelbase low-frequency effects (cmd 0x2D/0x77) on the
        // BASE primary connection. The base runs three identical concurrent
        // oscillators (wire effect ids 1/2/0) and sums them; each needs its own
        // latest-wins lane so all three can stream at once without coalescing.
        // The plugin's own LFE mode drives ids 1/2 continuously (engine/ABS) and
        // id 0 as a one-shot gearshift burst on the FIFO; the ShakeIt haptics
        // device drives all three as continuous tones, so id 0 gets Osc0 here.
        BaseLfeEngine = 41,   // wire id 1
        BaseLfeAbs = 42,      // wire id 2
        BaseLfeOsc0 = 43,     // wire id 0 (continuous-tone lane; ShakeIt only)

        // ── mBooster chained-axis motor lanes (absolute slots 44+) ───────────
        // One lane per pedal axis. Axis 0 keeps MBoosterEffect (17); a CHAINED
        // lane's axes 1/2 get these. Every axis runs its own effect worker at
        // ~50 Hz, but there used to be only the single slot at 17, so the
        // non-primary axes were shunted onto the paced one-shot FIFO instead
        // (MBoosterEffectWorker.SendMotor). That FIFO delivers EVERY frame
        // while the stream lane coalesces latest-wins, so a chained brake got
        // the full 50 Hz noise stream while the throttle on the stream lane
        // dropped frames whenever the write loop lagged — the brake's Road
        // Texture felt far stronger than the throttle's for identical
        // settings. Same transport for every axis now.
        MBoosterEffectAxis1 = 44,
        MBoosterEffectAxis2 = 45,
    }

    /// <summary>Device family targeted by the serial probe fallback (registry-empty case).</summary>
    public enum MozaProbeTarget
    {
        BaseAndHub,
        Ab9,
        // mBooster has no handshake (protocol note § 1) and the multi-device
        // discovery is registry-driven; the serial-probe fallback would have
        // to write at every COM port to find a unit, which we deliberately
        // skip to keep the per-port probe surface minimal. See FindMozaPort.
        MBooster,
        // Universal Hub on its OWN dedicated connection (PID 0x0020), used when
        // a wheelbase is also present so the base stays the telemetry-driving
        // primary and the hub is enumerated in parallel. Probe fallback issues
        // only the hub probe (0x64/0x12/0x03), a single pass. The hub-ONLY case
        // (no base) is still handled by the BaseAndHub primary, which falls back
        // to the hub when no wheelbase port exists — so this target never claims
        // a hub the primary already holds (the _activePorts guard enforces it).
        HubOnly,
        // Standalone-USB pedals / handbrake on their OWN dedicated connection
        // (pedals PID 0x0001/0x0003/0x0011, handbrake 0x001F), one per
        // MozaStandalonePeripheralRegistry controller. Like MBooster, discovery
        // is registry-only by design (the PIDs are registered, so the registry
        // always classifies them) and the serial-probe fallback is force-
        // disabled — these targets never write scan bytes to unclassified COM
        // ports. Kept as distinct values so the probe shape stays honest if the
        // fallback is ever enabled; no probe-emission branch is wired today.
        PedalsOnly,
        HandbrakeOnly,
        // Standalone-USB HGP (0x001E) / SGP (0x0023) shifter, one per
        // MozaStandalonePeripheralRegistry controller. Registry-only like the
        // pedals/handbrake targets; no probe-emission branch is wired.
        ShifterOnly,
    }

    public class MozaSerialConnection : IDisposable
    {
        // Stream-slot lanes (latest-wins coalescing). Layout:
        //   0..10  — tier-def pipeline at slot-base 0 (TierDash0-7, Enable, Sequence,
        //            Mode) — the WHEEL screen pipeline, or an FSR1 driver's records.
        //   11..17 — AB9 / mBooster (absolute, per StreamKind).
        //   18..28 — a SECOND tier-def pipeline at slot-base 18 (a bus-attached CM2
        //            dash sharing this connection). See TelemetrySender.StreamSlotBase.
        //   29..40 — LED lanes (CM2 dash RPM colours + bitmask + flag colours). Wheel
        //            live LED writes are NOT here — they stay on the paced one-shot
        //            FIFO (the rim drops unpaced bursts). See the LED StreamKind members.
        //   41..43 — wheelbase LFE lanes (the three summed host-rendered oscillator
        //            streams: engine id1, ABS id2, Osc0 id0).
        //   44..45 — mBooster chained-axis motor lanes (axes 1/2; axis 0 is slot 17).
        // A CM2 on its own USB connection runs at base 0 on THAT connection, so the
        // second block is only used when two pipelines share one connection.
        // NOTE: keep this >= (highest StreamKind + 1). The static ctor below
        // asserts the regions are disjoint and fit.
        private const int StreamSlotCount = 46;

        // Startup slot-layout invariant: the LED lanes (29+) must not alias the
        // wheel value pipeline (0..10), AB9/mBooster (11..17), or the CM2 second
        // tier-def pipeline (TelemetrySender.StreamSlotBase 18 + StreamBlockSize 11
        // = 18..28), and the highest LED slot must fit StreamSlotCount. Wrong slot
        // bases silently drop frames otherwise — fail loud in dev builds.
        static MozaSerialConnection()
        {
            const int ledFirst = (int)StreamKind.DashRpmColor0;  // 29
            const int ledLast = (int)StreamKind.DashFlagColors;  // 40 (highest LED slot)
            const int cm2ValueLast = 18 + 11 - 1;                // CM2 base 18 + block 11
            System.Diagnostics.Debug.Assert(ledFirst > cm2ValueLast,
                "LED stream slots must start after the CM2 value pipeline (18..28)");
            System.Diagnostics.Debug.Assert(ledLast < StreamSlotCount,
                "StreamSlotCount too small for the LED stream slots");
            const int lfeLast = (int)StreamKind.BaseLfeOsc0;     // 43
            System.Diagnostics.Debug.Assert(lfeLast > ledLast,
                "wheelbase LFE stream slots must start after the LED lanes");
            System.Diagnostics.Debug.Assert(lfeLast < StreamSlotCount,
                "StreamSlotCount too small for the wheelbase LFE stream slots");
            const int mbLast = (int)StreamKind.MBoosterEffectAxis2; // 45 (highest slot overall)
            System.Diagnostics.Debug.Assert((int)StreamKind.MBoosterEffectAxis1 > lfeLast,
                "mBooster chained-axis stream slots must start after the LFE lanes");
            System.Diagnostics.Debug.Assert(mbLast < StreamSlotCount,
                "StreamSlotCount too small for the mBooster chained-axis stream slots");
        }

        // Ports held by a live connection — probe path skips these (Wine pty
        // doesn't enforce O_EXCL, so a second Open would steal the device).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activePorts =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when a live sibling connection in this process currently
        /// holds <paramref name="portName"/>. Lets the plugin's primary→wheelbase
        /// migration skip a hub port already claimed by another connection.</summary>
        public static bool IsPortHeld(string? portName) =>
            !string.IsNullOrEmpty(portName) && _activePorts.ContainsKey(portName!);

        // PID filter for port discovery; null PID = probe-based (unknown).
        private readonly Func<string?, bool>? _pidFilter;
        private readonly MozaProbeTarget _probeTarget;
        // Hard-disable the probe fallback for THIS lane. Enumeration-only lanes
        // (base-aux, hub, dashboard, standalone peripherals) pass () => true;
        // lanes that still need the fallback leave it null. Not user-settable.
        private readonly Func<bool>? _disableProbeFallback;

        private volatile IMozaPort? _port;
        private Thread? _readThread;
        private Thread? _writeThread;
        private Thread? _dispatchThread;

        // Inbound dispatch queue. The read loop must NEVER run subscriber code:
        // every handler (session-ack round-trip, catalog append + full-buffer
        // END scan, FF-record reassembly, log writes) ran inline on the read
        // thread, so any one of them stalling stopped reception outright. On the
        // Wine device path a stalled read loses bytes: bundles 7H89M4JA /
        // EJ92X08Y / 15YZY7Z4 / W0V1PF9V / EKJXHSV9 (Wine 10.0) show RX going
        // deaf for 0.6-2.0 s while TX keeps running at ~100 frames/s, then a
        // burst of 4 frames in one millisecond with sequence numbers missing —
        // and resync counts of 56-465. The same plugin on native Windows
        // (bundle VG9V7XB2) reports resync=0. Decoupling reception from
        // dispatch keeps the drain rate independent of handler cost.
        //
        // Single consumer thread, so frame ORDER is preserved — session chunk
        // reassembly depends on it. Bounded and drop-on-full rather than
        // block-on-full: blocking the producer would reintroduce the very stall
        // this exists to prevent. Drops are counted and surfaced.
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _rxQueue
            = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent _rxSignal = new AutoResetEvent(false);
        private int _rxQueueDepth;
        private int _rxQueueHighWater;
        private int _rxQueueDrops;
        private const int RxQueueCapacity = 4096;

        // Read-cadence instrumentation. Proves (or refutes) "the read thread
        // could not keep up" from a bundle instead of by inference.
        private int _maxBytesToRead;      // largest BytesToRead ever observed
        private int _fullReadCount;       // reads clamped to the 4096B scratch = we were behind
        private int _maxReadGapMs;        // longest gap between successive non-empty reads
        // Ports with a probe thread that timed out and was ABANDONED (its
        // SerialPort.Open() is still blocked in a Wine syscall). We must NOT
        // close/dispose that SerialPort from another thread — doing so mid-Open
        // is a native-crash vector under Wine (the original "crashes SimHub on
        // a freshly-powered base": a not-ready CDC-ACM port makes Open() hang,
        // the old timeout path force-closed it cross-thread, Wine segfaulted).
        // Instead we abandon the background thread (it self-cleans when the
        // syscall finally returns) and skip re-probing the port until it does,
        // so we never spawn a second hung thread on the same stuck port and
        // never enter the cross-thread-dispose crash window. Keyed by port name.
        private static readonly ConcurrentDictionary<string, byte> _probeInFlight =
            new ConcurrentDictionary<string, byte>();

        // Per-port blocked-probe backoff. A port whose Open() keeps hanging (a
        // non-MOZA CDC device, or one another process holds open) used to be
        // re-probed on every sweep forever.
        //
        // The interval is CAPPED, never retired: probing a port must never stop
        // permanently. The whole reason ProbeWithTimeout abandons threads is the
        // Wine freshly-powered-base wedge, where Open() hangs on a port that IS
        // a MOZA base and only opens in time a few sweeps later. A give-up rule
        // would blind exactly that port for the rest of the session, and the
        // abandoned thread's late (responded, reachable) cannot rescue it — on
        // the timeout path it lands in locals of a call that already returned.
        //
        // Keyed per PORT, not per ProbeKind: a wedged Open() is a property of
        // the port. The value is immutable and replaced wholesale, so a
        // concurrent reader on another lane's reconnect timer sees one reference,
        // never a torn pair. Written only from the probe sweep (reconnect-timer
        // ThreadPool callback) — ConcurrentDictionary, no lock, per the hard rule
        // in docs/DEVELOPMENT.md § Threading model.
        private static readonly ConcurrentDictionary<string, ProbeBackoff> _probeBackoff =
            new ConcurrentDictionary<string, ProbeBackoff>();

        private sealed class ProbeBackoff
        {
            public readonly int Consecutive;
            public readonly DateTime NextEligibleUtc;

            public ProbeBackoff(int consecutive, DateTime nextEligibleUtc)
            {
                Consecutive = consecutive;
                NextEligibleUtc = nextEligibleUtc;
            }
        }

        private const int ProbeBackoffBaseMs = 10_000;
        private const int ProbeBackoffMaxMs = 300_000;

        /// <summary>Retry interval after <paramref name="consecutive"/> blocked
        /// probe cycles: ProbeBackoffBaseMs doubled per cycle, capped at
        /// ProbeBackoffMaxMs. The shift is clamped so a long-lived wedge can't
        /// overflow it.</summary>
        private static long ProbeBackoffFor(int consecutive)
        {
            int steps = Math.Min(Math.Max(consecutive, 1) - 1, 20);
            return Math.Min((long)ProbeBackoffBaseMs << steps, ProbeBackoffMaxMs);
        }

        // Priority lane: unpaced FIFO for tiny, time-critical frames (fc:00 session
        // acks). Drained ahead of one-shot every WriteLoop iteration so an ack
        // can't get buried behind a 1000-chunk tier-def burst — the wheel times
        // out sessions whose acks lag more than ~1 s and silently drops them.
        // Verified: PH sess=0x02 ack-lag stays ≤ 870 ms even during heavy bursts
        // across 9 PitHouse bridge captures (median 35–95 ms idle, 50–354 ms busy);
        // the plugin's single-FIFO setup let user-bundle sess=0x07 acks lag 4.5 s+
        // during dashboard switch tier-def floods, matching the "telemetry dies
        // after switch" symptom on issue #43.
        private readonly ConcurrentQueue<byte[]> _priorityQueue = new ConcurrentQueue<byte[]>();
        // One-shot lane: FIFO + 4 ms burst pacing (bases drop unpaced rapid writes).
        private readonly ConcurrentQueue<byte[]> _oneShotQueue = new ConcurrentQueue<byte[]>();
        // Stream lane: per-kind latest-wins slots, unpaced. SendStream overwrites pending values.
        private readonly byte[]?[] _streamSlots = new byte[StreamSlotCount][];
        private readonly WriteBudget _budget = new WriteBudget();
        private int _framesDropped;
        private int _checksumFailures;
        // Read-side frame decode errors (escape violation / short decode). Was
        // log-only, which meant one line per bad frame on a resync path that
        // retries per byte; the aggregate is the useful signal, so it rides the
        // same diagnostics surface as the other wire errors.
        private int _frameErrors;
        private int _frameStartScanResyncs;
        // Frame-start candidates rejected because the LEN byte was implausible.
        // This path used to `continue` silently, so a stream full of false 0x7E
        // locks showed up as a huge `resync` count with frameErr/cksumFail near
        // zero and no way to tell why — bundles 15YZY7Z4 / W0V1PF9V
        // (2026-08-23) read resync=278/465 against only 8 rejections total.
        private int _lengthRejects;
        // Doubled 0x7E skipped during a frame-start scan. A stuffed body byte
        // can never begin a frame, so this counts false locks AVOIDED; a high
        // value against a low resync count means the resync logic is working.
        private int _stuffedPairSkips;
        // Resync histogram by skip-byte count. Buckets [0]=1B, [1]=2B,
        // [2]=3-4B, [3]=5-8B, [4]=9-16B, [5]=17-32B, [6]=33-64B, [7]=>64B.
        // Lets diagnostics show "are resyncs single stray bytes (USB padding)
        // or multi-byte gaps (real wire corruption)?" without surfacing every
        // individual resync as a log line.
        private readonly int[] _resyncSkipBucket = new int[8];
        // Last-N skipped-byte samples (hex) for diagnostics. Newest-first
        // ring under a tiny lock — appended at every resync. Cap at 16 so
        // the tab shows enough variety to spot patterns (e.g. always 0x00,
        // always firmware-debug header bytes) without bloating the buffer.
        private const int ResyncSampleCapacity = 16;
        private readonly object _resyncSampleLock = new object();
        private readonly System.Collections.Generic.LinkedList<string> _resyncSamples
            = new System.Collections.Generic.LinkedList<string>();
        private volatile bool _running;
        // Per-open generation. Each I/O thread is bound to the generation of the
        // FinishOpen that created it; Disconnect bumps it, so a thread that
        // outlives its Join(1000) (syscall-wedged under Wine) exits when it
        // wakes instead of re-attaching to the next open's port and queues.
        private int _ioGeneration;

        /// <summary>
        /// Token identifying the current port session — bumped by every open and
        /// every teardown. Owners that hand the device per-session state (the AB9's
        /// FFB effect table) latch on this rather than on a bool, so a re-opened
        /// port always re-runs its handshake.
        /// </summary>
        public int IoGeneration => Volatile.Read(ref _ioGeneration);

        private readonly object _lock = new object();
        private string? _lastPortName;

        /// <summary>
        /// Last COM port that connected successfully. Persisted across sessions
        /// by the plugin so <see cref="Connect"/> can try it first on next launch.
        /// </summary>
        public string? LastPortName
        {
            get => _lastPortName;
            set => _lastPortName = value;
        }

        private string? _lastDeviceId;

        /// <summary>
        /// Durable identity (<c>VID:PID:serial</c>) of the last device that
        /// connected successfully — see <see cref="MozaPortDiscovery.DurableId"/>.
        /// Persisted by the plugin alongside <see cref="LastPortName"/> and
        /// preferred over it on reconnect, because a tty/COM name is only stable
        /// until the next replug while this is not.
        /// </summary>
        public string? LastDeviceId
        {
            get => _lastDeviceId;
            set => _lastDeviceId = value;
        }
        private volatile bool _shutdownRequested;

        // Consecutive I/O error tracking. After sleep/resume the SerialPort handle
        // stays .IsOpen==true but every read/write throws IOException("Not ready"),
        // so nothing triggers reconnect. Count failures and force-close at threshold.
        private int _consecutiveIoErrors;
        // 0 = healthy, 1 = port-dead branch already taken. int+CompareExchange
        // so two failure paths can race the threshold check without both
        // closing the port and double-logging.
        private int _portFailureLogged;
        private const int PortDeadThreshold = 10;
        // Half-open-tty liveness. The count-only PortDeadThreshold above never
        // fires for a "half-open" port that delivers BytesToRead==0 forever
        // WITHOUT throwing (a real failure mode: sleep/resume, USB stall) — the
        // ReadLoop just spins at Thread.Sleep(2) and nothing triggers reconnect.
        // We stamp the last successful read and, once the wheel HAS talked,
        // force a reconnect if inbound goes silent past ReadIdleDeadMs. The
        // plugin's ~1 Hz parity polls keep a healthy wheel answering well inside
        // this window, so a breach means the port is dead, not merely idle.
        private long _lastRxUtcTicks;
        private const int ReadIdleDeadMs = 30_000;

        // Classified open-failure surface. UI hint-builder reads this every
        // 500 ms to distinguish port-in-use from generic disconnect. Counter
        // is incremented atomically; the snapshot struct is copied under
        // _failureLock so the UI sees a consistent view across fields.
        private readonly object _failureLock = new object();
        private ConnectionFailureInfo _lastFailure;
        private int _consecutiveOpenFailures;
        private DateTime _lastSuccessfulOpenUtc;

        public event Action<byte[]>? MessageReceived;
        // Raised on the I/O thread after HandleIoFailure force-closes the port.
        // Subscribers must be background-safe and non-blocking.
        public event Action? Disconnected;
        public bool IsConnected => _port?.IsOpen == true;

        /// <summary>
        /// Label used when traffic on this connection is recorded by
        /// <see cref="SerialTrafficCapture"/>. Set by the owner (e.g. "wheelbase",
        /// "ab9") so the export can disambiguate frames from each pipe.
        /// </summary>
        public string CaptureLabel { get; set; } = "wheelbase";

        /// <summary>
        /// USB Product ID from device enumeration (the Windows registry, or sysfs
        /// under Wine/Proton). Null only when neither source saw the device and it
        /// was found by the serial probe, which carries no identity.
        /// </summary>
        public string? DiscoveredPid { get; private set; }

        /// <summary>True if the hub probe (0x64/0x12/0x03) succeeded — gates the post-session 5-slot burst.</summary>
        public bool HubProbeSucceeded { get; private set; }

        /// <summary>Sliding-window snapshot of write-budget utilization. Read by
        /// the diagnostics tab so the user can see when the link approaches
        /// saturation. Each call resets the rolling peak.</summary>
        public WriteBudget.Snapshot CurrentBudget => _budget.GetSnapshot();

        /// <summary>Wire-error counters surfaced together — drops on write,
        /// checksum mismatches on read, and frame-start resyncs (junk bytes
        /// skipped between frames).</summary>
        public WireErrorCounters WireErrors
        {
            get
            {
                int[] histo = new int[_resyncSkipBucket.Length];
                for (int i = 0; i < histo.Length; i++)
                    histo[i] = Interlocked.CompareExchange(ref _resyncSkipBucket[i], 0, 0);
                string[] samples;
                lock (_resyncSampleLock)
                {
                    samples = new string[_resyncSamples.Count];
                    int j = 0;
                    foreach (var s in _resyncSamples) samples[j++] = s;
                }
                return new WireErrorCounters(
                    Interlocked.CompareExchange(ref _framesDropped, 0, 0),
                    Interlocked.CompareExchange(ref _checksumFailures, 0, 0),
                    Interlocked.CompareExchange(ref _frameErrors, 0, 0),
                    Interlocked.CompareExchange(ref _frameStartScanResyncs, 0, 0),
                    histo,
                    samples,
                    Interlocked.CompareExchange(ref _lengthRejects, 0, 0),
                    Interlocked.CompareExchange(ref _stuffedPairSkips, 0, 0),
                    Interlocked.CompareExchange(ref _maxBytesToRead, 0, 0),
                    Interlocked.CompareExchange(ref _fullReadCount, 0, 0),
                    Interlocked.CompareExchange(ref _maxReadGapMs, 0, 0),
                    Interlocked.CompareExchange(ref _rxQueueHighWater, 0, 0),
                    Interlocked.CompareExchange(ref _rxQueueDrops, 0, 0));
            }
        }

        public readonly struct WireErrorCounters
        {
            public readonly int FramesDropped;
            public readonly int ChecksumFailures;
            /// <summary>Read-side decode errors: bad 0x7E escape or a frame
            /// whose decoded length didn't match its header length.</summary>
            public readonly int FrameErrors;
            public readonly int FrameStartScanResyncs;
            /// <summary>Distribution of bytes-skipped at each resync. Buckets:
            /// [0]=1B, [1]=2B, [2]=3-4B, [3]=5-8B, [4]=9-16B, [5]=17-32B,
            /// [6]=33-64B, [7]=>64B. Total across buckets ==
            /// <see cref="FrameStartScanResyncs"/>.</summary>
            public readonly int[] ResyncSkipHistogram;
            /// <summary>Most recent skipped-byte samples, hex-formatted
            /// ("3B: 00 41 0B"). Oldest first, newest last. Capped to 16.</summary>
            public readonly string[] RecentResyncSamples;
            /// <summary>Frame-start candidates rejected for an implausible LEN
            /// byte. Large relative to <see cref="FrameErrors"/> +
            /// <see cref="ChecksumFailures"/> means most resyncs are false 0x7E
            /// locks, not a genuinely corrupt line.</summary>
            public readonly int LengthRejects;
            /// <summary>Stuffed 0x7E pairs stepped over during a frame-start
            /// scan — false locks avoided.</summary>
            public readonly int StuffedPairSkips;
            /// <summary>Largest number of bytes a single read actually
            /// returned — how deep the driver queue got while we were away.</summary>
            public readonly int MaxBytesToRead;
            /// <summary>Reads that came back completely full, i.e. more was
            /// queued than one pass could take.</summary>
            public readonly int FullReads;
            /// <summary>Longest gap between successive non-empty reads.</summary>
            public readonly int MaxReadGapMs;
            /// <summary>Deepest the inbound dispatch queue got.</summary>
            public readonly int RxQueueHighWater;
            /// <summary>Frames dropped because the dispatch queue was full.</summary>
            public readonly int RxQueueDrops;

            public WireErrorCounters(int dropped, int cksum, int frameErrors, int resync,
                int[] histo, string[] samples, int lengthRejects = 0, int stuffedPairSkips = 0,
                int maxBytesToRead = 0, int fullReads = 0, int maxReadGapMs = 0,
                int rxQueueHighWater = 0, int rxQueueDrops = 0)
            {
                MaxBytesToRead = maxBytesToRead;
                FullReads = fullReads;
                MaxReadGapMs = maxReadGapMs;
                RxQueueHighWater = rxQueueHighWater;
                RxQueueDrops = rxQueueDrops;
                FramesDropped = dropped;
                ChecksumFailures = cksum;
                FrameErrors = frameErrors;
                FrameStartScanResyncs = resync;
                ResyncSkipHistogram = histo;
                RecentResyncSamples = samples;
                LengthRejects = lengthRejects;
                StuffedPairSkips = stuffedPairSkips;
            }
        }

        /// <summary>
        /// Snapshot of the most recent open-failure classification. Returns
        /// <see cref="ConnectionFailureInfo.None"/> when no failure is current
        /// (either we've never tried to connect or the last connect succeeded
        /// and <see cref="ResetFailureState"/> ran).
        /// </summary>
        public ConnectionFailureInfo LastFailure
        {
            get { lock (_failureLock) return _lastFailure; }
        }

        /// <summary>
        /// Number of consecutive <see cref="TryOpen"/> calls that have failed
        /// since the last successful open. Reset to 0 on each successful Open
        /// and on <see cref="ResetFailureState"/>. UI hint-builder requires
        /// >= 2 before showing the port-in-use banner so a single transient
        /// failure during plug-in doesn't flash a banner.
        /// </summary>
        public int ConsecutiveOpenFailures =>
            Interlocked.CompareExchange(ref _consecutiveOpenFailures, 0, 0);

        /// <summary>UTC of the most recent successful <see cref="TryOpen"/>;
        /// <see cref="DateTime.MinValue"/> if never connected this session.</summary>
        public DateTime LastSuccessfulOpenUtc
        {
            get { lock (_failureLock) return _lastSuccessfulOpenUtc; }
        }

        /// <summary>
        /// Clear the classified-failure surface. Called from <see cref="TryOpen"/>
        /// on success and from <see cref="MozaPlugin"/> when the user toggles
        /// the connection off (so a stale "port in use" banner doesn't linger
        /// after a deliberate disable).
        /// </summary>
        public void ResetFailureState()
        {
            Interlocked.Exchange(ref _consecutiveOpenFailures, 0);
            lock (_failureLock)
            {
                _lastFailure = ConnectionFailureInfo.None;
            }
        }

        private void RecordOpenFailure(string portName, ConnectionFailureKind kind, Exception ex)
        {
            Interlocked.Increment(ref _consecutiveOpenFailures);
            lock (_failureLock)
            {
                _lastFailure = new ConnectionFailureInfo(kind, portName, ex.Message, DateTime.UtcNow);
            }
        }

        // Record a POST-open runtime failure (port wedge / half-open tty / live IO
        // error) into the failure tracker WITHOUT touching _consecutiveOpenFailures
        // — that counter is open-retry-specific and drives the port-in-use banner.
        // This is what makes Diagnostics show a real failure instead of
        // "LastFailure: kind=None" while a connected-but-dead link is being reset.
        private void RecordRuntimeFailure(ConnectionFailureKind kind, string message)
        {
            lock (_failureLock)
            {
                _lastFailure = new ConnectionFailureInfo(kind, _lastPortName, message, DateTime.UtcNow);
            }
        }

        // SerialPort.Open / CreateFile under Wine and native Windows both
        // produce ERROR_ACCESS_DENIED (HResult 0x80070005) when another
        // process holds the port (PitHouse is the canonical case). The
        // exception type can be UnauthorizedAccessException OR IOException
        // depending on driver path; the substring check covers both, and
        // the HResult check is a belt-and-braces alternative when the
        // message has been localized.
        private static bool LooksLikeAccessDenied(Exception ex)
        {
            const int E_ACCESSDENIED = unchecked((int)0x80070005);
            if (ex.HResult == E_ACCESSDENIED) return true;
            var msg = ex.Message;
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.IndexOf("access is denied", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("already in use", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("sharing violation", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("resource busy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // "Port vanished" — registry enumeration showed the COM but Open
        // can't see it. Distinct from access-denied because the remediation
        // is different (replug, not close-other-app).
        private static bool LooksLikePortVanished(Exception ex)
        {
            var msg = ex.Message;
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("no such", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Connection scoped to a subset of MOZA PIDs. <paramref name="pidFilter"/>
        /// accepts/rejects ports by PID; <paramref name="probeTarget"/> selects which
        /// probe frames the fallback issues; <paramref name="disableProbeFallback"/>
        /// hard-disables the fallback for enumeration-only lanes (default keeps it
        /// armed for the empty-registry case).
        /// </summary>
        public MozaSerialConnection(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget = MozaProbeTarget.BaseAndHub,
            Func<bool>? disableProbeFallback = null)
        {
            _pidFilter = pidFilter;
            _probeTarget = probeTarget;
            _disableProbeFallback = disableProbeFallback;
        }

        /// <summary>
        /// Format a 16-bit PID as the canonical "0x" + 4-hex-uppercase string
        /// the rest of the plugin (PID filters, DiscoveredPid, device.json
        /// templates) is built around.
        /// </summary>
        private static string FormatPid(ushort pid) =>
            "0x" + pid.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Latch hub-observed (idempotent); used to fire the post-session 5-slot burst.</summary>
        public void MarkHubDetected()
        {
            HubProbeSucceeded = true;
        }

        public bool Connect()
        {
            if (_shutdownRequested)
                return false;

            // Tear down any stale threads/port from a previous dead session
            // (e.g. after sleep/resume killed the tty but handle stayed open).
            if (_running || _port != null)
                Disconnect();

            // Cached-identity fast path. The durable id is tried FIRST because it
            // survives the device landing on a different tty/COM after a replug —
            // the port name only survives until then. Both are gated on
            // _activePorts, which guards same-process sibling double-open (Wine
            // ptys have no O_EXCL).
            string? preferredPort = _lastPortName;
            if (_lastDeviceId != null
                && MozaPortDiscovery.Instance.TryGetByDurableId(_lastDeviceId, out var known)
                && (_pidFilter == null || _pidFilter(FormatPid(known.Pid)))
                && !_activePorts.ContainsKey(known.PortName))
            {
                DiscoveredPid = FormatPid(known.Pid);
                if (TryOpen(known.PortName, known.DevicePath))
                    return true;
                // Enumeration already proved this is the right device for this
                // lane, so re-running discovery would pick the same one and open
                // it a second time this tick. Let the 5 s reconnect timer retry.
                MozaLog.Debug(
                    $"[AZOM] Cached device {_lastDeviceId} present on {known.PortName} but failed to open — retrying next tick");
                return false;
            }
            else if (_lastPortName != null
                     && !_activePorts.ContainsKey(_lastPortName))
            {
                if (MozaPortDiscovery.Instance.TryGetByPort(_lastPortName, out var info)
                    && (_pidFilter == null || _pidFilter(FormatPid(info.Pid))))
                {
                    DiscoveredPid = FormatPid(info.Pid);
                    if (TryOpen(_lastPortName, info.DevicePath))
                        return true;
                    MozaLog.Debug(
                        $"[AZOM] Cached port {_lastPortName} validated but failed to open — clearing");
                    preferredPort = null;
                    _lastPortName = null;
                    _lastDeviceId = null;
                }
                else if (MozaPortDiscovery.Instance.IsAuthoritative)
                {
                    // A real device source answered and doesn't list the cached
                    // port as a matching MOZA device — genuinely stale.
                    MozaLog.Debug(
                        $"[AZOM] Cached port {_lastPortName} no longer matches a MOZA device ({MozaPortDiscovery.Instance.Source}) — clearing");
                    preferredPort = null;
                    _lastPortName = null;
                    _lastDeviceId = null;
                }
                // No source at all: nothing can validate the cached port, so keep
                // it — FindMozaPort's probe revalidates it first.
            }

            var (portName, pid, viaHubProbe, devicePath) = FindMozaPort(
                _pidFilter, _probeTarget, preferredPort, _disableProbeFallback,
                CaptureLabel, () => _shutdownRequested);
            if (portName == null)
                return false;

            if (pid != null)
                DiscoveredPid = pid;
            HubProbeSucceeded = viaHubProbe;

            return TryOpen(portName, devicePath);
        }

        // devicePath is set only by the sysfs source (Wine/Proton): there the COM
        // name is unknowable, so we open the unix device node directly. Empty on
        // Windows, where portName IS the thing to open.
        private bool TryOpen(string portName, string? devicePath = null)
        {
            try
            {
                IMozaPort port;
                if (string.IsNullOrEmpty(devicePath))
                {
                    port = new SerialPortMozaPort(portName, MozaProtocol.BaudRate);
                }
                else
                {
                    // Wine/Proton. sysfs already told us WHICH tty is the MOZA, so
                    // there is no blind probing either way; the only question is how
                    // to open it. Prefer the COM name when Wine's dosdevices mapping
                    // resolves — SerialPortMozaPort gets a 64 KB ReadBufferSize and
                    // does not gate reads on ClearCommError, which is the difference
                    // between resync=0 and resync 53-465 on the same hardware.
                    // The native stty warm-up still runs first: it is what makes the
                    // freshly-enumerated CDC-ACM endpoint safe to open at all.
                    string? com = WineComNameResolver.ResolveComName(portName);
                    port = null!;
                    if (!string.IsNullOrEmpty(com)
                        && MozaPlugin.Instance?.Settings?.PreferComPortOnWine != false)
                    {
                        try
                        {
                            WineDevicePathMozaPort.WarmUpEndpoint(devicePath!);
                            port = new SerialPortMozaPort(com!, MozaProtocol.BaudRate);
                            MozaLog.Debug(
                                $"[AZOM] Wine open: {portName} → {com} via SerialPort "
                                + "(buffered read path)");
                        }
                        catch (Exception ex)
                        {
                            MozaLog.Debug(
                                $"[AZOM] Wine open: {com} via SerialPort failed "
                                + $"({ex.GetType().Name}: {ex.Message}) — falling back to "
                                + "the device path");
                            port = null!;
                        }
                    }
                    if (port == null)
                        port = new WineDevicePathMozaPort(devicePath!);
                }
                return FinishOpen(port, portName);
            }
            catch (UnauthorizedAccessException ex)
            {
                RecordOpenFailure(portName, ConnectionFailureKind.AccessDenied, ex);
                MozaLog.Error($"[AZOM] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (IOException ex) when (LooksLikeAccessDenied(ex))
            {
                RecordOpenFailure(portName, ConnectionFailureKind.AccessDenied, ex);
                MozaLog.Error($"[AZOM] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (FileNotFoundException ex)
            {
                RecordOpenFailure(portName, ConnectionFailureKind.PortVanished, ex);
                MozaLog.Error($"[AZOM] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (IOException ex) when (LooksLikePortVanished(ex))
            {
                RecordOpenFailure(portName, ConnectionFailureKind.PortVanished, ex);
                MozaLog.Error($"[AZOM] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                RecordOpenFailure(portName, ConnectionFailureKind.OpenFailedOther, ex);
                MozaLog.Error($"[AZOM] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
        }

        // Shared commit path: wire an already-open port into the read/write loops.
        // Throws on thread-start failure (caller classifies); tears down on the way out.
        private bool FinishOpen(IMozaPort port, string portName)
        {
            // Drain any stale messages from a previous connection.
            while (_priorityQueue.TryDequeue(out _)) { }
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);

            _port = port;
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            _running = true;
            int gen = Interlocked.Increment(ref _ioGeneration);
            _readThread = new Thread(() => ReadLoop(gen)) { IsBackground = true, Name = "MozaSerialRead" };
            _writeThread = new Thread(() => WriteLoop(gen)) { IsBackground = true, Name = "MozaSerialWrite" };
            _dispatchThread = new Thread(() => DispatchLoop(gen))
                { IsBackground = true, Name = "MozaSerialDispatch" };
            // Fresh generation starts with an empty queue — a frame from the
            // previous open must never reach the new one's subscribers.
            DrainRxQueue();

            try
            {
                _readThread.Start();
                _writeThread.Start();
                _dispatchThread.Start();
            }
            catch
            {
                // If either start failed, tear down: signal stop, join whichever started,
                // close port, then rethrow so the caller logs it.
                _running = false;
                try { _rxSignal.Set(); } catch { }
                try { _readThread?.Join(500); } catch { }
                try { _writeThread?.Join(500); } catch { }
                try { _dispatchThread?.Join(500); } catch { }
                try { _port?.Close(); } catch { }
                _port = null;
                throw;
            }

            _lastPortName = portName;
            // Re-derive the durable identity from whichever source is live, so a
            // reconnect can find this device again after it lands on a different
            // tty/COM. Left untouched when no source can classify the port (the
            // probe-only fallback), where the port name is all we have.
            if (MozaPortDiscovery.Instance.TryGetByPort(portName, out var openedInfo))
                _lastDeviceId = MozaPortDiscovery.DurableId(openedInfo);
            _activePorts[portName] = 1;
            Interlocked.Exchange(ref _consecutiveIoErrors, 0);
            Interlocked.Exchange(ref _portFailureLogged, 0);
            // Fresh connection: clear the half-open idle stamp so a stale
            // value from a prior connection can't immediately force-close.
            Interlocked.Exchange(ref _lastRxUtcTicks, 0);
            lock (_failureLock)
            {
                _lastFailure = ConnectionFailureInfo.None;
                _lastSuccessfulOpenUtc = DateTime.UtcNow;
            }
            Interlocked.Exchange(ref _consecutiveOpenFailures, 0);
            MozaLog.Info($"[AZOM] Connected to {portName}");
            return true;
        }

        public void Disconnect()
        {
            Interlocked.Increment(ref _ioGeneration);
            _running = false;

            // Close before joining so a syscall-wedged R/W returns to its loop.
            IMozaPort? p;
            lock (_lock)
            {
                p = _port;
                _port = null;
            }
            if (p != null)
            {
                try { p.Close(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Port close: {ex.Message}"); }
            }

            var lastPort = _lastPortName;
            if (lastPort != null)
                _activePorts.TryRemove(lastPort, out _);

            // Wake the dispatch thread so it observes the generation bump
            // instead of sitting on its 20 ms wait.
            try { _rxSignal.Set(); } catch (ObjectDisposedException) { }
            _readThread?.Join(1000);
            _writeThread?.Join(1000);
            _dispatchThread?.Join(1000);
            DrainRxQueue();
            // The write lanes have no consumer until the next open; a Send that
            // slipped in before _running went false would otherwise sit here.
            while (_priorityQueue.TryDequeue(out _)) { }
            while (_oneShotQueue.TryDequeue(out _)) { }
        }

        private void DrainRxQueue()
        {
            while (_rxQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _rxQueueDepth, 0);
        }

        /// <summary>Hand a decoded frame to the dispatch thread. Never blocks:
        /// on a full queue the frame is dropped and counted, because stalling
        /// the read thread is strictly worse than losing one frame.</summary>
        private void EnqueueForDispatch(byte[] data)
        {
            int depth = Interlocked.Increment(ref _rxQueueDepth);
            if (depth > RxQueueCapacity)
            {
                Interlocked.Decrement(ref _rxQueueDepth);
                int drops = Interlocked.Increment(ref _rxQueueDrops);
                if (drops == 1 || drops % 500 == 0)
                {
                    MozaLog.Warn(
                        $"[AZOM] Inbound dispatch queue full ({RxQueueCapacity}) — dropped "
                        + $"{drops} frame(s). A subscriber is not keeping up with reception.");
                }
                return;
            }
            int hw = Volatile.Read(ref _rxQueueHighWater);
            if (depth > hw) Interlocked.CompareExchange(ref _rxQueueHighWater, depth, hw);
            _rxQueue.Enqueue(data);
            try { _rxSignal.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Drains <see cref="_rxQueue"/> and runs the subscribers. One
        /// thread, so frames reach handlers in wire order. A handler that throws
        /// is logged and skipped — previously such an exception unwound into the
        /// read loop's catch and was misreported as a serial I/O failure, which
        /// tore down a perfectly healthy port.</summary>
        private void DispatchLoop(int gen)
        {
            while (_running && gen == Volatile.Read(ref _ioGeneration))
            {
                if (!_rxQueue.TryDequeue(out var data))
                {
                    try { _rxSignal.WaitOne(20); }
                    catch (ObjectDisposedException) { return; }
                    continue;
                }
                Interlocked.Decrement(ref _rxQueueDepth);
                try { MessageReceived?.Invoke(data); }
                catch (Exception ex)
                {
                    MozaLog.Error($"[AZOM] Inbound handler threw: {ex.Message}");
                }
            }
        }

        public void Send(byte[] message)
        {
            // Nothing drains the lanes while the I/O threads are down (Disconnect);
            // FinishOpen dropped anything queued in that window anyway.
            if (message != null && _running)
                _oneShotQueue.Enqueue(message);
        }

        /// <summary>
        /// Enqueue a tiny, time-critical frame (fc:00 session ack) on the priority
        /// lane. WriteLoop drains this lane ahead of the one-shot FIFO and applies
        /// no pacing — acks are 10 bytes, negligible against the write budget, and
        /// the wheel times out sessions whose acks lag past ~1 s. Use only for
        /// frames the wheel will treat as overdue if delayed (acks); regular
        /// commands should still use <see cref="Send"/> so they share the paced
        /// queue with tier-def chunks and respect the bandwidth budget.
        /// </summary>
        public void SendPriority(byte[] message)
        {
            if (message != null && _running)
                _priorityQueue.Enqueue(message);
        }

        /// <summary>Enqueue a periodic-stream frame with latest-wins coalescing per <see cref="StreamKind"/>.</summary>
        public void SendStream(StreamKind kind, byte[] message)
        {
            if (message == null) return;
            int idx = (int)kind;
            if ((uint)idx >= (uint)_streamSlots.Length)
            {
                // Out-of-range = an off-by-one slot base / a StreamKind added past
                // StreamSlotCount. Silently dropping it looks like flaky hardware, so
                // surface it loudly (debug-assert in dev, log otherwise) instead of
                // returning quietly. See LedSlotLayout.Validate.
                System.Diagnostics.Debug.Fail(
                    $"SendStream slot {idx} >= StreamSlotCount {_streamSlots.Length} (kind={kind})");
                MozaLog.Warn($"[AZOM] SendStream out-of-range slot {idx} (kind={kind}) — frame dropped");
                return;
            }
            Interlocked.Exchange(ref _streamSlots[idx], message);
        }

        // Set by FlushPendingWrites (any thread), serviced by the write thread.
        // DiscardOutBuffer must run on the write thread so it never races the
        // unlocked _port.Write there (SerialPort is not thread-safe).
        private volatile bool _flushRequested;

        /// <summary>Drop priority + one-shot FIFOs + all stream slots + the OS write buffer (Stop button halts the wheel instantly).</summary>
        public void FlushPendingWrites()
        {
            while (_priorityQueue.TryDequeue(out _)) { }
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
            // Defer the OS-buffer discard to the write thread (see _flushRequested).
            _flushRequested = true;
        }

        /// <summary>
        /// Clear only one pipeline's stream-slot lane (slots [from, from+count)),
        /// leaving the shared queues, OS buffer, and the OTHER pipeline's slots
        /// intact. Used when one of two senders sharing this connection stops/
        /// restarts so it doesn't blank the co-resident pipeline's frames.
        /// </summary>
        public void ClearStreamSlots(int from, int count)
        {
            int end = Math.Min(from + count, _streamSlots.Length);
            for (int k = Math.Max(0, from); k < end; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
        }

        // Record an I/O failure. Throttles log spam and force-closes the port
        // once the failure count crosses the threshold so the reconnect timer
        // can reopen it (handles sleep/resume where .IsOpen stays true on dead tty).
        private void HandleIoFailure(string label, Exception ex)
        {
            if (!_running) return;

            int count = Interlocked.Increment(ref _consecutiveIoErrors);

            if (Volatile.Read(ref _portFailureLogged) == 0)
            {
                MozaLog.Error($"[AZOM] {label} error: {ex.GetType().Name}: {ex.Message}");
            }

            // Single-winner gate: only one thread crosses threshold AND wins
            // the CAS. Loser path skips the close+log entirely instead of
            // racing on a second close.
            if (count >= PortDeadThreshold &&
                Interlocked.CompareExchange(ref _portFailureLogged, 1, 0) == 0)
            {
                MozaLog.Warn(
                    $"[AZOM] Port wedged after {count} consecutive I/O errors — closing for reconnect");
                RecordRuntimeFailure(ConnectionFailureKind.IoFailureAfterOpen,
                    $"{label}: {ex.Message} (after {count} consecutive I/O errors — port wedged)");
                ClosePortAndNotify();
            }
        }

        // Close the port (so IsConnected goes false and the owner's reconnect
        // timer reopens it), drain queued/streamed frames, and raise
        // Disconnected so subscribers run their detection/telemetry resets.
        // Keeps _running true: the read/write loops stay alive and spin on the
        // null port until Connect() reopens. Callers MUST win the
        // _portFailureLogged CAS first so this can't race a double-close/notify.
        private void ClosePortAndNotify()
        {
            lock (_lock)
            {
                try { _port?.Close(); } catch { }
                _port = null;
            }
            // Release the port-held claim now (Disconnect does the same): until the
            // reconnect tick reopens, sibling lanes and the migration check would
            // otherwise still see this closed port as ours.
            var lastPort = _lastPortName;
            if (lastPort != null)
                _activePorts.TryRemove(lastPort, out _);
            while (_priorityQueue.TryDequeue(out _)) { }
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
            try { Disconnected?.Invoke(); } catch (Exception dex)
            {
                MozaLog.Debug($"[AZOM] Disconnected handler: {dex.Message}");
            }
        }

        /// <summary>
        /// Force this connection through its disconnect→reconnect recovery cycle
        /// without waiting for the consecutive-I/O-error threshold: close the
        /// port and raise <see cref="Disconnected"/> so the owner resets
        /// detection/telemetry and its reconnect timer reopens a fresh port.
        ///
        /// <para>Unlike <see cref="Disconnect"/> (which sets <c>_running=false</c>
        /// and stays silent) this keeps the I/O threads alive and DOES notify, so
        /// it drives the same tested path as a wedged port.</para>
        ///
        /// <para>Used on system resume: after sleep the wheel firmware
        /// power-cycles and silently tears down its display/telemetry sessions,
        /// but the host tty can stay <c>.IsOpen==true</c> (or the wheel resumes
        /// talking before the ~30 s half-open detector fires), so nothing else
        /// would trigger a clean session rebuild and the display stays blank.</para>
        /// </summary>
        public void ForceReconnect(string reason)
        {
            if (!_running) return;
            // Single-winner gate shared with HandleIoFailure so a concurrent
            // threshold breach can't double-close/notify. If a failure path
            // already owns the close, let it run — the effect is identical.
            if (Interlocked.CompareExchange(ref _portFailureLogged, 1, 0) != 0)
                return;
            if (_port == null)
            {
                // Already closed by a prior force/failure and not yet reopened —
                // nothing to do. Release the gate so a real reopen can re-arm it.
                Interlocked.Exchange(ref _portFailureLogged, 0);
                return;
            }
            MozaLog.Info($"[AZOM] {reason} — forcing reconnect");
            RecordRuntimeFailure(ConnectionFailureKind.IoFailureAfterOpen, reason);
            ClosePortAndNotify();
        }

        private void ReadLoop(int gen)
        {
            MozaLog.Debug("[AZOM] Read thread started");
            int messageCount = 0;
            // Bulk read buffer — drains all available bytes from the OS read
            // buffer in one SerialPort.Read() call, then parses frames from
            // this byte array in memory. Per-byte ReadByte() under Wine/tty0tty
            // had ~100μs per-call overhead which made multi-chunk burst pacing
            // marginal even for valid frames.
            var rx = new List<byte>(capacity: 8192);
            var tmp = new byte[4096];
            // Reusable de-stuff scratch (grown on demand) so each received frame
            // doesn't allocate a fresh byte[] on the high-frequency RX path. Only
            // the per-frame `data` copy handed to subscribers is freshly allocated.
            byte[] destuff = new byte[256];
            int lastReadTickMs = 0;

            while (_running && gen == Volatile.Read(ref _ioGeneration))
            {
                try
                {
                    var port = _port;
                    if (port == null)
                    {
                        Thread.Sleep(100);
                        continue;
                    }
                    if (!port.IsOpen)
                    {
                        // Driver-side close: a device that re-enumerates (reboot on
                        // an AB9 mode switch, firmware restart, replug) makes
                        // SerialPort dispose its own stream, so IsConnected goes
                        // false after only a couple of write errors — far short of
                        // PortDeadThreshold, and the read loop never throws at all.
                        // Without this nobody raises Disconnected and every owner
                        // keeps its per-session state (AB9 FFB effect table, wheel
                        // detection, telemetry sessions) across the silent reconnect.
                        if (Interlocked.CompareExchange(ref _portFailureLogged, 1, 0) == 0)
                        {
                            MozaLog.Warn(
                                "[AZOM] Port closed by the driver (device re-enumerated) — notifying for reconnect");
                            RecordRuntimeFailure(ConnectionFailureKind.IoFailureAfterOpen,
                                "port closed by the driver (device removed or re-enumerated)");
                            ClosePortAndNotify();
                        }
                        Thread.Sleep(100);
                        continue;
                    }

                    // .NET SerialPort.Read blocks until a byte arrives or
                    // ReadTimeout expires, so THAT path must be gated on
                    // BytesToRead. The Wine device path must NOT be: its
                    // COMMTIMEOUTS are set to return-immediately, so ReadFile
                    // already yields whatever is queued, and its BytesToRead is
                    // ClearCommError/COMSTAT.cbInQue — an ioctl Wine
                    // under-reports, and which this wrapper reports as 0 whenever
                    // the call fails. Gating on it suppresses reads that would
                    // have succeeded, and the tty buffer then overruns.
                    //
                    // This is the v1.5.0 regression on Linux/Wine rigs: v1.5.0
                    // opened EVERY port through SerialPortMozaPort (.NET
                    // SerialPort, 64 KB ReadBufferSize, never gated on cbInQue),
                    // so it never hit this. WineDevicePathMozaPort arrived after
                    // v1.5.0 and inherited the gate. Signature across five
                    // CS-Pro bundles: maxBytesToRead=278 alongside
                    // maxReadGap=3796ms, resync 29-465, and clustered loss of
                    // sess 0x01/0x02 chunks — including the wheel's post-switch
                    // catalog push, which is why a dashboard switch stopped
                    // refreshing the catalog and left the dash dead. Same plugin
                    // on native Windows reports resync=0.
                    int avail = port.ReadReturnsImmediately ? tmp.Length : port.BytesToRead;
                    if (avail == 0)
                    {
                        OnIdleRead();
                        continue;
                    }
                    if (avail > tmp.Length) avail = tmp.Length;
                    int nowMs = Environment.TickCount;
                    if (lastReadTickMs != 0)
                    {
                        int gapMs = nowMs - lastReadTickMs;
                        int maxGap = Volatile.Read(ref _maxReadGapMs);
                        if (gapMs > maxGap)
                            Interlocked.CompareExchange(ref _maxReadGapMs, gapMs, maxGap);
                    }
                    lastReadTickMs = nowMs;
                    int n = port.Read(tmp, 0, avail);
                    // Measure what the port ACTUALLY handed back. The old
                    // maxBytesToRead/fullReads pair measured `avail`, which on the
                    // return-immediately path is just tmp.Length — our own
                    // constant echoing back, telling us nothing. A read that comes
                    // back full means more was queued than one pass could take,
                    // i.e. we are behind and the driver buffer is filling.
                    if (n > 0)
                    {
                        int maxSeen = Volatile.Read(ref _maxBytesToRead);
                        if (n > maxSeen)
                            Interlocked.CompareExchange(ref _maxBytesToRead, n, maxSeen);
                        // Saturation means we filled the SCRATCH buffer, not that we
                        // took everything on offer: the gated path asks for exactly
                        // BytesToRead, so "n >= avail" is trivially true there and the
                        // counter read 3407 on a connection with zero loss.
                        if (n >= tmp.Length) Interlocked.Increment(ref _fullReadCount);
                    }
                    // Return-immediately ports report "nothing buffered" as a
                    // zero-length read rather than BytesToRead==0, so the idle
                    // bookkeeping has to run from here too or the half-open-tty
                    // detector never fires on that path.
                    if (n <= 0)
                    {
                        OnIdleRead();
                        continue;
                    }
                    // ICollection<T> fast path (one CopyTo), not 4096 bounds-checked Adds.
                    rx.AddRange(new ArraySegment<byte>(tmp, 0, n));
                    Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                    Interlocked.Exchange(ref _lastRxUtcTicks, DateTime.UtcNow.Ticks);

                    // Parse as many complete frames from `rx` as possible, then
                    // keep any trailing partial frame for the next bulk read.
                    int cursor = 0;
                    while (cursor < rx.Count)
                    {
                        int frameStart = cursor;
                        // Scan for frame start 0x7E, skipping stuffed pairs.
                        //
                        // A doubled 0x7E on the wire is a BODY byte (StuffFrame
                        // emits 7E 7E for a payload 0x7E) and can never begin a
                        // frame: the writer leaves the LEN byte unstuffed, so a
                        // genuine start is `7E <len>` and would need len == 0x7E
                        // (126) to look like a pair. The largest inbound LEN in
                        // any capture on record is 0x3F, half that. Landing on
                        // the first byte of a stuffed pair used to produce a
                        // false lock that claimed a 129-byte frame and ate the
                        // real frames behind it — the 58-byte high-entropy
                        // device-log chunks on sess=0x01 carry stuffed pairs
                        // constantly, which is where the clustered chunk loss in
                        // bundles 15YZY7Z4 / W0V1PF9V came from.
                        while (frameStart < rx.Count)
                        {
                            if (rx[frameStart] != MozaProtocol.MessageStart)
                            {
                                frameStart++;
                                continue;
                            }
                            if (frameStart + 1 < rx.Count
                                && rx[frameStart + 1] == MozaProtocol.MessageStart)
                            {
                                frameStart += 2;
                                Interlocked.Increment(ref _stuffedPairSkips);
                                continue;
                            }
                            break;
                        }
                        if (frameStart > cursor)
                        {
                            int skipped = frameStart - cursor;
                            long resyncNo = Interlocked.Increment(ref _frameStartScanResyncs);
                            // Histogram bucket (cheap — no allocation):
                            int b;
                            if (skipped <= 1) b = 0;
                            else if (skipped == 2) b = 1;
                            else if (skipped <= 4) b = 2;
                            else if (skipped <= 8) b = 3;
                            else if (skipped <= 16) b = 4;
                            else if (skipped <= 32) b = 5;
                            else if (skipped <= 64) b = 6;
                            else b = 7;
                            Interlocked.Increment(ref _resyncSkipBucket[b]);
                            // Sample the actual skipped bytes (capped to
                            // 24 hex chars) so the diagnostics tab can
                            // show what's between frames. Newest-first
                            // ring buffer with a tiny lock. Sampled: the
                            // first ring's worth, then every 64th — a noisy
                            // line resyncs per byte, and the string build
                            // sits on the read thread.
                            if (resyncNo <= ResyncSampleCapacity || (resyncNo & 63) == 0)
                            {
                            int sampleLen = Math.Min(skipped, 12);
                            var sb = new System.Text.StringBuilder(2 + sampleLen * 3);
                            // Trailing bytes of whatever we parsed LAST, before the
                            // skipped run. Without this a resync sample cannot
                            // distinguish the two causes, which need opposite fixes:
                            //   • we over-consumed — the skipped run then begins with
                            //     a LEN byte and decodes as a well-formed frame whose
                            //     only missing byte is its 0x7E start (e.g.
                            //     "03 A5 91 01 00 00 C5"), and prev ends with the
                            //     0x7E we swallowed;
                            //   • the peer emitted bytes that are not in a frame at
                            //     all — prev ends on a valid frame's checksum and the
                            //     skipped run is foreign.
                            // resync>0 with frameErr=cksumFail=lenReject=0 says no
                            // frame was ever REJECTED, so one of these two is
                            // happening and the counters alone cannot say which.
                            int back = Math.Min(6, cursor);
                            if (back > 0)
                            {
                                sb.Append("prev:");
                                for (int k = cursor - back; k < cursor; k++)
                                {
                                    sb.Append(' ');
                                    sb.Append(rx[k].ToString("X2"));
                                }
                                sb.Append(" | ");
                            }
                            sb.Append(skipped);
                            sb.Append("B:");
                            for (int k = 0; k < sampleLen; k++)
                            {
                                sb.Append(' ');
                                sb.Append(rx[cursor + k].ToString("X2"));
                            }
                            if (sampleLen < skipped) sb.Append(" …");
                            string sample = sb.ToString();
                            lock (_resyncSampleLock)
                            {
                                _resyncSamples.AddLast(sample);
                                while (_resyncSamples.Count > ResyncSampleCapacity)
                                    _resyncSamples.RemoveFirst();
                            }
                            }
                        }
                        if (frameStart >= rx.Count)
                        {
                            // No start byte found at all — discard junk.
                            cursor = rx.Count;
                            break;
                        }
                        // Need at least start + length byte to proceed.
                        if (frameStart + 1 >= rx.Count)
                        {
                            cursor = frameStart;
                            break;
                        }
                        int payloadLength = rx[frameStart + 1];
                        // LEN field counts CMD bytes only (group + dev + chk
                        // are framing). The lower bound was historically `< 2`
                        // as defensive noise rejection, but that silently
                        // dropped legitimate short wheel responses:
                        //   LEN=0  → `7E 00 [group] [dev] [chk]` — presence-
                        //            probe ACKs (e.g. `7E 00 80 dev_swap chk`),
                        //            simple polled-status responses (e.g.
                        //            `7E 00 C0 31 7C` channel-cfg response
                        //            from base, `7E 00 A2 21 4E` from main).
                        //   LEN=1  → `7E 01 [group] [dev] [cmd] [chk]` — minimal
                        //            session-mgmt responses (e.g. `7E 01 C3 71
                        //            80 40` SerialStream wheel response).
                        // Rejecting these produced silent resyncs (no DROP log,
                        // since neither frameError nor cksumFail fired) at
                        // ~3/s steady-state, and the rest of the plugin never
                        // got to see these frames — including `MozaPlugin
                        // .OnMessageReceived`'s `data.Length == 2 && data[0]
                        // == 0x80` presence-probe ACK handler, which was
                        // unreachable in practice. Accept any non-negative LEN;
                        // the checksum still has to validate, so a stray byte
                        // run that happens to look like `7E N` only survives if
                        // its checksum byte also coincidentally matches (1/256).
                        // Upper bound raised to 200 — larger than any observed
                        // wheel frame, matches the catalog parser's record-size
                        // ceiling for symmetric framing assumptions.
                        if (payloadLength > 200)
                        {
                            // Invalid length — skip this start byte and resync on
                            // the next 0x7E. Common at connect when junk precedes
                            // real frames, and the usual outcome of a false lock
                            // inside a high-entropy payload (~22% of random bytes
                            // exceed 200). Counted so a bundle can distinguish
                            // "noisy line" from "we keep mis-locking".
                            Interlocked.Increment(ref _lengthRejects);
                            cursor = frameStart + 1;
                            continue;
                        }
                        int needed = payloadLength + 3; // group + device + payload + checksum
                        // Walk wire bytes starting after [start, len], collapsing
                        // 0x7E 0x7E wire pairs back to a single 0x7E body byte.
                        if (needed > destuff.Length) destuff = new byte[needed];
                        byte[] raw = destuff;
                        int decoded = 0;
                        int wirePos = frameStart + 2;
                        bool frameError = false;
                        bool needMoreData = false;
                        while (decoded < needed)
                        {
                            if (wirePos >= rx.Count) { needMoreData = true; break; }
                            byte wb = rx[wirePos++];
                            if (wb == MozaProtocol.MessageStart)
                            {
                                if (wirePos >= rx.Count) { needMoreData = true; break; }
                                byte esc = rx[wirePos++];
                                if (esc != MozaProtocol.MessageStart)
                                {
                                    frameError = true;
                                    break;
                                }
                                raw[decoded++] = MozaProtocol.MessageStart;
                            }
                            else
                            {
                                raw[decoded++] = wb;
                            }
                        }
                        if (needMoreData)
                        {
                            // Frame straddles buffer end; wait for more bytes.
                            cursor = frameStart;
                            break;
                        }
                        if (frameError || decoded != needed)
                        {
                            // Sampled: resync is `frameStart + 1`, so one corrupted
                            // burst re-enters here once per byte. Logging each one
                            // buried the rest of the ring under a single wire fault;
                            // the running total is in WireErrors either way.
                            int fe = Interlocked.Increment(ref _frameErrors);
                            if (fe == 1 || fe % 500 == 0)
                            {
                                int nn = Math.Min(8, Math.Max(0, decoded));
                                string first8a = nn > 0 ? BitConverter.ToString(raw, 0, nn) : "(empty)";
                                MozaLog.Debug(
                                    $"[AZOM] DROP frame-error #{fe}: decoded={decoded}/{needed} " +
                                    $"len={payloadLength} first8={first8a}");
                            }
                            // Skip past the bad start byte and try to resync.
                            cursor = frameStart + 1;
                            continue;
                        }

                        // Validate wire-level checksum (includes 0x7E escape
                        // accounting per doc § 54). Allocation-free overload —
                        // ReadLoop used to allocate a wireFrame byte[] here every
                        // received frame just to feed the array-based checksum;
                        // CalculateWireChecksumFromParts derives the same value
                        // directly from raw + payloadLength.
                        byte expected = MozaProtocol.CalculateWireChecksumFromParts(
                            (byte)payloadLength, raw, payloadLength + 2);
                        byte actual = raw[needed - 1];
                        if (expected != actual)
                        {
                            // Sampled for the same reason as the frame-error branch
                            // above — a noisy line otherwise emits one line per bad
                            // frame. WireErrors.ChecksumFailures carries the total.
                            int cf = Interlocked.Increment(ref _checksumFailures);
                            if (cf == 1 || cf % 500 == 0)
                            {
                                int nn = Math.Min(8, needed);
                                string first8a = nn > 0 ? BitConverter.ToString(raw, 0, nn) : "(empty)";
                                MozaLog.Debug(
                                    $"[AZOM] DROP checksum mismatch #{cf}: expected=0x{expected:X2} " +
                                    $"actual=0x{actual:X2} len={payloadLength} group=0x{raw[0]:X2} " +
                                    $"dev=0x{raw[1]:X2} first8={first8a}");
                            }
                            cursor = frameStart + 1;
                            continue;
                        }

                        // Strip the checksum byte before passing to the parser.
                        var data = new byte[needed - 1];
                        Array.Copy(raw, 0, data, 0, data.Length);

                        messageCount++;
                        if (messageCount <= 5)
                        {
                            MozaLog.Debug(
                                $"[AZOM] Received msg #{messageCount}: len={payloadLength} " +
                                $"group=0x{data[0]:X2} dev=0x{data[1]:X2} ({data.Length} bytes)");
                        }
                        // Diagnostic: per-chunk log for SerialStream session-data
                        // frames (0xC3 / wheel / 7C / 00) — session 0x09 chunk reception.
                        if (MozaLog.WireDebugEnabled
                            && data.Length >= 8 && data[0] == MozaProtocol.SerialStreamRespGroup
                            && data[1] == MozaProtocol.WheelDeviceIdSwapped
                            && data[2] == MozaProtocol.SerialStreamOpcodeData
                            && data[3] == 0x00)
                        {
                            byte sess = data[4];
                            byte type = data[5];
                            int seqWire = data[6] | (data[7] << 8);
                            int bodyLen = data.Length - 8;
                            string first8 = bodyLen > 0
                                ? BitConverter.ToString(data, 8, Math.Min(8, bodyLen))
                                : "(empty)";
                            MozaLog.Debug(
                                $"[AZOM] WIRE sess=0x{sess:X2} type=0x{type:X2} seq={seqWire} " +
                                $"totalLen={data.Length} payload={bodyLen}B first8={first8}");
                        }
                        // Capture stays on the read thread so wire-trace
                        // timestamps remain reception times, not dispatch times.
                        SerialTrafficCapture.Instance.RecordRx(CaptureLabel, data);
                        EnqueueForDispatch(data);
                        // Move cursor past the consumed wire bytes.
                        cursor = wirePos;
                    }
                    // Drop consumed bytes so `rx` doesn't grow unbounded.
                    if (cursor > 0)
                    {
                        if (cursor >= rx.Count)
                            rx.Clear();
                        else
                            rx.RemoveRange(0, cursor);
                    }
                }
                catch (TimeoutException)
                {
                    // Normal timeout under Wine, continue
                }
                catch (Exception ex)
                {
                    HandleIoFailure("Read", ex);
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>Idle-read bookkeeping shared by the gated and
        /// return-immediately read paths: runs the half-open-tty detector, then
        /// paces the poll. If the wheel has talked before but has now been
        /// silent past <c>ReadIdleDeadMs</c>, the port is dead-but-IsOpen (no
        /// throw, nothing readable) which the count-only PortDeadThreshold can't
        /// catch — force a reconnect. Skipped until the first inbound so a
        /// never-engaged port doesn't self-trip.</summary>
        private void OnIdleRead()
        {
            long lastRx = Interlocked.Read(ref _lastRxUtcTicks);
            if (lastRx != 0
                && (DateTime.UtcNow.Ticks - lastRx) > ReadIdleDeadMs * TimeSpan.TicksPerMillisecond)
            {
                Interlocked.Exchange(ref _lastRxUtcTicks, 0);
                // Immediate close, same gate as ForceReconnect. The counting path in
                // HandleIoFailure can never reach PortDeadThreshold here: nothing
                // throws on a half-open tty and every successful write resets the
                // counter, so one breach would just disarm this detector for good.
                if (_running && Interlocked.CompareExchange(ref _portFailureLogged, 1, 0) == 0)
                {
                    string reason = $"ReadIdle: no inbound for >{ReadIdleDeadMs}ms while port open (half-open tty)";
                    MozaLog.Warn($"[AZOM] {reason} — closing for reconnect");
                    RecordRuntimeFailure(ConnectionFailureKind.IoFailureAfterOpen, reason);
                    ClosePortAndNotify();
                }
                return;
            }
            Thread.Sleep(2);
        }

        private void WriteLoop(int gen)
        {
            MozaLog.Debug("[AZOM] Write thread started");
            int writeCount = 0;
            // Pooled stuffing buffer. Worst-case stuffed size is 2 * decoded size;
            // grows on demand if a larger frame arrives.
            byte[] stuffBuf = new byte[512];
            // Monotonic 64-bit clock for write pacing. Replaces Environment.TickCount
            // (signed Int32, wraps every ~24.8 days) so the 4ms gate stays correct
            // across long uptime — Stopwatch.GetTimestamp ticks at high resolution
            // and never wraps for any plausible session length.
            long stopwatchFreq = System.Diagnostics.Stopwatch.Frequency;
            long fourMsTicks = stopwatchFreq * 4 / 1000;
            long oneSecTicks = stopwatchFreq;
            long lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp() - stopwatchFreq;
            long lastBudgetWarnTs = 0;
            bool lastWasOneShot = false;

            while (_running && gen == Volatile.Read(ref _ioGeneration))
            {
                bool didWork = false;

                // Serviced here (write thread) so DiscardOutBuffer never races
                // _port.Write below. Set by FlushPendingWrites on the Stop path.
                if (_flushRequested)
                {
                    _flushRequested = false;
                    try { _port?.DiscardOutBuffer(); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM] DiscardOutBuffer: {ex.Message}"); }
                }

                // 0) Priority lane: drain all queued fc:00 acks first, unpaced.
                //    Acks are 10 bytes each and the wheel times out sessions whose
                //    acks lag — they must NOT sit behind a tier-def burst in the
                //    one-shot FIFO. Loop drains all queued acks each iteration so
                //    a flurry of inbound chunks (every wheel tick during a switch)
                //    doesn't leave the back of the line stuck for another cycle.
                while (gen == Volatile.Read(ref _ioGeneration)
                    && _priorityQueue.TryDequeue(out var ackMsg))
                {
                    int writtenAck = WriteFrame(ackMsg, ref stuffBuf, MozaProtocol.StuffedFrameSize(ackMsg));
                    if (writtenAck > 0)
                    {
                        writeCount++;
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        // Don't claim "lastWasOneShot" — priority writes shouldn't
                        // count toward the 4ms one-shot inter-frame gate (acks
                        // back-to-back are fine; they're tiny and the wheel handles
                        // bursts of acks without issue per PH wire traces).
                        lastWasOneShot = false;
                        didWork = true;
                    }
                }

                // 1) One-shot FIFO with 4 ms inter-write pacing (bases drop unpaced bursts).
                //    WriteBudget extends the gate under bandwidth pressure.
                if (gen == Volatile.Read(ref _ioGeneration)
                    && _oneShotQueue.TryDequeue(out var msg))
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    int stuffedSize = MozaProtocol.StuffedFrameSize(msg);
                    int budgetExtraMs = _budget.RecommendOneShotDelayMs(stuffedSize);
                    int baseGapMs = 0;
                    if (lastWasOneShot)
                    {
                        long sinceTicks = now - lastWriteTs;
                        if (sinceTicks < fourMsTicks)
                            baseGapMs = (int)((fourMsTicks - sinceTicks) * 1000 / stopwatchFreq);
                    }
                    int sleepMs = Math.Max(baseGapMs, budgetExtraMs);
                    if (sleepMs > 0) Thread.Sleep(sleepMs);

                    int written = WriteFrame(msg, ref stuffBuf, stuffedSize);
                    if (written > 0)
                    {
                        writeCount++;
                        if (writeCount <= 5)
                            MozaLog.Debug($"[AZOM] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastWasOneShot = true;
                    }
                    didWork = true;
                }

                // 2) Stream lane drained after every FIFO item (retransmit bursts
                //    can keep FIFO non-empty for seconds). No software gating —
                //    latest-wins + OS write-buffer block provides backpressure.
                for (int k = 0; k < _streamSlots.Length && gen == Volatile.Read(ref _ioGeneration); k++)
                {
                    var slot = Interlocked.Exchange(ref _streamSlots[k], null);
                    if (slot == null) continue;
                    int written = WriteFrame(slot, ref stuffBuf, MozaProtocol.StuffedFrameSize(slot));
                    if (written > 0)
                    {
                        writeCount++;
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastWasOneShot = false;
                        didWork = true;
                    }
                }

                // Periodic budget warning — 1/s when over 90% of target.
                long warnNow = System.Diagnostics.Stopwatch.GetTimestamp();
                if (warnNow - lastBudgetWarnTs >= oneSecTicks)
                {
                    // PeekSnapshot preserves the rolling peak for the diagnostics tab.
                    var snap = _budget.PeekSnapshot();
                    if (snap.PercentBudget >= 90)
                    {
                        MozaLog.Warn(
                            $"[AZOM] Write budget {snap.PercentBudget}% ({snap.BytesLastSec} B/s, peak {snap.PeakBurstBytes})");
                    }
                    lastBudgetWarnTs = warnNow;
                }

                if (!didWork)
                    Thread.Sleep(2);
            }
        }

        /// <summary>Byte-stuff and write a frame. Returns wire-byte count or -1 on write failure.</summary>
        private int WriteFrame(byte[] msg, ref byte[] stuffBuf, int needed)
        {
            try
            {
                if (stuffBuf.Length < needed)
                    stuffBuf = new byte[Math.Max(needed, stuffBuf.Length * 2)];
                int len = MozaProtocol.StuffFrame(msg, stuffBuf);
                // No lock on Write: only this thread calls it; Close races
                // resolve via IOException/ObjectDisposedException below.
                var port = _port;
                if (port == null)
                {
                    // Closed by ClosePortAndNotify and not yet reopened: nothing left
                    // the machine, so don't record a phantom TX, charge the budget or
                    // reset the I/O-error counter.
                    Interlocked.Increment(ref _framesDropped);
                    return -1;
                }
                port.Write(stuffBuf, 0, len);
                Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                SerialTrafficCapture.Instance.RecordTx(CaptureLabel, msg);
                _budget.Record(len);
                return len;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _framesDropped);
                HandleIoFailure("Write", ex);
                return -1;
            }
        }

        /// <summary>
        /// Locate a MOZA device: (1) enumeration — registry on Windows, sysfs under
        /// Wine/Proton — with no serial bytes written, (2) probe fallback against
        /// unclassified ports only, and only when enumeration can be incomplete
        /// (never under sysfs). Classified mismatches are skipped.
        /// <paramref name="preferredPort"/> tilts toward the saved port on multi-rig setups.
        /// </summary>
        private static (string? PortName, string? Pid, bool ViaHubProbe, string? DevicePath) FindMozaPort(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget,
            string? preferredPort,
            Func<bool>? disableProbeFallback,
            // Which pipe is asking. Only used to key the repeat-suppressed status
            // lines below (every lane re-runs this on the 5 s reconnect tick) —
            // without a per-lane key one pipe's steady state would mask another's.
            string laneLabel,
            Func<bool>? cancel = null)
        {
            // Stage 1: registry walk. Take the full MOZA enumeration first
            // so we can distinguish "the registry is working but our PID
            // isn't there" from "the registry sees nothing at all".
            var allRegistryPorts = MozaPortDiscovery.Instance.Enumerate();

            // Per-port lookup we'll reuse in the probe loops below so the
            // probe never writes bytes at a port whose PID is already
            // known to belong to a different device category.
            var registryByPort = new Dictionary<string, MozaPortDiscovery.PortInfo>(
                allRegistryPorts.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < allRegistryPorts.Count; i++)
                registryByPort[allRegistryPorts[i].PortName] = allRegistryPorts[i];

            // Filter through the existing string-based pidFilter contract.
            // Also drop ports already held by a sibling connection in this
            // process: the cached-port path in Connect() honours _activePorts,
            // but this registry walk did not — so the dedicated hub connection
            // could otherwise try to re-open the port the primary already claimed
            // (the hub-only case, where the BaseAndHub primary took the hub).
            var matchingPorts = (pidFilter == null
                    ? (IEnumerable<MozaPortDiscovery.PortInfo>)allRegistryPorts
                    : allRegistryPorts.Where(p => pidFilter(FormatPid(p.Pid))))
                .Where(p => !_activePorts.ContainsKey(p.PortName))
                .ToList();

            if (matchingPorts.Count > 0)
            {
                MozaPortDiscovery.PortInfo chosen = matchingPorts[0];
                bool matchedPreferred = false;
                if (!string.IsNullOrEmpty(preferredPort))
                {
                    for (int i = 0; i < matchingPorts.Count; i++)
                    {
                        if (string.Equals(matchingPorts[i].PortName, preferredPort,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = matchingPorts[i];
                            matchedPreferred = true;
                            break;
                        }
                    }
                }
                // Wheel-location rule: when a wheelbase is present it must be the
                // telemetry-driving primary, so the BaseAndHub connection prefers a
                // Wheelbase-category port over a Hub/unknown one. Falls back to
                // matchingPorts[0] when no wheelbase exists — that's the hub-only
                // case, where the primary correctly binds to the hub and runs the
                // full wheel/session/telemetry pipeline. Only applied when the
                // saved preferred port didn't already pin the choice.
                if (!matchedPreferred && probeTarget == MozaProbeTarget.BaseAndHub)
                {
                    for (int i = 0; i < matchingPorts.Count; i++)
                    {
                        if (matchingPorts[i].Category == MozaDeviceCategory.Wheelbase)
                        {
                            chosen = matchingPorts[i];
                            break;
                        }
                    }
                }
                MozaLog.Debug(
                    $"[AZOM] Found MOZA device on {chosen.PortName} PID={FormatPid(chosen.Pid)} " +
                    $"({MozaPortDiscovery.Instance.Source})");
                return (chosen.PortName, FormatPid(chosen.Pid), false, chosen.DevicePath);
            }

            // sysfs enumerates the USB bus itself, so a MOZA that is attached is
            // in the list above by construction — a probe can only find OTHER
            // vendors' hardware, and opening that is the exact failure this
            // source exists to end. It is also the only correct stop here: under
            // sysfs PortName is a tty name, so the COM-keyed probe-eligibility
            // narrowing further down can never fire and the sweep would run over
            // every COM symlink in the prefix.
            //
            // The registry deliberately does NOT stop here. It lists only devices
            // that bound usbser, so a MOZA behind a different driver is invisible
            // to it and the probe still adds information; Windows keeps the
            // existing per-port narrowing below.
            if (MozaPortDiscovery.Instance.Source == MozaDiscoverySource.Sysfs)
            {
                MozaLog.DebugIfChanged($"probe-skip-enumerated:{laneLabel}",
                    $"[AZOM] [{laneLabel}] No matching MOZA device among the {allRegistryPorts.Count} " +
                    "enumerated by sysfs — not probing");
                return (null, null, false, null);
            }

            if (disableProbeFallback?.Invoke() == true)
            {
                MozaLog.DebugIfChanged($"probe-skip-disabled:{laneLabel}",
                    $"[AZOM] [{laneLabel}] No matching MOZA device in registry; this lane is enumeration-only so the probe is skipped");
                return (null, null, false, null);
            }

            // Probe-based discovery: try opening each COM port and sending a Moza read command.
            // This works under Proton/Wine where COM ports are symlinked to /dev/ttyACM*.
            // We probe high-numbered ports first since Wine maps ttyACM devices to COM33+.
            var ports = SerialPort.GetPortNames();
            Array.Sort(ports, (a, b) =>
            {
                int na = ExtractPortNumber(a);
                int nb = ExtractPortNumber(b);
                return nb.CompareTo(na); // Descending - check high ports first
            });

            // Reset blocked-probe backoff for ports that have left the
            // enumeration since the last sweep: an unplug/replug may be
            // different hardware, so it starts over at the full probe cadence
            // instead of inheriting a widened interval.
            PruneProbeBackoff(ports);

            // Saved-port hint: probe the remembered port first so registry-less
            // discovery (Wine/Proton) revalidates it before sweeping the full
            // list. The probe reply still decides — a different device on that
            // port fails the probe and the sweep continues normally.
            if (!string.IsNullOrEmpty(preferredPort))
            {
                for (int i = 1; i < ports.Length; i++)
                {
                    if (!string.Equals(ports[i], preferredPort,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;
                    var hint = ports[i];
                    Array.Copy(ports, 0, ports, 1, i);
                    ports[0] = hint;
                    break;
                }
            }

            // Skip the probe entirely when every COM port is registry-classified
            // with a non-matching PID (kept the AB9 probe storm off wheelbase-only users).
            int probeEligible = 0;
            for (int i = 0; i < ports.Length; i++)
            {
                if (!registryByPort.TryGetValue(ports[i], out var info))
                {
                    probeEligible++;
                    continue;
                }
                if (pidFilter == null || pidFilter(FormatPid(info.Pid)))
                    probeEligible++;
            }
            if (probeEligible == 0 && allRegistryPorts.Count > 0)
            {
                MozaLog.DebugIfChanged($"probe-skip-classified:{laneLabel}",
                    $"[AZOM] [{laneLabel}] Registry classifies all {ports.Length} COM port(s); none match this connection's PID filter — skipping probe (trust registry)");
                return (null, null, false, null);
            }

            if (allRegistryPorts.Count == 0)
                MozaLog.DebugIfChanged($"probe-fallback:{laneLabel}",
                    $"[AZOM] [{laneLabel}] No MOZA device in registry, falling back to serial probe");
            else
                MozaLog.DebugIfChanged($"probe-fallback:{laneLabel}",
                    $"[AZOM] [{laneLabel}] Registry classifies {registryByPort.Count} of {ports.Length} COM port(s); probing the remainder");

            // 600ms budget per port — SerialPort.Open can hang indefinitely under Wine
            // if another process holds the tty. Background-thread the probe so one bad
            // port can't block all detection.
            var unreachable = new HashSet<string>();

            // Skip ports held by a sibling connection (Wine pty has no O_EXCL).
            bool IsHeldByPeer(string port) => _activePorts.ContainsKey(port);

            // Per-port registry guard: unclassified → probe, matching → claim,
            // mismatching → caller skips. Shared by AB9 and BaseAndHub branches.
            bool RegistrySaysSkip(string port, out (string?, string?, bool, string?) decided)
            {
                decided = (null, null, false, null);
                if (!registryByPort.TryGetValue(port, out var info)) return false;
                string pidStr = FormatPid(info.Pid);
                if (pidFilter == null || pidFilter(pidStr))
                {
                    MozaLog.Debug(
                        $"[AZOM] Port {port} already classified by registry as PID={pidStr} ({MozaUsbIds.Describe(info.Pid)}) — claiming without probe");
                    decided = (port, pidStr, false, info.DevicePath);
                    return true;
                }
                MozaLog.Debug(
                    $"[AZOM] Port {port} classified by registry as PID={pidStr} ({MozaUsbIds.Describe(info.Pid)}) — not for this connection, skipping probe");
                // Sentinel: mismatching classification — caller treats as
                // "skip and continue" by checking decided.Item1 == null
                // && we returned true.
                return true;
            }

            if (probeTarget == MozaProbeTarget.MBooster)
            {
                // mBooster has no application-level handshake (protocol note § 1)
                // and dev id 0x12 collides with wheelbase Main + AB9 main, so
                // writing a discovery probe at every COM port is high-risk.
                // The multi-device registry path is registry-only by design —
                // if the registry doesn't see the device we don't probe.
                MozaLog.Debug("[AZOM] mBooster probe fallback is disabled by design (registry-only discovery)");
                return (null, null, false, null);
            }

            if (probeTarget == MozaProbeTarget.Ab9)
            {
                // AB9 dev id 0x12 collides with wheelbase Main — disambiguate
                // with a base probe (0x2B/0x13) first; only ports that don't
                // reply to it get the AB9 probe. Registry-classified wheelbase
                // ports are skipped entirely (no base probe written).
                foreach (var port in ports)
                {
                    if (cancel?.Invoke() == true) return (null, null, false, null);
                    if (IsHeldByPeer(port)) continue;
                    if (RegistrySaysSkip(port, out var decided))
                    {
                        if (decided.Item1 != null) return decided;
                        continue;
                    }

                    var (baseResp, baseReach) = ProbeWithTimeout(port, 600, ProbeKind.Base);
                    if (!baseReach) { unreachable.Add(port); continue; }
                    if (baseResp)
                    {
                        MozaLog.Debug($"[AZOM] Probe {port} Ab9: base probe matched — wheelbase territory, skipping");
                        continue;
                    }

                    var (ab9Resp, _) = ProbeWithTimeout(port, 600, ProbeKind.Ab9);
                    if (ab9Resp)
                    {
                        MozaLog.Info($"[AZOM] Found Moza AB9 shifter on {port} (probe)");
                        return (port, null, false, null);
                    }
                }

                MozaLog.DebugIfChanged($"ab9-probe:{laneLabel}", "[AZOM] No AB9 device found on any COM port");
                return (null, null, false, null);
            }

            if (probeTarget == MozaProbeTarget.HubOnly)
            {
                // Dedicated hub connection (base also present). Single hub-probe
                // pass; never sends the base probe, so it can't claim a wheelbase
                // port. Held ports (the primary's) are skipped via IsHeldByPeer,
                // and registry-classified non-hub ports via RegistrySaysSkip.
                foreach (var port in ports)
                {
                    if (cancel?.Invoke() == true) return (null, null, false, null);
                    if (IsHeldByPeer(port)) continue;
                    if (RegistrySaysSkip(port, out var decided))
                    {
                        if (decided.Item1 != null) return decided;
                        continue;
                    }

                    var (responded, _) = ProbeWithTimeout(port, 600, ProbeKind.Hub);
                    if (responded)
                    {
                        MozaLog.Info($"[AZOM] Found Moza hub on {port} (probe, dedicated hub connection)");
                        return (port, null, true, null);
                    }
                }

                MozaLog.Debug("[AZOM] No Moza hub found on any COM port (dedicated hub connection)");
                return (null, null, false, null);
            }

            // BaseAndHub: two-pass probe — bases first, then hubs. v0.7.0 sent both
            // probes per port and returned the first port with any 0x7E reply, which
            // mis-selected the hub when both base + hub were present, or when probe-
            // cycle timing left the base unresponsive after the wrong message hit it.
            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null, false, null);
                if (IsHeldByPeer(port)) continue;
                if (RegistrySaysSkip(port, out var decided))
                {
                    if (decided.Item1 != null) return decided;
                    continue;
                }

                var (responded, reachable) = ProbeWithTimeout(port, 600, ProbeKind.Base);
                if (responded)
                {
                    MozaLog.Info($"[AZOM] Found Moza base on {port} (probe)");
                    return (port, null, false, null);
                }
                if (!reachable) unreachable.Add(port);
            }

            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null, false, null);
                if (unreachable.Contains(port)) continue;
                if (IsHeldByPeer(port)) continue;
                // Pass 1 already short-circuited on registry-matching ports
                // via RegistrySaysSkip, so a registry-classified port reaching
                // here is guaranteed mismatching — skip without re-logging.
                if (registryByPort.ContainsKey(port)) continue;

                var (responded, _) = ProbeWithTimeout(port, 600, ProbeKind.Hub);
                if (responded)
                {
                    MozaLog.Info($"[AZOM] Found Moza hub on {port} (probe)");
                    return (port, null, true, null);
                }
            }

            // Drop to Debug — reconnect timer fires every 5s, so Info-level
            // would flood the log when no device is plugged in.
            MozaLog.Debug("[AZOM] No MOZA device found on any COM port");
            return (null, null, false, null);
        }

        // ProbeKind, the probe frames, and the open+probe core live in the
        // shared SerialProbeCore (Protocol/SerialProbeCore.cs).

        private static (bool responded, bool reachable) ProbeWithTimeout(string portName, int timeoutMs, ProbeKind kind)
        {
            // The open+probe runs on a throwaway background thread so a hung
            // SerialPort.Open() on a not-ready CDC-ACM port (Wine's freshly-
            // powered-base wedge) doesn't block detection: the thread is
            // abandoned at the deadline (see _probeInFlight docs) and self-cleans
            // when the syscall finally returns.

            // Blocked-probe backoff (see _probeBackoff). Silent while the
            // interval is unexpired — suppressing the per-sweep spam is the
            // whole point. Getting past this line therefore means we are on the
            // FIRST attempt of a backoff step, which is what keeps the two
            // MozaLog.Debug lines below to one-per-step instead of twice-per-sweep.
            _probeBackoff.TryGetValue(portName, out var backoff);
            if (backoff != null && DateTime.UtcNow < backoff.NextEligibleUtc)
                return (false, false);

            // A prior probe on this port hung in Open() and was abandoned (see
            // _probeInFlight docs). Its SerialPort still owns the OS handle, so
            // a new Open() here would fail anyway — and, more importantly,
            // re-probing keeps re-entering the Wine open path that can crash on
            // a not-ready CDC-ACM port. Skip until that thread self-cleans.
            if (_probeInFlight.ContainsKey(portName))
            {
                // The prior probe's Open() still has not returned. Count it as
                // another blocked cycle so a permanently-wedged port widens its
                // interval instead of relogging this on every sweep.
                NoteProbeBlocked(portName, backoff,
                    "skipped — a prior probe is still blocked in Open() (port not ready)");
                return (false, false);
            }

            bool responded = false;
            bool reachable = false;
            _probeInFlight[portName] = 1;

            var t = new Thread(() =>
            {
                try
                {
                    // SerialProbeCore opens, probes, and closes the port entirely
                    // on THIS thread (no cross-thread dispose — that segfaults
                    // Wine mid-Open). On the abandoned-timeout path the thread is
                    // simply left running; it self-cleans here when Open finally
                    // returns.
                    (responded, reachable) = SerialProbeCore.ProbeOnePort(
                        portName, kind, m => MozaLog.Debug($"[AZOM] {m}"));
                }
                catch { responded = false; reachable = false; }
                finally
                {
                    _probeInFlight.TryRemove(portName, out _);
                }
            })
            { IsBackground = true, Name = $"MozaProbe-{portName}" };
            try
            {
                t.Start();
            }
            catch
            {
                // Thread couldn't start (e.g. resource exhaustion). The probe
                // thread's finally — which removes the in-flight marker — will
                // never run, so clear it here; otherwise this port is skipped
                // forever (the marker would be a permanent in-flight tombstone).
                _probeInFlight.TryRemove(portName, out _);
                return (false, false);
            }

            if (!t.Join(timeoutMs))
            {
                // Timed out: the port is not ready and Open() is still blocked.
                // ABANDON the background thread instead of force-closing the
                // SerialPort from here — cross-thread Close/Dispose during a
                // native Open() is the freshly-powered-base crash. The thread
                // stays in _probeInFlight and self-cleans (Close on its own
                // thread, removes the in-flight marker) when the syscall finally
                // returns; until then this port is skipped above. The thread is
                // IsBackground, so it never blocks process exit.
                NoteProbeBlocked(portName, backoff,
                    $"timed out after {timeoutMs}ms — abandoning blocked probe thread " +
                    "(not force-closing cross-thread; port marked in-flight)");
                return (false, false);
            }

            // Open() returned inside the deadline, so the port is responsive
            // whatever it answered — clear any backoff it had accumulated. A
            // slow-but-real port (the Wine freshly-powered-base case the abandon
            // path exists for) recovers its full probe cadence here.
            _probeBackoff.TryRemove(portName, out _);
            return (responded, reachable);
        }

        /// <summary>
        /// Record a blocked/timed-out probe cycle for <paramref name="portName"/>
        /// and widen its retry interval. Logs once per widening step — callers
        /// only get here after the previous window expired — and goes quiet once
        /// the interval reaches its ceiling, while still retrying at that
        /// interval forever (the port is never given up on).
        /// </summary>
        private static void NoteProbeBlocked(string portName, ProbeBackoff? prior, string what)
        {
            int n = (prior?.Consecutive ?? 0) + 1;
            long ms = ProbeBackoffFor(n);
            bool wasAlreadyCapped = prior != null && ProbeBackoffFor(prior.Consecutive) >= ProbeBackoffMaxMs;

            _probeBackoff[portName] = new ProbeBackoff(n, DateTime.UtcNow.AddMilliseconds(ms));

            if (wasAlreadyCapped) return;   // ceiling already announced — stay quiet

            if (ms < ProbeBackoffMaxMs)
            {
                MozaLog.Debug(
                    $"[AZOM] Probe {portName}: {what} — next attempt in {ms / 1000}s (blocked {n}).");
                return;
            }

            MozaLog.Debug(
                $"[AZOM] Probe {portName}: {what} — blocked {n} times; settling at one attempt " +
                $"every {ms / 1000}s and logging no further. Likely not a MOZA port, or held " +
                "open by another process.");
        }

        /// <summary>
        /// Drop backoff entries for ports no longer in the enumeration, so an
        /// unplug/replug returns to the full probe cadence.
        /// </summary>
        private static void PruneProbeBackoff(string[] currentPorts)
        {
            if (_probeBackoff.IsEmpty) return;
            // ConcurrentDictionary.Keys is a snapshot — safe to remove while iterating.
            foreach (var known in _probeBackoff.Keys)
            {
                if (Array.IndexOf(currentPorts, known) < 0)
                    _probeBackoff.TryRemove(known, out _);
            }
        }

        private static int ExtractPortNumber(string portName)
        {
            int num = 0;
            for (int i = 0; i < portName.Length; i++)
            {
                if (portName[i] >= '0' && portName[i] <= '9')
                    num = num * 10 + (portName[i] - '0');
            }
            return num;
        }

        public void Dispose()
        {
            _shutdownRequested = true;
            Disconnect();
            // After Disconnect has joined the dispatch thread; the enqueue and
            // wait paths both tolerate ObjectDisposedException regardless.
            try { _rxSignal.Dispose(); } catch { }
        }
    }
}
