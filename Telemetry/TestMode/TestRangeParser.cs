using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MozaPlugin.Telemetry.TestMode
{
    /// <summary>
    /// Parses the free-form <c>range</c> / <c>data_type</c> fields shipped in
    /// Data/Telemetry.json into a numeric (min, max) pair + kind hint, used as
    /// the second-priority source of bounds for the test-mode sweep
    /// (overrides win, compression-table fallback loses).
    /// </summary>
    public static class TestRangeParser
    {
        // Strip Chinese punctuation that splits enumerations in some channels'
        // range string (e.g. Gear's "-1(R)、0(N)、1~12"). Also drop the
        // parenthetical annotations like "(R)" / "(N)".
        private static readonly Regex ParenAnnotation = new Regex(@"\([^)]*\)", RegexOptions.Compiled);

        public enum ParseHint { None, Toggle }

        public struct Result
        {
            public bool Ok;
            public double Min;
            public double Max;
            public ParseHint Hint;
        }

        /// <summary>
        /// Try to parse a Telemetry.json range string. Returns Ok=false if the
        /// string is unrecognised — caller decides the fallback (data_type
        /// default or compression test-range).
        /// </summary>
        public static Result Parse(string? range)
        {
            var fail = new Result { Ok = false };
            if (string.IsNullOrWhiteSpace(range)) return fail;

            string s = range!
                .Replace('、', ',')
                .Replace('，', ',')
                .Replace('～', '~');
            s = ParenAnnotation.Replace(s, "").Trim();

            // "a~b" — split on the first '~', tolerating leading minus on
            // either half (e.g. "-148~384"). Take the lowest and highest of
            // all numeric tokens we can find on each side of the first '~'.
            int tilde = s.IndexOf('~');
            if (tilde > 0 && tilde < s.Length - 1)
            {
                if (TryParseDouble(s.Substring(0, tilde).Trim(), out double lo) &&
                    TryParseDouble(s.Substring(tilde + 1).Trim(), out double hi))
                {
                    if (lo > hi) (lo, hi) = (hi, lo);
                    return new Result { Ok = true, Min = lo, Max = hi };
                }
            }

            // ">=N" or ">N" → leave Max unset; caller pairs with sensible upper.
            if (s.StartsWith(">="))
            {
                if (TryParseDouble(s.Substring(2).Trim(), out double lo))
                    return new Result { Ok = true, Min = lo, Max = double.NaN };
            }
            else if (s.StartsWith(">"))
            {
                if (TryParseDouble(s.Substring(1).Trim(), out double lo))
                    return new Result { Ok = true, Min = lo, Max = double.NaN };
            }

            // Paired labels like "0:off,1:on" or "1:true,0:false" → Toggle hint.
            if (s.Contains(":") && s.Contains(","))
            {
                return new Result { Ok = true, Min = 0, Max = 1, Hint = ParseHint.Toggle };
            }

            // Single numeric → fixed point; widen to a tiny range so a Sweep
            // doesn't degenerate. Caller will typically prefer Constant.
            if (TryParseDouble(s, out double single))
            {
                return new Result { Ok = true, Min = single, Max = single };
            }

            return fail;
        }

        private static bool TryParseDouble(string s, out double value)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
