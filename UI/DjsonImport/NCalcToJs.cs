using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>Outcome of transpiling one SimHub formula.</summary>
    public sealed class TranspileResult
    {
        public bool Ok { get; set; }
        /// <summary>JavaScript for the wheel's <c>binding.methods[0]</c>.</summary>
        public string Js { get; set; } = "";
        /// <summary>Channel URLs the expression reads.</summary>
        public List<string> Urls { get; } = new List<string>();
        /// <summary>Why it failed, or non-fatal notes. One line each.</summary>
        public List<string> Problems { get; } = new List<string>();

        public static TranspileResult Fail(string problem)
        {
            var r = new TranspileResult { Ok = false };
            r.Problems.Add(problem);
            return r;
        }
    }

    /// <summary>
    /// Translates a SimHub NCalc expression into JavaScript that the wheel evaluates.
    ///
    /// <para>The wheel runs a full modern JS engine inside <c>binding.methods[]</c> —
    /// arrow functions, template literals, default parameters, destructuring, closures,
    /// <c>Math.*</c>, <c>Date.now()</c>, <c>String.padStart</c> and <c>for</c> loops all
    /// appear in factory dashboards. So conditionals, thresholds, arithmetic and
    /// formatting run on the wheel rather than costing a repurposed channel each.</para>
    ///
    /// <para>NCalc's grammar is close enough to JavaScript's that a recursive-descent
    /// parse emitting JS bottom-up covers it. Precedence is written out explicitly rather
    /// than relying on a token rewrite, because NCalc's <c>=</c> (equality) and word
    /// operators (<c>and</c>/<c>or</c>/<c>not</c>) would otherwise be ambiguous.</para>
    /// </summary>
    public sealed class NCalcToJs
    {
        private readonly ChannelResolver _channels;

        public NCalcToJs(ChannelResolver channels) => _channels = channels;

        public TranspileResult Translate(string? expression)
        {
            string src = (expression ?? "").Trim();
            if (src.Length == 0) return TranspileResult.Fail("empty expression");

            // SimHub supports a `js:` escape whose body is already JavaScript — but it
            // is SimHub-flavoured ($prop(...) etc.), not wheel-flavoured, so passing it
            // through would emit something that silently reads undefined on the wheel.
            // SimHub also auto-detects JavaScript without the prefix, so catch that shape
            // too: without this it fails deep in the lexer as "unexpected character '$'",
            // which tells the user nothing about what to do.
            if (src.StartsWith("js:", StringComparison.OrdinalIgnoreCase) || LooksLikeJavaScript(src))
            {
                return TranspileResult.Fail(
                    "SimHub-side JavaScript ($prop/var/return) cannot run on the wheel — "
                    + "rewrite it as an NCalc expression to convert it");
            }

            var result = new TranspileResult();
            try
            {
                var parser = new Parser(src, _channels, result);
                string js = parser.ParseExpression();
                parser.ExpectEnd();
                result.Js = js;
                result.Ok = result.Problems.Count == 0;
            }
            catch (TranspileException ex)
            {
                result.Ok = false;
                result.Problems.Add(ex.Message);
            }
            return result;
        }

        /// <summary>SimHub accepts JavaScript in a binding without the <c>js:</c> prefix
        /// and detects it by shape. None of these tokens is valid NCalc, so their presence
        /// is unambiguous.</summary>
        private static bool LooksLikeJavaScript(string src)
            => src.IndexOf("$prop(", StringComparison.OrdinalIgnoreCase) >= 0
            || src.IndexOf("return ", StringComparison.Ordinal) >= 0
            || src.IndexOf("var ", StringComparison.Ordinal) >= 0
            || src.IndexOf(';') >= 0;

        private sealed class TranspileException : Exception
        {
            public TranspileException(string message) : base(message) { }
        }

        // ── Tokens ────────────────────────────────────────────────────────────

        private enum TokKind { Number, String, Property, Ident, Op, LParen, RParen, Comma, End }

        private readonly struct Tok
        {
            public readonly TokKind Kind;
            public readonly string Text;
            public Tok(TokKind kind, string text) { Kind = kind; Text = text; }
        }

        /// <summary>An evaluated argument. <see cref="Js"/> is always set; the literal
        /// fields are set only when the argument was written as a bare literal, which is
        /// what <c>format()</c> needs to pick a formatter at convert time.</summary>
        private readonly struct Arg
        {
            public readonly string Js;
            public readonly string? LiteralString;
            public readonly double? LiteralNumber;
            public Arg(string js, string? litStr, double? litNum)
            {
                Js = js; LiteralString = litStr; LiteralNumber = litNum;
            }
        }

        // ── Parser ────────────────────────────────────────────────────────────

        private sealed class Parser
        {
            private readonly List<Tok> _toks;
            private readonly ChannelResolver _channels;
            private readonly TranspileResult _result;
            private int _i;

            public Parser(string src, ChannelResolver channels, TranspileResult result)
            {
                _channels = channels;
                _result = result;
                _toks = Tokenize(src);
            }

            private Tok Peek => _toks[_i];
            private Tok Next() => _toks[_i++];
            private bool IsOp(string s) => Peek.Kind == TokKind.Op && Peek.Text == s;

            private bool IsWord(string s) =>
                Peek.Kind == TokKind.Ident && string.Equals(Peek.Text, s, StringComparison.OrdinalIgnoreCase);

            public void ExpectEnd()
            {
                if (Peek.Kind != TokKind.End)
                    throw new TranspileException($"unexpected '{Peek.Text}' after end of expression");
            }

            // expr := ternary
            public string ParseExpression() => ParseTernary().Js;

            private Arg ParseTernary()
            {
                var cond = ParseLogicalOr();
                if (!IsOp("?")) return cond;
                Next();
                var a = ParseTernary();
                if (!IsOp(":")) throw new TranspileException("'?' without matching ':'");
                Next();
                var b = ParseTernary();
                return Plain($"({cond.Js}?{a.Js}:{b.Js})");
            }

            private Arg ParseLogicalOr()
            {
                var left = ParseLogicalAnd();
                while (IsOp("||") || IsWord("or"))
                {
                    Next();
                    var right = ParseLogicalAnd();
                    left = Plain($"({left.Js}||{right.Js})");
                }
                return left;
            }

            private Arg ParseLogicalAnd()
            {
                var left = ParseEquality();
                while (IsOp("&&") || IsWord("and"))
                {
                    Next();
                    var right = ParseEquality();
                    left = Plain($"({left.Js}&&{right.Js})");
                }
                return left;
            }

            private Arg ParseEquality()
            {
                var left = ParseRelational();
                while (IsOp("==") || IsOp("!=") || IsOp("=") || IsOp("<>"))
                {
                    // NCalc spells equality '=' and inequality '<>'; JS needs '=='/'!='.
                    string op = Next().Text;
                    string js = (op == "=" || op == "==") ? "==" : "!=";
                    var right = ParseRelational();
                    left = Plain($"({left.Js}{js}{right.Js})");
                }
                return left;
            }

            private Arg ParseRelational()
            {
                var left = ParseAdditive();
                while (IsOp("<") || IsOp(">") || IsOp("<=") || IsOp(">="))
                {
                    string op = Next().Text;
                    var right = ParseAdditive();
                    left = Plain($"({left.Js}{op}{right.Js})");
                }
                return left;
            }

            private Arg ParseAdditive()
            {
                var left = ParseMultiplicative();
                while (IsOp("+") || IsOp("-"))
                {
                    string op = Next().Text;
                    var right = ParseMultiplicative();
                    left = Plain($"({left.Js}{op}{right.Js})");
                }
                return left;
            }

            private Arg ParseMultiplicative()
            {
                var left = ParseUnary();
                while (IsOp("*") || IsOp("/") || IsOp("%"))
                {
                    string op = Next().Text;
                    var right = ParseUnary();
                    left = Plain($"({left.Js}{op}{right.Js})");
                }
                return left;
            }

            private Arg ParseUnary()
            {
                if (IsOp("!") || IsWord("not"))
                {
                    Next();
                    return Plain($"(!{ParseUnary().Js})");
                }
                if (IsOp("-")) { Next(); return Plain($"(-{ParseUnary().Js})"); }
                if (IsOp("+")) { Next(); return ParseUnary(); }
                return ParsePrimary();
            }

            private Arg ParsePrimary()
            {
                var t = Next();
                switch (t.Kind)
                {
                    case TokKind.Number:
                        return new Arg(t.Text, null, ParseNumber(t.Text));

                    case TokKind.String:
                        return new Arg(JsString(t.Text), t.Text, null);

                    case TokKind.Property:
                        return Plain(ResolveProperty(t.Text));

                    case TokKind.LParen:
                    {
                        var inner = ParseTernary();
                        if (Peek.Kind != TokKind.RParen)
                            throw new TranspileException("unbalanced '('");
                        Next();
                        return Plain($"({inner.Js})");
                    }

                    case TokKind.Ident:
                    {
                        if (Peek.Kind == TokKind.LParen) return ParseCall(t.Text);
                        // A bare word is NCalc's boolean literal or an unbracketed
                        // property reference. Only the literals are safe to pass through.
                        if (string.Equals(t.Text, "true", StringComparison.OrdinalIgnoreCase)) return Plain("true");
                        if (string.Equals(t.Text, "false", StringComparison.OrdinalIgnoreCase)) return Plain("false");
                        if (string.Equals(t.Text, "null", StringComparison.OrdinalIgnoreCase)) return Plain("null");
                        // SimHub allows a bare name as shorthand for [name].
                        return Plain(ResolveProperty(t.Text));
                    }

                    default:
                        throw new TranspileException($"unexpected token '{t.Text}'");
                }
            }

            private string ResolveProperty(string path)
            {
                var r = _channels.Resolve(path);
                if (!r.IsUsable)
                {
                    throw new TranspileException(
                        $"property [{path}] has no MOZA channel ({r.Reason})");
                }
                foreach (var u in r.Urls)
                    if (!_result.Urls.Contains(u)) _result.Urls.Add(u);
                return r.Js;
            }

            private Arg ParseCall(string name)
            {
                Next(); // consume '('
                var args = new List<Arg>();
                if (Peek.Kind != TokKind.RParen)
                {
                    args.Add(ParseTernary());
                    while (Peek.Kind == TokKind.Comma)
                    {
                        Next();
                        args.Add(ParseTernary());
                    }
                }
                if (Peek.Kind != TokKind.RParen)
                    throw new TranspileException($"unbalanced '(' in {name}(...)");
                Next();
                return Plain(EmitCall(name, args));
            }

            private string EmitCall(string name, List<Arg> a)
            {
                string n = name.ToLowerInvariant();
                string A(int i) => i < a.Count ? a[i].Js : "undefined";

                void Need(int count)
                {
                    if (a.Count != count)
                        throw new TranspileException($"{name}() expects {count} argument(s), got {a.Count}");
                }

                switch (n)
                {
                    case "if":
                        Need(3);
                        return $"(({A(0)})?({A(1)}):({A(2)}))";

                    // NCalc's isnull yields the fallback for null; on the wheel an absent
                    // channel reads back NaN, so test for that too (v!==v is the NaN test).
                    case "isnull":
                        Need(2);
                        return $"((function(v){{return (v==null||v!==v)?({A(1)}):v;}})({A(0)}))";

                    case "ceiling": Need(1); return $"Math.ceil({A(0)})";
                    case "floor":   Need(1); return $"Math.floor({A(0)})";
                    case "truncate": Need(1); return $"Math.trunc({A(0)})";
                    case "abs":     Need(1); return $"Math.abs({A(0)})";
                    case "sqrt":    Need(1); return $"Math.sqrt({A(0)})";
                    case "sign":    Need(1); return $"Math.sign({A(0)})";
                    case "pow":     Need(2); return $"Math.pow({A(0)},{A(1)})";
                    case "max":     Need(2); return $"Math.max({A(0)},{A(1)})";
                    case "min":     Need(2); return $"Math.min({A(0)},{A(1)})";

                    case "round":
                        if (a.Count == 1) return $"Math.round({A(0)})";
                        if (a.Count == 2) return $"Number(Number({A(0)}).toFixed({A(1)}))";
                        throw new TranspileException($"round() expects 1 or 2 arguments, got {a.Count}");

                    // Our time channels are already seconds, so the conversion is identity.
                    case "timespantoseconds": Need(1); return $"({A(0)})";

                    case "format":
                        Need(2);
                        return JsFormatters.Apply(A(0), a[1].LiteralString, a[1].LiteralNumber);

                    case "isnan":
                        Need(1); return $"isNaN({A(0)})";

                    // String helpers. NCalc's replace() swaps every occurrence, which
                    // split/join does without needing a regex or escaping the needle.
                    case "replace":
                        Need(3);
                        return $"String({A(0)}).split({A(1)}).join({A(2)})";
                    case "ucase":
                    case "upper": Need(1); return $"String({A(0)}).toUpperCase()";
                    case "lcase":
                    case "lower": Need(1); return $"String({A(0)}).toLowerCase()";
                    case "trim": Need(1); return $"String({A(0)}).trim()";
                    case "len": Need(1); return $"String({A(0)}).length";
                    case "left": Need(2); return $"String({A(0)}).slice(0,{A(1)})";
                    case "right": Need(2); return $"String({A(0)}).slice(-({A(1)}))";
                    case "contains": Need(2); return $"(String({A(0)}).indexOf({A(1)})>=0)";
                    case "startswith": Need(2); return $"(String({A(0)}).indexOf({A(1)})===0)";
                    case "concat": Need(2); return $"(String({A(0)})+String({A(1)}))";

                    // NCalc renders a TimeSpan as short time; our channels are seconds.
                    case "toshorttime": Need(1); return JsFormatters.InlineMinutesSeconds(A(0));

                    // NCalc's blink() keeps per-key state in the host engine. On the wheel
                    // the same square wave is stateless off the clock — Date.now() is
                    // available (used by factory dashboards).
                    case "blink":
                    {
                        if (a.Count != 3)
                            throw new TranspileException($"blink() expects 3 arguments, got {a.Count}");
                        return $"(((Date.now()%(2*({A(1)})))<({A(1)}))&&({A(2)}))";
                    }

                    // changed()/inertia()/scroll() are genuinely stateful — they compare
                    // against a previous evaluation the wheel never saw. No stateless
                    // equivalent; these need the plugin-side NCalc fallback.
                    case "changed":
                    case "inertia":
                    case "scroll":
                    case "increasing":
                    case "decreasing":
                    case "maximum":
                    case "minimum":
                        throw new TranspileException(
                            $"{name}() is stateful and has no wheel-side equivalent");

                    default:
                        throw new TranspileException($"unsupported function {name}()");
                }
            }

            private static Arg Plain(string js) => new Arg(js, null, null);

            private static double? ParseNumber(string s)
                => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d : (double?)null;
        }

        // ── Lexer ─────────────────────────────────────────────────────────────

        private static List<Tok> Tokenize(string s)
        {
            var toks = new List<Tok>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '[')
                {
                    // Property names may contain dots, spaces and punctuation, so take
                    // everything up to the closing bracket verbatim.
                    int end = s.IndexOf(']', i + 1);
                    if (end < 0) throw new TranspileException("unterminated '[' property reference");
                    toks.Add(new Tok(TokKind.Property, s.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    char quote = c;
                    var sb = new StringBuilder();
                    i++;
                    while (i < s.Length && s[i] != quote)
                    {
                        // NCalc escapes with a backslash; a doubled quote is also a
                        // literal quote. Both survive as plain characters here.
                        if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                        sb.Append(s[i]); i++;
                    }
                    if (i >= s.Length) throw new TranspileException("unterminated string literal");
                    i++; // closing quote
                    toks.Add(new Tok(TokKind.String, sb.ToString()));
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    toks.Add(new Tok(TokKind.Number, s.Substring(start, i - start)));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                    toks.Add(new Tok(TokKind.Ident, s.Substring(start, i - start)));
                    continue;
                }

                if (c == '(') { toks.Add(new Tok(TokKind.LParen, "(")); i++; continue; }
                if (c == ')') { toks.Add(new Tok(TokKind.RParen, ")")); i++; continue; }
                if (c == ',') { toks.Add(new Tok(TokKind.Comma, ",")); i++; continue; }

                // Two-character operators first, so '<=' never lexes as '<' then '='.
                if (i + 1 < s.Length)
                {
                    string two = s.Substring(i, 2);
                    if (two == "==" || two == "!=" || two == "<=" || two == ">="
                        || two == "&&" || two == "||" || two == "<>")
                    {
                        toks.Add(new Tok(TokKind.Op, two)); i += 2; continue;
                    }
                }

                if ("+-*/%<>=!?:&|".IndexOf(c) >= 0)
                {
                    toks.Add(new Tok(TokKind.Op, c.ToString())); i++; continue;
                }

                throw new TranspileException($"unexpected character '{c}'");
            }
            toks.Add(new Tok(TokKind.End, ""));
            return toks;
        }

        /// <summary>Emit a JS single-quoted string literal.</summary>
        internal static string JsString(string value)
        {
            var sb = new StringBuilder("'");
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.Append('\'').ToString();
        }
    }
}
