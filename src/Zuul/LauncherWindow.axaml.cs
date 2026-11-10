using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Zuul.Core;

namespace Zuul;

public sealed record ResultItem(LaunchResult Result)
{
    public string Title => Result.Title;
    public string Subtitle => Result.Subtitle;
    public string? Detail => Result.Detail;

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
        ResultKind.Info => Result.Target == "more" ? "" : "",
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
    // "Hide from results" belongs to the app too: it's a setting.
    internal event Action<LaunchResult>? HideRequested;
    // Offered as "Edit with ..." on files.
    internal Func<IReadOnlyList<EditorEntry>> Editors { get; set; } = () => [];
    private ContextMenu? _menu;
    internal bool MenuIsOpen => _menu?.IsOpen == true;

    // Off: every open starts empty. On: the last search is back, selected.
    internal bool RememberSearch { get; set; }

    // How f: searches behave, from the Search tab in settings.
    internal EverythingSettings FileOptions { get; set; } = new();
    // Swapped out by tests; null back means Everything isn't running.
    internal Func<string, CancellationToken, Task<IReadOnlyList<EverythingItem>?>> AskEverything { get; set; }
    // The f: search in flight; cancelled by the next keystroke, Esc, or anything else that hides the launcher.
    private CancellationTokenSource? _fileSearch;
    // Enter pressed before Everything answered: the admin choice, to run the top result once it does.
    private bool? _runWhenFound;
    // Everything's answer, ranked once and shown a batch at a time, so rows already seen never reshuffle.
    private IReadOnlyList<LaunchResult> _found = [];
    private const string MoreTarget = "more";

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public LauncherWindow()
    {
        InitializeComponent();
        AskEverything = (term, token) => Everything.SearchAsync(FileSearch.BuildQuery(term, FileOptions.IncludeFolders, FileOptions.ExcludedList),
            FileSearch.MatchPath(term), FileSearch.Fetch, TimeSpan.FromSeconds(2), token);
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Results.ItemsSource = _items;
        Results.ContextRequested += Results_ContextRequested;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        // A launcher that stays up after you click elsewhere is just in the way.
        // Its own right-click menu doesn't count as elsewhere.
        Deactivated += (_, _) => { if (_menu?.IsOpen != true) HideLauncher(); };
    }

    internal LauncherWindow(SearchEngine engine, UsageStore usage) : this()
    {
        _engine = engine;
        _usage = usage;
    }

    public void ShowLauncher()
    {
        if (!RememberSearch) Query.Text = "";
        Search();
        PlaceOnScreen();
        // A hidden window isn't redrawn, and Windows shows its last picture the moment it's shown again: the old
        // results, for a frame or two. So it opens cloaked (shown, but not on screen) until the new picture is drawn.
        var opening = !IsVisible;
        if (opening) Cloak(true);
        Show();
        Activate();
        Query.Focus();
        // Typing replaces a remembered search; arrow keys or Enter pick it up again.
        Query.SelectAll();
        if (!opening) return;
        var shown = ++_shows;
        void Reveal() { if (shown == _shows) Cloak(false); }
        // The first callback comes before the next frame is drawn, the second after it.
        RequestAnimationFrame(_ => RequestAnimationFrame(_ => Reveal()));
        // In case no frame comes: a launcher that never appears would be far worse than a flash.
        DispatcherTimer.RunOnce(Reveal, TimeSpan.FromMilliseconds(150));
    }

    // Each open counts, so a late reveal from an earlier open can't uncover a newer one too soon.
    private int _shows;
    internal bool Cloaked { get; private set; }

    private void Cloak(bool on)
    {
        Cloaked = on;
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle) return;
        var value = on ? 1 : 0;
        DwmSetWindowAttribute(handle.Handle, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    public void HideLauncher()
    {
        _armed = null;
        CancelFileSearch();
        _menu?.Close();
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
        if (FileSearch.TryParse(Query.Text ?? "", FileOptions.Prefix, out var term))
        {
            if (term.Length == 0) ShowResults([Info($"Type a file name after {FileOptions.Prefix.Trim()}", "Everything searches the whole computer")]);
            else SearchFiles(term);
            return;
        }
        ShowResults(_engine.Search(Query.Text ?? ""));
    }

    private void ShowResults(IEnumerable<LaunchResult> results)
    {
        // A new search sizes the window again; only more of the same scrolls.
        Results.MaxHeight = double.PositiveInfinity;
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
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(FileOptions.DelayMs, 0, 2000)), search.Token);
            var items = await AskEverything(term, search.Token);
            if (search.IsCancellationRequested) return;
            _found = items == null || items.Count == 0 ? [] : [.. FileSearch.Rank(term, items).Where(r => !_engine.Hidden.Contains(r.Key))];
            _foundAll = items?.Count < FileSearch.Fetch;
            results = items == null ? [Info("Everything isn't running", $"Start Everything to search files with {FileOptions.Prefix.Trim()}")]
                : items.Count == 0 ? [Info("No files match", term)]
                : [.. _found.Take(Batch)];
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is TimeoutException or IOException or Win32Exception)
        {
            if (search.IsCancellationRequested) return;
            results = [Info("Everything didn't answer", e.Message)];
        }
        _fileSearch = null;
        ShowResults(results);
        AddMoreRow();
        if (_runWhenFound is { } admin && results[0].Kind != ResultKind.Info) _ = Run(admin);
        _runWhenFound = null;
    }

    private int Batch => Math.Clamp(FileOptions.BatchSize, 1, 100);
    // False when Everything had more than it was asked for, so the count gets a +.
    private bool _foundAll;

    private void AddMoreRow()
    {
        var shown = _items.Count(i => i.Result.Kind == ResultKind.Path);
        if (shown == 0 || shown >= _found.Count) return;
        var total = _foundAll ? _found.Count.ToString("N0") : _found.Count.ToString("N0") + "+";
        _items.Add(new ResultItem(new LaunchResult(ResultKind.Info, $"Show {Math.Min(Batch, _found.Count - shown)} more",
            $"{shown:N0} of {total} shown · Enter or ↓ for the next batch", MoreTarget, 0)));
    }

    // The window keeps its height and the list scrolls, so it never grows off the screen.
    private void ShowMore()
    {
        var more = _items.Count - 1;
        if (more < 0 || _items[more].Result.Target != MoreTarget) return;
        Results.MaxHeight = Results.Bounds.Height;
        _items.RemoveAt(more);
        foreach (var result in _found.Skip(more).Take(Batch)) _items.Add(new ResultItem(result));
        AddMoreRow();
        Results.SelectedIndex = more;
        Results.ScrollIntoView(more);
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
        // Going down past the last row asks for the next batch.
        if (by > 0 && Results.SelectedIndex == _items.Count - 1 && _items[^1].Result.Target == MoreTarget)
        {
            ShowMore();
            return;
        }
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + by, 0, _items.Count - 1);
        Results.ScrollIntoView(Results.SelectedIndex);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // The open menu gets the arrows, Enter and Esc.
        if (_menu?.IsOpen == true) return;
        switch (e.Key)
        {
            // The search box keeps focus, so without this the Menu key would open its Cut/Copy/Paste menu.
            case Key.Apps:
            case Key.F10 when e.KeyModifiers == KeyModifiers.Shift:
                OpenMenuForSelection();
                break;
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

    private void Results_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } row) return;
        Results.SelectedItem = row.DataContext;
        OpenMenu(row, PlacementMode.Pointer);
    }

    private void OpenMenuForSelection()
    {
        if (Results.SelectedIndex < 0) return;
        Results.ScrollIntoView(Results.SelectedIndex);
        if (Results.ContainerFromIndex(Results.SelectedIndex) is Control row) OpenMenu(row, PlacementMode.BottomEdgeAlignedLeft);
    }

    private void OpenMenu(Control row, PlacementMode placement)
    {
        if (Results.SelectedItem is not ResultItem { Result: var result }) return;
        var items = MenuItems(result);
        if (items.Count == 0) return;
        _menu?.Close();
        var menu = _menu = new ContextMenu { ItemsSource = items, Placement = placement };
        // From the keyboard, the first item is ready for the arrows and Enter.
        menu.Opened += (_, _) => items.OfType<MenuItem>().FirstOrDefault()?.Focus(NavigationMethod.Directional);
        // Closed by clicking somewhere else: hide like any other click elsewhere. Otherwise back to typing.
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            if (!IsActive) HideLauncher();
            else Query.Focus();
        });
        menu.Open(row);
    }

    // What makes sense for each kind of result; sums, URLs and commands get no menu.
    internal List<Control> MenuItems(LaunchResult result)
    {
        var list = new List<Control>();
        void Add(string header, Action action, bool hide = true)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Act(header, action, hide);
            list.Add(item);
        }
        void Line() { if (list.Count > 0 && list[^1] is not Separator) list.Add(new Separator()); }
        void Copy(string header, string text) => Add(header, () => _ = Clipboard?.SetTextAsync(text));
        void HideItem() => Add("Hide from results", () => HideRequested?.Invoke(result), hide: false);

        if (result.Kind == ResultKind.App)
        {
            var app = @"shell:AppsFolder\" + result.Target;
            Add("Open", () => ResultActions.Open(app));
            Add("Run as administrator", () => ResultActions.Open(app, admin: true));
            Line();
            Copy("Copy name", result.Title);
            Line();
            HideItem();
            return list;
        }
        if (result.Kind != ResultKind.Path) return list;

        var path = result.Target;
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path)) return list;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        Add("Open", () => ResultActions.Open(path));
        if (isFile && extension is ".exe" or ".lnk" or ".bat" or ".cmd" or ".com" or ".msc")
            Add("Run as administrator", () => ResultActions.Open(path, admin: true));
        // Programs and shortcuts aren't text, so editing them is never what's wanted.
        if (isFile && extension is not (".exe" or ".dll" or ".com" or ".lnk"))
            foreach (var editor in Editors())
                Add($"Edit with {editor.Name}", () => ResultActions.Edit(editor.Path, path));
        Line();
        Add("Open location", () => ResultActions.OpenLocation(path));
        // A Start menu shortcut's own folder is rarely the interesting one.
        if (extension == ".lnk" && ShortcutTarget.Read(path) is { } target && (File.Exists(target) || Directory.Exists(target)))
            Add("Open target location", () => ResultActions.OpenLocation(target));
        Add("Open terminal here", () => ResultActions.Terminal(isFile ? Path.GetDirectoryName(path)! : path));
        Line();
        Copy("Copy path", path);
        Copy("Copy name", Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path);
        Line();
        HideItem();
        Add("Properties", () => ResultActions.Properties(path));
        return list;
    }

    // Like Run: it goes away once the thing has started, and says why if it couldn't.
    private void Act(string what, Action action, bool hide)
    {
        try { action(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            // 1223 is the UAC prompt being cancelled, which needs no explaining.
            if (e is Win32Exception { NativeErrorCode: 1223 }) return;
            ShowNotice($"Couldn't {what.ToLowerInvariant()}: {e.Message}");
            return;
        }
        if (hide) HideLauncher();
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
        if (Results.SelectedItem is not ResultItem { Result: var result }) return;
        if (result.Kind == ResultKind.Info)
        {
            if (result.Target == MoreTarget) ShowMore();
            return;
        }
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
