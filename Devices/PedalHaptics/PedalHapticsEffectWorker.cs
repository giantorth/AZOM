using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// The motor loop for one S12 unit: three pedals, eight oscillators each.
    ///
    /// ShakeIt has already synthesized and mixed by the time anything reaches
    /// here, so each channel arrives as a plain (gain, frequency) pair and maps
    /// straight onto its own hardware slot. Effects were spread across those
    /// channels when they were created (see
    /// <see cref="Integration.MozaPedalHapticsChannelsProvider"/>), which is what
    /// lets the module mix them itself at their own frequencies rather than
    /// receiving one collapsed tone — and it happens without the user assigning
    /// anything.
    ///
    /// The module stops an effect when its duration elapses, so a sustained tone
    /// is held by re-sending. That is deliberate: a host crash silences the
    /// motors within one duration instead of leaving them running.
    /// </summary>
    internal sealed class PedalHapticsEffectWorker : IDisposable
    {
        private const int Pedals = MozaPedalHapticsProtocol.PedalCount;
        private const int Channels = MozaPedalHapticsProtocol.ChannelsPerPedal;

        /// <summary>
        /// Below this a channel counts as silent. ShakeIt emits very small
        /// non-zero gains on an effect's decay tail; driving an oscillator for
        /// those achieves nothing audible and wastes the frame budget.
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
        private readonly bool[,] _active = new bool[Pedals, Channels];
        private int _cursor;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread? _thread;
        private volatile bool _running;

        internal PedalHapticsEffectWorker(PedalHapticsDeviceController device, Func<bool>? isShuttingDown)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _isShuttingDown = isShuttingDown ?? (() => false);
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
            int total = Pedals * Channels;

            // Releases first and unbudgeted: a dropped disable leaves a motor
            // buzzing for the rest of its duration window.
            for (int i = 0; i < total; i++)
            {
                int p = i / Channels;
                int ch = i % Channels;
                if (!_active[p, ch]) continue;
                if (!IsSilent(p, ch, now, shuttingDown)) continue;

                _active[p, ch] = false;
                _device.SendFrame(MozaPedalHapticsProtocol.BuildDisableFrame(
                    _device.Addressing,
                    MozaPedalHapticsProtocol.PedalForIndex(p),
                    MozaPedalHapticsProtocol.SlotForChannel(ch)));
            }

            // Then refresh the live ones, round-robin under the frame budget so
            // no one channel monopolises it.
            for (int n = 0; n < total && budget > 0; n++)
            {
                int i = (_cursor + n) % total;
                int p = i / Channels;
                int ch = i % Channels;
                if (IsSilent(p, ch, now, shuttingDown)) continue;

                double gain, freq;
                lock (_postLock)
                {
                    gain = _postedGain[p, ch];
                    freq = _postedFreq[p, ch];
                }

                _active[p, ch] = true;
                _device.SendFrame(MozaPedalHapticsProtocol.BuildSetFrame(
                    _device.Addressing,
                    MozaPedalHapticsProtocol.PedalForIndex(p),
                    MozaPedalHapticsProtocol.SlotForChannel(ch),
                    enable: true,
                    durationMs: MozaPedalHapticsProtocol.StreamDurationMs,
                    freqHz: freq, strength01: gain));
                budget--;
                _cursor = i + 1;
            }
        }

        private bool IsSilent(int p, int ch, double now, bool shuttingDown)
        {
            if (shuttingDown) return true;
            lock (_postLock)
            {
                if (now - _lastPostSec[p] > StaleAfterSec) return true;
                return _postedGain[p, ch] <= MinGain;
            }
        }

        /// <summary>
        /// Stop the loop, then silence every oscillator still running. Disables go
        /// out after the thread has joined so the loop cannot race a retrigger in
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
                for (int ch = 0; ch < Channels; ch++)
                {
                    if (!_active[p, ch]) continue;
                    _active[p, ch] = false;
                    try
                    {
                        _device.SendFrame(MozaPedalHapticsProtocol.BuildDisableFrame(
                            _device.Addressing,
                            MozaPedalHapticsProtocol.PedalForIndex(p),
                            MozaPedalHapticsProtocol.SlotForChannel(ch)));
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Debug($"[AZOM] Pedal-haptics disable on stop failed: {ex.Message}");
                    }
                }
            }
        }

        public void Dispose() => Stop();
    }
}
