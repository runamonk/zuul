using System.Globalization;

namespace Zuul.Core;

// A small recursive descent parser: + - * / % ^, brackets, a few functions, pi and e.
// Not eval, so typing into the launcher can never run anything.
public static class Calculator
{
    private static readonly Dictionary<string, Func<double, double>> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = Math.Sqrt, ["abs"] = Math.Abs, ["round"] = Math.Round, ["floor"] = Math.Floor, ["ceil"] = Math.Ceiling,
        ["sin"] = Math.Sin, ["cos"] = Math.Cos, ["tan"] = Math.Tan, ["log"] = Math.Log10, ["ln"] = Math.Log
    };

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI, ["e"] = Math.E
    };

    // Plain numbers aren't sums, so "42" stays a search instead of answering itself.
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        text = text.Trim().TrimEnd('=').Trim();
        if (text.Length == 0 || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
        if (!text.Any(c => "+-*/%^(x".Contains(c))) return false;
        try
        {
            var parser = new Parser(text);
            value = parser.Expression();
            parser.SkipSpaces();
            return parser.AtEnd && !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (FormatException) { return false; }
    }

    // Up to 10 decimals, no float fuzz like 0.30000000000000004.
    public static string Format(double value) =>
        Math.Round(value, 10).ToString("#,0.##########", CultureInfo.CurrentCulture);

    // Same number without group separators, for pasting into other things.
    public static string Plain(double value) =>
        Math.Round(value, 10).ToString("0.##########", CultureInfo.InvariantCulture);

    private sealed class Parser(string text)
    {
        private int _at;

        public bool AtEnd => _at >= text.Length;

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(text[_at])) _at++;
        }

        private bool Take(char c)
        {
            SkipSpaces();
            if (AtEnd || text[_at] != c) return false;
            _at++;
            return true;
        }

        public double Expression()
        {
            var value = Term();
            while (true)
            {
                if (Take('+')) value += Term();
                else if (Take('-')) value -= Term();
                else return value;
            }
        }

        private double Term()
        {
            var value = Power();
            while (true)
            {
                if (Take('*') || Take('x')) value *= Power();
                else if (Take('/')) value /= Power();
                else if (Take('%')) value %= Power();
                else return value;
            }
        }

        // Right associative, and binds tighter than a leading minus: -2^2 is -4.
        private double Power()
        {
            if (Take('-')) return -Power();
            if (Take('+')) return Power();
            var value = Atom();
            return Take('^') ? Math.Pow(value, Power()) : value;
        }

        private double Atom()
        {
            SkipSpaces();
            if (Take('('))
            {
                var inner = Expression();
                // A missing close bracket at the very end is forgiven, since it's often still being typed.
                if (!Take(')') && !AtEnd) throw new FormatException();
                return inner;
            }
            var start = _at;
            if (!AtEnd && char.IsLetter(text[_at]))
            {
                while (!AtEnd && char.IsLetter(text[_at])) _at++;
                var name = text[start.._at];
                if (Constants.TryGetValue(name, out var constant)) return constant;
                if (Functions.TryGetValue(name, out var function))
                {
                    if (!Take('(')) throw new FormatException();
                    var argument = Expression();
                    if (!Take(')') && !AtEnd) throw new FormatException();
                    return function(argument);
                }
                throw new FormatException();
            }
            while (!AtEnd && (char.IsDigit(text[_at]) || text[_at] is '.' or ',')) _at++;
            // Commas are thousands separators here; this calculator only speaks invariant decimals.
            var number = text[start.._at].Replace(",", "");
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new FormatException();
            return value;
        }
    }
}
