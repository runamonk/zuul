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
    [InlineData(@"C:\f:", false, "")]
    public void ParsesTheFilePrefix(string query, bool found, string term)
    {
        Assert.Equal(found, FileSearch.TryParse(query, FileSearch.DefaultPrefix, out var parsed));
        Assert.Equal(term, parsed);
    }

    [Fact]
    public void ThePrefixCanBeChanged()
    {
        Assert.True(FileSearch.TryParse("ff invoice", "ff ", out var term));
        Assert.Equal("invoice", term);
        Assert.False(FileSearch.TryParse("f: invoice", "ff ", out _));
        // Without the space, "ff" would swallow "firefox"; with it, it doesn't.
        Assert.False(FileSearch.TryParse("ffmpeg", "ff ", out _));
        Assert.False(FileSearch.TryParse("anything", "", out _));
    }

    [Fact]
    public void LeftOutFoldersAndFilesOnlyUseEverythingsSyntax()
    {
        var windows = Environment.ExpandEnvironmentVariables("%SystemRoot%");
        Assert.Equal($"report !path:\"{windows}\\WinSxS\" !path:\"node_modules\" file:",
            FileSearch.BuildQuery("report", includeFolders: false, [@"%SystemRoot%\WinSxS", " node_modules ", "", "\"\""]));
        Assert.Equal("report", FileSearch.BuildQuery("report", includeFolders: true, []));
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
        ]);
        Assert.Equal(["report", "report.pdf", "annual-report.pdf", "summary.txt"], ranked.Select(r => r.Title));
        Assert.Equal(@"C:\Work", ranked[1].Subtitle);
        Assert.Equal(@"C:\Work\report.pdf", ranked[1].Target);
        Assert.All(ranked, r => Assert.Equal(ResultKind.Path, r.Kind));
    }

    // Everything sorts by name, so the best match can be far down its list; all of it gets ranked.
    [Fact]
    public void TheBestMatchWinsEvenWhenEverythingListsItLast()
    {
        var items = Enumerable.Range(0, 999).Select(i => new EverythingItem($@"C:\a\aaa-report-{i:000}.txt", false))
            .Append(new EverythingItem(@"C:\z\report.txt", false));
        Assert.Equal("report.txt", FileSearch.Rank("report", items)[0].Title);
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
    public async Task EverythingLeavesOutWhatItIsTold()
    {
        Assert.SkipUnless(Everything.IsRunning, "Everything isn't running.");
        var token = TestContext.Current.CancellationToken;
        var system = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32");
        var excluded = await Everything.SearchAsync(FileSearch.BuildQuery("comctl32.dll", true, [@"%SystemRoot%\System32"]), false, 1000, TimeSpan.FromSeconds(5), token);
        Assert.DoesNotContain(excluded!, i => i.Path.StartsWith(system + @"\", StringComparison.OrdinalIgnoreCase));
        var files = await Everything.SearchAsync(FileSearch.BuildQuery("windows", false, []), false, 200, TimeSpan.FromSeconds(5), token);
        Assert.NotEmpty(files!);
        Assert.DoesNotContain(files!, i => i.IsFolder);
    }

    [Fact]
    public async Task ACancelledSearchStops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Everything.SearchAsync("x", false, 10, TimeSpan.FromSeconds(5), cancelled.Token));
    }
}
