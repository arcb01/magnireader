using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MagniReader.Helpers;

namespace MagniReader.UI;

/// <summary>
/// Lightweight topmost overlay that shows a glowing gold border around
/// the UI element under the cursor when Ctrl+Alt is held.
/// Click-through, no text, no magnification — just a clean highlight.
/// </summary>
public partial class HoverHighlightWindow : Window
{
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private Rect _currentRect = Rect.Empty;

    public HoverHighlightWindow()
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
                style | NativeInterop.WS_EX_TRANSPARENT | NativeInterop.WS_EX_TOOLWINDOW);
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var m = source.CompositionTarget.TransformFromDevice;
            _dpiScaleX = m.M11;
            _dpiScaleY = m.M22;
        }
    }

    /// <summary>
    /// Shows a gold border around the given screen rect (physical pixels).
    /// </summary>
    public void HighlightElement(Rect physicalRect, string _unused = "")
    {
        if (physicalRect == _currentRect && Visibility == Visibility.Visible)
            return;

        _currentRect = physicalRect;

        // Convert physical pixels → WPF DIPs
        const double pad = 4;
        double x = physicalRect.X * _dpiScaleX - pad;
        double y = physicalRect.Y * _dpiScaleY - pad;
        double w = physicalRect.Width * _dpiScaleX + pad * 2;
        double h = physicalRect.Height * _dpiScaleY + pad * 2;

        Left = x;
        Top = y;
        Width = w;
        Height = h;

        // Fade in
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            GlowBorder.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            GlowBorder.BeginAnimation(OpacityProperty, null);
            GlowBorder.Opacity = 1;
        }
    }

    /// <summary>
    /// Smoothly hides the highlight.
    /// </summary>
    public void HideHighlight()
    {
        if (Visibility != Visibility.Visible) return;

        var fadeOut = new DoubleAnimation(GlowBorder.Opacity, 0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (s, e) =>
        {
            Visibility = Visibility.Collapsed;
            _currentRect = Rect.Empty;
        };
        GlowBorder.BeginAnimation(OpacityProperty, fadeOut);
    }

    public void SetHighlightColor(Color color)
    {
        GlowBorder.BorderBrush = new SolidColorBrush(color);
        GlowBorder.Background = new SolidColorBrush(color) { Opacity = 0.05 };
        if (GlowBorder.Effect is System.Windows.Media.Effects.DropShadowEffect fx)
            fx.Color = color;
    }
}
