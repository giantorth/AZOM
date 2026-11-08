using System;
using System.Threading.Tasks;
using System.Windows;
using MozaPlugin.Telemetry;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Stored formula string ↔ SimHub's formula editor. Stored form: a bare
    /// property path, an NCalc expression, or <c>js:</c>-prefixed JavaScript
    /// (what <see cref="SimHubPropertyResolver.ResolveAsDouble(string)"/> reads).
    /// Same conventions as MBoosterCustomEffectRow / ChannelMappingRow.
    /// </summary>
    internal static class FormulaEditing
    {
        /// <summary>Open SimHub's formula editor on a stored formula.</summary>
        /// <returns>The new stored formula ("" when cleared), or null when cancelled or unavailable.</returns>
        public static async Task<string?> EditAsync(DependencyObject owner, NCalcEngineBase? engine, string? stored, string name)
        {
            if (engine == null)
            {
                MozaLog.Warn("[AZOM] Formula editor unavailable (SimHub engine not loaded)");
                return null;
            }
            var data = new DashboardBindingData
            {
                Formula = ToExpression(stored),
                TargetPropertyName = name,
                TargetType = typeof(double),
            };
            data.Mode = string.IsNullOrWhiteSpace(data.Formula.Expression) ? BindingMode.None : BindingMode.Formula;
            try
            {
                var editor = new BindingEditor(engine) { DataContext = data };
                var result = await editor.ShowDialogWindowAsync(owner);
                if ((int)result != 1) return null; // not OK
                return data.Mode == BindingMode.Formula ? Serialize(data.Formula) : "";
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] Formula editor failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>A stored formula as an editor expression; a bare path is wrapped as <c>[path]</c>.</summary>
        public static ExpressionValue ToExpression(string? stored)
        {
            var ev = new ExpressionValue();
            var s = (stored ?? "").Trim();
            if (s.Length == 0) return ev;
            if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
            {
                ev.UseJavascript = true;
                ev.Expression = s.Substring(3);
                return ev;
            }
            ev.Expression = NCalcExpressionEvaluator.LooksLikeExpression(s) ? s : "[" + s + "]";
            return ev;
        }

        /// <summary>An editor expression as a stored formula; a sole <c>[property]</c> is unwrapped.</summary>
        public static string Serialize(ExpressionValue? ev)
        {
            var expr = (ev?.Expression ?? "").Trim();
            if (expr.Length == 0) return "";
            if (ev!.UseJavascript) return "js:" + expr;
            if (expr.Length >= 2 && expr[0] == '[' && expr[expr.Length - 1] == ']')
            {
                var inner = expr.Substring(1, expr.Length - 2);
                if (inner.IndexOf('[') < 0 && inner.IndexOf(']') < 0
                    && !NCalcExpressionEvaluator.LooksLikeExpression(inner))
                    return inner;
            }
            return expr;
        }
    }
}
