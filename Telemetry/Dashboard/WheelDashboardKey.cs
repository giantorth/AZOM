using System;
using System.Collections.Generic;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// <c>wheel:&lt;name&gt;</c> — profile key for a wheel-resident dashboard, keyed by
    /// its slot-table name (<see cref="WheelDashboardEntry.SlotName"/>). Earlier builds
    /// keyed on the configJson <c>id</c>, which the wheel reissues on every upload, so a
    /// re-uploaded dashboard orphaned the profile's binding and its channel mappings.
    /// <see cref="Resolve"/> still accepts those legacy keys so they can be migrated.
    /// </summary>
    public static class WheelDashboardKey
    {
        public const string Prefix = "wheel:";

        public static bool IsWheelKey(string? key)
            => key != null && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        public static string For(WheelDashboardEntry entry) => Prefix + entry.SlotName;

        /// <summary>Entry for <paramref name="key"/>: by name first, then by legacy id.
        /// Null when the key is not a wheel key or nothing on the wheel matches.</summary>
        public static WheelDashboardEntry? Resolve(WheelDashboardState? state, string? key, bool includeDisabled = false)
        {
            if (state == null || !IsWheelKey(key)) return null;
            string ident = key!.Substring(Prefix.Length);
            if (ident.Length == 0) return null;

            WheelDashboardEntry? byId = null;
            var byName = Find(state.EnabledDashboards, ident, ref byId);
            if (byName == null && includeDisabled)
                byName = Find(state.DisabledDashboards, ident, ref byId);
            return byName ?? byId;
        }

        private static WheelDashboardEntry? Find(IReadOnlyList<WheelDashboardEntry>? entries, string ident, ref WheelDashboardEntry? byId)
        {
            if (entries == null) return null;
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (e.MatchesName(ident)) return e;
                if (byId == null && !string.IsNullOrEmpty(e.Id)
                    && string.Equals(e.Id, ident, StringComparison.OrdinalIgnoreCase))
                    byId = e;
            }
            return null;
        }
    }
}
