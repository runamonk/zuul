using Zuul.Core;

namespace Zuul.Tests;

public sealed class FolderWatcherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZuulWatch-" + Guid.NewGuid().ToString("N"));
    private int _changes;

    public FolderWatcherTests() => Directory.CreateDirectory(Path.Combine(_folder, "Deep"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private FolderWatcher Watch(bool subfolders = true)
    {
        var watcher = new FolderWatcher([new FolderSource(_folder, subfolders)], FolderIndex.DefaultExtensions, TimeSpan.FromMilliseconds(200));
        watcher.Changed += () => Interlocked.Increment(ref _changes);
        return watcher;
    }

    // Long enough for the events to arrive and the quiet spell to pass.
    private static Task Settle() => Task.Delay(1500);

    [Fact]
    public async Task NewShortcutsAreNoticed()
    {
        using var watcher = Watch();
        File.WriteAllText(Path.Combine(_folder, "Deep", "Test.lnk"), "");
        await Settle();
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task OtherFilesAreIgnored()
    {
        using var watcher = Watch();
        File.WriteAllText(Path.Combine(_folder, "Deep", "scratch.tmp"), "");
        File.AppendAllText(Path.Combine(_folder, "Deep", "scratch.tmp"), "more");
        await Settle();
        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task RenamingToAShortcutCounts()
    {
        var text = Path.Combine(_folder, "Test.txt");
        File.WriteAllText(text, "");
        using var watcher = Watch();
        File.Move(text, Path.Combine(_folder, "Test.lnk"));
        await Settle();
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task ABurstOfChangesIsOneNudge()
    {
        using var watcher = Watch();
        for (var i = 0; i < 50; i++) File.WriteAllText(Path.Combine(_folder, $"App{i}.exe"), "");
        await Settle();
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task SubfoldersCanBeLeftOut()
    {
        using var watcher = Watch(subfolders: false);
        File.WriteAllText(Path.Combine(_folder, "Deep", "Test.lnk"), "");
        await Settle();
        Assert.Equal(0, _changes);
    }
}
