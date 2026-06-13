using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Frames;
using SimHub.Plugins;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Resolves SimHub property paths to numeric/string values for the telemetry
    /// sender. Paths starting with <c>@internal/</c> are plugin-computed (e.g.
    /// live wheel angle) and bypass SimHub.
    /// </summary>
    internal sealed class SimHubPropertyResolver
    {
        private readonly PluginManager _pluginManager;
        private readonly MozaData _data;
        private readonly MozaHidReader _hidReader;
        private bool _allPropertiesNamesWarned;

        public SimHubPropertyResolver(PluginManager pluginManager, MozaData data, MozaHidReader hidReader)
        {
            _pluginManager = pluginManager;
            _data = data;
            _hidReader = hidReader;
        }

        public double ResolveAsDouble(string path)
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith("@internal/", StringComparison.Ordinal))
                return ResolveInternalChannel(path);

            // A throwing property must not abort the whole frame tick (mirrors
            // the guard in ResolveAsString); an unresolvable channel reads 0.
            try
            {
                return PropertyCoercion.Coerce(_pluginManager?.GetPropertyValue(path), path);
            }
            catch { return 0.0; }
        }

        public string? ResolveAsString(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("@internal/", StringComparison.Ordinal))
            {
                var str = ResolveInternalStringChannel(path);
                if (str != null) return str;
                return ResolveInternalChannel(path)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            try
            {
                var v = _pluginManager?.GetPropertyValue(path);
                return v?.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// Snapshot of every property name SimHub currently exposes. Sorted
        /// case-insensitively, deduplicated. Falls back to
        /// <see cref="KnownSimHubProperties.Paths"/> when the live API isn't
        /// available (older SimHub builds, missing method, or exception).
        /// </summary>
        public IReadOnlyList<string> GetAllSimHubPropertyNames()
        {
            try
            {
                var pm = _pluginManager;
                if (pm != null)
                {
                    var mi = pm.GetType().GetMethod("GetAllPropertiesNames",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        var names = mi.Invoke(pm, null) as System.Collections.IEnumerable;
                        if (names != null)
                        {
                            // Dedup: SimHub registers properties under multiple plugins
                            // (DataCorePlugin / PersistantTracker overlap, Custom Series).
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var list = new List<string>(256);
                            foreach (var n in names)
                            {
                                if (n is string s && !string.IsNullOrEmpty(s) && seen.Add(s))
                                    list.Add(s);
                            }
                            list.Sort(StringComparer.OrdinalIgnoreCase);
                            return list;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_allPropertiesNamesWarned)
                {
                    _allPropertiesNamesWarned = true;
                    MozaLog.Warn("[AZOM] GetAllPropertiesNames failed; falling back to static list: " + ex.Message);
                }
            }
            return KnownSimHubProperties.Paths;
        }

        /// <summary>
        /// Resolve the raw value of a SimHub property for UI display. Returns null
        /// when the path is empty or unresolvable; <c>@internal/</c> paths return
        /// the resolved double so the UI shows live values.
        /// </summary>
        public object? GetValueForDisplay(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path!.StartsWith("@internal/", StringComparison.Ordinal))
                return ResolveInternalStringChannel(path) ?? (object)ResolveInternalChannel(path);
            try { return _pluginManager?.GetPropertyValue(path); }
            catch { return null; }
        }

        /// <summary>
        /// Plugin-computed channels whose value is a string rather than a
        /// number (the numeric counterpart is <see cref="ResolveInternalChannel"/>).
        /// Returns null for paths that are not string-valued so callers fall
        /// through to the numeric resolver.
        /// </summary>
        private string? ResolveInternalStringChannel(string path)
        {
            switch (path)
            {
                case "@internal/GameName":
                {
                    string? game = null;
                    try { game = _pluginManager?.GameName; }
                    catch { /* older SimHub / not yet running a game */ }
                    return Dashboard.GameNameMap.Resolve(game);
                }
                default:
                    return null;
            }
        }

        public double ResolveInternalChannel(string path)
        {
            switch (path)
            {
                case "@internal/SteeringWheelAngle":
                {
                    // Half-range ±maxAngleDeg/2 from the base's reported max-angle.
                    var hid = _hidReader;
                    int maxAngleDeg = _data?.MaxAngle * 2 ?? 0;
                    if (hid == null || maxAngleDeg <= 0) return 0.0;
                    return hid.GetCurrentAngleDegrees(maxAngleDeg);
                }
                // NOTE: there is deliberately no "@internal/TimeStamp" case.
                // v1/preset/* is the wheel-internal namespace — the firmware
                // supplies TimeStamp/CurrentTorque/SteeringWheelAngle to its own
                // dashboard engine, so DashboardProfileStore.IsWheelInternalPresetChannel
                // drops preset/* from every subscription and the host never
                // emits it. (Telemetry.json still maps it to @internal/TimeStamp,
                // but that mapping is never reached.)
                default:
                    return 0.0;
            }
        }

        /// <summary>
        /// Stable per-physical-wheel key: 24-char lowercase hex of the wheel's
        /// STM32 MCU UID. Empty when UID hasn't been read yet or is all zeros
        /// (both treated as "unknown wheel").
        /// </summary>
        public string CurrentWheelKey()
        {
            var uid = _data?.WheelMcuUid;
            if (uid == null || uid.Length != 12) return "";
            bool allZero = true;
            for (int i = 0; i < 12; i++) { if (uid[i] != 0) { allZero = false; break; } }
            if (allZero) return "";
            var sb = new StringBuilder(24);
            for (int i = 0; i < 12; i++) sb.Append(uid[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Convert a 100×-scaled integer temperature to the user's chosen unit.</summary>
        public double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return (_data?.UseFahrenheit ?? false) ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        }
    }
}
