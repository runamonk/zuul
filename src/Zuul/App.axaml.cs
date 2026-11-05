using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Zuul.Core;

namespace Zuul;

public partial class App : Application
{
    private ZuulSettings _settings = new();
    private readonly ThemeCatalog _themes = new();
    private SearchEngine? _engine;
    private LauncherWindow? _launcher;
    private SettingsDialog? _settingsDialog;
    private GlobalHotKey? _hotKey;
    private TrayIcon? _tray;
    private DateTime _indexed;
    private bool _loadingIndex;
    private bool _reloadAgain;
    private FolderWatcher? _watcher;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Headless tests have no desktop lifetime, so none of this runs there.
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Lives in the tray; hiding the launcher must not end the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ZuulSettings.CopyFromDoIt();
            _settings = ZuulSettings.Read();
            try { _themes.Reload(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
            ApplyTheme(_settings);
            // Only matters while following Windows' light or dark mode.
            if (PlatformSettings is { } platform) platform.ColorValuesChanged += (_, _) => ApplyTheme(_settings);

            var usage = new UsageStore(Path.Combine(ZuulSettings.Folder, "usage.json"));
            _engine = new SearchEngine(usage);
            _launcher = new LauncherWindow(_engine, usage);
            _launcher.InternalCommand += RunInternal;
            ApplySearchSettings();
            // Not the MainWindow: the lifetime shows that at startup, which flashed the launcher on screen.
            // It stays out of sight until the hotkey, the tray icon or a second start asks for it.
            CreateTray();
            RegisterHotKey();
            ReloadIndex();
            WatchFolders();
            InitializeUpdates();
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal void ShowLauncher()
    {
        if (_launcher == null) return;
        // Cheap enough to redo now and then, and it picks up apps and files added since.
        if (DateTime.UtcNow - _indexed > TimeSpan.FromMinutes(5)) ReloadIndex();
        _launcher.ShowLauncher();
    }

    private void ToggleLauncher()
    {
        if (_launcher is { IsVisible: true, IsActive: true }) _launcher.HideLauncher();
        else ShowLauncher();
    }

    // Automatic reloads don't overlap; one asked for while another runs happens once that's done,
    // since the running one may have read a folder before the change. The settings button doesn't wait.
    private async void ReloadIndex()
    {
        if (_engine == null) return;
        if (_loadingIndex) { _reloadAgain = true; return; }
        _loadingIndex = true;
        try { await RebuildIndex(_settings); }
        catch (Exception e) { _launcher?.ShowNotice("Couldn't build the index: " + e.Message); }
        finally { _loadingIndex = false; }
        if (_reloadAgain)
        {
            _reloadAgain = false;
            ReloadIndex();
        }
    }

    // New shortcuts show up within seconds; the five-minute rebuild still catches anything the watchers miss.
    private void WatchFolders()
    {
        _watcher?.Dispose();
        _watcher = new FolderWatcher(_settings.FolderList, _settings.FileTypes);
        _watcher.Changed += () => Dispatcher.UIThread.Post(ReloadIndex);
    }

    // Apps and folders load side by side; the old index stays searchable until both are done.
    private async Task<string> RebuildIndex(ZuulSettings settings)
    {
        if (_engine == null) return "";
        var apps = settings.StoreApps ? AppIndex.LoadAsync() : Task.FromResult<IReadOnlyList<AppEntry>>([]);
        var files = FolderIndex.LoadAsync(settings.FolderList, settings.FileTypes);
        _engine.Apps = await apps;
        _engine.Files = await files;
        _indexed = DateTime.UtcNow;
        if (_launcher?.IsVisible == true) _launcher.Search();
        return $"{_engine.Apps.Count:N0} Store apps and {_engine.Files.Count:N0} files indexed.";
    }

    private void RunInternal(string command)
    {
        switch (command)
        {
            case "settings": OpenSettings(); break;
            case "reload": ReloadIndex(); break;
            case "update": CheckForUpdates_Click(); break;
            case "quit": Quit(); break;
        }
    }

    private void RegisterHotKey()
    {
        _hotKey?.Dispose();
        _hotKey = GlobalHotKey.Register(_settings.HotKey, ToggleLauncher, out var error);
        if (_tray != null) _tray.ToolTipText = _hotKey == null ? "Zuul" : $"Zuul ({_settings.HotKey})";
        if (error == null) return;
        ShowLauncher();
        _launcher?.ShowNotice(error);
    }

    private void CreateTray()
    {
        NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }
        var menu = new NativeMenu
        {
            Item("Open Zuul", ShowLauncher), Item("Settings", OpenSettings), Item("Reload index", ReloadIndex),
            Item("Check for updates", CheckForUpdates_Click),
            new NativeMenuItemSeparator(), Item("Quit", Quit)
        };
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Zuul/Assets/Zuul.ico"))),
            ToolTipText = "Zuul", Menu = menu
        };
        _tray.Clicked += (_, _) => ShowLauncher();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private void OpenSettings()
    {
        if (_settingsDialog != null)
        {
            _settingsDialog.Activate();
            return;
        }
        // Off while the dialog is open, so pressing the current hotkey records it instead of opening the launcher.
        _hotKey?.Dispose();
        _hotKey = null;
        var dialog = new SettingsDialog(_settings, _themes, ResultActions.StartsWithWindows);
        dialog.ThemePreview += preview =>
        {
            ApplyTheme(preview);
            AppThemes.ApplyTitleBar(dialog, _themes.Resolve(preview));
        };
        dialog.Rebuild = RebuildIndex;
        dialog.Opened += (_, _) => AppThemes.ApplyTitleBar(dialog, _themes.Resolve(_settings));
        dialog.Closed += (_, _) =>
        {
            _settingsDialog = null;
            if (dialog.Saved is { } saved)
            {
                // A check made while the dialog was open must not be forgotten.
                _settings = saved.Settings with { Updates = (saved.Settings.Updates ?? new()) with { LastCheckUtc = _settings.Updates?.LastCheckUtc } };
                try
                {
                    _settings.Save();
                    if (saved.StartWithWindows != ResultActions.StartsWithWindows) ResultActions.StartsWithWindows = saved.StartWithWindows;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    ShowLauncher();
                    _launcher?.ShowNotice("Couldn't save settings: " + e.Message);
                }
                ApplySearchSettings();
                WatchFolders();
            }
            ApplyTheme(_settings);
            RegisterHotKey();
            // Saved folders or file types may differ from the last build, and Cancel must undo a rebuild of unsaved ones.
            if (dialog.Saved != null || dialog.Rebuilt) ReloadIndex();
        };
        _settingsDialog = dialog;
        dialog.Show();
        dialog.Activate();
    }

    private void ApplySearchSettings()
    {
        if (_engine == null) return;
        _engine.MaxResults = Math.Clamp(_settings.MaxResults, 3, 20);
        _engine.WebSearchUrl = _settings.WebSearchUrl;
        if (_launcher != null) _launcher.RememberSearch = _settings.RememberSearch;
    }

    private void ApplyTheme(ZuulSettings settings) => AppThemes.Apply(_themes.Resolve(settings));

    private void Quit()
    {
        _hotKey?.Dispose();
        _watcher?.Dispose();
        _tray?.Dispose();
        _launcher?.Quit();
        StartUpdater();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
