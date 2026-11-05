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
        window.ShowLauncher();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", window.Query.Text);
        Assert.False(window.ResultsPanel.IsVisible);
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
