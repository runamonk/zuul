using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Zuul.Core;

namespace Zuul.Tests;

public sealed class FolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZuulFolders-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Everything = [".pdf", ".txt", ".lnk", ".cmd"];

    public FolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Work", "Deep"));
        File.WriteAllText(Path.Combine(_folder, "Work", "report.pdf"), "");
        File.WriteAllText(Path.Combine(_folder, "Work", "Editor.LNK"), "");
        File.WriteAllText(Path.Combine(_folder, "Work", "build.cmd"), "");
        File.WriteAllText(Path.Combine(_folder, "Work", "Deep", "notes.txt"), "");
        Directory.CreateDirectory(Path.Combine(_folder, "Desk"));
        File.WriteAllText(Path.Combine(_folder, "Desk", "editor.lnk"), "");
        var hidden = Path.Combine(_folder, "Work", "secret.txt");
        File.WriteAllText(hidden, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string Work => Path.Combine(_folder, "Work");

    private List<string> Names(IEnumerable<FolderSource> folders, IEnumerable<string> extensions, IEnumerable<string>? checkedFolders = null) =>
        FolderIndex.Load(folders, extensions, checkedFolders).Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

    [Fact]
    public void IndexesMatchingFilesButNotFoldersOrHiddenOnes() =>
        Assert.Equal(["build.cmd", "Editor", "notes.txt", "report.pdf"], Names([new FolderSource(Work)], Everything));

    [Fact]
    public void DefaultsPickUpLaunchableFilesOnly() =>
        Assert.Equal(["build.cmd", "Editor"], Names([new FolderSource(Work)], FolderIndex.DefaultExtensions));

    [Fact]
    public void NoFileTypesMeansNothing() => Assert.Empty(Names([new FolderSource(Work)], []));

    [Fact]
    public void SubfoldersCanBeLeftOut() =>
        Assert.Equal(["build.cmd", "Editor", "report.pdf"], Names([new FolderSource(Work, IncludeSubfolders: false)], Everything));

    [Fact]
    public void SwitchedOffFoldersAddNothing() => Assert.Empty(Names([new FolderSource(Work, Enabled: false)], Everything));

    // The same shortcut on the desktop and in the Start menu is one result, from the folder higher up the list.
    [Fact]
    public void SameNamedShortcutsAreOneResult()
    {
        var entries = FolderIndex.Load([new FolderSource(Path.Combine(_folder, "Desk")), new FolderSource(Work)], Everything);
        var editor = entries.Single(e => e.Name.Equals("editor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(_folder, "Desk", "editor.lnk"), editor.Path);
    }

    // Made the way installers make them, so the target really comes from the .lnk.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private string Shortcut(string folder, string name, string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var path = Path.Combine(folder, name + ".lnk");
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Save();
        return path;
    }

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ShortcutsCountByWhatTheyPointAt()
    {
        var menu = Path.Combine(_folder, "Menu");
        Directory.CreateDirectory(menu);
        Shortcut(menu, "What is new", Path.Combine(Work, "Deep", "notes.txt"));
        Shortcut(menu, "Notepad", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"));
        Assert.Equal(["Notepad"], Names([new FolderSource(menu)], FolderIndex.DefaultExtensions, [menu]));
        // Adding .txt to the file types lets the readme back in.
        Assert.Equal(["Notepad", "What is new"], Names([new FolderSource(menu)], [.. FolderIndex.DefaultExtensions, ".txt"], [menu]));
    }

    // Only the Start menus and desktops get checked; a shortcut in a folder of your own was put there on purpose.
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ShortcutsElsewhereAllCount()
    {
        var menu = Path.Combine(_folder, "Menu");
        var tools = Path.Combine(_folder, "Tools");
        Directory.CreateDirectory(Path.Combine(menu, "Inner"));
        Directory.CreateDirectory(tools);
        Shortcut(Path.Combine(menu, "Inner"), "What is new", Path.Combine(Work, "Deep", "notes.txt"));
        Shortcut(tools, "Invoice", Path.Combine(Work, "report.pdf"));
        // The readme is still left out when found through a folder that contains the Start menu.
        var names = Names([new FolderSource(_folder)], FolderIndex.DefaultExtensions, [menu]);
        Assert.Contains("Invoice", names);
        Assert.DoesNotContain("What is new", names);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WinRAR\WhatsNew.txt", false)]
    [InlineData(@"C:\Program Files\WinRAR\WinRAR.chm", false)]
    [InlineData(@"C:\Program Files\Git\ReleaseNotes.html", false)]
    [InlineData(@"C:\Program Files\WinRAR\WinRAR.exe", true)]
    [InlineData(@"C:\Windows\system32\compmgmt.msc", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Syncthing\ConfigurationPage.url", true)]
    [InlineData(null, true)]
    public void ShortcutTargetRules(string? target, bool launchable) =>
        Assert.Equal(launchable, ShortcutTarget.Launchable(target, FolderIndex.DefaultExtensions.ToHashSet()));

    // WSL's shortcut "points at" wsl.ico; Windows Installer shortcuts all look like that.
    [Fact]
    public void InstallerShortcutsGetTheBenefitOfTheDoubt() =>
        Assert.True(ShortcutTarget.Launchable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Installer", "{6EB15DDD-5983-4269-A73E-F459398B8B98}", "wsl.ico"), FolderIndex.DefaultExtensions.ToHashSet()));

    [Fact]
    public void DefaultsAreTheStartMenusAndDesktops()
    {
        var paths = FolderIndex.DefaultFolders.Select(f => f.Path).ToList();
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.Programs), paths);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), paths);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), paths);
        Assert.All(FolderIndex.DefaultFolders, f => Assert.True(f.Enabled));
    }

    // Rank counts places from the bottom, so the top of three folders is 2.
    [Fact]
    public void OverlappingFoldersTakeTheHigherPlace()
    {
        var entries = FolderIndex.Load([new FolderSource(Path.Combine(Work, "Deep")), new FolderSource(Work), new FolderSource(_folder)], Everything);
        Assert.Equal(2, entries.Single(e => e.Name == "notes.txt").Rank);
        Assert.Equal(1, entries.Single(e => e.Name == "report.pdf").Rank);
    }

    // Turning a folder off doesn't move the ones below it up.
    [Fact]
    public void SwitchedOffFoldersKeepTheirPlace()
    {
        var entries = FolderIndex.Load([new FolderSource(_folder, Enabled: false), new FolderSource(Work), new FolderSource(Path.Combine(_folder, "Desk"))], Everything);
        Assert.Equal(1, entries.Single(e => e.Name == "report.pdf").Rank);
    }

    [Fact]
    public void MissingFoldersAreSkipped() => Assert.Empty(FolderIndex.Load([new FolderSource(Path.Combine(_folder, "Nope"))], Everything));

    [Theory]
    [InlineData(".lnk", ".lnk")]
    [InlineData("LNK", ".lnk")]
    [InlineData("*.Bat", ".bat")]
    [InlineData(" .appref-ms ", ".appref-ms")]
    [InlineData("", null)]
    [InlineData(".", null)]
    [InlineData("tar.gz", null)]
    [InlineData("a b", null)]
    [InlineData("a?", null)]
    public void NormalizesExtensions(string typed, string? expected) => Assert.Equal(expected, FolderIndex.NormalizeExtension(typed));

    [Fact]
    public void FindsFilesWithoutTheirExtension()
    {
        var engine = new SearchEngine(new UsageStore(null)) { Files = FolderIndex.Load([new FolderSource(Work)], Everything) };
        var result = engine.Search("report")[0];
        Assert.Equal(ResultKind.Path, result.Kind);
        Assert.Equal("report.pdf", result.Title);
    }

    // The Store app ranks with the bottom folder; ties go to the shorter name.
    [Fact]
    public void ListOrderDecidesBetweenEqualMatches()
    {
        var engine = new SearchEngine(new UsageStore(null))
        {
            Apps = [new("Budget", "Budget_8wekyb3d8bbwe!App", "Store app")],
            Files = [new("Budget.xlsx", @"C:\a\Budget.xlsx", 1), new("Budget.docx", @"C:\b\Budget.docx", 0)]
        };
        Assert.Equal(["Budget.xlsx", "Budget", "Budget.docx"], engine.Search("budget").Take(3).Select(r => r.Title));
    }

    // A much better name match still wins, however low its folder.
    [Fact]
    public void ListOrderIsOnlyANudge()
    {
        var engine = new SearchEngine(new UsageStore(null))
        {
            Files = [new("Notepad Helper.exe", @"C:\a\Notepad Helper.exe", 50), new("Notepad.exe", @"C:\b\Notepad.exe", 0)]
        };
        Assert.Equal("Notepad.exe", engine.Search("notepad")[0].Title);
    }

    [Fact]
    public void FoldersAndFileTypesSurviveSaving()
    {
        var path = Path.Combine(_folder, "settings.json");
        new ZuulSettings(Folders: [new FolderSource(@"C:\Work", false, Enabled: false)], Extensions: [".lnk", ".ps1"],
            StoreApps: false, DefaultFoldersAdded: true).Save(path);
        var read = ZuulSettings.Read(path);
        Assert.Equal(new FolderSource(@"C:\Work", false, Enabled: false), read.Folders!.Single());
        // Order is the priority, so there's no number to save.
        Assert.DoesNotContain("Priority", File.ReadAllText(path));
        Assert.False(read.StoreApps);
        Assert.DoesNotContain("FileTypes", File.ReadAllText(path));
        Assert.Equal([".lnk", ".ps1"], read.FileTypes);
        Assert.Equal(FolderIndex.DefaultExtensions, new ZuulSettings().FileTypes);
    }

    [Fact]
    public void OlderFolderListsGetTheDefaultsInFront()
    {
        var path = Path.Combine(_folder, "old.json");
        File.WriteAllText(path, """{ "Folders": [ { "Path": "C:\\my.files\\Tools", "Priority": 0, "IncludeSubfolders": true } ] }""");
        var read = ZuulSettings.Read(path);
        Assert.Equal([.. FolderIndex.DefaultFolders.Select(f => f.Path), @"C:\my.files\Tools"], read.FolderList.Select(f => f.Path));
        // Once saved, it isn't done again, so removing a default sticks.
        (read with { Folders = [new FolderSource(@"C:\my.files\Tools")] }).Save(path);
        Assert.Equal([@"C:\my.files\Tools"], ZuulSettings.Read(path).FolderList.Select(f => f.Path));
    }

    [Fact]
    public void SettingsComeAcrossFromDoItOnce()
    {
        var old = Path.Combine(_folder, "DoIt");
        var now = Path.Combine(_folder, "Zuul");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "settings.json"), """{ "ColorTheme": "Dracula" }""");
        File.WriteAllText(Path.Combine(old, "usage.json"), "{}");
        ZuulSettings.CopyFromDoIt(old, now);
        Assert.Equal("Dracula", ZuulSettings.Read(Path.Combine(now, "settings.json")).ColorTheme);
        Assert.True(File.Exists(Path.Combine(old, "settings.json")));
        // Once Zuul has its own folder, DoIt's is left alone.
        File.WriteAllText(Path.Combine(old, "settings.json"), """{ "ColorTheme": "Midnight" }""");
        ZuulSettings.CopyFromDoIt(old, now);
        Assert.Equal("Dracula", ZuulSettings.Read(Path.Combine(now, "settings.json")).ColorTheme);
    }

    [Fact]
    public void NoSavedFoldersMeansTheDefaults() => Assert.Equal(FolderIndex.DefaultFolders, new ZuulSettings().FolderList);

    private SettingsDialog Dialog(ZuulSettings settings)
    {
        var dialog = new SettingsDialog(settings, new ThemeCatalog(Path.Combine(_folder, "settings.json")), startWithWindows: false);
        dialog.Show();
        return dialog;
    }

    private static void Click(Avalonia.Controls.Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    private static Avalonia.Controls.Button Button(SettingsDialog dialog, string content) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>().First(b => (b.Content as string) == content);

    [AvaloniaFact]
    public async Task RebuildUsesWhatIsListedNotWhatIsSaved()
    {
        var dialog = Dialog(new ZuulSettings(Folders: [new FolderSource(Work)], DefaultFoldersAdded: true));
        ZuulSettings? built = null;
        dialog.Rebuild = settings =>
        {
            built = settings;
            return Task.FromResult("1 apps and 3 files indexed.");
        };
        var row = ((IEnumerable<FolderRow>)dialog.Folders.ItemsSource!).Single();
        row.IncludeSubfolders = false;
        dialog.NewFileType.Text = "ps1";
        dialog.FileTypesTab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        Click(Button(dialog, "Add"));
        Click(dialog.RebuildButton);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new FolderSource(Work, false), built!.FolderList.Single());
        Assert.Contains(".ps1", built.FileTypes);
        Assert.True(dialog.Rebuilt);
        Assert.Equal("1 apps and 3 files indexed.", dialog.IndexStatus.Text);
    }

    [AvaloniaFact]
    public void DefaultFoldersCanBeSwitchedOffAndRestored()
    {
        var dialog = Dialog(new ZuulSettings());
        var rows = (IList<FolderRow>)dialog.Folders.ItemsSource!;
        Assert.Equal(FolderIndex.DefaultFolders.Length, rows.Count);
        // Untouched defaults save as "the defaults".
        Click(Button(dialog, "Save"));
        Assert.Null(dialog.Saved!.Settings.Folders);

        var edit = Dialog(new ZuulSettings());
        rows = (IList<FolderRow>)edit.Folders.ItemsSource!;
        rows[0].Enabled = false;
        rows.RemoveAt(rows.Count - 1);
        edit.StoreApps.IsChecked = false;
        edit.FoldersTab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        var removed = FolderIndex.DefaultFolders[^1].Path;
        Click(Button(edit, "Restore defaults"));
        // The removed one is back at the bottom; the switched off one stays off.
        Assert.Equal(FolderIndex.DefaultFolders.Length, rows.Count);
        Assert.Equal(removed, rows[^1].Path);
        Click(Button(edit, "Save"));
        var saved = edit.Saved!.Settings;
        Assert.False(saved.FolderList.Single(f => f.Path == FolderIndex.DefaultFolders[0].Path).Enabled);
        Assert.False(saved.StoreApps);
    }

    [AvaloniaFact]
    public void FoldersMoveUpAndDownAndStopAtTheEnds()
    {
        var dialog = Dialog(new ZuulSettings(Folders: [new FolderSource(@"C:\A"), new FolderSource(@"C:\B"), new FolderSource(@"C:\C")], DefaultFoldersAdded: true));
        dialog.FoldersTab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        var rows = (IList<FolderRow>)dialog.Folders.ItemsSource!;
        Avalonia.Controls.Button Move(string path, string direction) =>
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>()
                .First(b => b.DataContext is FolderRow r && r.Path == path && Avalonia.Automation.AutomationProperties.GetName(b) == $"Move folder {direction}");

        Click(Move(@"C:\C", "up"));
        Assert.Equal([@"C:\A", @"C:\C", @"C:\B"], rows.Select(r => r.Path));
        Click(Move(@"C:\A", "up"));
        Click(Move(@"C:\B", "down"));
        Assert.Equal([@"C:\A", @"C:\C", @"C:\B"], rows.Select(r => r.Path));
        Click(Move(@"C:\A", "down"));
        Click(Button(dialog, "Save"));
        Assert.Equal([@"C:\C", @"C:\A", @"C:\B"], dialog.Saved!.Settings.FolderList.Select(f => f.Path));
    }

    [AvaloniaFact]
    public void FileTypesCanBeAddedRemovedAndReset()
    {
        var dialog = Dialog(new ZuulSettings());
        dialog.FileTypesTab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        var types = (IList<string>)dialog.FileTypes.ItemsSource!;

        dialog.NewFileType.Text = ".PS1, vbs tar.gz";
        Click(Button(dialog, "Add"));
        Assert.Contains(".ps1", types);
        Assert.Contains(".vbs", types);
        // The bad one stays in the box with a reason.
        Assert.Equal("tar.gz", dialog.NewFileType.Text);
        Assert.True(dialog.FileTypeError.IsVisible);

        types.Remove(".exe");
        Click(Button(dialog, "Save"));
        Assert.DoesNotContain(".exe", dialog.Saved!.Settings.FileTypes);

        var reset = Dialog(new ZuulSettings(Extensions: [".ps1"]));
        reset.FileTypesTab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        Click(Button(reset, "Reset to defaults"));
        Click(Button(reset, "Save"));
        // Back to the defaults is stored as "the defaults", not a copy of today's list.
        Assert.Null(reset.Saved!.Settings.Extensions);
    }
}
