namespace Zuul.Core;

public static class FuzzyMatch
{
    // Higher is better; -1 means no match. Ranges don't overlap, so a prefix always beats a substring.
    public static int Score(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0 || text.Length == 0) return -1;
        const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

        if (text.Equals(query, Ci)) return 1000;
        if (text.StartsWith(query, Ci)) return 900 - Math.Min(text.Length - query.Length, 99);
        var words = Words(text);
        if (words.Any(w => w.StartsWith(query, Ci))) return 800 - Math.Min(text.Length - query.Length, 99);
        // "vsc" finds Visual Studio Code.
        if (query.Length > 1 && string.Concat(words.Select(w => w[0])).StartsWith(query, Ci)) return 750;
        if (text.Contains(query, Ci)) return 700 - Math.Min(text.IndexOf(query, Ci), 99);

        // Every typed word has to start a word in the text, in any order: "code studio".
        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts.All(p => words.Any(w => w.StartsWith(p, Ci)))) return 650;

        // Letters in order with gaps; tighter matches score higher.
        var index = 0;
        var gaps = 0;
        var last = -1;
        foreach (var c in query.Where(c => c != ' '))
        {
            var found = text.IndexOf(char.ToString(c), index, Ci);
            if (found < 0) return -1;
            if (last >= 0) gaps += found - last - 1;
            last = found;
            index = found + 1;
        }
        return Math.Max(100, 500 - gaps * 10);
    }

    // Splits on spaces and punctuation, and on lower-to-upper case changes: "PowerShell" is two words.
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var end = i == text.Length || !char.IsLetterOrDigit(text[i])
                || (i > 0 && char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
            if (end && start >= 0)
            {
                words.Add(text[start..i]);
                start = -1;
            }
            if (i < text.Length && char.IsLetterOrDigit(text[i]) && start < 0) start = i;
        }
        return words;
    }
}
