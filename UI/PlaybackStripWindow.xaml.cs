using System.Windows;
using System.Windows.Interop;
using MagniReader.Helpers;

namespace MagniReader.UI;

/// <summary>
/// Small strip of playback controls (back 10 s, pause/play, forward 10 s, stop) that
/// floats just above the box of the text being read.
///
/// Like the control bar it never takes focus (WS_EX_NOACTIVATE), so the reading and the
/// app being read are not disturbed, and it is kept out of screen captures (OCR).
/// The click hook lets clicks on it through (see App: InputManager.IsOwnUiAt), so they
/// reach the buttons instead of stopping the reading.
/// The strip only raises events; App decides what each button does.
/// </summary>
public partial class PlaybackStripWindow : Window
{
    public event Action? PlayPauseRequested;
    public event Action<double>? SkipRequested;   // seconds, negative = back
    public event Action? StopRequested;

    public const double SkipSeconds = 10;

    private IntPtr _hwnd;

    public PlaybackStripWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        var style = NativeInterop.GetWindowLong(_hwnd, NativeInterop.GWL_EXSTYLE);
        NativeInterop.SetWindowLong(_hwnd, NativeInterop.GWL_EXSTYLE,
            style | NativeInterop.WS_EX_NOACTIVATE | NativeInterop.WS_EX_TOOLWINDOW);

        try { NativeInterop.SetWindowDisplayAffinity(_hwnd, NativeInterop.WDA_EXCLUDEFROMCAPTURE); }
        catch { /* older Windows: the strip may show up in OCR screenshots */ }
    }

    /// <summary>
    /// Shows the strip just above <paramref name="box"/> (screen DIPs), aligned with its
    /// left edge. Goes below the box when there's no room above, and inside its top
    /// edge when there's no room below either (a box filling the screen).
    /// </summary>
    public void ShowAt(Rect box)
    {
        // Size before showing, so it doesn't flash at the old place
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Root.DesiredSize; // includes the shadow margin
        double w = size.Width;
        double h = size.Height;

        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        // The shadow margin overlaps the box border a little, so the strip sits on it.
        const double overlap = 4;
        double top = box.Top - h + overlap;
        if (top < vTop)
        {
            top = box.Bottom - overlap;
            if (top + h > vBottom) top = Math.Max(vTop, box.Top + overlap);
        }
        double left = Math.Clamp(box.Left - Root.Margin.Left, vLeft, Math.Max(vLeft, vRight - w));

        Left = left;
        Top = top;

        if (!IsVisible) Show();
        BringToFront();
        // The overlay's activation is processed after this call; raise the strip again
        // once it has settled, so it never ends up under the dim layer.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(BringToFront));
    }

    /// <summary>
    /// Puts the strip above the overlay (both are topmost; the overlay is activated when
    /// a reading starts, which lifts it over windows shown earlier) without activating it.
    /// </summary>
    public void BringToFront()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeInterop.SetWindowPos(_hwnd, NativeInterop.HWND_TOPMOST, 0, 0, 0, 0,
            NativeInterop.SWP_NOMOVE | NativeInterop.SWP_NOSIZE | NativeInterop.SWP_NOACTIVATE |
            NativeInterop.SWP_SHOWWINDOW);
    }

    public void SetPaused(bool paused)
    {
        // E768 = Play, E769 = Pause
        PlayPauseGlyph.Text = paused ? "" : "";
        PlayPauseButton.ToolTip = paused ? "Continue reading" : "Pause";
    }

    /// <summary>
    /// True when the physical-pixel screen point (from the low-level mouse hook) is on
    /// the visible strip.
    /// </summary>
    /// <remarks>Called from the input hook thread: uses only Win32, no WPF properties.</remarks>
    public bool ContainsScreenPoint(int x, int y)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !NativeInterop.IsWindowVisible(hwnd)) return false;
        if (!NativeInterop.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
    private void RewindButton_Click(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(-SkipSeconds);
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(SkipSeconds);
    private void StopButton_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
}
