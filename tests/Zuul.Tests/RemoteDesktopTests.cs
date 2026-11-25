using Microsoft.Win32;
using Zuul.Core;

namespace Zuul.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RemoteDesktopTests : IDisposable
{
    // Laid out like Remote Desktop's own key, somewhere harmless.
    private readonly string _key = @"Software\ZuulTests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    private void Write(string[] recent, params (string Computer, string? User)[] servers)
    {
        using var root = Registry.CurrentUser.CreateSubKey(_key);
        using (var dropdown = root.CreateSubKey("Default"))
            for (var i = 0; i < recent.Length; i++) dropdown.SetValue("MRU" + i, recent[i]);
        foreach (var (computer, user) in servers)
        {
            using var server = root.CreateSubKey(@"Servers\" + computer);
            if (user != null) server.SetValue("UsernameHint", user);
        }
    }

    [Fact]
    public void TheDropdownComesFirstThenTheRest()
    {
        Write(["workacfr", "RTNDVWSRHINES99", "anton", "  "],
            ("anton", @"ANTON\runamonk"), ("monk", "ron@example.com"), ("Lister", null), ("workacfr", @"work\runamonk"));
        Assert.Equal(
        [
            new RemoteDesktop("workacfr", @"work\runamonk"), new RemoteDesktop("RTNDVWSRHINES99", null), new RemoteDesktop("anton", @"ANTON\runamonk"),
            new RemoteDesktop("Lister", null), new RemoteDesktop("monk", "ron@example.com"),
        ], RemoteDesktops.Load(_key));
    }

    [Fact]
    public void NeverUsedMeansNone() => Assert.Empty(RemoteDesktops.Load(_key));

    [Theory]
    [InlineData("remote", "")]
    [InlineData("RDP", "")]
    [InlineData("rdp work", "work")]
    [InlineData("remote desktop", "")]
    [InlineData("remote desktop anton", "anton")]
    [InlineData("mstsc  anton ", "anton")]
    [InlineData("remotes", null)]
    [InlineData("rdpx", null)]
    [InlineData("anton", null)]
    public void KeywordsListTheComputers(string query, string? rest) => Assert.Equal(rest, RemoteDesktops.AfterKeyword(query));

    private static SearchEngine Engine(UsageStore? usage = null) => new(usage ?? new UsageStore(null))
    {
        Remotes = [new("workacfr", @"work\runamonk"), new("RTNDVWSRHINES99", null), new("anton", @"ANTON\runamonk"), new("work", null)],
        Apps = [new("Remote Desktop Connection", "mstsc", "Start menu")],
        WebSearchUrl = ""
    };

    private static List<string> Remotes(IEnumerable<LaunchResult> results) =>
        results.Where(r => r.Kind == ResultKind.Remote).Select(r => r.Title).ToList();

    [Fact]
    public void TheKeywordListsAllInRemoteDesktopsOrderAboveEverythingElse()
    {
        var results = Engine().Search("remote");
        Assert.Equal(["workacfr", "RTNDVWSRHINES99", "anton", "work"], results.Take(4).Select(r => r.Title));
        Assert.Contains(results, r => r.Title == "Remote Desktop Connection");
        Assert.Equal(@"Remote desktop · ANTON\runamonk", results.First(r => r.Title == "anton").Subtitle);
        Assert.Equal("Remote desktop", results.First(r => r.Title == "RTNDVWSRHINES99").Subtitle);
    }

    [Fact]
    public void WordsAfterTheKeywordNarrowItDown() => Assert.Equal(["work", "workacfr"], Remotes(Engine().Search("rdp work")));

    [Fact]
    public void TheNameAloneFindsIt()
    {
        var results = Engine().Search("anto");
        Assert.Equal("anton", results[0].Title);
        Assert.Equal(ResultKind.Remote, results[0].Kind);
        Assert.Equal("anton", results[0].Target);
    }

    [Fact]
    public void HiddenOnesStayHidden()
    {
        var engine = Engine();
        engine.Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Remote:ANTON" };
        Assert.DoesNotContain("anton", Remotes(engine.Search("remote")));
    }

    // Read from the real key where there is one, so not on the build server.
    [Fact]
    public void ReadsThisComputersList()
    {
        using var real = Registry.CurrentUser.OpenSubKey(RemoteDesktops.Key + @"\Default");
        Assert.SkipUnless(real?.GetValue("MRU0") is string, "Remote Desktop has never been used here.");
        Assert.Equal((string)real!.GetValue("MRU0")!, RemoteDesktops.Load()[0].Computer);
    }
}
