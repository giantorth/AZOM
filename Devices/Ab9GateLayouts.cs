using System.Collections.Generic;

namespace MozaPlugin.Devices
{
    /// <summary>One gate end: column (0-based, left to right), top or bottom row, and its gear.</summary>
    internal readonly struct Ab9GateSlot
    {
        public readonly int Column;
        public readonly bool Top;
        public readonly int Gear; // Ab9Gear.Reverse or 1..8

        public Ab9GateSlot(int column, bool top, int gear)
        {
            Column = column;
            Top = top;
            Gear = gear;
        }
    }

    /// <summary>Gate geometry per <see cref="Ab9Mode"/>, matching PitHouse's layout icons.</summary>
    internal static class Ab9GateLayouts
    {
        private const int R = Ab9Gear.Reverse;

        // Firmware 1.1.6.2 moved 5+R Layout 2 to R/2/4 over 1/3/5.
        private static readonly Ab9GateSlot[] FivePlusR_L2_Revised = Build(new[] { R, 2, 4 }, new[] { 1, 3, 5 });

        private static readonly Dictionary<Ab9Mode, Ab9GateSlot[]> Layouts = new Dictionary<Ab9Mode, Ab9GateSlot[]>
        {
            [Ab9Mode.FivePlusR_L1]  = Build(new[] { 1, 3, 5 },    new[] { 2, 4, R }),
            [Ab9Mode.FivePlusR_L2]  = Build(new[] { 1, 3, R },    new[] { 2, 4, 5 }),
            [Ab9Mode.RPlusFive]     = Build(new[] { R, 1, 3, 5 }, new[] { 0, 2, 4, 0 }),
            [Ab9Mode.RPlusSix]      = Build(new[] { R, 1, 3, 5 }, new[] { 0, 2, 4, 6 }),
            [Ab9Mode.SixPlusR_L1]   = Build(new[] { 1, 3, 5, 0 }, new[] { 2, 4, 6, R }),
            [Ab9Mode.SixPlusR_L2]   = Build(new[] { 1, 3, 5, R }, new[] { 2, 4, 6, 0 }),
            [Ab9Mode.SevenPlusR_L1] = Build(new[] { 1, 3, 5, 7 }, new[] { 2, 4, 6, R }),
            [Ab9Mode.SevenPlusR_L2] = Build(new[] { 1, 3, 5, R }, new[] { 2, 4, 6, 7 }),
            [Ab9Mode.RPlusEight]    = Build(new[] { R, 1, 3, 5, 7 }, new[] { 0, 2, 4, 6, 8 }),
        };

        /// <summary>Slots for an H-pattern layout; null for <see cref="Ab9Mode.Sequential"/> or an unknown value.
        /// <paramref name="revisedFw"/>: the device runs firmware 1.1.6.2 or later.</summary>
        public static Ab9GateSlot[]? Get(Ab9Mode mode, bool revisedFw)
        {
            if (revisedFw && mode == Ab9Mode.FivePlusR_L2) return FivePlusR_L2_Revised;
            return Layouts.TryGetValue(mode, out var slots) ? slots : null;
        }

        // 0 = no gate end in that column/row.
        private static Ab9GateSlot[] Build(int[] top, int[] bottom)
        {
            var slots = new List<Ab9GateSlot>();
            for (int c = 0; c < top.Length; c++)
                if (top[c] != 0) slots.Add(new Ab9GateSlot(c, true, top[c]));
            for (int c = 0; c < bottom.Length; c++)
                if (bottom[c] != 0) slots.Add(new Ab9GateSlot(c, false, bottom[c]));
            return slots.ToArray();
        }
    }
}
