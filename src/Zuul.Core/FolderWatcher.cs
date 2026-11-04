namespace Zuul.Core;

// Says when something in the folders may have changed, so the index can be rebuilt.
// Windows can drop or repeat these events, so it's only a nudge, never a list of changes.
// It waits for a quiet spell first, so unzipping a few hundred files means one rebuild.
public sealed class FolderWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _extensions;
    private readonly Timer _quiet;
    private readonly TimeSpan _delay;

    // Raised on a thread pool thread.
    public event Action? Changed;

    public FolderWatcher(IEnumerable<FolderSource> folders, IEnumerable<string> extensions, TimeSpan? delay = null)
    {
        _extensions = extensions.Select(FolderIndex.NormalizeExtension).OfType<string>().ToHashSet();
        _delay = delay ?? TimeSpan.FromSeconds(2);
        _quiet = new Timer(_ => Changed?.Invoke());
        foreach (var folder in folders)
        {
            if (!folder.Enabled) continue;
            var root = Environment.ExpandEnvironmentVariables(folder.Path);
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = folder.IncludeSubfolders, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                // A folder appearing or going may carry shortcuts with it. Folders also get Changed whenever
                // anything inside them does, so Changed only counts for the file types.
                watcher.Created += (_, e) => { if (Wanted(e.FullPath) || Directory.Exists(e.FullPath)) Poke(); };
                watcher.Deleted += (_, e) => { if (Wanted(e.FullPath) || Path.GetExtension(e.FullPath).Length == 0) Poke(); };
                watcher.Renamed += (_, e) => { if (Wanted(e.OldFullPath) || Wanted(e.FullPath) || Directory.Exists(e.FullPath)) Poke(); };
                watcher.Changed += (_, e) => { if (Wanted(e.FullPath)) Poke(); };
                // Usually a full buffer, meaning changes were missed.
                watcher.Error += (_, _) => Poke();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            // Some places can't be watched, like a few network drives; the regular rebuild still covers them.
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        }
    }

    private bool Wanted(string path) => _extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void Poke()
    {
        // An event can still land while the watchers are being replaced.
        try { _quiet.Change(_delay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _quiet.Dispose();
    }
}
