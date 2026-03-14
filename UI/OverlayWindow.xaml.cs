using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MagniReader.Helpers;

namespace MagniReader.UI;

/// <summary>
/// Overlay that highlights the text element being read on screen.
/// 
/// Instead of trying to guess individual word positions (which is unreliable
/// without access to font metrics), we use:
///   - A gold border around the full text element
///   - A progress fill that grows within the element as reading advances
///   - A thin scanning line that sweeps through the element
///   - A progress bar at the top of the screen
/// </summary>
public partial class OverlayWindow : Window
{
    private double _dimOpacity = 0.6;
    private Color _highlightColor = Colors.Gold;
    private bool _isShowing;
    private string _fullText = string.Empty;
    private double _screenW, _screenH;

    // DPI conversion (physical pixels → WPF DIPs)
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // Current text element rect (in overlay DIPs)
    private Rect _holeRect;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var style = NativeInterop.GetWindowLong(hwnd, NativeInterop.GWL_EXSTYLE);
            NativeInterop.SetWindowLong(hwnd, NativeInterop.GWL_EXSTYLE,
                style | NativeInterop.WS_EX_TOOLWINDOW);
        }
    }

    public void ShowOverlay(Rect sourceRect, string fullText, string languageName = "")
    {
        _fullText = fullText;
        _isShowing = true;

        // Cover the entire virtual screen
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = _screenW = SystemParameters.VirtualScreenWidth;
        Height = _screenH = SystemParameters.VirtualScreenHeight;
        OverlayCanvas.Width = _screenW;
        OverlayCanvas.Height = _screenH;

        BeginAnimation(OpacityProperty, null);
        Opacity = 1.0;

        // Get DPI scale
        var presentationSource = PresentationSource.FromVisual(this);
        if (presentationSource?.CompositionTarget != null)
        {
            var m = presentationSource.CompositionTarget.TransformFromDevice;
            _dpiScaleX = m.M11;
            _dpiScaleY = m.M22;
        }

        // Convert physical pixels → WPF DIPs and position
        _holeRect = ScaleAndPosition(sourceRect);
        LayoutHighlight(_holeRect);

        // Reset reading progress indicators
        ReadingFill.Fill = new SolidColorBrush(_highlightColor);
        Canvas.SetLeft(ReadingFill, _holeRect.X);
        Canvas.SetTop(ReadingFill, _holeRect.Y);
        ReadingFill.Width = 0;
        ReadingFill.Height = _holeRect.Height;
        ReadingFill.Visibility = Visibility.Visible;

        ScanLine.Fill = new SolidColorBrush(_highlightColor);
        Canvas.SetLeft(ScanLine, _holeRect.X);
        Canvas.SetTop(ScanLine, _holeRect.Y);
        ScanLine.Height = _holeRect.Height;
        ScanLine.Visibility = Visibility.Visible;



        Visibility = Visibility.Visible;
        Activate();
    }

    /// <summary>
    /// Moves the overlay to a new text element for continuous reading.
    /// </summary>
    public void MoveToElement(Rect sourceRect, string newText)
    {
        if (!_isShowing) return;

        _fullText = newText;

        Dispatcher.BeginInvoke(() =>
        {
            _holeRect = ScaleAndPosition(sourceRect);
            LayoutHighlight(_holeRect);

            // Reset progress fill for new element
            Canvas.SetLeft(ReadingFill, _holeRect.X);
            Canvas.SetTop(ReadingFill, _holeRect.Y);
            ReadingFill.Width = 0;
            ReadingFill.Height = _holeRect.Height;

            Canvas.SetLeft(ScanLine, _holeRect.X);
            Canvas.SetTop(ScanLine, _holeRect.Y);
            ScanLine.Height = _holeRect.Height;
        });
    }

    public void HighlightWord(int charPosition, int charLength)
    {
        if (!_isShowing || string.IsNullOrEmpty(_fullText)) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_fullText.Length <= 0) return;

            double progress = Math.Min(1.0, (double)(charPosition + charLength) / _fullText.Length);



            // Reading fill grows from left within the text element
            ReadingFill.Width = _holeRect.Width * progress;

            // Scanning line moves across the element
            double scanX = _holeRect.X + _holeRect.Width * progress;
            Canvas.SetLeft(ScanLine, Math.Min(scanX, _holeRect.X + _holeRect.Width - 2));
        });
    }

    public void HideOverlay()
    {
        if (!_isShowing) return;
        _isShowing = false;

        Dispatcher.BeginInvoke(() =>
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s, e) =>
            {
                Visibility = Visibility.Collapsed;
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;
                HighlightBorder.Visibility = Visibility.Collapsed;
                ReadingFill.Visibility = Visibility.Collapsed;
                ScanLine.Visibility = Visibility.Collapsed;
            };
            BeginAnimation(OpacityProperty, fadeOut);
        });
    }

    public void UpdateSettings(double dimOpacity, string highlightColorHex)
    {
        _dimOpacity = dimOpacity;
        try { _highlightColor = (Color)ColorConverter.ConvertFromString(highlightColorHex); }
        catch { _highlightColor = Colors.Gold; }
    }

    // --- Private helpers ---

    private Rect ScaleAndPosition(Rect sourceRect)
    {
        double pad = 6;
        var scaled = new Rect(
            sourceRect.X * _dpiScaleX,
            sourceRect.Y * _dpiScaleY,
            sourceRect.Width * _dpiScaleX,
            sourceRect.Height * _dpiScaleY);

        var hole = new Rect(
            Math.Max(0, scaled.X - Left - pad),
            Math.Max(0, scaled.Y - Top - pad),
            Math.Min(_screenW, scaled.Width + pad * 2),
            Math.Min(_screenH, scaled.Height + pad * 2));

        if (hole.X + hole.Width > _screenW) hole.Width = _screenW - hole.X;
        if (hole.Y + hole.Height > _screenH) hole.Height = _screenH - hole.Y;

        return hole;
    }

    private void LayoutHighlight(Rect hole)
    {
        // Dim rects around the cutout
        DimBackground.Visibility = Visibility.Collapsed;
        var dimBrush = new SolidColorBrush(Color.FromArgb((byte)(_dimOpacity * 255), 0, 0, 0));
        PositionDim(DimTop, 0, 0, _screenW, hole.Y, dimBrush);
        PositionDim(DimBottom, 0, hole.Y + hole.Height, _screenW, _screenH - (hole.Y + hole.Height), dimBrush);
        PositionDim(DimLeft, 0, hole.Y, hole.X, hole.Height, dimBrush);
        PositionDim(DimRight, hole.X + hole.Width, hole.Y, _screenW - (hole.X + hole.Width), hole.Height, dimBrush);

        // Highlight border
        Canvas.SetLeft(HighlightBorder, hole.X);
        Canvas.SetTop(HighlightBorder, hole.Y);
        HighlightBorder.Width = hole.Width;
        HighlightBorder.Height = hole.Height;
        HighlightBrush.Color = _highlightColor;
        if (HighlightBorder.Effect is System.Windows.Media.Effects.DropShadowEffect glow)
            glow.Color = _highlightColor;
        HighlightBorder.Visibility = Visibility.Visible;
    }

    private static void PositionDim(System.Windows.Shapes.Rectangle rect,
        double x, double y, double w, double h, Brush fill)
    {
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = Math.Max(0, w);
        rect.Height = Math.Max(0, h);
        rect.Fill = fill;
    }
}
