using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Zuul;

// One line in the corner above the taskbar, for things happening in the background like updates.
// It never takes focus, and a click puts it away.
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hide = new();

    public ToastWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        _hide.Tick += (_, _) => Dismiss();
        PointerPressed += (_, _) => Dismiss();
        // Its size is only known once laid out, and changes with the text.
        SizeChanged += (_, _) => PlaceInCorner();
    }

    // A percent shows the bar. Without hideAfter it stays until the next message or Dismiss.
    public void ShowMessage(string text, double? percent = null, TimeSpan? hideAfter = null)
    {
        Message.Text = text;
        Progress.IsVisible = percent != null;
        if (percent is { } value) Progress.Value = value;
        _hide.Stop();
        if (hideAfter is { } after)
        {
            _hide.Interval = after;
            _hide.Start();
        }
        if (!IsVisible) Show();
        PlaceInCorner();
    }

    // Hidden rather than closed, so the next message reuses it.
    public void Dismiss()
    {
        _hide.Stop();
        Hide();
    }

    private void PlaceInCorner()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(ClientSize, screen.Scaling);
        Position = new PixelPoint(area.Right - size.Width, area.Bottom - size.Height);
    }
}
