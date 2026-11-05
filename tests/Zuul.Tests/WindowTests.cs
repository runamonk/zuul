using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Zuul.Core;

[assembly: AvaloniaTestApplication(typeof(Zuul.Tests.TestApplication))]
// All UI tests share one dispatcher.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Zuul.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class LauncherWindowTests
{
    private static LauncherWindow Open()
    {
        var engine = new SearchEngine(new UsageStore(null)) { Apps = [new("Notepad", "notepad", "C:\\Windows\\notepad.exe")] };
        var window = new LauncherWindow(engine, new UsageStore(null));
        window.ShowLauncher();
        return window;
    }

    // TextChanged arrives through the dispatcher.
    private static void Type(LauncherWindow window, string text)
    {
        window.Query.Text = text;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TypingShowsResults()
    {
        var window = Open();
        Assert.False(window.ResultsPanel.IsVisible);
        Type(window, "note");
        Assert.True(window.ResultsPanel.IsVisible);
        Assert.Equal("Notepad", ((ResultItem)window.Results.SelectedItem!).Title);
    }

    [AvaloniaFact]
    public void ArrowsMoveAndStopAtTheEnds()
    {
        var window = Open();
        Type(window, "note");
        var count = window.Results.ItemCount;
        for (var i = 0; i < count + 2; i++) window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(count - 1, window.Results.SelectedIndex);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(count - 2, window.Results.SelectedIndex);
    }

    [AvaloniaFact]
    public void EscapeAndAltF4OnlyHide()
    {
        var window = Open();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
        window.ShowLauncher();
        window.Close();
        Assert.False(window.IsVisible);
        // Still usable, so it wasn't really closed.
        window.ShowLauncher();
        Assert.True(window.IsVisible);
        window.Quit();
    }

    [AvaloniaFact]
    public async Task DangerousCommandsAskForASecondEnter()
    {
        var window = Open();
        Type(window, "shut down");
        var item = (ResultItem)window.Results.SelectedItem!;
        Assert.Equal("Shut down", item.Title);
        // Only the first press: a second would really shut down the test machine.
        await window.Run(admin: false);
        Assert.True(window.IsVisible);
        Assert.Equal("Press Enter again to shut down", ((ResultItem)window.Results.SelectedItem!).Subtitle);
        // Typing again disarms it.
        Type(window, "shut dow");
        Assert.Equal("Turn this computer off", ((ResultItem)window.Results.SelectedItem!).Subtitle);
    }

    [AvaloniaFact]
    public async Task InternalCommandsGoToTheApp()
    {
        var window = Open();
        string? asked = null;
        window.InternalCommand += c => asked = c;
        Type(window, "zuul settings");
        await window.Run(admin: false);
        Assert.Equal("settings", asked);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void ReopeningStartsEmpty()
    {
        var window = Open();
        Type(window, "note");
        window.HideLauncher();
        // The old results must be gone when the window appears, not a moment later, or it visibly shrinks.
        bool? resultsWhenShown = null;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Visual.IsVisibleProperty && window.IsVisible) resultsWhenShown = window.ResultsPanel.IsVisible;
        };
        window.ShowLauncher();
        Assert.False(resultsWhenShown);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", window.Query.Text);
        Assert.False(window.ResultsPanel.IsVisible);
    }

    // Runs the dispatcher until done says so, or two seconds pass.
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Titles(LauncherWindow window) => window.Results.Items.Cast<ResultItem>().Select(i => i.Title).ToArray();

    [AvaloniaFact]
    public async Task FileSearchShowsWhatEverythingFinds()
    {
        var window = Open();
        string? asked = null;
        window.AskEverything = (term, _) =>
        {
            asked = term;
            return Task.FromResult<IReadOnlyList<EverythingItem>?>([new(@"C:\Work\annual-report.pdf", false), new(@"C:\Work\report.pdf", false)]);
        };
        Type(window, "f: report");
        await Until(() => Titles(window).Contains("report.pdf"));
        Assert.Equal("report", asked);
        Assert.Equal(["report.pdf", "annual-report.pdf"], Titles(window));
    }

    // The answer to "a" arrives after "ab" was typed, and must never show.
    [AvaloniaFact]
    public async Task TypingOnThrowsAwayTheOldAnswer()
    {
        var window = Open();
        var tokens = new Dictionary<string, CancellationToken>();
        window.AskEverything = async (term, token) =>
        {
            tokens[term] = token;
            if (term == "a") await Task.Delay(300, CancellationToken.None);
            return [new(@"C:\Work\" + term + ".txt", false)];
        };
        Type(window, "f: a");
        await Until(() => tokens.ContainsKey("a"));
        Type(window, "f: ab");
        await Until(() => Titles(window).Contains("ab.txt"));
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["ab.txt"], Titles(window));
        Assert.True(tokens["a"].IsCancellationRequested);
    }

    [AvaloniaFact]
    public async Task EscapeCancelsTheFileSearch()
    {
        var window = Open();
        CancellationToken? asked = null;
        window.AskEverything = async (_, token) =>
        {
            asked = token;
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };
        Type(window, "f: report");
        await Until(() => asked != null);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
        Assert.True(asked!.Value.IsCancellationRequested);
    }

    [AvaloniaFact]
    public async Task SaysSoWhenEverythingIsNotRunning()
    {
        var window = Open();
        window.AskEverything = (_, _) => Task.FromResult<IReadOnlyList<EverythingItem>?>(null);
        Type(window, "f: report");
        await Until(() => Titles(window).Contains("Everything isn't running"));
        // Enter on a message does nothing.
        await window.Run(admin: false);
        Assert.True(window.IsVisible);
    }

    // Enter before the answer runs the top new result, not the stale row on screen.
    [AvaloniaFact]
    public async Task EnterBeforeTheAnswerRunsTheTopNewResult()
    {
        var window = Open();
        var missing = Path.Combine(Path.GetTempPath(), "zuul-" + Guid.NewGuid().ToString("N"), "found.txt");
        var answer = new TaskCompletionSource<IReadOnlyList<EverythingItem>?>();
        window.AskEverything = (_, _) => answer.Task;
        Type(window, "f: found");
        await Until(() => Titles(window).Contains("Searching Everything…"));
        await window.Run(admin: false);
        answer.SetResult([new(missing, false)]);
        // It doesn't exist, so running it shows why; that's the proof it ran.
        await Until(() => window.Notice.IsVisible);
        Assert.Contains("found.txt", window.NoticeText.Text);
    }

    private static Func<string, CancellationToken, Task<IReadOnlyList<EverythingItem>?>> Files(int count) =>
        (_, _) => Task.FromResult<IReadOnlyList<EverythingItem>?>(
            Enumerable.Range(1, count).Select(i => new EverythingItem($@"C:\Work\report {i:00}.txt", false)).ToList());

    [AvaloniaFact]
    public async Task FileResultsComeABatchAtATime()
    {
        var window = Open();
        window.FileOptions = new EverythingSettings(BatchSize: 8);
        window.AskEverything = Files(20);
        Type(window, "f: report");
        await Until(() => Titles(window).Length == 9);
        Assert.Equal("Show 8 more", Titles(window)[^1]);
        Assert.StartsWith("8 of 20 shown", ((ResultItem)window.Results.Items[^1]!).Subtitle);

        // Enter on the last row adds the next batch and selects its first row.
        window.Results.SelectedIndex = 8;
        await window.Run(admin: false);
        Assert.Equal(17, Titles(window).Length);
        Assert.Equal("report 09.txt", ((ResultItem)window.Results.SelectedItem!).Title);
        Assert.Equal("Show 4 more", Titles(window)[^1]);

        // The last batch has no row after it. The rows already seen keep their places.
        window.Results.SelectedIndex = 16;
        await window.Run(admin: false);
        Assert.Equal(20, Titles(window).Length);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"report {i:00}.txt"), Titles(window));
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task DownOnTheLastRowGetsTheNextBatch()
    {
        var window = Open();
        window.FileOptions = new EverythingSettings(BatchSize: 3);
        window.AskEverything = Files(5);
        Type(window, "f: report");
        await Until(() => Titles(window).Length == 4);
        for (var i = 0; i < 3; i++) window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal("Show 2 more", ((ResultItem)window.Results.SelectedItem!).Title);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(5, Titles(window).Length);
        Assert.Equal("report 04.txt", ((ResultItem)window.Results.SelectedItem!).Title);
    }

    // Everything hit its limit, so there may be more than it sent.
    [AvaloniaFact]
    public async Task AFullAnswerSaysThereMayBeMore()
    {
        var window = Open();
        window.AskEverything = Files(FileSearch.Fetch);
        Type(window, "f: report");
        await Until(() => Titles(window).Length == 9);
        Assert.StartsWith("8 of 1,000+ shown", ((ResultItem)window.Results.Items[^1]!).Subtitle);
    }

    [AvaloniaFact]
    public async Task ThePrefixComesFromSettings()
    {
        var window = Open();
        window.FileOptions = new EverythingSettings(Prefix: "ff ");
        window.AskEverything = Files(1);
        Type(window, "f: report");
        Assert.DoesNotContain("Searching Everything…", Titles(window));
        Type(window, "ff report");
        await Until(() => Titles(window).Contains("report 01.txt"));
    }

    [AvaloniaFact]
    public void ReopeningCanSelectTheLastSearch()
    {
        var window = Open();
        window.RememberSearch = true;
        Type(window, "note");
        window.HideLauncher();
        window.ShowLauncher();
        Assert.Equal("note", window.Query.SelectedText);
    }
}

public sealed class ThemeTests
{
    [Fact]
    public void DefaultThemesAreValid()
    {
        var themes = ThemeCatalog.Parse(ThemeCatalog.Serialize(AppThemes.All));
        Assert.Contains(themes, t => t.Name == "Light");
        Assert.Contains(themes, t => t.Name == "Dark");
    }

    [Fact]
    public void RejectsBadColors() =>
        Assert.Throws<InvalidDataException>(() => ThemeCatalog.Parse("""[{"Name":"X","Dark":true,"Surface":"nope","Text":"#FFF","Accent":"#FFF","Selection":"#FFF","Icon":"#FFF","Line":"#FFF","Link":"#FFF"}]"""));

    [Fact]
    public void FollowsWindowsWhenNoThemeIsPicked()
    {
        var catalog = new ThemeCatalog(Path.Combine(Path.GetTempPath(), "ZuulThemes-" + Guid.NewGuid().ToString("N"), "settings.json"));
        Assert.Equal("Dark", catalog.Resolve(new ZuulSettings(), systemDark: true).Name);
        Assert.Equal("Light", catalog.Resolve(new ZuulSettings(), systemDark: false).Name);
        Assert.Equal("Dracula", catalog.Resolve(new ZuulSettings(ColorTheme: "dracula"), systemDark: false).Name);
    }

    [AvaloniaFact]
    public void SettingsPreviewsAndSavesTheme()
    {
        var catalog = new ThemeCatalog(Path.Combine(Path.GetTempPath(), "ZuulThemes-" + Guid.NewGuid().ToString("N"), "settings.json"));
        var dialog = new SettingsDialog(new ZuulSettings(), catalog, startWithWindows: false);
        ZuulSettings? previewed = null;
        dialog.ThemePreview += s => previewed = s;
        dialog.Show();
        dialog.Themes.SelectedItem = ((IEnumerable<ThemeChoice>)dialog.Themes.ItemsSource!).First(c => c.Name == "Midnight");
        Assert.Equal("Midnight", previewed?.ColorTheme);
        Assert.True(previewed?.DarkTheme);
        dialog.Close();
        // Closing without Save keeps nothing.
        Assert.Null(dialog.Saved);
    }
}
