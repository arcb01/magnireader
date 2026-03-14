using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using MagniReader.Helpers;

namespace MagniReader.UI;

/// <summary>
/// Full-screen overlay that lets the user drag-select a screen region.
/// Returns the captured area as a PNG byte array.
/// </summary>
public partial class SnipWindow : Window
{
    private System.Windows.Point _startPoint;
    private bool _isDragging;
    private TaskCompletionSource<byte[]?> _completionSource = new();

    // Store DPI for coordinate conversion
    private double _dpiX = 1.0, _dpiY = 1.0;

    public SnipWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Make tool window (hide from Alt+Tab)
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var style = NativeInterop.GetWindowLong(hwnd, NativeInterop.GWL_EXSTYLE);
            NativeInterop.SetWindowLong(hwnd, NativeInterop.GWL_EXSTYLE,
                style | NativeInterop.WS_EX_TOOLWINDOW);
        }

        // Get DPI scale for correct screen capture
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiX = source.CompositionTarget.TransformToDevice.M11;
            _dpiY = source.CompositionTarget.TransformToDevice.M22;
        }

        // Cover full virtual screen
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        SnipCanvas.Width = Width;
        SnipCanvas.Height = Height;

        // Full dim
        Canvas.SetLeft(FullDim, 0);
        Canvas.SetTop(FullDim, 0);
        FullDim.Width = Width;
        FullDim.Height = Height;

        // Center instruction banner
        InstructionBanner.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(InstructionBanner, (Width - InstructionBanner.DesiredSize.Width) / 2);
        Canvas.SetTop(InstructionBanner, 40);

        Activate();
        Focus();
    }

    /// <summary>
    /// Shows the snip window and waits for the user to select an area.
    /// Returns the screenshot as PNG bytes, or null if cancelled.
    /// </summary>
    public Task<byte[]?> CaptureAreaAsync()
    {
        _completionSource = new TaskCompletionSource<byte[]?>();
        Show();
        return _completionSource.Task;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(SnipCanvas);
        _isDragging = true;

        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _startPoint.X);
        Canvas.SetTop(SelectionRect, _startPoint.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;

        SizeBadge.Visibility = Visibility.Visible;

        // Hide instruction
        InstructionBanner.Visibility = Visibility.Collapsed;

        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var current = e.GetPosition(SnipCanvas);

        double x = Math.Min(_startPoint.X, current.X);
        double y = Math.Min(_startPoint.Y, current.Y);
        double w = Math.Abs(current.X - _startPoint.X);
        double h = Math.Abs(current.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        // Show size badge near cursor
        int pixelW = (int)(w * _dpiX);
        int pixelH = (int)(h * _dpiY);
        SizeText.Text = $"{pixelW} × {pixelH}";
        Canvas.SetLeft(SizeBadge, current.X + 12);
        Canvas.SetTop(SizeBadge, current.Y + 12);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();

        var endPoint = e.GetPosition(SnipCanvas);

        double x = Math.Min(_startPoint.X, endPoint.X);
        double y = Math.Min(_startPoint.Y, endPoint.Y);
        double w = Math.Abs(endPoint.X - _startPoint.X);
        double h = Math.Abs(endPoint.Y - _startPoint.Y);

        // Minimum selection size
        if (w < 10 || h < 10)
        {
            _completionSource.TrySetResult(null);
            Hide();
            return;
        }

        // Convert WPF DIPs to physical pixels for screen capture
        int physX = (int)((x + Left) * _dpiX);
        int physY = (int)((y + Top) * _dpiY);
        int physW = (int)(w * _dpiX);
        int physH = (int)(h * _dpiY);

        // Hide overlay before capturing (so it's not in the screenshot)
        Hide();

        // Small delay to let the window hide
        Task.Delay(150).ContinueWith(_ =>
        {
            var screenshot = CaptureScreen(physX, physY, physW, physH);
            _completionSource.TrySetResult(screenshot);
        }, TaskScheduler.Default);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _isDragging = false;
            _completionSource.TrySetResult(null);
            Hide();
        }
    }

    private static byte[]? CaptureScreen(int x, int y, int width, int height)
    {
        try
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] Screen capture error: {ex.Message}");
            return null;
        }
    }
}
