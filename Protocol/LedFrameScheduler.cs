using System;
using System.Collections.Generic;
using System.Threading;

namespace MozaPlugin.Protocol
{
    /// <summary>Live LED zones on one connection. Declaration order is the tie-break priority.</summary>
    public enum LedZone
    {
        WheelRpm = 0,
        WheelButtons = 1,
        WheelKnobs = 2,
        BaseStrip0 = 3,
        BaseStrip1 = 4,
    }

    /// <summary>Payload shape of a zone's bitmask write.</summary>
    public enum LedMaskEncoding
    {
        /// <summary>active(u32 LE) + window(u32 LE) — wheel <c>1A [G]</c>.</summary>
        ActiveWindowLe8,
        /// <summary>active(u32 LE) — base ambient <c>1B [S]</c>.</summary>
        ActiveLe4,
        /// <summary>active as a big-endian int setting — old-protocol <c>41 FD DE</c>.</summary>
        ActiveInt,
    }

    /// <summary>
    /// Wire shape of one zone: where its colour and bitmask writes go and how dark LEDs
    /// are expressed. Compared by value, so callers may rebuild it per frame.
    /// </summary>
    public sealed class LedZoneLayout
    {
        public LedZoneLayout(MozaCommand colorCommand, byte deviceId, byte[] ledIndex,
            MozaCommand[] maskCommands, LedMaskEncoding[] maskEncodings,
            bool offViaMask, bool maskWithColors, bool sparseColors, int lapseMs)
        {
            ColorCommand = colorCommand;
            DeviceId = deviceId;
            LedIndex = ledIndex;
            MaskCommands = maskCommands;
            MaskEncodings = maskEncodings;
            OffViaMask = offViaMask;
            MaskWithColors = maskWithColors;
            SparseColors = sparseColors;
            LapseMs = lapseMs;
        }

        public MozaCommand ColorCommand { get; }
        public byte DeviceId { get; }
        /// <summary>Protocol LED index per slot; also the slot's bit in the active mask.</summary>
        public byte[] LedIndex { get; }
        public MozaCommand[] MaskCommands { get; }
        public LedMaskEncoding[] MaskEncodings { get; }
        /// <summary>An LED outside the active mask is dark whatever its colour, so its
        /// colour is only written once it lights.</summary>
        public bool OffViaMask { get; }
        /// <summary>Every colour write must be followed by the bitmask (the knob ring only
        /// re-renders on a bitmask write).</summary>
        public bool MaskWithColors { get; }
        /// <summary>Write only the changed LEDs; otherwise any change rewrites the whole zone.</summary>
        public bool SparseColors { get; }
        /// <summary>Bitmask silence after which the firmware may have dropped the live
        /// frame, so the next write rewrites every colour. 0 = never.</summary>
        public int LapseMs { get; }

        public int Count => LedIndex.Length;

        internal bool SameAs(LedZoneLayout? o)
        {
            if (o == null) return false;
            if (ReferenceEquals(this, o)) return true;
            if (!ReferenceEquals(ColorCommand, o.ColorCommand) || DeviceId != o.DeviceId
                || OffViaMask != o.OffViaMask || MaskWithColors != o.MaskWithColors
                || SparseColors != o.SparseColors || LapseMs != o.LapseMs
                || LedIndex.Length != o.LedIndex.Length || MaskCommands.Length != o.MaskCommands.Length)
                return false;
            for (int i = 0; i < LedIndex.Length; i++)
                if (LedIndex[i] != o.LedIndex[i]) return false;
            for (int i = 0; i < MaskCommands.Length; i++)
                if (!ReferenceEquals(MaskCommands[i], o.MaskCommands[i]) || MaskEncodings[i] != o.MaskEncodings[i])
                    return false;
            return true;
        }
    }

    /// <summary>
    /// Latest-state lane for live LED writes. Producers publish the colours + bitmask a
    /// zone should show; the write loop pulls one frame at a time when its paced gate
    /// opens and builds it from the state at that moment, diffed against what the wheel
    /// was actually sent. Nothing queues, so a frame can never be older than one pacing
    /// slot — the old FIFO path kept every frame and fell tens of seconds behind once
    /// demand passed the 4 ms drain rate (bundle M3D9WHJE).
    ///
    /// <para>Per zone, colours always go out before the bitmask that lights them: a turn
    /// writes the changed colours, re-checks for LEDs that changed meanwhile, then writes
    /// the bitmask. Across zones the highest-priority zone (<see cref="LedZone"/> order)
    /// goes next, with a starvation guard, so under load the low-priority zones update
    /// less often instead of everything lagging together.</para>
    ///
    /// <para>Wheel firmware drops a group's live frame ~1 s after its last bitmask
    /// (docs/protocol/leds/color-commands.md). A zone whose bitmask lapsed past its
    /// layout's <see cref="LedZoneLayout.LapseMs"/> gets every colour rewritten, since the
    /// wheel may no longer hold them.</para>
    /// </summary>
    public sealed class LedFrameScheduler
    {
        private const int EntriesPerChunk = 5;
        // Nominal stuffed size of a live LED frame, for the write budget check.
        internal const int NominalFrameBytes = 28;
        // A zone waiting this long jumps the priority order. A full RPM turn is ~5 frames
        // (~20 ms at 4 ms pacing), doubled when alternating with the FIFO.
        private const long StarveMs = 50;

        private sealed class Zone
        {
            public LedZoneLayout? Layout;
            public bool HasWant;
            public int[] Want = Array.Empty<int>();
            public int WantActive, WantWindow;
            public int[] Sent = Array.Empty<int>();
            public bool[] SentValid = Array.Empty<bool>();
            public bool MaskSentValid;
            public int SentActive, SentWindow;
            public long LastMaskTicks;
            public bool ForceColors, ForceMask;
            // Set by PublishMaskOnly: the next Publish rewrites every colour.
            public bool ColorsStale;
            public long PendingSinceTicks;

            public bool InTurn;
            public readonly List<int> TurnSlots = new List<int>(32);
            public int TurnPos;
            public bool TurnMask, TurnRechecked;
            public int TurnMaskIdx, TurnActive, TurnWindow;

            public long Frames, ColorEntries, Masks;
            public double LastWaitMs, MaxWaitMs;
        }

        private readonly object _lock = new object();
        private readonly Zone[] _zones;
        private Zone? _current;
        private int _workHint;

        public LedFrameScheduler()
        {
            _zones = new Zone[Enum.GetValues(typeof(LedZone)).Length];
            for (int i = 0; i < _zones.Length; i++) _zones[i] = new Zone();
        }

        /// <summary>Cheap pre-check for the write loop; may be stale-true, never stale-false.</summary>
        public bool MayHaveWork => Volatile.Read(ref _workHint) != 0;

        private static long Now => System.Diagnostics.Stopwatch.GetTimestamp();
        private static long MsToTicks(long ms) => ms * System.Diagnostics.Stopwatch.Frequency / 1000;
        private static double TicksToMs(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// Set what a zone should show. <paramref name="rgb"/> is 0xRRGGBB per layout slot;
        /// a slot is lit iff its <see cref="LedZoneLayout.LedIndex"/> bit is in
        /// <paramref name="activeMask"/>. Unchanged state produces no wire traffic.
        /// </summary>
        public void Publish(LedZone zone, LedZoneLayout layout, int[] rgb, int activeMask, int windowMask,
            bool forceColors = false, bool forceMask = false)
        {
            lock (_lock)
            {
                var z = _zones[(int)zone];
                EnsureLayout(z, layout);
                int n = Math.Min(rgb.Length, z.Want.Length);
                Array.Copy(rgb, z.Want, n);
                for (int i = n; i < z.Want.Length; i++) z.Want[i] = 0;
                z.WantActive = activeMask;
                z.WantWindow = windowMask;
                z.HasWant = true;
                if (z.ColorsStale)
                {
                    z.ColorsStale = false;
                    forceColors = true;
                }
                z.ForceColors |= forceColors;
                z.ForceMask |= forceMask;
                NotePending(z);
            }
        }

        /// <summary>
        /// Change only the bitmask and drop any colour change not yet written — for
        /// handing a group back to its stored render, where a colour write would
        /// repaint the frame buffer the firmware is about to show.
        /// </summary>
        public void PublishMaskOnly(LedZone zone, LedZoneLayout layout, int activeMask, int windowMask)
        {
            lock (_lock)
            {
                var z = _zones[(int)zone];
                EnsureLayout(z, layout);
                z.HasWant = true;
                for (int i = 0; i < z.Want.Length; i++)
                {
                    if (z.SentValid[i]) z.Want[i] = z.Sent[i];
                    else { z.Sent[i] = z.Want[i]; z.SentValid[i] = true; }
                }
                // The firmware shows its stored render from here, so whatever re-engages
                // the zone rewrites every colour.
                z.ColorsStale = true;
                z.ForceColors = false;
                z.WantActive = activeMask;
                z.WantWindow = windowMask;
                // A turn in flight would still write its snapshot's colours.
                if (z.InTurn)
                {
                    z.TurnSlots.Clear();
                    z.TurnPos = 0;
                }
                NotePending(z);
            }
        }

        /// <summary>Keepalive: re-send the zone's bitmask, and its colours unless
        /// <paramref name="colors"/> is false. No-op for a zone never published.</summary>
        public void RequestRefresh(LedZone zone, bool colors)
        {
            lock (_lock)
            {
                var z = _zones[(int)zone];
                if (!z.HasWant) return;
                z.ForceMask = true;
                if (colors) z.ForceColors = true;
                NotePending(z);
            }
        }

        /// <summary>An out-of-band write repainted the zone: forget what was sent so the
        /// current state is rewritten in full.</summary>
        public void Invalidate(LedZone zone)
        {
            lock (_lock)
            {
                var z = _zones[(int)zone];
                Array.Clear(z.SentValid, 0, z.SentValid.Length);
                z.MaskSentValid = false;
                if (z.HasWant) NotePending(z);
            }
        }

        /// <summary>
        /// The zone was cleared outside this lane (bitmask written directly). Record that
        /// bitmask as the wheel's state and drop pending work, so nothing re-lights the
        /// zone until a producer publishes again. Colours are forgotten: the next lit
        /// frame rewrites them in full.
        /// </summary>
        public void MarkCleared(LedZone zone, int activeMask, int windowMask)
        {
            lock (_lock)
            {
                var z = _zones[(int)zone];
                if (ReferenceEquals(_current, z)) _current = null;
                z.InTurn = false;
                z.ForceColors = z.ForceMask = false;
                Array.Clear(z.SentValid, 0, z.SentValid.Length);
                z.WantActive = z.SentActive = activeMask;
                z.WantWindow = z.SentWindow = windowMask;
                z.MaskSentValid = true;
                z.LastMaskTicks = Now;
                z.PendingSinceTicks = 0;
            }
        }

        /// <summary>Forget a zone entirely (its device went away); the next publish starts fresh.</summary>
        public void Drop(LedZone zone)
        {
            lock (_lock) DropLocked(_zones[(int)zone]);
        }

        /// <summary>Drop every zone's state and pending work (port closed / reopened / flushed).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                foreach (var z in _zones) DropLocked(z);
                Volatile.Write(ref _workHint, 0);
            }
        }

        private void DropLocked(Zone z)
        {
            if (ReferenceEquals(_current, z)) _current = null;
            z.Layout = null;
            z.HasWant = false;
            z.InTurn = false;
            z.ForceColors = z.ForceMask = z.ColorsStale = false;
            z.MaskSentValid = false;
            z.PendingSinceTicks = 0;
        }

        /// <summary>
        /// Next frame to write, built from the current state, or null when every zone is
        /// in sync. Called by the write loop once its pacing gate is open.
        /// </summary>
        public byte[]? TryTakeFrame()
        {
            lock (_lock)
            {
                long now = Now;
                if (_current != null && _current.InTurn)
                {
                    var msg = TakeFromTurn(_current, now);
                    if (msg != null) return msg;
                }
                _current = null;

                // Highest-priority zone with work goes next, unless some zone has waited
                // past StarveMs — then the longest-waiting one does.
                Zone? best = null;
                Zone? starved = null;
                long starveTicks = MsToTicks(StarveMs);
                for (int i = 0; i < _zones.Length; i++)
                {
                    var z = _zones[i];
                    if (!HasWork(z, now)) continue;
                    if (z.PendingSinceTicks == 0) z.PendingSinceTicks = now;
                    if (best == null) best = z;
                    if (now - z.PendingSinceTicks > starveTicks
                        && (starved == null || z.PendingSinceTicks < starved.PendingSinceTicks))
                        starved = z;
                }
                if (starved != null) best = starved;
                if (best == null)
                {
                    Volatile.Write(ref _workHint, 0);
                    return null;
                }

                double waited = TicksToMs(now - best.PendingSinceTicks);
                best.LastWaitMs = waited;
                if (waited > best.MaxWaitMs) best.MaxWaitMs = waited;
                best.PendingSinceTicks = 0;
                StartTurn(best, now);
                _current = best;
                var first = TakeFromTurn(best, now);
                if (first == null) _current = null;
                return first;
            }
        }

        // A different wire shape (model swap, map change) invalidates everything sent.
        private void EnsureLayout(Zone z, LedZoneLayout layout)
        {
            if (layout.SameAs(z.Layout)) return;
            if (ReferenceEquals(_current, z)) _current = null;
            z.InTurn = false;
            z.Layout = layout;
            z.Want = new int[layout.Count];
            z.Sent = new int[layout.Count];
            z.SentValid = new bool[layout.Count];
            z.MaskSentValid = false;
            z.ColorsStale = false;
        }

        private void NotePending(Zone z)
        {
            if (z.PendingSinceTicks == 0) z.PendingSinceTicks = Now;
            Volatile.Write(ref _workHint, 1);
        }

        private static bool IsLit(Zone z, int slot) => (z.WantActive & (1 << z.Layout!.LedIndex[slot])) != 0;

        private static bool ColorDirty(Zone z, int slot) => !z.SentValid[slot] || z.Sent[slot] != z.Want[slot];

        // A dirty colour that is visible now — dark LEDs on an OffViaMask zone wait until they light.
        private static bool NeedsColor(Zone z, int slot)
            => ColorDirty(z, slot) && (!z.Layout!.OffViaMask || IsLit(z, slot));

        private static bool MaskDirty(Zone z)
            => !z.MaskSentValid || z.SentActive != z.WantActive || z.SentWindow != z.WantWindow;

        private static bool Lapsed(Zone z, long now)
            => z.Layout!.LapseMs > 0 && z.MaskSentValid && now - z.LastMaskTicks > MsToTicks(z.Layout.LapseMs);

        private static bool HasWork(Zone z, long now)
        {
            if (!z.HasWant || z.Layout == null) return false;
            if (z.InTurn || z.ForceColors || z.ForceMask || MaskDirty(z)) return true;
            for (int i = 0; i < z.Want.Length; i++)
                if (NeedsColor(z, i)) return true;
            return false;
        }

        private static void StartTurn(Zone z, long now)
        {
            bool full = z.ForceColors || Lapsed(z, now);
            z.TurnSlots.Clear();
            bool anyNeeded = false;
            for (int i = 0; i < z.Want.Length; i++)
                if (NeedsColor(z, i)) { anyNeeded = true; break; }
            if (full || (anyNeeded && !z.Layout!.SparseColors))
            {
                for (int i = 0; i < z.Want.Length; i++) z.TurnSlots.Add(i);
            }
            else
            {
                for (int i = 0; i < z.Want.Length; i++)
                    if (NeedsColor(z, i)) z.TurnSlots.Add(i);
            }
            z.TurnMask = full || z.ForceMask || MaskDirty(z)
                         || (z.Layout!.MaskWithColors && z.TurnSlots.Count > 0);
            z.ForceColors = false;
            z.ForceMask = false;
            z.TurnPos = 0;
            z.TurnMaskIdx = 0;
            z.TurnRechecked = false;
            z.InTurn = true;
        }

        private static byte[]? TakeFromTurn(Zone z, long now)
        {
            var layout = z.Layout!;
            if (z.TurnPos < z.TurnSlots.Count)
                return BuildChunk(z, layout);

            if (z.TurnMask)
            {
                if (z.TurnMaskIdx == 0 && !z.TurnRechecked)
                {
                    // Colours that changed while this turn was writing go out before
                    // the bitmask (every slot so far is written, so append them all).
                    z.TurnRechecked = true;
                    for (int i = 0; i < z.Want.Length; i++)
                        if (NeedsColor(z, i)) z.TurnSlots.Add(i);
                    if (z.TurnPos < z.TurnSlots.Count)
                        return BuildChunk(z, layout);
                }
                if (z.TurnMaskIdx == 0)
                {
                    z.TurnActive = z.WantActive;
                    z.TurnWindow = z.WantWindow;
                    // Never light a dark LED whose colour hasn't landed: it would show a
                    // stale colour. It lights on the next turn, after its colour write.
                    if (layout.OffViaMask)
                        for (int i = 0; i < z.Want.Length; i++)
                        {
                            int bit = 1 << layout.LedIndex[i];
                            bool wasLit = z.MaskSentValid && (z.SentActive & bit) != 0;
                            if ((z.TurnActive & bit) != 0 && !wasLit && ColorDirty(z, i))
                                z.TurnActive &= ~bit;
                        }
                }
                var msg = BuildMask(layout, z.TurnMaskIdx, z.TurnActive, z.TurnWindow);
                z.TurnMaskIdx++;
                if (z.TurnMaskIdx >= layout.MaskCommands.Length)
                {
                    z.TurnMask = false;
                    z.SentActive = z.TurnActive;
                    z.SentWindow = z.TurnWindow;
                    z.MaskSentValid = true;
                    z.LastMaskTicks = now;
                    z.Masks++;
                }
                if (msg == null) return TakeFromTurn(z, now);
                z.Frames++;
                return msg;
            }

            z.InTurn = false;
            return null;
        }

        private static byte[]? BuildChunk(Zone z, LedZoneLayout layout)
        {
            int n = Math.Min(EntriesPerChunk, z.TurnSlots.Count - z.TurnPos);
            var payload = new byte[n * 4];
            for (int k = 0; k < n; k++)
            {
                int slot = z.TurnSlots[z.TurnPos + k];
                int c = z.Want[slot];
                int o = k * 4;
                payload[o] = layout.LedIndex[slot];
                payload[o + 1] = (byte)(c >> 16);
                payload[o + 2] = (byte)(c >> 8);
                payload[o + 3] = (byte)c;
                z.Sent[slot] = c;
                z.SentValid[slot] = true;
            }
            z.TurnPos += n;
            z.Frames++;
            z.ColorEntries += n;
            return layout.ColorCommand.BuildWriteMessage(layout.DeviceId, payload);
        }

        private static byte[]? BuildMask(LedZoneLayout layout, int idx, int active, int window)
        {
            var cmd = layout.MaskCommands[idx];
            switch (layout.MaskEncodings[idx])
            {
                case LedMaskEncoding.ActiveWindowLe8:
                    return cmd.BuildWriteMessage(layout.DeviceId, new[]
                    {
                        (byte)active, (byte)(active >> 8), (byte)(active >> 16), (byte)(active >> 24),
                        (byte)window, (byte)(window >> 8), (byte)(window >> 16), (byte)(window >> 24),
                    });
                case LedMaskEncoding.ActiveLe4:
                    return cmd.BuildWriteMessage(layout.DeviceId, new[]
                    {
                        (byte)active, (byte)(active >> 8), (byte)(active >> 16), (byte)(active >> 24),
                    });
                default:
                    return cmd.BuildWriteInt(layout.DeviceId, active);
            }
        }

        /// <summary>Per-zone counters for the Diagnostics tab.</summary>
        public IReadOnlyList<(LedZone Zone, long Frames, long ColorEntries, long Masks,
                              double LastWaitMs, double MaxWaitMs)> Snapshot()
        {
            var list = new List<(LedZone, long, long, long, double, double)>();
            lock (_lock)
            {
                for (int i = 0; i < _zones.Length; i++)
                {
                    var z = _zones[i];
                    if (z.Frames == 0 && !z.HasWant) continue;
                    list.Add(((LedZone)i, z.Frames, z.ColorEntries, z.Masks, z.LastWaitMs, z.MaxWaitMs));
                }
            }
            return list;
        }
    }
}
