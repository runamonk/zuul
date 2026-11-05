using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zuul;

// Everything that leaves Zuul: opening things, running commands, and the power buttons.
internal static class ResultActions
{
    // Whatever Explorer would do with it: apps, files, folders and URLs all go through the shell.
    public static void Open(string target, bool admin = false) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = admin ? "runas" : "" })?.Dispose();

    // /k keeps the window open, so the output can be read.
    public static void RunCommand(string command, bool admin = false) =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/k " + command)
        {
            UseShellExecute = true, Verb = admin ? "runas" : "",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        })?.Dispose();

    // Asks the shell rather than starting explorer.exe, so a replacement like Directory Opus opens it instead.
    public static void OpenLocation(string path)
    {
        var hr = SHParseDisplayName(path, 0, out var item, 0, out _);
        if (hr != 0) throw new IOException($"Couldn't find {path}.", hr);
        try
        {
            hr = SHOpenFolderAndSelectItems(item, 0, 0, 0);
            if (hr != 0) throw new IOException($"Couldn't open the folder for {path}.", hr);
        }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    // Windows' own Properties window.
    public static void Properties(string path)
    {
        if (!SHObjectProperties(0, 2 /* SHOP_FILEPATH */, path, null)) throw new IOException($"Couldn't show the properties of {path}.");
    }

    // Windows Terminal if it's there, Command Prompt otherwise.
    public static void Terminal(string folder)
    {
        if (AppPath("wt.exe") is { } terminal)
            Process.Start(new ProcessStartInfo(terminal) { ArgumentList = { "-d", folder }, UseShellExecute = false })?.Dispose();
        else
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
    }

    public static void Edit(string editor, string file) =>
        Process.Start(new ProcessStartInfo(editor) { ArgumentList = { file }, UseShellExecute = false })?.Dispose();

    // The editors offered when settings.json doesn't list its own. Only the ones that are installed show up.
    public static IReadOnlyList<EditorEntry> DefaultEditors()
    {
        var found = new List<EditorEntry>();
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if ((AppPath("notepad++.exe") ?? Existing(Path.Combine(programs, "Notepad++", "notepad++.exe"))) is { } notepad)
            found.Add(new EditorEntry("Notepad++", notepad));
        if ((AppPath("code.exe") ?? Existing(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"))
             ?? Existing(Path.Combine(programs, "Microsoft VS Code", "Code.exe"))) is { } code)
            found.Add(new EditorEntry("Code", code));
        return found;
    }

    // Where installers register a program for Win+R, so it's found wherever it was put.
    private static string? AppPath(string exe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
            if (key?.GetValue(null) is string path && Existing(Environment.ExpandEnvironmentVariables(path.Trim('"'))) is { } found) return found;
        }
        return null;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    public static void System(string command)
    {
        switch (command)
        {
            case "lock": LockWorkStation(); break;
            case "sleep": SetSuspendState(false, false, false); break;
            // Windows would quietly do nothing, so say why instead.
            case "hibernate":
                if (!IsPwrHibernateAllowed()) throw new InvalidOperationException("Hibernate is turned off on this computer. \"powercfg /hibernate on\" as administrator turns it on.");
                SetSuspendState(true, false, false);
                break;
            case "signout": ExitWindowsEx(0, 0); break;
            case "restart": Shutdown("/r /t 0"); break;
            case "shutdown": Shutdown("/s /t 0"); break;
            // Windows shows its own "are you sure", so there's no second one here.
            case "recyclebin": SHEmptyRecycleBin(IntPtr.Zero, null, 0); break;
        }
    }

    private static void Shutdown(string arguments) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false })?.Dispose();

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartsWithWindows
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Zuul") is string;
        }
        set
        {
            if (!OperatingSystem.IsWindows()) return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Zuul", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Zuul", throwOnMissingValue: false);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(nint window, uint type, string name, string? page);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);

    [DllImport("powrprof.dll")]
    private static extern bool IsPwrHibernateAllowed();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);
}
