using System;
using System.Globalization;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// The JavaScript formatters a converted dashboard uses.
    ///
    /// <para>The <c>Chain*</c> members are <b>lifted verbatim from factory dashboards</b>
    /// (<c>~/dashes/*/*.mzdash</c>) — they are the second step of a <c>methods</c> chain
    /// and operate on the magic variable <c>_result</c>. Using MOZA's own strings rather
    /// than equivalents of our own means the number, gear and lap-time rendering match
    /// what the wheel already ships, including their edge cases (NaN → <c>undefined</c>,
    /// float-min sentinel → <c>--:--.---</c>).</para>
    ///
    /// <para>The <c>Inline*</c> members do the same job on an arbitrary sub-expression,
    /// for NCalc's <c>format()</c> appearing partway through a larger formula.</para>
    /// </summary>
    public static class JsFormatters
    {
        /// <summary>Pass the value through unchanged. The most common second step in
        /// ground truth (287 of 633 bindings).</summary>
        public const string ChainIdentity = "_result";

        // MOZA's own prebinding library (:/elementMetaProperty in Dashboard Studio) uses
        // toFixed for every numeric readout it ships. Some community dashboards carry a
        // longer split-on-the-decimal-point variant instead, but that one TRUNCATES where
        // toFixed rounds — a visible difference on a speed or temperature readout — so the
        // vendor's own form is the one to emit.
        private const string ChainDecimalsTemplate =
            "((r=Number(_result))=>isNaN(r)?undefined:r.toFixed(__D__))()";

        /// <summary>Gear: 0 → <c>N</c>, −1 → <c>R</c>, positive → the number.</summary>
        public const string ChainGear =
            "((r=Number(_result))=>{if(isNaN(r))return _result;else if(r==0)return'N';"
            + "else if(r>0)return r.toString();else if(r==-1)return 'R';"
            + "else if(r<-1)return 'R'+Math.abs(r).toString();})()";

        /// <summary>Lap time as <c>mm:ss.mmm</c>. The float-min comparison is MOZA's own
        /// "no time set" sentinel check.</summary>
        public const string ChainLapTime =
            "((r=Math.abs(_result),f=(a,b)=>Math.floor(a).toString().padStart(b,'0'),"
            + "m=-3.4028234e+38,s=_result<0?'-':'')=>isNaN(r)?undefined:_result>m?"
            + "`${s}${f(r/60,2)}:${f(r%60,2)}.${f(r%1*1000,3)}`:'--:--.---')()";

        /// <summary>Lap/sector time as <c>m:ss.mmm</c> without a leading zero on minutes.</summary>
        public const string ChainLapTimeShort =
            "((r=Math.abs(_result),f=(a,b)=>Math.floor(a).toString().padStart(b,'0'),"
            + "m=-3.4028234e+38,s=_result<0?'-':'')=>isNaN(r)?undefined:_result>m?"
            + "`${s}${Math.floor(r/60)}:${f(r%60,2)}.${f(r%1*1000,3)}`:'-:--.---')()";

        /// <summary>Signed delta as <c>±s.mmm</c>.</summary>
        public const string ChainDelta =
            "((r=Number(_result))=>isNaN(r)?'--.---':(r<0?'-':'+')+Math.abs(r).toFixed(3))()";

        /// <summary>The chain step that renders a number with <paramref name="decimals"/>
        /// fixed decimal places.</summary>
        public static string ChainDecimals(int decimals)
            => ChainDecimalsTemplate.Replace("__D__",
                Math.Max(0, decimals).ToString(CultureInfo.InvariantCulture));

        // ── Inline forms, for format() inside a larger expression ─────────────

        /// <summary>Render <paramref name="valueJs"/> with a fixed number of decimals.</summary>
        public static string InlineDecimals(string valueJs, int decimals)
            => $"((function(v){{v=Number(v);return isNaN(v)?'':v.toFixed("
             + $"{Math.Max(0, decimals).ToString(CultureInfo.InvariantCulture)});}})({valueJs}))";

        /// <summary>Render <paramref name="valueJs"/> seconds as <c>mm:ss.mmm</c>.</summary>
        public static string InlineLapTime(string valueJs)
            => "((function(v){var t=Number(v);if(isNaN(t))return '--:--.---';"
             + "var s=t<0?'-':'';t=Math.abs(t);"
             + "var p=function(a,b){return Math.floor(a).toString().padStart(b,'0');};"
             + "return s+p(t/60,2)+':'+p(t%60,2)+'.'+p(t%1*1000,3);})("
             + valueJs + "))";

        /// <summary>Render <paramref name="valueJs"/> seconds as <c>mm:ss</c>.</summary>
        public static string InlineMinutesSeconds(string valueJs)
            => "((function(v){var t=Number(v);if(isNaN(t))return '--:--';"
             + "var s=t<0?'-':'';t=Math.abs(t);"
             + "var p=function(a,b){return Math.floor(a).toString().padStart(b,'0');};"
             + "return s+p(t/60,2)+':'+p(t%60,2);})("
             + valueJs + "))";

        /// <summary>Implements NCalc's <c>format(value, spec)</c>.
        ///
        /// <para>The spec is a .NET format string and is nearly always a literal, so the
        /// right formatter is chosen at convert time rather than shipping a .NET format
        /// interpreter to the wheel. A non-literal spec falls back to plain stringification
        /// — wrong-looking output beats a dropped widget.</para></summary>
        public static string Apply(string valueJs, string? literalSpec, double? literalNumber)
        {
            // format(x, 2) — SimHub allows a bare decimal count.
            if (literalNumber.HasValue)
                return InlineDecimals(valueJs, (int)literalNumber.Value);

            if (literalSpec == null)
                return $"String({valueJs})";

            string spec = literalSpec.Trim();
            if (spec.Length == 0) return $"String({valueJs})";

            if (LooksLikeTimeSpec(spec))
            {
                return spec.IndexOf('f') >= 0 || spec.IndexOf('F') >= 0
                    ? InlineLapTime(valueJs)
                    : InlineMinutesSeconds(valueJs);
            }

            int decimals = CountFormatDecimals(spec);
            if (decimals >= 0) return InlineDecimals(valueJs, decimals);

            return $"String({valueJs})";
        }

        /// <summary>True for .NET timespan specs such as <c>mm\:ss</c> or <c>mm\:ss\.fff</c>.
        /// Deliberately narrow: a spec must carry BOTH minutes and seconds, so a plain
        /// numeric spec containing an 's' can't be mistaken for one.</summary>
        private static bool LooksLikeTimeSpec(string spec)
            => spec.IndexOf("mm", StringComparison.Ordinal) >= 0
            && spec.IndexOf("ss", StringComparison.Ordinal) >= 0;

        /// <summary>Decimal places in a .NET numeric spec (<c>0.00</c>, <c>#,##0.0</c>),
        /// or −1 when the spec isn't numeric.</summary>
        private static int CountFormatDecimals(string spec)
        {
            foreach (char c in spec)
                if (c != '0' && c != '#' && c != '.' && c != ',') return -1;

            int dot = spec.IndexOf('.');
            if (dot < 0) return 0;

            int n = 0;
            for (int i = dot + 1; i < spec.Length; i++)
                if (spec[i] == '0' || spec[i] == '#') n++;
            return n;
        }
    }
}
