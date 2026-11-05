using System.Text;
using Zuul.Core;

namespace Zuul.Tests;

public sealed class EverythingTests
{
    [Theory]
    [InlineData("f: invoice", true, "invoice")]
    [InlineData("F:invoice 2026", true, "invoice 2026")]
    [InlineData("  f:  ", true, "")]
    [InlineData("firefox", false, "")]
    [InlineData("C:\f:", false, "")]
    public void ParsesTheFilePrefix(string query, bool found, string term)
    {
        Assert.Equal(found, FileSearch.TryParse(query, out var parsed));
        Assert.Equal(term, parsed);
    }

    [Fact]
    public void ABackslashMatchesWholePaths()
    {
        Assert.True(FileSearch.MatchPath(@"tools\zuul"));
        Assert.False(FileSearch.MatchPath("zuul"));
    }

    [Fact]
    public void ClosestNamesComeFirstAndPathMatchesStay()
    {
        var ranked = FileSearch.Rank("report",
        [
            new(@"C:\Work\old reports\summary.txt", false),
            new(@"C:\Work\annual-report.pdf", false),
            new(@"C:\Work\report.pdf", false),
            new(@"D:\report", true),
        ], 10);
        Assert.Equal(["report", "report.pdf", "annual-report.pdf", "summary.txt"], ranked.Select(r => r.Title));
        Assert.Equal(@"C:\Work", ranked[1].Subtitle);
        Assert.Equal(@"C:\Work\report.pdf", ranked[1].Target);
        Assert.All(ranked, r => Assert.Equal(ResultKind.Path, r.Kind));
        Assert.Equal(2, FileSearch.Rank("report", [new(@"C:\a\report", false), new(@"C:\b\report", false), new(@"C:\c\report", false)], 2).Count);
    }

    // Built by hand the way Everything lays out EVERYTHING_IPC_LIST2.
    [Fact]
    public void ReadsEverythingsAnswer()
    {
        string[] paths = [@"C:\Windows\notepad.exe", @"C:\Tools"];
        var data = new MemoryStream();
        var writer = new BinaryWriter(data);
        writer.Write(2); writer.Write(2); writer.Write(0); writer.Write(4); writer.Write(1);
        var offset = 20 + paths.Length * 8;
        for (var i = 0; i < paths.Length; i++)
        {
            writer.Write(i == 1 ? 1 : 0);
            writer.Write(offset);
            offset += 4 + (paths[i].Length + 1) * 2;
        }
        foreach (var path in paths)
        {
            writer.Write(path.Length);
            writer.Write(Encoding.Unicode.GetBytes(path + "\0"));
        }
        var items = Everything.Parse(data.ToArray());
        Assert.Equal([new EverythingItem(paths[0], false), new EverythingItem(paths[1], true)], items);
    }

    // Only where Everything runs, so not on the build server.
    [Fact]
    public async Task FindsNotepadWhenEverythingRuns()
    {
        Assert.SkipUnless(Everything.IsRunning, "Everything isn't running.");
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var items = await Everything.SearchAsync("notepad.exe", false, 50, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains(items!, i => i.Path.Equals(notepad, StringComparison.OrdinalIgnoreCase));
        var byPath = await Everything.SearchAsync(@"windows\notepad.exe", true, 50, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains(byPath!, i => i.Path.Equals(notepad, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACancelledSearchStops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Everything.SearchAsync("x", false, 10, TimeSpan.FromSeconds(5), cancelled.Token));
    }
}
