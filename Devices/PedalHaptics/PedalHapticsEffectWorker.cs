using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// The motor loop for one S12 unit, covering all 27 channels (three pedals ×
    /// nine effect slots) on a single thread. Unlike
    /// <see cref="MBooster.MBoosterEffectWorker"/> — which runs one thread per
    /// pedal because each synthesizes its own waveform from telemetry — ShakeIt
    /// has already done the synthesis here: every channel arrives as a plain
    /// (gain, frequency) pair, so there is nothing to parallelize and 27 threads
    /// would be absurd.
    ///
    /// Two things shape the loop:
    ///
    /// <list type="bullet">
    /// <item><b>The unit stops an effect when its duration elapses</b>, so a
    /// sustained tone is held by re-sending. That is a feature, not overhead: a
    /// host crash silences the motors within one duration instead of leaving
    /// them running.</item>
    /// <item><b>27 channels cannot all be refreshed every tick.</b> That would be
    /// 1350 frames/s. Active channels are serviced round-robin under
    /// <see cref="MozaPedalHapticsProtocol.MaxFramesPerTick"/>, which costs
    /// nothing when a few channels are live and degrades gracefully when many
    /// are. Activation edges bypass the budget so attack is never delayed.</item>
    /// </list>
    /// </summary>
    internal sealed class PedalHapticsEffectWorker : IDisposable
    {
        private const int ChannelCount = MozaPedalHapticsProtocol.ChannelCount;

        /// <summary>
        /// Below this the channel counts as silent. ShakeIt emits very small
        /// non-zero gains on an effect's decay tail; treating those as active
        /// would hold 27 slots enabled for nothing audible and starve the
        /// per-tick budget.
        /// </summary>
        private const double MinGain = 0.002;

        /// <summary>
        /// Drop everything to silent if ShakeIt stops posting for this long.
        /// SimHub calls the provider's Stop() on a clean teardown, but a game
        /// crash or a profile swap mid-tick can simply stop the calls.
        /// </summary>
        private const double StaleAfterSec = 0.5;

        private readonly PedalHapticsDeviceController _device;
        private readonly Func<bool> _isShuttingDown;

        private readonly object _postLock = new object();
        private readonly double[] _postedGain = new double[ChannelCount];
        private readonly double[] _postedFreq = new double[ChannelCount];
        private double _lastPostSec;

        // Loop-thread state — no locking needed.
        private readonly bool[] _channelActive = new bool[ChannelCount];
        private readonly bool[] _sentThisTick = new bool[ChannelCount];
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
        /// Latest ShakeIt output for one channel. Called from SimHub's data
        /// thread at whatever rate the tone mixer runs; the loop samples it.
        /// </summary>
        public void PostChannel(int channel, double gain01, double freqHz)
        {
            if (channel < 0 || channel >= ChannelCount) return;
            lock (_postLock)
            {
                _postedGain[channel] = gain01;
                _postedFreq[channel] = freqHz;
                _lastPostSec = _clock.Elapsed.TotalSeconds;
            }
        }

        /// <summary>Force every channel silent — the provider's Stop() path.</summary>
        public void ClearChannels()
        {
            lock (_postLock)
            {
                for (int i = 0; i < ChannelCount; i++)
                {
                    _postedGain[i] = 0;
                    _postedFreq[i] = 0;
                }
            }
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
            bool silenceAll = _isShuttingDown() || !_device.IsConnected;

            double now = _clock.Elapsed.TotalSeconds;
            lock (_postLock)
            {
                if (now - _lastPostSec > StaleAfterSec) silenceAll = true;
            }

            Array.Clear(_sentThisTick, 0, _sentThisTick.Length);

            // Pass 1 — edges. An activation is sent immediately so an effect's
            // attack is not held up behind the round-robin, and a deactivation
            // must go out at once or the motor keeps running to its duration.
            for (int ch = 0; ch < ChannelCount; ch++)
            {
                double gain;
                lock (_postLock) gain = _postedGain[ch];

                bool active = !silenceAll && gain > MinGain;
                if (active == _channelActive[ch]) continue;

                _channelActive[ch] = active;
                if (active)
                {
                    SendChannel(ch);
                    _sentThisTick[ch] = true;
                }
                else
                {
                    SendDisable(ch);
                }
            }

            // Pass 2 — refresh the still-active channels, round-robin, so no one
            // channel monopolises the budget and every active one is serviced
            // well inside StreamDurationMs.
            int budget = MozaPedalHapticsProtocol.MaxFramesPerTick;
            for (int n = 0; n < ChannelCount && budget > 0; n++)
            {
                int ch = (_cursor + n) % ChannelCount;
                if (!_channelActive[ch] || _sentThisTick[ch]) continue;

                SendChannel(ch);
                _sentThisTick[ch] = true;
                budget--;
                _cursor = (ch + 1) % ChannelCount;
            }
        }

        private void SendChannel(int ch)
        {
            double gain, freq;
            lock (_postLock)
            {
                gain = _postedGain[ch];
                freq = _postedFreq[ch];
            }

            byte pedal = MozaPedalHapticsProtocol.PedalForChannel(ch);
            byte slot = MozaPedalHapticsProtocol.SlotForChannel(ch);

            byte[] frame;
            if (slot == (byte)PedalHapticsEffectSlot.RoadTexture)
            {
                // This slot takes a suspension position, not a frequency, and
                // generates texture from the position CHANGING — a constant value
                // settles to silence. ShakeIt's gain is the field that actually
                // moves with an effect, so it drives the position; its frequency
                // would sit still for most effects and produce nothing. Strength
                // tracks gain too, so the channel still fades in and out.
                frame = MozaPedalHapticsProtocol.BuildRoadTextureFrame(
                    _device.Addressing, pedal, enable: true,
                    suspensionPosition: (int)Math.Round(gain * MozaPedalHapticsProtocol.MaxRoadPosition),
                    strength01: gain);
            }
            else
            {
                frame = MozaPedalHapticsProtocol.BuildSetFrame(
                    _device.Addressing, pedal, slot, enable: true,
                    durationMs: MozaPedalHapticsProtocol.StreamDurationMs,
                    freqHz: freq,
                    strength01: gain);
            }

            _device.SendFrame(frame);
        }

        private void SendDisable(int ch)
        {
            _device.SendFrame(MozaPedalHapticsProtocol.BuildDisableFrame(
                _device.Addressing,
                MozaPedalHapticsProtocol.PedalForChannel(ch),
                MozaPedalHapticsProtocol.SlotForChannel(ch)));
        }

        /// <summary>
        /// Stop the loop, then disable every channel still live. The disable
        /// frames go out after the thread has joined so the loop cannot race a
        /// retrigger in behind them. Each slot must be disabled individually —
        /// stopping one leaves the other eight on that pedal running.
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _thread?.Join(500); } catch { /* shutting down */ }
            _thread = null;

            for (int ch = 0; ch < ChannelCount; ch++)
            {
                if (!_channelActive[ch]) continue;
                _channelActive[ch] = false;
                try { SendDisable(ch); }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] Pedal-haptics disable on stop failed: {ex.Message}");
                }
            }
        }

        public void Dispose() => Stop();
    }
}
