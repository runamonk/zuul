using System.Runtime.InteropServices;

namespace Zuul.Core;

// ParsingName is the app's ID in shell:AppsFolder, like Microsoft.WindowsCalculator_8wekyb3d8bbwe!App.
public sealed record AppEntry(string Name, string ParsingName, string Location);

// Store apps only. Desktop apps come from the Start menu folders like any other folder,
// so the File types list and the folder switches apply to them too.
public static class AppIndex
{
    public static Task<IReadOnlyList<AppEntry>> LoadAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<AppEntry>>();
        // Shell objects want a single threaded apartment, and the UI thread has better things to do.
        var thread = new Thread(() =>
        {
            try { done.SetResult(Load()); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true, Name = "App index" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static IReadOnlyList<AppEntry> Load()
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID("Shell.Application") is not { } type) return [];
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic items = shell.NameSpace("shell:AppsFolder").Items();
            var apps = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < (int)items.Count; i++)
            {
                dynamic item = items.Item(i);
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || !IsStoreApp(path) || !seen.Add(path)) continue;
                apps.Add(new AppEntry(name, path, "Store app"));
            }
            return apps;
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    // Packaged app IDs are "family!app"; everything else in AppsFolder is a Start menu shortcut.
    internal static bool IsStoreApp(string? parsingName) =>
        !string.IsNullOrWhiteSpace(parsingName) && parsingName.Contains('!') && !parsingName.Contains('\\') && !parsingName.Contains("://");
}
