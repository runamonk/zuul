using Zuul.Core;

namespace Zuul.Tests;

public sealed class CalculatorTests
{
    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("2^3^2", 512)]
    [InlineData("-2^2", -4)]
    [InlineData("10 % 4", 2)]
    [InlineData("sqrt(16) + 1", 5)]
    [InlineData("1,000 * 2", 2000)]
    [InlineData("3x4", 12)]
    [InlineData("(1+2", 3)]
    [InlineData("2*pi", 2 * Math.PI)]
    [InlineData("5 * 5 =", 25)]
    public void Evaluates(string text, double expected)
    {
        Assert.True(Calculator.TryEvaluate(text, out var value));
        Assert.Equal(expected, value, 10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("-5")]
    [InlineData("notepad")]
    [InlineData("1/0")]
    [InlineData("2 +")]
    [InlineData("foo(2)")]
    [InlineData("pi")]
    public void IgnoresThingsThatArentSums(string text) => Assert.False(Calculator.TryEvaluate(text, out _));

    [Fact]
    public void PlainHasNoFloatFuzz() => Assert.Equal("0.3", Calculator.Plain(0.1 + 0.2));
}

public sealed class FuzzyMatchTests
{
    [Fact]
    public void RanksPrefixAboveWordAboveSubstringAboveScatter()
    {
        var prefix = FuzzyMatch.Score("code", "Code Insiders");
        var word = FuzzyMatch.Score("code", "Visual Studio Code");
        var substring = FuzzyMatch.Score("ode", "Visual Studio Code");
        var scatter = FuzzyMatch.Score("vsd", "Visual Studio Code");
        Assert.True(prefix > word && word > substring && substring > scatter && scatter > 0);
    }

    [Theory]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("ps", "PowerShell")]
    [InlineData("studio code", "Visual Studio Code")]
    public void MatchesInitialsAndWords(string query, string text) => Assert.True(FuzzyMatch.Score(query, text) >= 650);

    [Fact]
    public void NoMatchIsNegative() => Assert.Equal(-1, FuzzyMatch.Score("xyz", "Notepad"));

    [Fact]
    public void ExactBeatsEverything() => Assert.Equal(1000, FuzzyMatch.Score("NOTEPAD", "Notepad"));
}

public sealed class HotKeyTextTests
{
    [Theory]
    [InlineData("Alt+Space", HotKeyText.Alt, 0x20u)]
    [InlineData("Ctrl+Shift+D5", HotKeyText.Control | HotKeyText.Shift, 0x35u)]
    [InlineData("Win+K", HotKeyText.Win, 0x4Bu)]
    [InlineData("F13", 0u, 0x7Cu)]
    public void Parses(string text, uint modifiers, uint key)
    {
        Assert.True(HotKeyText.TryParse(text, out var m, out var k));
        Assert.Equal(modifiers, m);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("Alt")]
    [InlineData("Alt+Banana")]
    [InlineData("Alt+A+B")]
    public void RejectsUnusable(string text) => Assert.False(HotKeyText.TryParse(text, out _, out _));

    [Fact]
    public void FormatsInAFixedOrder() => Assert.Equal("Ctrl+Alt+Shift+Win+Space", HotKeyText.Format(true, true, true, true, "Space"));
}

public sealed class SearchEngineTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZuulTests-" + Guid.NewGuid().ToString("N"));

    public SearchEngineTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private SearchEngine Engine(UsageStore? usage = null) => new(usage ?? new UsageStore(null))
    {
        Apps =
        [
            new("Notepad", "notepad", "C:\\Windows\\notepad.exe"),
            new("Notepad++", "notepad++", "C:\\Program Files\\Notepad++\\notepad++.exe"),
            new("Visual Studio Code", "code", "C:\\Code.exe"),
            new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Store app"),
        ]
    };

    [Fact]
    public void EmptyQueryFindsNothing() => Assert.Empty(Engine().Search("   "));

    [Fact]
    public void FindsAppsAndEndsWithWebSearch()
    {
        var results = Engine().Search("note");
        Assert.Equal(["Notepad", "Notepad++"], results.Where(r => r.Kind == ResultKind.App).Select(r => r.Title));
        Assert.Equal(ResultKind.Web, results[^1].Kind);
        Assert.Equal("https://www.google.com/search?q=note", results[^1].Target);
    }

    [Fact]
    public void SumsComeFirst()
    {
        var results = Engine().Search("12*12");
        Assert.Equal(ResultKind.Calculator, results[0].Kind);
        Assert.Equal("144", results[0].Target);
    }

    [Fact]
    public void UsageLiftsAnApp()
    {
        var usage = new UsageStore(Path.Combine(_folder, "usage.json"));
        for (var i = 0; i < 10; i++) usage.Record($"{ResultKind.App}:notepad++");
        Assert.Equal("Notepad++", Engine(usage).Search("note")[0].Title);
        // And it was saved for next time.
        Assert.Equal(10, new UsageStore(Path.Combine(_folder, "usage.json")).Count($"{ResultKind.App}:notepad++"));
    }

    [Fact]
    public void FindsSystemCommandsByAlias()
    {
        var result = Engine().Search("reboot").First(r => r.Kind == ResultKind.System);
        Assert.Equal("Restart", result.Title);
        Assert.True(result.Confirm);
    }

    [Fact]
    public void ChevronRunsACommand()
    {
        var results = Engine().Search("> ipconfig /all");
        Assert.Single(results);
        Assert.Equal(ResultKind.Command, results[0].Kind);
        Assert.Equal("ipconfig /all", results[0].Target);
    }

    [Fact]
    public void RecognisesUrls()
    {
        var result = Engine().Search("www.example.com")[0];
        Assert.Equal(ResultKind.Url, result.Kind);
        Assert.Equal("https://www.example.com/", result.Target);
    }

    [Fact]
    public void ListsAFolderAsYouType()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Alpha"));
        File.WriteAllText(Path.Combine(_folder, "Apple.txt"), "");
        File.WriteAllText(Path.Combine(_folder, "Banana.txt"), "");
        var results = Engine().Search(Path.Combine(_folder, "A")).Where(r => r.Kind == ResultKind.Path).Select(r => r.Title);
        // Folders before files.
        Assert.Equal(["Alpha", "Apple.txt"], results);
    }

    [Fact]
    public void WebSearchCanBeTurnedOff()
    {
        var engine = Engine();
        engine.WebSearchUrl = "";
        Assert.DoesNotContain(engine.Search("note"), r => r.Kind == ResultKind.Web);
    }

    [Fact]
    public void KeepsToTheLimit()
    {
        var engine = Engine();
        engine.MaxResults = 3;
        Assert.Equal(3, engine.Search("a").Count);
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("MSEdge", false)]
    [InlineData(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe", false)]
    [InlineData(@"C:\Program Files\WinRAR\WhatsNew.txt", false)]
    [InlineData("steam://rungameid/31110", false)]
    [InlineData("", false)]
    public void OnlyStoreAppsComeFromTheShell(string parsingName, bool store) => Assert.Equal(store, AppIndex.IsStoreApp(parsingName));
}
