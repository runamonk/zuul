using Microsoft.Win32;

namespace Zuul.Core;

// User is the name last used for it, when Remote Desktop remembered one.
public sealed record RemoteDesktop(string Computer, string? User);

// The computers Remote Desktop Connection remembers: its Computer dropdown first, most recent on top,
// then the others it kept details for, which drop off the dropdown after ten.
public static class RemoteDesktops
{
    public const string Key = @"Software\Microsoft\Terminal Server Client";

    // Typing one of these, alone or before a name, lists the computers.
    public static readonly string[] Keywords = ["remote desktop", "remote", "rdp", "mstsc"];

    public static IReadOnlyList<RemoteDesktop> Load(string key = Key)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(key);
            if (root == null) return [];
            var users = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            using (var servers = root.OpenSubKey("Servers"))
                foreach (var name in servers?.GetSubKeyNames() ?? [])
                {
                    using var server = servers!.OpenSubKey(name);
                    users[name] = (server?.GetValue("UsernameHint") as string) is { Length: > 0 } user ? user : null;
                }
            var found = new List<RemoteDesktop>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var recent = root.OpenSubKey("Default"))
                for (var i = 0; i < 10; i++)
                    if ((recent?.GetValue("MRU" + i) as string)?.Trim() is { Length: > 0 } computer && seen.Add(computer))
                        found.Add(new RemoteDesktop(computer, users.GetValueOrDefault(computer)));
            foreach (var (computer, user) in users.OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase))
                if (seen.Add(computer)) found.Add(new RemoteDesktop(computer, user));
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    // "rdp work" is the keyword and "work"; "remote" alone is the keyword and nothing. Null when there's no keyword.
    public static string? AfterKeyword(string query)
    {
        foreach (var keyword in Keywords)
            if (query.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return "";
            else if (query.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase)) return query[(keyword.Length + 1)..].Trim();
        return null;
    }
}
