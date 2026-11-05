namespace Zuul.Core;

// "f: invoice" searches the whole computer through Everything instead of Zuul's own index.
public static class FileSearch
{
    // Asked of Everything, then ranked here and cut down to the launcher's size.
    public const int Fetch = 100;

    public static bool TryParse(string query, out string term)
    {
        var q = query.TrimStart();
        var found = q.StartsWith("f:", StringComparison.OrdinalIgnoreCase);
        term = found ? q[2..].Trim() : "";
        return found;
    }

    // A \ means a path is being typed, so Everything matches against whole paths, as it does by itself.
    public static bool MatchPath(string term) => term.Contains('\\');

    // Everything's order is by name; this puts the closest names first, like the rest of Zuul.
    // Its matches always stay in, even where FuzzyMatch disagrees, such as on a path.
    public static IReadOnlyList<LaunchResult> Rank(string term, IEnumerable<EverythingItem> items, int max) =>
        items.Select(item =>
            {
                var name = Path.GetFileName(item.Path.TrimEnd('\\'));
                if (name.Length == 0) name = item.Path;
                var score = Math.Max(FuzzyMatch.Score(term, name), FuzzyMatch.Score(term, Path.GetFileNameWithoutExtension(name)));
                return new LaunchResult(ResultKind.Path, name, Path.GetDirectoryName(item.Path) ?? item.Path, item.Path, Math.Max(score, 0));
            })
            .OrderByDescending(r => r.Score).ThenBy(r => r.Target.Length).ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .Take(max).ToList();
}
