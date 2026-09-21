using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// The motor loop for one S12 unit. ShakeIt has already synthesized the
    /// waveforms by the time anything reaches here — every channel arrives as a
    /// plain (gain, frequency) pair — so one thread covers all three pedals.
    ///
    /// Its real job is allocation. Each pedal exposes more ShakeIt channels than
    /// the hardware has oscillators, because the user can build as many effects
    /// as they like while the module has eight interchangeable slots. A channel
    /// takes a slot when it goes active and keeps it until it goes silent; once
    /// the pool is exhausted, latecomers share a slot and their gains are mixed.
    /// Slots are interchangeable — the firmware names them after game events but
    /// synthesizes nothing from those names — which is what makes this sound.
    ///
    /// Road Texture sits outside the pool. It takes a suspension position rather
    /// than a frequency and cannot stand in for a tone, so it owns its slot.
    /// </summary>
    internal sealed class PedalHapticsEffectWorker : IDisposable
    {
        private const int Pedals = MozaPedalHapticsProtocol.PedalCount;
        private const int Channels = MozaPedalHapticsProtocol.ChannelsPerPedal;
        private const int Pool = MozaPedalHapticsProtocol.SlotPoolSize;

        /// <summary>Marker for "this channel holds no slot".</summary>
        private const int NoSlot = -1;

        /// <summary>
        /// Below this a channel counts as silent and gives its slot back.
        /// ShakeIt emits very small non-zero gains on an effect's decay tail;
        /// holding a slot for those would starve channels that need one.
        /// </summary>
        private const double MinGain = 0.002;

        /// <summary>
        /// Drop everything to silent if ShakeIt stops posting for this long. A
        /// clean teardown calls the provider's Stop(), but a game crash or a
        /// profile swap mid-tick can simply stop the calls.
        /// </summary>
        private const double StaleAfterSec = 0.5;

        private readonly PedalHapticsDeviceController _device;
        private readonly Func<bool> _isShuttingDown;

        private readonly object _postLock = new object();
        private readonly double[,] _postedGain = new double[Pedals, Channels];
        private readonly double[,] _postedFreq = new double[Pedals, Channels];
        private readonly double[] _lastPostSec = new double[Pedals];

        // Loop-thread state — no locking needed.
        private readonly int[,] _channelSlot = new int[Pedals, Channels];   // channel -> pool slot
        private readonly int[,] _slotUsers = new int[Pedals, Pool];         // pool slot -> channel count
        private readonly bool[,] _slotActive = new bool[Pedals, Pool];      // pool slot currently driven
        private readonly bool[] _roadActive = new bool[Pedals];
        private readonly int[] _cursor = new int[Pedals];

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread? _thread;
        private volatile bool _running;

        internal PedalHapticsEffectWorker(PedalHapticsDeviceController device, Func<bool>? isShuttingDown)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _isShuttingDown = isShuttingDown ?? (() => false);

            for (int p = 0; p < Pedals; p++)
                for (int ch = 0; ch < Channels; ch++)
                    _channelSlot[p, ch] = NoSlot;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "MozaPedalHaptics",
            };
            _thread.Start();
        }

        /// <summary>
        /// Latest ShakeIt output for one channel of one pedal. Called from
        /// SimHub's data thread by that pedal's device; the loop samples it.
        /// </summary>
        public void PostChannel(int pedalIndex, int channel, double gain01, double freqHz)
        {
            if (pedalIndex < 0 || pedalIndex >= Pedals) return;
            if (channel < 0 || channel >= Channels) return;
            lock (_postLock)
            {
                _postedGain[pedalIndex, channel] = gain01;
                _postedFreq[pedalIndex, channel] = freqHz;
                _lastPostSec[pedalIndex] = _clock.Elapsed.TotalSeconds;
            }
        }

        /// <summary>Force one pedal silent — that device's provider Stop() path.</summary>
        public void ClearPedal(int pedalIndex)
        {
            if (pedalIndex < 0 || pedalIndex >= Pedals) return;
            lock (_postLock)
            {
                for (int ch = 0; ch < Channels; ch++)
                {
                    _postedGain[pedalIndex, ch] = 0;
                    _postedFreq[pedalIndex, ch] = 0;
                }
            }
        }

        /// <summary>Force every pedal silent.</summary>
        public void ClearAll()
        {
            for (int p = 0; p < Pedals; p++) ClearPedal(p);
        }

        private void Loop()
        {
            double next = _clock.Elapsed.TotalMilliseconds;
            while (_running)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] Pedal-haptics effect tick failed: {ex.Message}");
                }

                next += MozaPedalHapticsProtocol.StreamPeriodMs;
                double delta = next - _clock.Elapsed.TotalMilliseconds;
                if (delta < 1)
                {
                    // Fell behind (GC pause, port stall) — resync rather than
                    // spin through a backlog of ticks whose frames are stale.
                    next = _clock.Elapsed.TotalMilliseconds;
                    delta = 1;
                }
                Thread.Sleep((int)Math.Min(50, delta));
            }
        }

        private void Tick()
        {
            bool shuttingDown = _isShuttingDown() || !_device.IsConnected;
            double now = _clock.Elapsed.TotalSeconds;
            int budget = MozaPedalHapticsProtocol.MaxFramesPerTick;

            for (int p = 0; p < Pedals; p++)
            {
                bool silence = shuttingDown;
                lock (_postLock)
                {
                    if (now - _lastPostSec[p] > StaleAfterSec) silence = true;
                }

                Allocate(p, silence);
                budget = EmitPedal(p, budget);
            }
        }

        /// <summary>
        /// Settle slot ownership for one pedal. A channel keeps its slot for as
        /// long as it stays active, so a sustained effect never jumps oscillator
        /// mid-note; slots are only handed out on a rising edge and returned on a
        /// falling one.
        /// </summary>
        private void Allocate(int p, bool silence)
        {
            for (int ch = 0; ch < MozaPedalHapticsProtocol.GenericChannelsPerPedal; ch++)
            {
                double gain;
                lock (_postLock) gain = _postedGain[p, ch];

                bool active = !silence && gain > MinGain;
                int held = _channelSlot[p, ch];

                if (active && held == NoSlot)
                {
                    _channelSlot[p, ch] = TakeSlot(p);
                    _slotUsers[p, _channelSlot[p, ch]]++;
                }
                else if (!active && held != NoSlot)
                {
                    _slotUsers[p, held]--;
                    _channelSlot[p, ch] = NoSlot;
                }
            }
        }

        /// <summary>
        /// Next slot for a channel that just went active: the first unused one
        /// from a rotating cursor, else — once every slot is taken — the cursor's
        /// own slot, shared. Rotating rather than always starting at zero keeps
        /// the sharing spread evenly instead of piling onto slot 0.
        /// </summary>
        private int TakeSlot(int p)
        {
            for (int n = 0; n < Pool; n++)
            {
                int slot = MozaPedalHapticsProtocol.PoolSlot(_cursor[p] + n);
                if (_slotUsers[p, slot] != 0) continue;
                _cursor[p] = slot + 1;
                return slot;
            }

            int shared = MozaPedalHapticsProtocol.PoolSlot(_cursor[p]);
            _cursor[p] = shared + 1;
            return shared;
        }

        /// <summary>
        /// Emit one pedal's slots, newly-silent ones first so a motor never keeps
        /// running because the budget ran out. Returns the budget left.
        /// </summary>
        private int EmitPedal(int p, int budget)
        {
            byte pedal = MozaPedalHapticsProtocol.PedalForIndex(p);

            // Releases are not budgeted: a dropped disable leaves a motor buzzing
            // for the rest of its duration window.
            for (int slot = 0; slot < Pool; slot++)
            {
                if (_slotUsers[p, slot] != 0 || !_slotActive[p, slot]) continue;
                _slotActive[p, slot] = false;
                _device.SendFrame(MozaPedalHapticsProtocol.BuildDisableFrame(
                    _device.Addressing, pedal, (byte)slot));
            }

            bool roadActive = MixRoad(p, out int position, out double roadGain);
            if (!roadActive && _roadActive[p])
            {
                _roadActive[p] = false;
                _device.SendFrame(MozaPedalHapticsProtocol.BuildDisableFrame(
                    _device.Addressing, pedal, MozaPedalHapticsProtocol.RoadTextureSlot));
            }

            for (int slot = 0; slot < Pool && budget > 0; slot++)
            {
                if (_slotUsers[p, slot] == 0) continue;
                if (!Mix(p, slot, out double gain, out double freq)) continue;

                _slotActive[p, slot] = true;
                _device.SendFrame(MozaPedalHapticsProtocol.BuildSetFrame(
                    _device.Addressing, pedal, (byte)slot, enable: true,
                    durationMs: MozaPedalHapticsProtocol.StreamDurationMs,
                    freqHz: freq, strength01: gain));
                budget--;
            }

            if (roadActive && budget > 0)
            {
                _roadActive[p] = true;
                _device.SendFrame(MozaPedalHapticsProtocol.BuildRoadTextureFrame(
                    _device.Addressing, pedal, enable: true,
                    suspensionPosition: position, strength01: roadGain));
                budget--;
            }

            return budget;
        }

        /// <summary>
        /// Combine every channel sharing one slot: gains add (clamped), frequency
        /// is their gain-weighted mean. The hardware takes one tone per slot, so
        /// two effects sharing cannot both be reproduced faithfully — summing
        /// keeps both audible rather than dropping the quieter one.
        /// </summary>
        private bool Mix(int p, int slot, out double gain, out double freq)
        {
            double sum = 0, weighted = 0;
            lock (_postLock)
            {
                for (int ch = 0; ch < MozaPedalHapticsProtocol.GenericChannelsPerPedal; ch++)
                {
                    if (_channelSlot[p, ch] != slot) continue;
                    double g = _postedGain[p, ch];
                    if (g <= 0) continue;
                    sum += g;
                    weighted += g * _postedFreq[p, ch];
                }
            }

            if (sum <= 0)
            {
                gain = 0;
                freq = 0;
                return false;
            }

            freq = weighted / sum;
            gain = sum > 1.0 ? 1.0 : sum;
            return true;
        }

        /// <summary>
        /// Road Texture's own channel. Its position field is what the firmware
        /// generates texture from, and only while that position keeps moving — a
        /// steady value settles to silence. ShakeIt's gain is the part that
        /// actually varies with an effect, so it drives the position; the
        /// frequency it reports would sit still and produce nothing.
        /// </summary>
        private bool MixRoad(int p, out int position, out double gain)
        {
            lock (_postLock) gain = _postedGain[p, MozaPedalHapticsProtocol.RoadTextureChannel];

            if (_isShuttingDown() || gain <= MinGain)
            {
                position = 0;
                gain = 0;
                return false;
            }

            position = (int)Math.Round(gain * MozaPedalHapticsProtocol.MaxRoadPosition);
            return true;
        }

        /// <summary>
        /// Stop the loop, then release every slot still held. Disables go out
        /// after the thread has joined so the loop cannot race a retrigger in
        /// behind them, and each slot is disabled individually — stopping one
        /// leaves the rest of that pedal's slots running.
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _thread?.Join(500); } catch { /* shutting down */ }
            _thread = null;

            for (int p = 0; p < Pedals; p++)
            {
                byte pedal = MozaPedalHapticsProtocol.PedalForIndex(p);
                for (int slot = 0; slot < Pool; slot++)
                {
                    if (!_slotActive[p, slot]) continue;
                    _slotActive[p, slot] = false;
                    TrySend(pedal, (byte)slot);
                }
                if (!_roadActive[p]) continue;
                _roadActive[p] = false;
                TrySend(pedal, MozaPedalHapticsProtocol.RoadTextureSlot);
            }
        }

        private void TrySend(byte pedal, byte slot)
        {
            try
            {
                _device.SendFrame(MozaPedalHapticsProtocol.BuildDisableFrame(
                    _device.Addressing, pedal, slot));
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Pedal-haptics disable on stop failed: {ex.Message}");
            }
        }

        public void Dispose() => Stop();
    }
}
