using System;
using System.Collections.Generic;
using System.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// The motor state for one S12 unit: three pedals, nine slots each.
    ///
    /// Two producers, never both at once (Options picks one):
    /// <list type="bullet">
    /// <item>ShakeIt — already synthesized and mixed, each channel arrives as a
    /// plain (gain, frequency) pair on its own oscillator, slots 0..7 (see
    /// <see cref="Integration.MozaPedalHapticsChannelsProvider"/>).</item>
    /// <item>The plugin's S12 tab (<see cref="PedalHapticsEffectWorker"/>) — one
    /// named channel per slot, 0..8, including Road Texture's position stream,
    /// plus firmware-timed one-shot bursts.</item>
    /// </list>
    ///
    /// Latest-state, pulled by the connection's write loop through the paced
    /// gate (<see cref="IPacedFrameSource"/>): each frame is built from the newest
    /// value at the moment it can go out, so a busy link lowers the update rate
    /// instead of delaying every frame. Pushing frames into the FIFO let demand
    /// outrun the 4 ms drain and replayed effects seconds late (bundles QK7EC6FQ,
    /// Y2MYEM9E).
    ///
    /// Order of service: disables, live slots near the end of their duration,
    /// enables and bursts, then the longest-unsent live slot. A live slot is
    /// re-sent when it changes (at most every
    /// <see cref="MozaPedalHapticsProtocol.UpdateIntervalMs"/>) and otherwise every
    /// <see cref="MozaPedalHapticsProtocol.RefreshIntervalMs"/>, inside its
    /// <see cref="MozaPedalHapticsProtocol.StreamDurationMs"/> window.
    /// </summary>
    internal sealed class PedalHapticsEffectScheduler : IPacedFrameSource
    {
        private const int Pedals = MozaPedalHapticsProtocol.PedalCount;
        private const int Slots = MozaPedalHapticsProtocol.SlotsPerPedal;
        private const int Total = Pedals * Slots;
        private const int RoadSlot = MozaPedalHapticsProtocol.RoadTextureSlot;

        /// <summary>
        /// Below this a slot counts as silent. ShakeIt emits very small non-zero
        /// gains on an effect's decay tail; driving an oscillator for those
        /// achieves nothing audible and wastes link time.
        /// </summary>
        private const double MinGain = 0.002;

        /// <summary>
        /// Drop everything to silent if the producer stops posting for this long.
        /// A clean teardown clears explicitly, but a game crash or a profile swap
        /// mid-tick can simply stop the calls.
        /// </summary>
        private const double StaleAfterSec = 0.5;

        private static readonly long StaleTicks = (long)(StaleAfterSec * Stopwatch.Frequency);
        private static readonly long UpdateTicks = MozaPedalHapticsProtocol.UpdateIntervalMs * Stopwatch.Frequency / 1000;
        private static readonly long RefreshTicks = MozaPedalHapticsProtocol.RefreshIntervalMs * Stopwatch.Frequency / 1000;

        // A live slot this long unsent jumps ahead of new enables, so a burst of
        // effect edges cannot starve it past its duration window.
        private static readonly long ExpiringTicks = MozaPedalHapticsProtocol.StreamDurationMs / 2 * Stopwatch.Frequency / 1000;

        // Ascending urgency.
        private enum Due { None, Update, Enable, Expiring, Disable }

        private readonly PedalHapticsDeviceController _device;
        private readonly Func<bool> _isShuttingDown;

        private readonly object _lock = new object();

        // Latest producer output. Slot 8 carries a suspension position in _freq.
        private readonly double[,] _gain = new double[Pedals, Slots];
        private readonly double[,] _freq = new double[Pedals, Slots];
        // Per slot, so a slot one producer stopped posting goes stale even while
        // another keeps the same pedal's other slots fresh.
        private readonly long[,] _lastPostTicks = new long[Pedals, Slots];

        // Pending firmware-timed one-shots (timed slots only).
        private readonly bool[,] _burst = new bool[Pedals, Slots];
        private readonly double[,] _burstGain = new double[Pedals, Slots];
        private readonly double[,] _burstFreq = new double[Pedals, Slots];
        private readonly int[,] _burstMs = new int[Pedals, Slots];

        // What the unit was last sent.
        private readonly bool[,] _sentOn = new bool[Pedals, Slots];
        private readonly ushort[,] _sentStrength = new ushort[Pedals, Slots];
        private readonly ushort[,] _sentFreq = new ushort[Pedals, Slots];
        private readonly long[,] _sentTicks = new long[Pedals, Slots];

        private bool _stopped;

        internal PedalHapticsEffectScheduler(PedalHapticsDeviceController device, Func<bool>? isShuttingDown)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _isShuttingDown = isShuttingDown ?? (() => false);
        }

        public int NominalFrameBytes => _device.Addressing == PedalHapticsAddressing.Extended
            ? MozaPedalHapticsProtocol.FrameLenExtended
            : MozaPedalHapticsProtocol.FrameLenUsb;

        /// <summary>
        /// Latest ShakeIt output for one oscillator (0..7) of one pedal. Called
        /// from SimHub's data thread by that pedal's device.
        /// </summary>
        public void PostChannel(int pedalIndex, int channel, double gain01, double freqHz)
        {
            if (channel < 0 || channel >= MozaPedalHapticsProtocol.ChannelsPerPedal) return;
            PostSlot(pedalIndex, MozaPedalHapticsProtocol.SlotForChannel(channel), gain01, freqHz);
        }

        /// <summary>
        /// Latest level for one slot (0..8) of one pedal. <paramref name="freqOrPosition"/>
        /// is Hz, or the suspension position on Road Texture.
        /// </summary>
        public void PostSlot(int pedalIndex, int slot, double gain01, double freqOrPosition)
        {
            if (pedalIndex < 0 || pedalIndex >= Pedals) return;
            if (slot < 0 || slot >= Slots) return;
            lock (_lock)
            {
                _gain[pedalIndex, slot] = gain01;
                _freq[pedalIndex, slot] = freqOrPosition;
                _lastPostTicks[pedalIndex, slot] = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// One firmware-timed burst: a single enable frame carrying
        /// <paramref name="durationMs"/>, which the unit stops by itself. Timed
        /// slots only — Engine and Road Texture ignore duration.
        /// </summary>
        public void PostBurst(int pedalIndex, int slot, double gain01, double freqHz, int durationMs)
        {
            if (pedalIndex < 0 || pedalIndex >= Pedals) return;
            if (slot < 0 || slot >= Slots || !MozaPedalHapticsProtocol.IsTimedSlot((byte)slot)) return;
            if (gain01 <= MinGain || durationMs <= 0) return;
            lock (_lock)
            {
                _burst[pedalIndex, slot] = true;
                _burstGain[pedalIndex, slot] = gain01;
                _burstFreq[pedalIndex, slot] = freqHz;
                _burstMs[pedalIndex, slot] = durationMs;
            }
        }

        /// <summary>Force one pedal silent — a provider Stop() path or a mode switch.</summary>
        public void ClearPedal(int pedalIndex)
        {
            if (pedalIndex < 0 || pedalIndex >= Pedals) return;
            lock (_lock)
            {
                for (int s = 0; s < Slots; s++)
                {
                    _gain[pedalIndex, s] = 0;
                    _freq[pedalIndex, s] = 0;
                    _burst[pedalIndex, s] = false;
                }
            }
        }

        public bool MayHaveWork
        {
            get
            {
                // Gated on Detected: a routed lane is registered speculatively on
                // every base pipe, and a base with no S12 attached must never be
                // written effect frames.
                if (!_device.Detected) return false;
                bool shuttingDown = _isShuttingDown();
                lock (_lock)
                {
                    if (_stopped) return false;
                    return PickLocked(Stopwatch.GetTimestamp(), shuttingDown, out _) != Due.None;
                }
            }
        }

        public byte[]? TryTakeFrame()
        {
            if (!_device.Detected) return null;
            bool shuttingDown = _isShuttingDown();
            lock (_lock)
            {
                if (_stopped) return null;
                long now = Stopwatch.GetTimestamp();
                var due = PickLocked(now, shuttingDown, out int i);
                if (due == Due.None) return null;

                int p = i / Slots;
                int s = i % Slots;
                byte pedal = MozaPedalHapticsProtocol.PedalForIndex(p);
                byte slot = (byte)s;
                _sentTicks[p, s] = now;

                if (due == Due.Disable)
                {
                    _sentOn[p, s] = false;
                    return MozaPedalHapticsProtocol.BuildDisableFrame(_device.Addressing, pedal, slot);
                }

                // A burst is self-terminating, so it does not mark the slot on —
                // no disable follows it.
                if (_burst[p, s])
                {
                    _burst[p, s] = false;
                    return MozaPedalHapticsProtocol.BuildSetFrame(
                        _device.Addressing, pedal, slot,
                        enable: true,
                        durationMs: _burstMs[p, s],
                        freqHz: _burstFreq[p, s], strength01: _burstGain[p, s]);
                }

                double gain = _gain[p, s];
                double freq = _freq[p, s];
                _sentOn[p, s] = true;
                _sentStrength[p, s] = MozaPedalHapticsProtocol.EncodeStrength(gain);
                _sentFreq[p, s] = EncodeFreqField(s, freq);
                if (s == RoadSlot)
                    return MozaPedalHapticsProtocol.BuildRoadTextureFrame(
                        _device.Addressing, pedal, enable: true,
                        suspensionPosition: RoadPosition(freq), strength01: gain);
                return MozaPedalHapticsProtocol.BuildSetFrame(
                    _device.Addressing, pedal, slot,
                    enable: true,
                    durationMs: MozaPedalHapticsProtocol.StreamDurationMs,
                    freqHz: freq, strength01: gain);
            }
        }

        private static int RoadPosition(double v) => (int)Math.Round(double.IsNaN(v) ? 0 : v);

        private static ushort EncodeFreqField(int slot, double v)
            => slot == RoadSlot
                ? MozaPedalHapticsProtocol.ClampRoadPosition(RoadPosition(v))
                : MozaPedalHapticsProtocol.EncodeFrequency(v);

        // Most urgent slot, oldest send first within a class.
        private Due PickLocked(long now, bool shuttingDown, out int pick)
        {
            pick = -1;
            var best = Due.None;
            long bestSent = long.MaxValue;
            for (int i = 0; i < Total; i++)
            {
                int p = i / Slots;
                int s = i % Slots;
                var due = ClassifyLocked(p, s, now, shuttingDown);
                if (due == Due.None) continue;
                if (due > best || (due == best && _sentTicks[p, s] < bestSent))
                {
                    best = due;
                    bestSent = _sentTicks[p, s];
                    pick = i;
                }
            }
            return best;
        }

        private Due ClassifyLocked(int p, int s, long now, bool shuttingDown)
        {
            if (shuttingDown) _burst[p, s] = false;

            bool silent = shuttingDown
                || now - _lastPostTicks[p, s] > StaleTicks
                || _gain[p, s] <= MinGain;

            if (!_sentOn[p, s])
            {
                if (_burst[p, s]) return Due.Enable;
                return silent ? Due.None : Due.Enable;
            }
            // Already vibrating from a stream — a burst on top adds nothing.
            _burst[p, s] = false;
            if (silent)
                return Due.Disable;

            long since = now - _sentTicks[p, s];
            if (since >= ExpiringTicks) return Due.Expiring;
            if (since >= RefreshTicks) return Due.Update;
            if (since >= UpdateTicks
                && (MozaPedalHapticsProtocol.EncodeStrength(_gain[p, s]) != _sentStrength[p, s]
                    || EncodeFreqField(s, _freq[p, s]) != _sentFreq[p, s]))
                return Due.Update;
            return Due.None;
        }

        /// <summary>
        /// Stop producing frames and silence every slot still running. The
        /// disables go out on the FIFO because the caller is about to detach this
        /// source; each slot is disabled individually — stopping one leaves the
        /// rest of that pedal's slots running. Bursts in flight end on their own.
        /// </summary>
        public void Stop()
        {
            var disables = new List<byte[]>();
            lock (_lock)
            {
                if (_stopped) return;
                _stopped = true;
                for (int p = 0; p < Pedals; p++)
                {
                    for (int s = 0; s < Slots; s++)
                    {
                        _burst[p, s] = false;
                        if (!_sentOn[p, s]) continue;
                        _sentOn[p, s] = false;
                        disables.Add(MozaPedalHapticsProtocol.BuildDisableFrame(
                            _device.Addressing,
                            MozaPedalHapticsProtocol.PedalForIndex(p),
                            (byte)s));
                    }
                }
            }

            foreach (var frame in disables)
                _device.SendFrame(frame);
        }
    }
}
