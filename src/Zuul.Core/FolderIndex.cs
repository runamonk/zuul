namespace Zuul.Core;

// The list's order is the priority: folders nearer the top rank higher.
// A folder that's switched off stays in the list, so it's easy to turn back on.
public sealed record FolderSource(string Path, bool IncludeSubfolders = true, bool Enabled = true);

// Rank counts places from the bottom of the folder list; the bottom folder is 0.
public sealed record FileEntry(string Name, string Path, int Rank);

public static class FolderIndex
{
    // Per folder, so pointing it at C:\ doesn't eat all the memory and every keystroke with it.
    public const int MaxEntries = 10_000;

    // Things that do something when opened: programs, shortcuts, scripts and control panel bits.
    public static readonly string[] DefaultExtensions = [".exe", ".lnk", ".url", ".appref-ms", ".bat", ".cmd", ".com", ".msc", ".cpl"];

    // Shown by name alone: "Google Chrome", not "Google Chrome.lnk".
    private static readonly HashSet<string> Shortcuts = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".appref-ms" };

    // Where Windows keeps app shortcuts: both Start menus and both desktops.
    // Real paths rather than %APPDATA%, so a desktop moved to OneDrive is still found.
    public static FolderSource[] DefaultFolders =>
        new[]
        {
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory
        }
        .Select(Environment.GetFolderPath).Where(p => p.Length > 0).Select(p => new FolderSource(p)).ToArray();

    // ".LNK", "lnk" and "*.lnk" all become ".lnk"; null when it can't be an extension.
    public static string? NormalizeExtension(string text)
    {
        var extension = text.Trim().TrimStart('*').Trim().ToLowerInvariant();
        if (extension.Length == 0) return null;
        if (!extension.StartsWith('.')) extension = "." + extension;
        return extension.Length > 1 && extension.IndexOf('.', 1) < 0 && extension.IndexOfAny([.. Path.GetInvalidFileNameChars(), ' ']) < 0
            ? extension : null;
    }

    // Each place up the list is a small nudge, capped so a long list can't bury a much better match.
    public static int Boost(int rank) => Math.Clamp(rank, 0, 10) * 10;

    public static Task<IReadOnlyList<FileEntry>> LoadAsync(IEnumerable<FolderSource> folders, IEnumerable<string> extensions) =>
        Task.Run(() => Load(folders, extensions));

    // Only files with one of the extensions, from folders that are switched on.
    // Shortcuts in the Start menus and desktops (or checkedFolders) also need their target to have one; that's where
    // installers leave readmes and help files. Shortcuts anywhere else were put there on purpose, so they all count.
    // A path found twice, or a shortcut name found in several places, is one result from the highest folder:
    // Chrome on the desktop and in the Start menu shows once.
    public static IReadOnlyList<FileEntry> Load(IEnumerable<FolderSource> folders, IEnumerable<string> extensions, IEnumerable<string>? checkedFolders = null)
    {
        var wanted = extensions.Select(NormalizeExtension).OfType<string>().ToHashSet();
        var check = (checkedFolders ?? DefaultFolders.Select(f => f.Path))
            .Select(p => Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(p)) + Path.DirectorySeparatorChar).ToList();
        var entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var list = folders.ToList();
        for (var index = 0; index < list.Count; index++)
        {
            var folder = list[index];
            if (!folder.Enabled) continue;
            // Switched off folders still count as places, so turning one off doesn't reshuffle the rest.
            var rank = list.Count - 1 - index;
            var root = Environment.ExpandEnvironmentVariables(folder.Path);
            if (!Directory.Exists(root)) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = folder.IncludeSubfolders, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };
            try
            {
                foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options)
                    .Where(f => wanted.Contains(f.Extension.ToLowerInvariant())).Take(MaxEntries))
                {
                    // A shortcut counts by what it points at: WinRAR's "What is new" is a .lnk to a .txt.
                    if (info.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        && check.Any(c => info.FullName.StartsWith(c, StringComparison.OrdinalIgnoreCase))
                        && !ShortcutTarget.Launchable(ShortcutTarget.Read(info.FullName), wanted)) continue;
                    var shortcut = Shortcuts.Contains(info.Extension);
                    var name = shortcut ? Path.GetFileNameWithoutExtension(info.Name) : info.Name;
                    var key = shortcut ? "shortcut:" + name : info.FullName;
                    // Folders are read top first, so whatever's already there came from higher up.
                    if (!entries.TryAdd(key, new FileEntry(name, info.FullName, rank))) continue;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return entries.Values.ToList();
    }
}
