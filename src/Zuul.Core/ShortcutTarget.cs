using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Zuul.Core;

// What a .lnk points at, so a shortcut to a readme isn't mistaken for an app.
public static class ShortcutTarget
{
    // Null when there's no file target (File Explorer, Control Panel) or it can't be read.
    public static string? Read(string shortcut)
    {
        if (!OperatingSystem.IsWindows()) return null;
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(shortcut, 0);
            var target = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.Length == 0 ? null : target.ToString();
        }
        catch (Exception e) when (e is COMException or FileNotFoundException or UnauthorizedAccessException or InvalidCastException) { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    // Shortcuts made by Windows Installer report an icon in C:\Windows\Installer as their target,
    // which says nothing about what they start, so they get the benefit of the doubt.
    public static bool Launchable(string? target, IReadOnlySet<string> extensions)
    {
        if (string.IsNullOrEmpty(target)) return true;
        var installer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer") + Path.DirectorySeparatorChar;
        if (target.StartsWith(installer, StringComparison.OrdinalIgnoreCase)) return true;
        var extension = Path.GetExtension(target).ToLowerInvariant();
        return extension.Length == 0 || extensions.Contains(extension);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    // Only GetPath is used; it's first in the vtable, so the rest can be left out.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
    }
}
