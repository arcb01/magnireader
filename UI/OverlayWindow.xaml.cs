using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MagniReader.Helpers;

namespace MagniReader.UI;

/// <summary>
/// Overlay that highlights the text being read on screen:
///   - The rest of the screen is dimmed and a gold border surrounds the text element
///   - A box sits over the word being spoken and glides to the next word as
///     speech advances (word positions come from <see cref="Core.WordLocator"/>)
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

    // Current word box rect (in overlay DIPs), Rect.Empty when hidden
    private Rect _wordRect = Rect.Empty;
    private static readonly Duration WordMoveDuration = new(TimeSpan.FromMilliseconds(60));

    /// <summary>
    /// Raised when the highlighted box is placed or moves to another element.
    /// Gives the box in screen DIPs (for the playback controls that sit on top of it).
    /// </summary>
    public event Action<Rect>? HighlightBoxChanged;

    /// <summary>The highlighted box in screen DIPs.</summary>
    public Rect HighlightBoxOnScreen => new(_holeRect.X + Left, _holeRect.Y + Top, _holeRect.Width, _holeRect.Height);

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

            // Keep the overlay out of screen captures, so the OCR screenshot of
            // the next element isn't dimmed or covered by the word box.
            // Best effort: needs Windows 10 2004 or later.
            try { NativeInterop.SetWindowDisplayAffinity(hwnd, NativeInterop.WDA_EXCLUDEFROMCAPTURE); }
            catch { }
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

        // Word box starts hidden until the first word is located
        ApplyWordBoxColor();
        HideWordBox();

        Visibility = Visibility.Visible;
        Activate();
        HighlightBoxChanged?.Invoke(HighlightBoxOnScreen);
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

            // New element: the word box reappears on its first word
            HideWordBox();
            HighlightBoxChanged?.Invoke(HighlightBoxOnScreen);
        });
    }

    /// <summary>
    /// Moves the word box over the word being spoken.
    /// <paramref name="physicalRect"/> is in physical screen pixels (as returned
    /// by UI Automation / OCR). Pass null when the word's position is unknown:
    /// the box then stays on the previous word rather than flickering.
    /// Must be called on the UI thread.
    /// </summary>
    public void HighlightWord(Rect? physicalRect)
    {
        if (!_isShowing || physicalRect is not Rect r || r.IsEmpty || r.Width <= 0 || r.Height <= 0)
            return;

        var target = ToOverlay(r, pad: 3);
        if (!(target.Width > 0) || !(target.Height > 0)) return; // off this screen (or NaN)

        // Ignore words that are scrolled out of the highlighted element
        if (!_holeRect.IntersectsWith(target)) return;

        bool wasHidden = _wordRect.IsEmpty || WordBox.Visibility != Visibility.Visible;
        // Jump instead of gliding when moving to another line, so the box
        // doesn't slide diagonally across the text.
        bool newLine = !wasHidden && Math.Abs(target.Y - _wordRect.Y) > _wordRect.Height / 2;

        _wordRect = target;

        if (wasHidden || newLine)
        {
            StopWordBoxAnimations();
            Canvas.SetLeft(WordBox, target.X);
            Canvas.SetTop(WordBox, target.Y);
            WordBox.Width = target.Width;
            WordBox.Height = target.Height;
            WordBox.Visibility = Visibility.Visible;
            return;
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        AnimateTo(Canvas.LeftProperty, target.X, ease);
        AnimateTo(Canvas.TopProperty, target.Y, ease);
        AnimateTo(WidthProperty, target.Width, ease);
        AnimateTo(HeightProperty, target.Height, ease);
    }

    private void AnimateTo(DependencyProperty property, double to, IEasingFunction ease)
    {
        var anim = new DoubleAnimation(to, WordMoveDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        WordBox.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopWordBoxAnimations()
    {
        WordBox.BeginAnimation(Canvas.LeftProperty, null);
        WordBox.BeginAnimation(Canvas.TopProperty, null);
        WordBox.BeginAnimation(WidthProperty, null);
        WordBox.BeginAnimation(HeightProperty, null);
    }

    private void HideWordBox()
    {
        StopWordBoxAnimations();
        WordBox.Visibility = Visibility.Collapsed;
        _wordRect = Rect.Empty;
    }

    private void ApplyWordBoxColor()
    {
        WordBoxBorderBrush.Color = _highlightColor;
        WordBoxFillBrush.Color = _highlightColor;
        WordBoxGlow.Color = _highlightColor;
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
                HideWordBox();
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

    private Rect ScaleAndPosition(Rect sourceRect) => ToOverlay(sourceRect, pad: 6);

    /// <summary>
    /// Converts a physical-pixel screen rect to overlay canvas coordinates (DIPs),
    /// adding <paramref name="pad"/> on each side and clamping to the screen.
    /// </summary>
    private Rect ToOverlay(Rect sourceRect, double pad)
    {
        if (sourceRect.IsEmpty) return new Rect(0, 0, 0, 0);

        var scaled = new Rect(
            sourceRect.X * _dpiScaleX,
            sourceRect.Y * _dpiScaleY,
            sourceRect.Width * _dpiScaleX,
            sourceRect.Height * _dpiScaleY);

        // Clamp each edge to the screen. A rect partly or wholly off this screen (text
        // scrolled away, a word on another monitor) used to give a negative width,
        // which WPF rejects with an exception that closed the app.
        double left = Math.Clamp(scaled.X - Left - pad, 0, _screenW);
        double top = Math.Clamp(scaled.Y - Top - pad, 0, _screenH);
        double right = Math.Clamp(scaled.X - Left + scaled.Width + pad, 0, _screenW);
        double bottom = Math.Clamp(scaled.Y - Top + scaled.Height + pad, 0, _screenH);

        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
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
