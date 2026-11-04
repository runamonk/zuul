using Avalonia;
using Avalonia.Threading;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Zuul;

class Program
{
    // Per user, so two people signed in to one computer each get their own copy.
    private static readonly string InstanceName = "Zuul-" + Environment.UserName;

    // Avalonia and SynchronizationContext-dependent APIs are unavailable until AppMain starts.
    [STAThread]
    public static void Main(string[] args)
    {
        // Two launchers fighting over one hotkey helps nobody, so a second start just opens the first.
        using var running = new Mutex(false, InstanceName, out var first);
        if (!first && ShowOpenCopy()) return;
        ListenForCopies();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // False when the open copy doesn't answer, so this one starts instead of doing nothing.
    private static bool ShowOpenCopy()
    {
        try
        {
            // Lets the open copy take the foreground; Windows only allows that with this process's say-so.
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1);
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out);
            // Connecting is the whole message; the open copy hangs up straight away.
            pipe.Connect(2000);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    private static void ListenForCopies() => new Thread(() =>
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                Dispatcher.UIThread.Post(() => (Application.Current as App)?.ShowLauncher());
            }
            catch (IOException) { return; }
        }
    }) { IsBackground = true }.Start();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
