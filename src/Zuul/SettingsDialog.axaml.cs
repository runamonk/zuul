using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Zuul.Core;

namespace Zuul;

// A null Name is "Match Windows".
public sealed record ThemeChoice(string? Name, string Label, IBrush Swatch);

// Editable copy of a FolderSource.
public sealed class FolderRow(FolderSource source)
{
    public string Path { get; } = source.Path;
    public bool IncludeSubfolders { get; set; } = source.IncludeSubfolders;
    public bool Enabled { get; set; } = source.Enabled;

    internal FolderSource ToSource() => new(Path, IncludeSubfolders, Enabled);
}

public partial class SettingsDialog : Window
{
    internal sealed record Result(ZuulSettings Settings, bool StartWithWindows);

    private readonly ThemeCatalog _themes = new();
    private readonly ZuulSettings _settings = new();
    private readonly ObservableCollection<FolderRow> _folders = [];
    private readonly ObservableCollection<string> _extensions = [];

    // Set by Save; null after Cancel or closing the window.
    internal Result? Saved { get; private set; }
    // Live preview while picking a theme.
    internal event Action<ZuulSettings>? ThemePreview;
    // Builds the index from the folders and file types as listed here, saved or not, and says what it found.
    internal Func<ZuulSettings, Task<string>>? Rebuild { get; set; }
    // True once Rebuild ran, so Cancel knows the index may hold folders that were never saved.
    internal bool Rebuilt { get; private set; }

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public SettingsDialog()
    {
        InitializeComponent();
    }

    internal SettingsDialog(ZuulSettings settings, ThemeCatalog themes, bool startWithWindows) : this()
    {
        _settings = settings;
        _themes = themes;
        HotKey.Text = settings.HotKey;
        HotKey.AddHandler(KeyDownEvent, HotKey_KeyDown, RoutingStrategies.Tunnel);
        MaxResults.Value = settings.MaxResults;
        WebSearch.Text = settings.WebSearchUrl;
        StartWithWindows.IsChecked = startWithWindows;
        foreach (var folder in settings.FolderList) _folders.Add(new FolderRow(folder));
        StoreApps.IsChecked = settings.StoreApps;
        Folders.ItemsSource = _folders;
        _folders.CollectionChanged += (_, _) => NoFolders.IsVisible = _folders.Count == 0;
        NoFolders.IsVisible = _folders.Count == 0;
        foreach (var extension in settings.FileTypes) _extensions.Add(extension);
        FileTypes.ItemsSource = _extensions;
        _extensions.CollectionChanged += (_, _) => NoFileTypes.IsVisible = _extensions.Count == 0;
        NoFileTypes.IsVisible = _extensions.Count == 0;
        NewFileType.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddFileType();
        };
        var updates = settings.Updates ?? new();
        VersionText.Text = "You have version " + App.AppVersion + ".";
        CheckOnStartup.IsChecked = updates.CheckOnStartup;
        EveryDays.Value = updates.EveryDays;
        UpdateEveryDaysRow();
        CheckOnStartup.IsCheckedChanged += (_, _) => UpdateEveryDaysRow();
        FillThemes();
        Themes.SelectionChanged += (_, _) => ThemePreview?.Invoke(Chosen);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
    }

    private void FillThemes()
    {
        var current = (Themes.SelectedItem as ThemeChoice)?.Name ?? _settings.ColorTheme;
        var choices = _themes.Themes.Select(t => new ThemeChoice(t.Name, t.Name, new SolidColorBrush(Color.Parse(t.Swatch ?? t.Accent))))
            .Prepend(new ThemeChoice(null, "Match Windows", Brushes.Transparent)).ToList();
        Themes.ItemsSource = choices;
        Themes.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Name, current, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    private ZuulSettings Chosen
    {
        get
        {
            var name = (Themes.SelectedItem as ThemeChoice)?.Name;
            var dark = name == null ? null : _themes.Themes.FirstOrDefault(t => t.Name == name)?.Dark;
            return _settings with
            {
                HotKey = HotKey.Text ?? _settings.HotKey,
                MaxResults = (int)(MaxResults.Value ?? 8),
                WebSearchUrl = WebSearch.Text?.Trim() ?? "",
                ColorTheme = name, DarkTheme = dark,
                // Like file types, untouched defaults are stored as null so they follow the defaults.
                Folders = ChosenFolders.SequenceEqual(FolderIndex.DefaultFolders) ? null : ChosenFolders.ToArray(),
                StoreApps = StoreApps.IsChecked == true,
                // Stored as null while it matches the defaults, so new defaults reach everyone who never changed it.
                Extensions = _extensions.Order().SequenceEqual(FolderIndex.DefaultExtensions.Order()) ? null : _extensions.ToArray(),
                Updates = (_settings.Updates ?? new()) with { CheckOnStartup = CheckOnStartup.IsChecked == true, EveryDays = (int)(EveryDays.Value ?? 0) }
            };
        }
    }

    private void UpdateEveryDaysRow()
    {
        var on = CheckOnStartup.IsChecked == true;
        EveryDaysRow.IsEnabled = on;
        EveryDaysRow.Opacity = on ? 1 : 0.4;
    }

    // Records the combination instead of typing it. Tab still moves on, so the box can be left by keyboard.
    private void HotKey_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Escape) return;
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System) return;
        var name = e.Key.ToString();
        var text = HotKeyText.Format(e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Meta), name);
        if (!HotKeyText.IsKey(name)) HotKeyHelp.Text = $"{name} can't be part of a hotkey. Try a letter, number, Space or F1–F24.";
        else if (!HotKeyText.TryParse(text, out _, out _)) HotKeyHelp.Text = "Add Ctrl, Alt, Shift or Win, or typing that key would open Zuul.";
        else
        {
            HotKey.Text = text;
            HotKeyHelp.Text = "Saved when you press Save.";
        }
    }

    private IEnumerable<FolderSource> ChosenFolders => _folders.Select(f => f.ToSource());

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose folders to index", AllowMultiple = true });
        foreach (var path in picked.Select(f => f.TryGetLocalPath()).OfType<string>())
            if (!_folders.Any(f => ZuulSettings.SamePath(f.Path, path))) _folders.Add(new FolderRow(new FolderSource(path)));
    }

    // Only adds what's missing; folders that are still there keep their switches and priorities.
    private void RestoreFolders_Click(object? sender, RoutedEventArgs e)
    {
        var missing = FolderIndex.DefaultFolders.Where(d => !_folders.Any(f => ZuulSettings.SamePath(f.Path, d.Path))).ToList();
        // At the bottom, so restoring doesn't jump them above the order already picked.
        foreach (var folder in missing) _folders.Add(new FolderRow(folder));
    }

    private void MoveFolderUp_Click(object? sender, RoutedEventArgs e) => MoveFolder(sender, -1);

    private void MoveFolderDown_Click(object? sender, RoutedEventArgs e) => MoveFolder(sender, 1);

    // Up the list is up the results; the ends just stay put.
    private void MoveFolder(object? sender, int by)
    {
        if ((sender as Control)?.DataContext is not FolderRow row) return;
        var from = _folders.IndexOf(row);
        var to = from + by;
        if (from < 0 || to < 0 || to >= _folders.Count) return;
        _folders.Move(from, to);
        Folders.SelectedItem = row;
    }

    private void RemoveFolder_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FolderRow row) _folders.Remove(row);
    }

    private async void Rebuild_Click(object? sender, RoutedEventArgs e)
    {
        if (Rebuild == null) return;
        RebuildButton.IsEnabled = false;
        IndexStatus.Text = "Indexing…";
        try
        {
            Rebuilt = true;
            IndexStatus.Text = await Rebuild(Chosen);
        }
        catch (Exception error) { IndexStatus.Text = "Couldn't build the index: " + error.Message; }
        finally { RebuildButton.IsEnabled = true; }
    }

    private void AddFileType_Click(object? sender, RoutedEventArgs e) => AddFileType();

    private void AddFileType()
    {
        // Several at once is fine: ".ps1 .vbs" or ".ps1, .vbs".
        var typed = (NewFileType.Text ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        var bad = typed.Where(t => FolderIndex.NormalizeExtension(t) == null).ToList();
        foreach (var extension in typed.Select(FolderIndex.NormalizeExtension).OfType<string>())
            if (!_extensions.Contains(extension)) _extensions.Add(extension);
        // Bad ones stay in the box to be fixed.
        NewFileType.Text = string.Join(" ", bad);
        FileTypeError.Text = $"{string.Join(", ", bad)} can't be a file extension.";
        FileTypeError.IsVisible = bad.Count > 0;
    }

    private void RemoveFileType_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is string extension) _extensions.Remove(extension);
    }

    private void ResetFileTypes_Click(object? sender, RoutedEventArgs e)
    {
        _extensions.Clear();
        foreach (var extension in FolderIndex.DefaultExtensions) _extensions.Add(extension);
        FileTypeError.IsVisible = false;
    }

    private void OpenThemes_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _themes.EnsureFile();
            ResultActions.Open(_themes.FilePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { ShowThemeError("Could not open the themes file: " + error.Message); }
    }

    private void ReloadThemes_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _themes.Reload();
            ThemeError.IsVisible = false;
            FillThemes();
            ThemePreview?.Invoke(Chosen);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { ShowThemeError("The themes file has a problem, so the old themes are still in use: " + error.Message); }
    }

    private void ShowThemeError(string text)
    {
        ThemeError.Text = text;
        ThemeError.IsVisible = true;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Saved = new Result(Chosen, StartWithWindows.IsChecked == true);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
