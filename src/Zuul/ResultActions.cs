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

    public static void System(string command)
    {
        switch (command)
        {
            case "lock": LockWorkStation(); break;
            case "sleep": SetSuspendState(false, false, false); break;
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

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);
}
