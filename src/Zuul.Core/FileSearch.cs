namespace Zuul.Core;

// "f: invoice" searches the whole computer through Everything instead of Zuul's own index.
public static class FileSearch
{
    public const string DefaultPrefix = "f:";

    // Asked of Everything in one go, ranked here once, then shown a batch at a time.
    // Everything sorts by name, so asking for only a few would rank the alphabetically first ones, not the best.
    public const int Fetch = 1000;

    // System clutter that buries real files. A whole path or just part of one; %variables% work.
    public static readonly string[] DefaultExcluded = [@"%SystemRoot%\WinSxS", "$Recycle.Bin", @"AppData\Local\Temp"];

    public static bool TryParse(string query, string prefix, out string term)
    {
        var q = query.TrimStart();
        var found = prefix.Length > 0 && q.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        term = found ? q[prefix.Length..].Trim() : "";
        return found;
    }

    // A \ means a path is being typed, so Everything matches against whole paths, as it does by itself.
    public static bool MatchPath(string term) => term.Contains('\\');

    // In Everything's own syntax: !path:"..." leaves a folder out, file: leaves folders out.
    public static string BuildQuery(string term, bool includeFolders, IEnumerable<string> excluded) =>
        term + string.Concat(excluded.Select(e => Environment.ExpandEnvironmentVariables(e.Trim().Replace("\"", "")))
                .Where(e => e.Length > 0).Select(e => $" !path:\"{e}\""))
            + (includeFolders ? "" : " file:");

    // Puts the closest names first, like the rest of Zuul.
    // Everything's matches always stay in, even where FuzzyMatch disagrees, such as on a path.
    public static IReadOnlyList<LaunchResult> Rank(string term, IEnumerable<EverythingItem> items) =>
        items.Select(item =>
            {
                var name = Path.GetFileName(item.Path.TrimEnd('\\'));
                if (name.Length == 0) name = item.Path;
                var score = Math.Max(FuzzyMatch.Score(term, name), FuzzyMatch.Score(term, Path.GetFileNameWithoutExtension(name)));
                return new LaunchResult(ResultKind.Path, name, Path.GetDirectoryName(item.Path) ?? item.Path, item.Path, Math.Max(score, 0));
            })
            .OrderByDescending(r => r.Score).ThenBy(r => r.Target.Length).ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
