using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MagniReader.Helpers;

namespace MagniReader.UI;

/// <summary>
/// Small always-on-top control bar, in the spirit of the Windows Magnifier toolbar.
/// It never takes focus away from the app being read (WS_EX_NOACTIVATE), and it is
/// excluded from screen captures so it doesn't end up in snips or OCR screenshots.
/// The bar only raises events; App decides what each control does.
/// </summary>
public partial class ControlBarWindow : Window
{
    public event Action? ReadRequested;
    public event Action? StopRequested;
    public event Action<int>? RateChangeRequested;   // -1 slower, +1 faster
    public event Action? EngineToggleRequested;
    public event Action? SnipRequested;
    public event Action? SettingsRequested;
    public event Action? MinimizeRequested;
    public event Action? ExitRequested;

    /// <summary>Raised after the user finishes dragging the bar. Gives the new Left/Top.</summary>
    public event Action<double, double>? Moved;

    private readonly double? _savedLeft;
    private readonly double? _savedTop;
    private IntPtr _hwnd;

    private static readonly Brush ArmedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));

    public ControlBarWindow(double? left, double? top)
    {
        InitializeComponent();
        _savedLeft = left;
        _savedTop = top;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PlaceOnScreen();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        // Don't steal focus from the document the user is reading, and stay out of Alt+Tab.
        var style = NativeInterop.GetWindowLong(_hwnd, NativeInterop.GWL_EXSTYLE);
        NativeInterop.SetWindowLong(_hwnd, NativeInterop.GWL_EXSTYLE,
            style | NativeInterop.WS_EX_NOACTIVATE | NativeInterop.WS_EX_TOOLWINDOW);

        try { NativeInterop.SetWindowDisplayAffinity(_hwnd, NativeInterop.WDA_EXCLUDEFROMCAPTURE); }
        catch { /* older Windows: the bar may show up in screenshots, harmless */ }
    }

    /// <summary>
    /// Restores the saved position if it's still on a connected screen;
    /// otherwise centers the bar at the top of the primary work area.
    /// </summary>
    private void PlaceOnScreen()
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        if (_savedLeft is double l && _savedTop is double t &&
            l >= vLeft && t >= vTop && l + 40 <= vRight && t + 20 <= vBottom)
        {
            Left = l;
            Top = t;
            return;
        }

        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + 4;
    }

    /// <summary>
    /// True when the physical-pixel screen point (as given by the low-level mouse hook)
    /// is on the visible bar. Used so clicking the bar doesn't cancel or trigger a reading.
    /// </summary>
    /// <remarks>Called from the input hook thread: uses only Win32, no WPF properties.</remarks>
    public bool ContainsScreenPoint(int x, int y)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !NativeInterop.IsWindowVisible(hwnd)) return false;
        if (!NativeInterop.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    // ─── State shown on the bar ─────────────────────────────────

    public void SetReading(bool isReading) => StopButton.IsEnabled = isReading;

    /// <summary>Highlights the Read button while it's waiting for the user to click some text.</summary>
    public void SetPickArmed(bool armed)
    {
        ReadGlyph.Foreground = armed ? ArmedBrush : NormalBrush;
        ReadButton.ToolTip = armed
            ? "Now click any text to read it (Esc to cancel)"
            : "Read: click this, then click any text (or Ctrl+Alt+Click anytime)";
    }

    public void SetRate(int rate)
    {
        SpeedText.Text = rate > 0 ? $"Speed +{rate}" : $"Speed {rate}";
        SlowerButton.IsEnabled = rate > -10;
        FasterButton.IsEnabled = rate < 10;
    }

    public void SetEngine(bool kokoro) => EngineText.Text = kokoro ? "Kokoro" : "Windows";

    // ─── Handlers ───────────────────────────────────────────────

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch (InvalidOperationException) { return; }
        Moved?.Invoke(Left, Top);
    }

    private void ReadButton_Click(object sender, RoutedEventArgs e) => ReadRequested?.Invoke();
    private void StopButton_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void SlowerButton_Click(object sender, RoutedEventArgs e) => RateChangeRequested?.Invoke(-1);
    private void FasterButton_Click(object sender, RoutedEventArgs e) => RateChangeRequested?.Invoke(+1);
    private void EngineButton_Click(object sender, RoutedEventArgs e) => EngineToggleRequested?.Invoke();
    private void SnipButton_Click(object sender, RoutedEventArgs e) => SnipRequested?.Invoke();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
