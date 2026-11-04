using System.Runtime.InteropServices;
using Avalonia.Threading;
using Zuul.Core;

namespace Zuul;

// RegisterHotKey on its own thread with no window: WM_HOTKEY lands in that thread's queue.
// Keeps Avalonia's window procedure out of it, and pressing the hotkey lets this process take the foreground.
internal sealed class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int WmQuit = 0x0012;
    private const uint ModNoRepeat = 0x4000;

    private readonly Thread _thread;
    private uint _threadId;

    private GlobalHotKey(Thread thread) => _thread = thread;

    // Null with an error when the text isn't a hotkey or another program already has it.
    public static GlobalHotKey? Register(string text, Action pressed, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) return null;
        if (!HotKeyText.TryParse(text, out var modifiers, out var key))
        {
            error = $"\"{text}\" isn't a hotkey Zuul understands.";
            return null;
        }
        var registered = new TaskCompletionSource<bool>();
        GlobalHotKey? hotKey = null;
        var thread = new Thread(() =>
        {
            hotKey!._threadId = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, 1, modifiers | ModNoRepeat, key))
            {
                registered.SetResult(false);
                return;
            }
            registered.SetResult(true);
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                if (message.Message == WmHotKey) Dispatcher.UIThread.Post(pressed);
            UnregisterHotKey(IntPtr.Zero, 1);
        }) { IsBackground = true, Name = "Hotkey" };
        hotKey = new GlobalHotKey(thread);
        thread.Start();
        if (registered.Task.Result) return hotKey;
        error = $"{text} is already used by another program. Pick a different hotkey in settings.";
        return null;
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
