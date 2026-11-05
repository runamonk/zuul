using System.Text.Json;
using System.Text.Json.Serialization;
using Zuul.Core;

namespace Zuul;

// A null ColorTheme follows Windows' light or dark mode.
// StartWithWindows isn't here: the Run key in the registry is the truth, so it can't drift.
// DefaultFoldersAdded marks lists saved since the Start menu and desktops became folders in the list.
// It defaults to false because the JSON reader uses this default for files that don't have it, and those are the old ones.
internal sealed record ZuulSettings(string? ColorTheme = null, bool? DarkTheme = null, string HotKey = "Alt+Space",
    string WebSearchUrl = SearchEngine.DefaultWebSearch, int MaxResults = 8, FolderSource[]? Folders = null,
    string[]? Extensions = null, bool StoreApps = true, bool DefaultFoldersAdded = false, UpdateSettings? Updates = null,
    bool RememberSearch = false, EverythingSettings? Everything = null)
{
    // Null means the defaults, so types added to the defaults later still show up.
    [JsonIgnore] public IReadOnlyList<string> FileTypes => Extensions ?? FolderIndex.DefaultExtensions;

    [JsonIgnore] public IReadOnlyList<FolderSource> FolderList => Folders ?? FolderIndex.DefaultFolders;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zuul");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    // Zuul used to be called DoIt. Copied, not moved, so an old copy still running keeps working.
    public static void CopyFromDoIt(string? from = null, string? to = null)
    {
        from ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoIt");
        to ??= Folder;
        if (Directory.Exists(to) || !Directory.Exists(from)) return;
        try
        {
            Directory.CreateDirectory(to);
            foreach (var name in new[] { "settings.json", "themes.json", "usage.json" })
                if (File.Exists(Path.Combine(from, name))) File.Copy(Path.Combine(from, name), Path.Combine(to, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static ZuulSettings Read(string? filePath = null)
    {
        ZuulSettings settings;
        try { settings = JsonSerializer.Deserialize<ZuulSettings>(File.ReadAllText(filePath ?? FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        // Older lists predate the default folders; without them every desktop app would vanish.
        if (settings.Folders != null && !settings.DefaultFoldersAdded)
            settings = settings with { Folders = [.. FolderIndex.DefaultFolders.Where(d => !settings.Folders.Any(f => SamePath(f.Path, d.Path))), .. settings.Folders] };
        return settings with { DefaultFoldersAdded = true };
    }

    public void Save(string? filePath = null) =>
        AtomicFile.WriteAllText(filePath ?? FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(a)),
            Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(b)), StringComparison.OrdinalIgnoreCase);
}

// The prefix starts a file search through Everything; it waits DelayMs after typing stops.
// Excluded null means the defaults, so new defaults reach everyone who never changed them.
internal sealed record EverythingSettings(string Prefix = FileSearch.DefaultPrefix, int BatchSize = 8, int DelayMs = 100,
    bool IncludeFolders = true, string[]? Excluded = null)
{
    [JsonIgnore] public IReadOnlyList<string> ExcludedList => Excluded ?? FileSearch.DefaultExcluded;
}

// EveryDays = 0 checks on every startup.
internal sealed record UpdateSettings(bool CheckOnStartup = true, int EveryDays = 0, DateTime? LastCheckUtc = null);
