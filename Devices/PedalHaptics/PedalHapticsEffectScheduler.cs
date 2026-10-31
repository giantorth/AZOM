using System;
using System.Collections.Generic;
using System.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// The motor state for one S12 unit: three pedals, eight oscillators each.
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
    /// Latest-state, pulled by the connection's write loop through the paced
    /// gate (<see cref="IPacedFrameSource"/>): each frame is built from the newest
    /// ShakeIt output at the moment it can go out, so a busy link lowers the
    /// update rate instead of delaying every frame. Pushing frames into the FIFO
    /// let demand outrun the 4 ms drain and replayed effects seconds late
    /// (bundles QK7EC6FQ, Y2MYEM9E).
    ///
    /// Order of service: disables, live slots near the end of their duration,
    /// enables, then the longest-unsent live slot. A live slot is re-sent when it changes (at most every
    /// <see cref="MozaPedalHapticsProtocol.UpdateIntervalMs"/>) and otherwise every
    /// <see cref="MozaPedalHapticsProtocol.RefreshIntervalMs"/>, inside its
    /// <see cref="MozaPedalHapticsProtocol.StreamDurationMs"/> window.
    /// </summary>
    internal sealed class PedalHapticsEffectScheduler : IPacedFrameSource
    {
        private const int Pedals = MozaPedalHapticsProtocol.PedalCount;
        private const int Channels = MozaPedalHapticsProtocol.ChannelsPerPedal;
        private const int Total = Pedals * Channels;

        /// <summary>
        /// Below this a channel counts as silent. ShakeIt emits very small
        /// non-zero gains on an effect's decay tail; driving an oscillator for
        /// those achieves nothing audible and wastes link time.
        /// </summary>
        private const double MinGain = 0.002;

        /// <summary>
        /// Drop everything to silent if ShakeIt stops posting for this long. A
        /// clean teardown calls the provider's Stop(), but a game crash or a
        /// profile swap mid-tick can simply stop the calls.
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

        // Latest ShakeIt output.
        private readonly double[,] _gain = new double[Pedals, Channels];
        private readonly double[,] _freq = new double[Pedals, Channels];
        private readonly long[] _lastPostTicks = new long[Pedals];

        // What the unit was last sent.
        private readonly bool[,] _sentOn = new bool[Pedals, Channels];
        private readonly ushort[,] _sentStrength = new ushort[Pedals, Channels];
        private readonly ushort[,] _sentFreq = new ushort[Pedals, Channels];
        private readonly long[,] _sentTicks = new long[Pedals, Channels];

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
        /// Latest ShakeIt output for one channel of one pedal. Called from
        /// SimHub's data thread by that pedal's device.
        /// </summary>
        public void PostChannel(int pedalIndex, int channel, double gain01, double freqHz)
        {
            if (pedalIndex < 0 || pedalIndex >= Pedals) return;
            if (channel < 0 || channel >= Channels) return;
            lock (_lock)
            {
                _gain[pedalIndex, channel] = gain01;
                _freq[pedalIndex, channel] = freqHz;
                _lastPostTicks[pedalIndex] = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>Force one pedal silent — that device's provider Stop() path.</summary>
        public void ClearPedal(int pedalIndex)
        {
            if (pedalIndex < 0 || pedalIndex >= Pedals) return;
            lock (_lock)
            {
                for (int ch = 0; ch < Channels; ch++)
                {
                    _gain[pedalIndex, ch] = 0;
                    _freq[pedalIndex, ch] = 0;
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

                int p = i / Channels;
                int ch = i % Channels;
                byte pedal = MozaPedalHapticsProtocol.PedalForIndex(p);
                byte slot = MozaPedalHapticsProtocol.SlotForChannel(ch);
                _sentTicks[p, ch] = now;

                if (due == Due.Disable)
                {
                    _sentOn[p, ch] = false;
                    return MozaPedalHapticsProtocol.BuildDisableFrame(_device.Addressing, pedal, slot);
                }

                double gain = _gain[p, ch];
                double freq = _freq[p, ch];
                _sentOn[p, ch] = true;
                _sentStrength[p, ch] = MozaPedalHapticsProtocol.EncodeStrength(gain);
                _sentFreq[p, ch] = MozaPedalHapticsProtocol.EncodeFrequency(freq);
                return MozaPedalHapticsProtocol.BuildSetFrame(
                    _device.Addressing, pedal, slot,
                    enable: true,
                    durationMs: MozaPedalHapticsProtocol.StreamDurationMs,
                    freqHz: freq, strength01: gain);
            }
        }

        // Most urgent slot, oldest send first within a class.
        private Due PickLocked(long now, bool shuttingDown, out int pick)
        {
            pick = -1;
            var best = Due.None;
            long bestSent = long.MaxValue;
            for (int i = 0; i < Total; i++)
            {
                int p = i / Channels;
                int ch = i % Channels;
                var due = ClassifyLocked(p, ch, now, shuttingDown);
                if (due == Due.None) continue;
                if (due > best || (due == best && _sentTicks[p, ch] < bestSent))
                {
                    best = due;
                    bestSent = _sentTicks[p, ch];
                    pick = i;
                }
            }
            return best;
        }

        private Due ClassifyLocked(int p, int ch, long now, bool shuttingDown)
        {
            bool silent = shuttingDown
                || now - _lastPostTicks[p] > StaleTicks
                || _gain[p, ch] <= MinGain;

            if (!_sentOn[p, ch])
                return silent ? Due.None : Due.Enable;
            if (silent)
                return Due.Disable;

            long since = now - _sentTicks[p, ch];
            if (since >= ExpiringTicks) return Due.Expiring;
            if (since >= RefreshTicks) return Due.Update;
            if (since >= UpdateTicks
                && (MozaPedalHapticsProtocol.EncodeStrength(_gain[p, ch]) != _sentStrength[p, ch]
                    || MozaPedalHapticsProtocol.EncodeFrequency(_freq[p, ch]) != _sentFreq[p, ch]))
                return Due.Update;
            return Due.None;
        }

        /// <summary>
        /// Stop producing frames and silence every oscillator still running. The
        /// disables go out on the FIFO because the caller is about to detach this
        /// source; each slot is disabled individually — stopping one leaves the
        /// rest of that pedal's slots running.
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
                    for (int ch = 0; ch < Channels; ch++)
                    {
                        if (!_sentOn[p, ch]) continue;
                        _sentOn[p, ch] = false;
                        disables.Add(MozaPedalHapticsProtocol.BuildDisableFrame(
                            _device.Addressing,
                            MozaPedalHapticsProtocol.PedalForIndex(p),
                            MozaPedalHapticsProtocol.SlotForChannel(ch)));
                    }
                }
            }

            foreach (var frame in disables)
                _device.SendFrame(frame);
        }
    }
}
