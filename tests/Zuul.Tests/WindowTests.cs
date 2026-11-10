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

    // Windows would show the last picture of the old results; cloaked, nobody sees it.
    [AvaloniaFact]
    public void OpensCloakedUntilTheNewPictureIsDrawn()
    {
        var window = Open();
        Type(window, "note");
        window.HideLauncher();
        window.ShowLauncher();
        Assert.True(window.Cloaked);
        Assert.True(window.IsVisible);
        for (var i = 0; i < 5 && window.Cloaked; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(window.Cloaked);
    }

    // If no frame comes, the timer still brings it out: an invisible launcher is the worst outcome.
    [AvaloniaFact]
    public async Task ComesOutEvenWithoutAFrame()
    {
        var window = Open();
        window.HideLauncher();
        window.ShowLauncher();
        Assert.True(window.Cloaked);
        for (var i = 0; i < 50 && window.Cloaked; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.False(window.Cloaked);
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

public sealed class ResultMenuTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZuulMenu-" + Guid.NewGuid().ToString("N"));

    public ResultMenuTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string File(string name)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, "");
        return path;
    }

    private static LauncherWindow Open(SearchEngine? engine = null)
    {
        var window = new LauncherWindow(engine ?? new SearchEngine(new UsageStore(null)), new UsageStore(null))
        {
            Editors = () => [new EditorEntry("Notepad++", @"C:\Tools\notepad++.exe"), new EditorEntry("Code", @"C:\Code.exe")]
        };
        window.ShowLauncher();
        return window;
    }

    private static List<string> Headers(LauncherWindow window, LaunchResult result) =>
        window.MenuItems(result).Select(c => c is MenuItem item ? (string)item.Header! : "-").ToList();

    private static LaunchResult PathResult(string path) => new(ResultKind.Path, Path.GetFileName(path), path, path, 0);

    private static void Click(LauncherWindow window, LaunchResult result, string header) =>
        window.MenuItems(result).OfType<MenuItem>().Single(i => (string)i.Header! == header)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public void TextFilesCanBeEditedButNotRunAsAdministrator()
    {
        var window = Open();
        Assert.Equal(["Open", "Edit with Notepad++", "Edit with Code", "-", "Open location", "Open terminal here", "-",
            "Copy path", "Copy name", "-", "Hide from results", "Properties"], Headers(window, PathResult(File("notes.txt"))));
    }

    [AvaloniaFact]
    public void ProgramsRunAsAdministratorButAreNotEdited()
    {
        var headers = Headers(Open(), PathResult(File("tool.exe")));
        Assert.Contains("Run as administrator", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Edit with"));
    }

    [AvaloniaFact]
    public void FoldersGetNoEditorsOrAdministrator()
    {
        var headers = Headers(Open(), PathResult(_folder));
        Assert.Equal(["Open", "-", "Open location", "Open terminal here", "-", "Copy path", "Copy name", "-", "Hide from results", "Properties"], headers);
    }

    [AvaloniaFact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ShortcutsCanOpenWhereTheyPoint()
    {
        var target = File("real.cmd");
        var link = Path.Combine(_folder, "Real.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = target;
        shortcut.Save();
        var headers = Headers(Open(), PathResult(link));
        Assert.Contains("Open target location", headers);
        Assert.Contains("Run as administrator", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Edit with"));
    }

    [AvaloniaFact]
    public void AppsAndEverythingElse()
    {
        var window = Open();
        Assert.Equal(["Open", "Run as administrator", "-", "Copy name", "-", "Hide from results"],
            Headers(window, new LaunchResult(ResultKind.App, "Calculator", "Store app", "Microsoft.WindowsCalculator!App", 0)));
        Assert.Empty(window.MenuItems(new LaunchResult(ResultKind.Calculator, "= 4", "2+2", "4", 0)));
        // Gone since it was found: nothing to offer.
        Assert.Empty(window.MenuItems(PathResult(Path.Combine(_folder, "gone.txt"))));
    }

    [AvaloniaFact]
    public void HidingAsksTheAppAndKeepsTheLauncherOpen()
    {
        var window = Open();
        LaunchResult? hidden = null;
        window.HideRequested += r => hidden = r;
        var result = PathResult(File("junk.txt"));
        Click(window, result, "Hide from results");
        Assert.Equal(result, hidden);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task CopyPathCopiesAndCloses()
    {
        var window = Open();
        var path = File("report.txt");
        Click(window, PathResult(path), "Copy path");
        Assert.False(window.IsVisible);
        Assert.Equal(path, await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
    }

    [AvaloniaFact]
    public void HiddenResultsLeaveTheSearch()
    {
        var engine = new SearchEngine(new UsageStore(null))
        {
            Files = [new FileEntry("report.txt", @"C:\Work\report.txt", 0), new FileEntry("report.pdf", @"C:\Work\report.pdf", 0)],
            Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"Path:c:\work\REPORT.TXT" },
            WebSearchUrl = ""
        };
        Assert.Equal(["report.pdf"], engine.Search("report").Select(r => r.Title));
    }

    [AvaloniaFact]
    public void TheMenuKeyOpensTheMenuForTheSelectedRow()
    {
        var engine = new SearchEngine(new UsageStore(null)) { Files = [new FileEntry("notes.txt", File("notes.txt"), 0)], WebSearchUrl = "" };
        var window = Open(engine);
        window.Query.Text = "notes";
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.MenuIsOpen);
        // While it's open, Esc is the menu's, not the launcher's.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(window.IsVisible);
    }
}

public sealed class ToastTests
{
    [AvaloniaFact]
    public void ShowsTextAndABarOnlyWithProgress()
    {
        var toast = new ToastWindow();
        toast.ShowMessage("Checking for updates…");
        Assert.True(toast.IsVisible);
        Assert.Equal("Checking for updates…", toast.Message.Text);
        Assert.False(toast.Progress.IsVisible);
        toast.ShowMessage("Downloading Zuul 1.2.0… 45%", 45);
        Assert.True(toast.Progress.IsVisible);
        Assert.Equal(45, toast.Progress.Value);
        // A click puts it away; the next message brings it back.
        toast.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(toast, new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true), toast,
            default, 0, new Avalonia.Input.PointerPointProperties(RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        Assert.False(toast.IsVisible);
        toast.ShowMessage("Back");
        Assert.True(toast.IsVisible);
        toast.Dismiss();
    }

    [AvaloniaFact]
    public async Task GoesAwayByItselfWhenAsked()
    {
        var toast = new ToastWindow();
        toast.ShowMessage("You're on the latest version (1.0.0).", hideAfter: TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 100 && toast.IsVisible; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.False(toast.IsVisible);
    }

    // A new message without a time limit cancels the old one's, so progress doesn't vanish halfway.
    [AvaloniaFact]
    public async Task ANewMessageCancelsTheOldTimeLimit()
    {
        var toast = new ToastWindow();
        toast.ShowMessage("Short", hideAfter: TimeSpan.FromMilliseconds(50));
        toast.ShowMessage("Downloading… 10%", 10);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.True(toast.IsVisible);
        toast.Dismiss();
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
