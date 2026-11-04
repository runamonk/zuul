namespace Zuul.Core;

public enum ResultKind { App, Calculator, System, Command, Path, Url, Web, Internal }

// Target is whatever running the result needs: an app's parsing name, a path, a URL, a command, or a value to copy.
public sealed record LaunchResult(ResultKind Kind, string Title, string Subtitle, string Target, int Score, bool Confirm = false)
{
    public string Key => $"{Kind}:{Target}";
}

// Aliases are other words people type for the same thing.
internal sealed record BuiltInCommand(ResultKind Kind, string Title, string Subtitle, string Target, bool Confirm, params string[] Aliases);

public sealed class SearchEngine(UsageStore usage)
{
    public const string DefaultWebSearch = "https://www.google.com/search?q={0}";

    internal static readonly BuiltInCommand[] Commands =
    [
        new(ResultKind.System, "Lock", "Lock this computer", "lock", false, "lock screen"),
        new(ResultKind.System, "Sleep", "Put this computer to sleep", "sleep", false, "suspend"),
        new(ResultKind.System, "Sign out", "Sign out of Windows", "signout", true, "log off", "logout"),
        new(ResultKind.System, "Restart", "Restart this computer", "restart", true, "reboot"),
        new(ResultKind.System, "Shut down", "Turn this computer off", "shutdown", true, "power off", "turn off"),
        new(ResultKind.System, "Empty Recycle Bin", "Windows asks before deleting anything", "recyclebin", false, "trash"),
        new(ResultKind.Internal, "Zuul settings", "Hotkey, theme and startup", "settings", false, "preferences", "options"),
        new(ResultKind.Internal, "Reload index", "Look for new apps, and new files in your folders", "reload", false, "reload apps", "refresh"),
        new(ResultKind.Internal, "Check for updates", "See if there is a newer Zuul", "update", false, "update zuul", "upgrade"),
        new(ResultKind.Internal, "Quit Zuul", "Close Zuul until it is started again", "quit", false, "exit"),
    ];

    public IReadOnlyList<AppEntry> Apps { get; set; } = [];
    // From the folders added in settings.
    public IReadOnlyList<FileEntry> Files { get; set; } = [];
    // {0} is replaced with the escaped query. Empty turns the web search row off.
    public string WebSearchUrl { get; set; } = DefaultWebSearch;
    public int MaxResults { get; set; } = 8;

    public IReadOnlyList<LaunchResult> Search(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        if (q.StartsWith('>')) return [RunCommand(q[1..].Trim())];

        var results = new List<LaunchResult>();
        if (Calculator.TryEvaluate(q, out var value))
            results.Add(new(ResultKind.Calculator, "= " + Calculator.Format(value), q + " · Enter copies the answer", Calculator.Plain(value), 2000));
        if (AsUrl(q) is { } url) results.Add(new(ResultKind.Url, url, "Open in your browser", url, 1900));
        results.AddRange(Paths(q));

        foreach (var app in Apps)
        {
            var score = FuzzyMatch.Score(q, app.Name);
            if (score < 0) continue;
            var result = new LaunchResult(ResultKind.App, app.Name, app.Location, app.ParsingName, score);
            results.Add(result with { Score = score + usage.Boost(result.Key) });
        }
        foreach (var file in Files)
        {
            // "report" finds report.pdf as well as "report.pdf" does.
            var score = Math.Max(FuzzyMatch.Score(q, file.Name), FuzzyMatch.Score(q, Path.GetFileNameWithoutExtension(file.Name)));
            if (score < 0) continue;
            var result = new LaunchResult(ResultKind.Path, file.Name, file.Path, file.Path, score);
            results.Add(result with { Score = score + FolderIndex.Boost(file.Rank) + usage.Boost(result.Key) });
        }
        foreach (var command in Commands)
        {
            var score = command.Aliases.Prepend(command.Title).Max(name => FuzzyMatch.Score(q, name));
            if (score < 0) continue;
            var result = new LaunchResult(command.Kind, command.Title, command.Subtitle, command.Target, score, command.Confirm);
            results.Add(result with { Score = score + usage.Boost(result.Key) });
        }

        var top = results.OrderByDescending(r => r.Score).ThenBy(r => r.Title.Length)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).Take(MaxResults).ToList();
        if (!string.IsNullOrWhiteSpace(WebSearchUrl) && WebSearchUrl.Contains("{0}"))
        {
            if (top.Count >= MaxResults) top.RemoveAt(top.Count - 1);
            var site = Uri.TryCreate(WebSearchUrl.Replace("{0}", ""), UriKind.Absolute, out var uri) ? uri.Host : "Web search";
            top.Add(new(ResultKind.Web, $"Search the web for \"{q}\"", site, WebSearchUrl.Replace("{0}", Uri.EscapeDataString(q)), 0));
        }
        return top;
    }

    private static LaunchResult RunCommand(string command) => command.Length == 0
        ? new(ResultKind.Command, "Type a command after >", "It runs in Command Prompt", "", 0)
        : new(ResultKind.Command, command, "Run in Command Prompt · Ctrl+Shift+Enter runs as administrator", command, 0);

    private static string? AsUrl(string q)
    {
        if (q.Contains(' ')) return null;
        if (q.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) q = "https://" + q;
        return Uri.TryCreate(q, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    // A full path opens directly; a partial one lists what's in the folder so far: C:\Win finds C:\Windows.
    private IEnumerable<LaunchResult> Paths(string q)
    {
        var expanded = Environment.ExpandEnvironmentVariables(q);
        if (expanded.StartsWith('~')) expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        if (!Path.IsPathFullyQualified(expanded)) return [];

        var results = new List<LaunchResult>();
        if (File.Exists(expanded) || Directory.Exists(expanded))
            results.Add(new(ResultKind.Path, expanded, File.Exists(expanded) ? "Open file" : "Open folder", expanded, 1900));
        var folder = Path.EndsInDirectorySeparator(expanded) ? expanded : Path.GetDirectoryName(expanded);
        var leaf = Path.EndsInDirectorySeparator(expanded) ? "" : Path.GetFileName(expanded);
        if (folder == null || !Directory.Exists(folder)) return results;
        try
        {
            // Bounded, so a folder with a hundred thousand files doesn't stall typing.
            var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(e => e.Name.StartsWith(leaf, StringComparison.OrdinalIgnoreCase) && !e.FullName.Equals(expanded, StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.Attributes.HasFlag(FileAttributes.Hidden))
                .Take(200).OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxResults);
            results.AddRange(entries.Select((e, i) => new LaunchResult(ResultKind.Path, e.Name, e.FullName, e.FullName, 1800 - i)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return results;
    }
}
