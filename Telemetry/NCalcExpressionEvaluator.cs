using System;
using System.Collections.Concurrent;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Evaluates a channel mapping that is a SimHub <b>formula</b> rather than a
    /// plain property path — the same <c>[property]</c> NCalc (and <c>js:</c>
    /// JavaScript) language used by SimHub dashboards. We reuse SimHub's own
    /// <see cref="NCalcEngineBase"/> so the dialect, function set, and property
    /// resolution match dashboards exactly (no bundled NCalc, no version drift).
    /// See <c>docs/ncalc-channel-mapping.md</c>.
    /// </summary>
    /// <remarks>
    /// The engine is created lazily inside a try/catch: if a future SimHub build
    /// removes/renames the type, expressions fall back to their default value and
    /// the failure is logged once — plain property paths are unaffected because
    /// <see cref="SimHubPropertyResolver"/> only calls in here for strings that
    /// <see cref="LooksLikeExpression"/> identifies as formulas.
    /// </remarks>
    internal sealed class NCalcExpressionEvaluator
    {
        private readonly object _gate = new object();
        // Compiled ExpressionValue objects reused across frames (ExpressionValue
        // caches its parse internally; recreating it per tick would defeat that
        // and churn allocations). Keyed by the raw stored string.
        private readonly ConcurrentDictionary<string, ExpressionValue> _compiled
            = new ConcurrentDictionary<string, ExpressionValue>(StringComparer.Ordinal);
        // The plugin's real formula set is a few dozen strings. A source that
        // synthesises expressions per call (the LFE test sweep bakes the sweeping
        // RPM into the string) would otherwise grow this forever; past the cap
        // the cache is simply dropped and the live formulas recompile once.
        private const int MaxCompiled = 256;

        private NCalcEngineBase? _engine;
        private bool _engineInitFailed;
        private bool _evalWarned;

        /// <summary>
        /// The shared formula engine, lazily constructed. Bound to
        /// <c>PluginManager.Instance</c> internally by SimHub. Returns null only
        /// if construction threw (logged once). The channel-mapper's advanced
        /// editor passes this to SimHub's <c>BindingEditor</c> dialog so its
        /// preview uses the same engine as the live telemetry path.
        /// </summary>
        public NCalcEngineBase? Engine
        {
            get
            {
                if (_engine != null || _engineInitFailed) return _engine;
                lock (_gate)
                {
                    if (_engine == null && !_engineInitFailed)
                    {
                        try { _engine = new NCalcEngineBase(); }
                        catch (Exception ex)
                        {
                            _engineInitFailed = true;
                            MozaLog.Warn("[AZOM] NCalc engine init failed; channel-mapping "
                                + "formulas disabled (plain property paths still work): " + ex.Message);
                        }
                    }
                }
                return _engine;
            }
        }

        /// <summary>
        /// True when <paramref name="s"/> is a SimHub formula rather than a plain
        /// property path. A formula either starts with <c>js:</c> or contains the
        /// NCalc property-bracket / operator characters; a dotted property path
        /// (<c>DataCorePlugin.GameData.SpeedKmh</c>) contains none of these, so it
        /// stays on the resolver's fast <c>GetPropertyValue</c> path. No shipped
        /// default in <c>Data/Telemetry.json</c> contains these characters, so
        /// existing mappings are never misread as formulas.
        /// </summary>
        public static bool LooksLikeExpression(string? s)
        {
            if (s == null || s.Length == 0) return false;
            if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase)) return true;
            // '-' and '.' are intentionally excluded: property paths use '.', and a
            // bare arithmetic formula with no [property] reference is meaningless as
            // a channel mapping. Any real formula referencing telemetry uses '['.
            for (int i = 0; i < s.Length; i++)
            {
                switch (s[i])
                {
                    case '[': case ']':
                    case '+': case '*': case '/': case '%':
                    case '(': case ')':
                    case '<': case '>': case '=': case '!':
                    case '&': case '|': case '?': case ',':
                    case '\'':
                        return true;
                }
            }
            return false;
        }

        /// <summary>Evaluate a formula to a double; returns 0 on any failure.</summary>
        public double EvalDouble(string expression)
        {
            var ev = Compile(expression);
            var engine = Engine;
            if (ev == null || engine == null) return 0.0;
            try
            {
                lock (_gate) { return engine.ParseValueOrDefault(ev, 0.0); }
            }
            catch (Exception ex) { WarnEvalOnce(expression, ex); return 0.0; }
        }

        /// <summary>Evaluate a formula to a string; returns null on any failure.</summary>
        public string? EvalString(string expression)
        {
            var ev = Compile(expression);
            var engine = Engine;
            if (ev == null || engine == null) return null;
            try
            {
                lock (_gate) { return engine.ParseValueOrDefault(ev, (string?)null); }
            }
            catch (Exception ex) { WarnEvalOnce(expression, ex); return null; }
        }

        /// <summary>
        /// Evaluate for UI display — returns a string when the formula yields one,
        /// else the numeric result boxed, else null. Mirrors how the resolver's
        /// double/string paths split.
        /// </summary>
        public object EvalForDisplay(string expression)
        {
            var s = EvalString(expression);
            if (s != null) return s;
            return EvalDouble(expression);
        }

        private ExpressionValue? Compile(string expression)
        {
            if (string.IsNullOrEmpty(expression)) return null;
            if (_compiled.TryGetValue(expression, out var ev)) return ev;
            try
            {
                // Implicit string->ExpressionValue: defaults to NCalc, auto-selects
                // JavaScript on a "js:" prefix — exactly SimHub's dashboard semantics.
                ev = (ExpressionValue)expression;
            }
            catch (Exception ex) { WarnEvalOnce(expression, ex); return null; }
            if (_compiled.Count >= MaxCompiled) _compiled.Clear();
            _compiled[expression] = ev;
            return ev;
        }

        private void WarnEvalOnce(string expression, Exception ex)
        {
            if (_evalWarned) return;
            _evalWarned = true;
            MozaLog.Warn($"[AZOM] channel-mapping formula evaluation failed (first only): "
                + $"\"{expression}\" — {ex.Message}");
        }
    }
}
