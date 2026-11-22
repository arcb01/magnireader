using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MagniReader.Helpers;

namespace MagniReader.UI;

/// <summary>
/// Small strip of playback controls (back 10 s, pause/play, forward 10 s, speed, reading
/// language, stop) that
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
    public event Action? SpeedRequested;          // next speed preset
    public event Action<string>? LanguageRequested; // "auto" or a two-letter code

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

    /// <summary>Shows the reading speed on the speed button, e.g. 1.5 → "1.5x".</summary>
    public void SetSpeed(double multiplier)
    {
        multiplier = Math.Round(multiplier * 20) / 20; // nearest 0.05: SAPI's scale isn't linear
        string label = multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";
        SpeedText.Text = label;
        SpeedButton.ToolTip = $"Reading speed {label} (click for the next speed)";
    }

    // ─── Reading language ────────────────────────────────────────

    /// <summary>
    /// Shows the reading language on its button: "Auto · ES" while detecting (with the
    /// language detected for this text), or just "ES" when the user chose one.
    /// </summary>
    /// <param name="setting">"auto" or a two-letter code.</param>
    /// <param name="current">Language being read now (two-letter code), if known.</param>
    public void SetLanguage(string setting, string? current)
    {
        _languageSetting = string.IsNullOrEmpty(setting) ? "auto" : setting;
        bool auto = _languageSetting == "auto";
        string? shown = auto ? current : _languageSetting;
        string code = shown?.ToUpperInvariant() ?? "";

        LanguageText.Text = auto ? (code.Length > 0 ? $"Auto · {code}" : "Auto") : code;
        LanguageButton.ToolTip = auto
            ? $"Reading language: detected automatically{(shown != null ? $" ({LanguageName(shown)})" : "")}. Click to choose one."
            : $"Reading language: {LanguageName(_languageSetting)} (chosen by you). Click to change it or go back to Auto.";
        System.Windows.Automation.AutomationProperties.SetName(LanguageButton,
            auto ? $"Reading language, auto{(shown != null ? ", " + LanguageName(shown) : "")}"
                 : $"Reading language, {LanguageName(_languageSetting)}");
    }

    private string _languageSetting = "auto";
    private IntPtr _popupHwnd;
    private System.Windows.Threading.DispatcherTimer? _popupCloseTimer;

    private static string LanguageName(string code) =>
        Core.LanguageDetector.ManualChoices.FirstOrDefault(c => c.Code == code).Name ?? code;

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (LanguagePopup.IsOpen) { LanguagePopup.IsOpen = false; return; }
        BuildLanguageList();
        LanguagePopup.IsOpen = true;
    }

    private void BuildLanguageList()
    {
        LanguageList.Children.Clear();
        foreach (var (code, name) in Core.LanguageDetector.ManualChoices)
        {
            bool selected = code == _languageSetting;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new System.Windows.Controls.TextBlock
            {
                Text = selected ? "" : "",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = name,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tag = new System.Windows.Controls.TextBlock
            {
                Text = code == "auto" ? "" : code.ToUpperInvariant(),
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0x70, 0x86)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(tag, 2);
            row.Children.Add(check);
            row.Children.Add(label);
            row.Children.Add(tag);

            var button = new System.Windows.Controls.Button
            {
                Content = row,
                Style = (Style)FindResource("ListButton"),
                ToolTip = code == "auto"
                    ? "Detect the language of each text automatically"
                    : $"Always read in {name} until you change it",
            };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            string chosen = code;
            button.Click += (_, _) =>
            {
                LanguagePopup.IsOpen = false;
                if (chosen != _languageSetting) LanguageRequested?.Invoke(chosen);
            };
            LanguageList.Children.Add(button);

            if (code == "auto")
                LanguageList.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Height = 1, Margin = new Thickness(6, 3, 6, 3),
                    Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A)),
                });
        }
    }

    private void LanguagePopup_Opened(object? sender, EventArgs e)
    {
        // The list is its own window: remember it so clicks on it count as clicks on
        // MagniReader (see ContainsScreenPoint), and keep it out of OCR screenshots.
        if (PresentationSource.FromVisual(LanguagePopup.Child) is HwndSource source)
        {
            _popupHwnd = source.Handle;
            try { NativeInterop.SetWindowDisplayAffinity(_popupHwnd, NativeInterop.WDA_EXCLUDEFROMCAPTURE); }
            catch { }
        }

        // The strip never takes focus, so it can't rely on a click elsewhere to close the
        // list: close it once the mouse has been away from the list and its button for a
        // moment, like a menu.
        if (_popupCloseTimer == null)
        {
            _popupCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _popupCloseTimer.Tick += (_, _) =>
            {
                if (LanguageList.IsMouseOver || LanguageButton.IsMouseOver) { _mouseAwayTicks = 0; return; }
                if (++_mouseAwayTicks >= 2) LanguagePopup.IsOpen = false;
            };
        }
        _mouseAwayTicks = 0;
        _popupCloseTimer.Start();
    }

    private int _mouseAwayTicks;

    private void LanguagePopup_Closed(object? sender, EventArgs e)
    {
        _popupCloseTimer?.Stop();
        _popupHwnd = IntPtr.Zero;
    }

    /// <summary>Closes the language list (the strip is being hidden).</summary>
    public void CloseMenus() => LanguagePopup.IsOpen = false;

    /// <summary>
    /// True when the physical-pixel screen point (from the low-level mouse hook) is on
    /// the visible strip or its open language list.
    /// </summary>
    /// <remarks>Called from the input hook thread: uses only Win32, no WPF properties.</remarks>
    public bool ContainsScreenPoint(int x, int y) =>
        WindowContains(_hwnd, x, y) || WindowContains(_popupHwnd, x, y);

    private static bool WindowContains(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero || !NativeInterop.IsWindowVisible(hwnd)) return false;
        if (!NativeInterop.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
    private void RewindButton_Click(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(-SkipSeconds);
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(SkipSeconds);
    private void StopButton_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void SpeedButton_Click(object sender, RoutedEventArgs e) => SpeedRequested?.Invoke();
}
