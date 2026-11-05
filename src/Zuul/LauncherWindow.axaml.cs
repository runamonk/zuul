using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Zuul.Core;

namespace Zuul;

public sealed record ResultItem(LaunchResult Result)
{
    public string Title => Result.Title;
    public string Subtitle => Result.Subtitle;

    // Real icons for apps and files; a glyph from Segoe Fluent Icons for everything else.
    public Bitmap? Icon => Result.Kind switch
    {
        ResultKind.App => ShellIcons.Get(@"shell:AppsFolder\" + Result.Target, 64),
        ResultKind.Path => ShellIcons.Get(Result.Target, 64),
        _ => null
    };

    public string? Glyph => Icon != null ? null : Result.Kind switch
    {
        ResultKind.Calculator => "",
        ResultKind.Url => "",
        ResultKind.Web => "",
        ResultKind.Command => "",
        ResultKind.Path => Directory.Exists(Result.Target) ? "" : "",
        ResultKind.App => "",
        ResultKind.Info => "",
        _ => Result.Target switch
        {
            "lock" => "", "sleep" => "", "hibernate" => "", "signout" => "", "restart" => "",
            "shutdown" => "", "recyclebin" => "", "settings" => "", "reload" => "", "update" => "",
            _ => ""
        }
    };
}

public partial class LauncherWindow : Window
{
    private readonly SearchEngine _engine = new(new UsageStore(null));
    private readonly UsageStore _usage = new(null);
    private readonly ObservableCollection<ResultItem> _items = [];
    // The result waiting for a second Enter, for things like Shut down.
    private string? _armed;
    private bool _quitting;

    // Settings, reload and quit belong to the app, not this window.
    internal event Action<string>? InternalCommand;

    // Off: every open starts empty. On: the last search is back, selected.
    internal bool RememberSearch { get; set; }

    // f: searches go to Everything once typing pauses this long.
    internal static TimeSpan FileSearchDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    // Swapped out by tests; null back means Everything isn't running.
    internal Func<string, CancellationToken, Task<IReadOnlyList<EverythingItem>?>> AskEverything { get; set; } =
        (term, token) => Everything.SearchAsync(term, FileSearch.MatchPath(term), FileSearch.Fetch, TimeSpan.FromSeconds(2), token);
    // The f: search in flight; cancelled by the next keystroke, Esc, or anything else that hides the launcher.
    private CancellationTokenSource? _fileSearch;
    // Enter pressed before Everything answered: the admin choice, to run the top result once it does.
    private bool? _runWhenFound;

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public LauncherWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Results.ItemsSource = _items;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        // A launcher that stays up after you click elsewhere is just in the way.
        Deactivated += (_, _) => HideLauncher();
    }

    internal LauncherWindow(SearchEngine engine, UsageStore usage) : this()
    {
        _engine = engine;
        _usage = usage;
    }

    public void ShowLauncher()
    {
        // Cleared and searched before showing, so it opens at its new size instead of shrinking on screen.
        if (!RememberSearch) Query.Text = "";
        Search();
        PlaceOnScreen();
        Show();
        Activate();
        Query.Focus();
        // Typing replaces a remembered search; arrow keys or Enter pick it up again.
        Query.SelectAll();
    }

    public void HideLauncher()
    {
        _armed = null;
        CancelFileSearch();
        Notice.IsVisible = false;
        Hide();
    }

    public void Toggle()
    {
        if (IsVisible && IsActive) HideLauncher();
        else ShowLauncher();
    }

    public void ShowNotice(string text)
    {
        NoticeText.Text = text;
        Notice.IsVisible = true;
    }

    public void Quit()
    {
        _quitting = true;
        Close();
    }

    // Alt+F4 hides it like Esc does; only Quit really closes it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            HideLauncher();
        }
        base.OnClosing(e);
    }

    // On the screen with the mouse, centred, a sixth of the way down.
    private void PlaceOnScreen()
    {
        var screen = (OperatingSystem.IsWindows() && GetCursorPos(out var cursor) ? Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y)) : null)
            ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var width = (int)(Width * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + area.Height / 6);
    }

    private void Query_Changed(object? sender, TextChangedEventArgs e) => Search();

    internal void Search()
    {
        _armed = null;
        Notice.IsVisible = false;
        CancelFileSearch();
        if (FileSearch.TryParse(Query.Text ?? "", out var term))
        {
            if (term.Length == 0) ShowResults([Info("Type a file name after f:", "Everything searches the whole computer")]);
            else SearchFiles(term);
            return;
        }
        ShowResults(_engine.Search(Query.Text ?? ""));
    }

    private void ShowResults(IEnumerable<LaunchResult> results)
    {
        _items.Clear();
        foreach (var result in results) _items.Add(new ResultItem(result));
        ResultsPanel.IsVisible = _items.Count > 0;
        Results.SelectedIndex = _items.Count == 0 ? -1 : 0;
    }

    private static LaunchResult Info(string title, string subtitle) => new(ResultKind.Info, title, subtitle, "", 0);

    // The last results stay up while Everything is asked, so the list doesn't blink on every key.
    // Results from a search that was cancelled meanwhile are thrown away.
    private async void SearchFiles(string term)
    {
        var search = _fileSearch = new CancellationTokenSource();
        if (_items.All(i => i.Result.Kind == ResultKind.Info)) ShowResults([Info("Searching Everything…", term)]);
        IReadOnlyList<LaunchResult> results;
        try
        {
            await Task.Delay(FileSearchDelay, search.Token);
            var items = await AskEverything(term, search.Token);
            if (search.IsCancellationRequested) return;
            results = items == null ? [Info("Everything isn't running", "Start Everything to search files with f:")]
                : items.Count == 0 ? [Info("No files match", term)]
                : FileSearch.Rank(term, items, _engine.MaxResults);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is TimeoutException or IOException or Win32Exception)
        {
            if (search.IsCancellationRequested) return;
            results = [Info("Everything didn't answer", e.Message)];
        }
        _fileSearch = null;
        ShowResults(results);
        if (_runWhenFound is { } admin && results[0].Kind != ResultKind.Info) _ = Run(admin);
        _runWhenFound = null;
    }

    private void CancelFileSearch()
    {
        _fileSearch?.Cancel();
        _fileSearch = null;
        _runWhenFound = null;
    }

    private void Move(int by)
    {
        if (_items.Count == 0) return;
        _armed = null;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + by, 0, _items.Count - 1);
        Results.ScrollIntoView(Results.SelectedIndex);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: HideLauncher(); break;
            case Key.Enter: _ = Run(e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)); break;
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(5); break;
            case Key.PageUp: Move(-5); break;
            default: return;
        }
        e.Handled = true;
    }

    // Only run when an item was clicked, not the scrollbar or empty space.
    private void Results_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null) _ = Run(admin: false);
    }

    internal async Task Run(bool admin)
    {
        // Still waiting on Everything: the rows showing are stale, so run the top new one when it lands.
        if (_fileSearch != null)
        {
            _runWhenFound = admin;
            return;
        }
        if (Results.SelectedItem is not ResultItem { Result: var result } || result.Kind == ResultKind.Info) return;
        if (result.Confirm && _armed != result.Key)
        {
            var index = Results.SelectedIndex;
            _items[index] = new ResultItem(result with { Subtitle = $"Press Enter again to {result.Title.ToLowerInvariant()}" });
            Results.SelectedIndex = index;
            _armed = result.Key;
            return;
        }
        try
        {
            switch (result.Kind)
            {
                case ResultKind.App: ResultActions.Open(@"shell:AppsFolder\" + result.Target, admin); break;
                case ResultKind.Path: ResultActions.Open(result.Target, admin); break;
                case ResultKind.Url or ResultKind.Web: ResultActions.Open(result.Target); break;
                case ResultKind.Command:
                    if (result.Target.Length == 0) return;
                    ResultActions.RunCommand(result.Target, admin);
                    break;
                case ResultKind.Calculator:
                    if (Clipboard is { } clipboard) await clipboard.SetTextAsync(result.Target);
                    break;
                case ResultKind.System: ResultActions.System(result.Target); break;
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            // 1223 is the UAC prompt being cancelled, which needs no explaining.
            if (e is Win32Exception { NativeErrorCode: 1223 }) return;
            if (!IsVisible) ShowLauncher();
            ShowNotice($"Couldn't run {result.Title}: {e.Message}");
            return;
        }
        // Typed things like sums and URLs aren't worth remembering.
        if (result.Kind is ResultKind.App or ResultKind.Path or ResultKind.System or ResultKind.Internal) _usage.Record(result.Key);
        HideLauncher();
        if (result.Kind == ResultKind.Internal) InternalCommand?.Invoke(result.Target);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
