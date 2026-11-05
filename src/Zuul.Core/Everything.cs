using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Zuul.Core;

// Size and Modified are null where Everything doesn't know them. Modified is UTC.
public sealed record EverythingItem(string Path, bool IsFolder, long? Size = null, DateTime? Modified = null);

// Asks a running Everything (voidtools) for files, over the window messages its SDK uses.
// No SDK DLL: it only looks for 1.4's window, and this way the 1.5 alpha answers too.
// Each search gets its own thread and a hidden window for the answer to land in.
public static class Everything
{
    // The 1.5 alpha uses its own name unless set to run as the main instance.
    private static readonly string[] WindowClasses = ["EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)", "EVERYTHING_TASKBAR_NOTIFICATION"];
    private const string ReplyClass = "ZuulEverythingReply";
    private const uint WmCopyData = 0x004A;
    private const nuint Query2 = 18; // EVERYTHING_IPC_COPYDATA_QUERY2W
    private const uint ReplyId = 0x5A55; // Marks Everything's answer to us.
    private const uint MatchPath = 4;
    private const uint RequestFullPath = 4, RequestSize = 0x10, RequestDateModified = 0x40;
    private const uint SortNameAscending = 1;
    private const uint FolderFlag = 1;

    [ThreadStatic] private static byte[]? _reply;
    // Kept in a field so the collector can't take it while Windows still calls it.
    private static readonly WndProc ReplyProc = OnMessage;
    private static readonly Lazy<bool> Registered = new(Register);

    public static bool IsRunning => OperatingSystem.IsWindows() && Find() != 0;

    // Null when Everything isn't running. Cancelling stops waiting; Everything still answers, and it's ignored.
    public static Task<IReadOnlyList<EverythingItem>?> SearchAsync(string search, bool matchPath, int max, TimeSpan timeout, CancellationToken token) =>
        Task.Factory.StartNew(() => Search(search, matchPath, max, timeout, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static IReadOnlyList<EverythingItem>? Search(string search, bool matchPath, int max, TimeSpan timeout, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var everything = Find();
        if (everything == 0) return null;
        if (!Registered.Value) throw new Win32Exception(Marshal.GetLastWin32Error());
        var window = CreateWindowExW(0, ReplyClass, "", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, GetModuleHandleW(null), 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _reply = null;
        try
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            Send(everything, window, search, matchPath ? MatchPath : 0, max, timeout);
            // Pumped by hand: the answer arrives as a message to the hidden window.
            while (_reply == null)
            {
                token.ThrowIfCancellationRequested();
                var left = deadline - Environment.TickCount64;
                if (left <= 0) throw new TimeoutException("Everything didn't answer in time.");
                MsgWaitForMultipleObjects(0, null, false, (uint)Math.Min(left, 50), 0x04FF /* QS_ALLINPUT */);
                while (PeekMessageW(out var message, 0, 0, 0, 1 /* PM_REMOVE */))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            return Parse(_reply);
        }
        finally
        {
            DestroyWindow(window);
            _reply = null;
        }
    }

    // EVERYTHING_IPC_QUERY2: seven DWORDs, then the search as UTF-16 with a terminator.
    private static void Send(nint everything, nint reply, string search, uint flags, int max, TimeSpan timeout)
    {
        var text = Encoding.Unicode.GetBytes(search + "\0");
        var size = 7 * 4 + text.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint[] header = [(uint)reply, ReplyId, flags, 0, (uint)max, RequestFullPath | RequestSize | RequestDateModified, SortNameAscending];
            for (var i = 0; i < header.Length; i++) Marshal.WriteInt32(buffer, i * 4, (int)header[i]);
            Marshal.Copy(text, 0, buffer + 7 * 4, text.Length);
            var data = new CopyData { Data = Query2, Size = size, Pointer = buffer };
            // Not SMTO_BLOCK: Everything may answer while we wait, and that has to get through.
            if (SendMessageTimeoutW(everything, WmCopyData, reply, ref data, 2 /* SMTO_ABORTIFHUNG */, (uint)timeout.TotalMilliseconds, out var accepted) == 0)
                throw new TimeoutException("Everything didn't answer in time.");
            if (accepted == 0) throw new IOException("Everything turned the search down. Its database may still be loading.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // EVERYTHING_IPC_LIST2: five DWORDs, then (flags, data offset) per item. Each item's data holds what was asked for,
    // in flag order: the full path as a DWORD length and UTF-16 text with a terminator, then the size, then the date.
    // Everything says -1 for what it doesn't know, such as a folder's size when folder sizes aren't indexed.
    internal static IReadOnlyList<EverythingItem> Parse(byte[] reply)
    {
        var count = BitConverter.ToInt32(reply, 4);
        var asked = BitConverter.ToUInt32(reply, 12);
        var items = new List<EverythingItem>(count);
        for (var i = 0; i < count; i++)
        {
            var flags = BitConverter.ToUInt32(reply, 20 + i * 8);
            var offset = BitConverter.ToInt32(reply, 24 + i * 8);
            var length = BitConverter.ToInt32(reply, offset);
            var path = Encoding.Unicode.GetString(reply, offset + 4, length * 2);
            offset += 4 + (length + 1) * 2;
            long? size = null;
            DateTime? modified = null;
            if ((asked & RequestSize) != 0)
            {
                var value = BitConverter.ToInt64(reply, offset);
                if (value >= 0) size = value;
                offset += 8;
            }
            if ((asked & RequestDateModified) != 0)
            {
                var value = BitConverter.ToInt64(reply, offset);
                if (value > 0) modified = DateTime.FromFileTimeUtc(value);
            }
            items.Add(new EverythingItem(path, (flags & FolderFlag) != 0, size, modified));
        }
        return items;
    }

    private static nint Find()
    {
        foreach (var name in WindowClasses)
            if (FindWindowW(name, null) is var window and not 0) return window;
        return 0;
    }

    private static bool Register()
    {
        var type = new WindowClass
        {
            Proc = Marshal.GetFunctionPointerForDelegate(ReplyProc), Instance = GetModuleHandleW(null), ClassName = ReplyClass
        };
        return RegisterClassW(ref type) != 0;
    }

    private static nint OnMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message != WmCopyData) return DefWindowProcW(window, message, wParam, lParam);
        var data = Marshal.PtrToStructure<CopyData>(lParam);
        if (data.Data != ReplyId) return 0;
        var bytes = new byte[data.Size];
        Marshal.Copy(data.Pointer, bytes, 0, data.Size);
        _reply = bytes;
        return 1;
    }

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public nuint Data; public int Size; public nint Pointer; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, ref CopyData lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, nint[]? handles, bool waitAll, uint milliseconds, uint wakeMask);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
