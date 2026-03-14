using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using MagniReader.Core;

namespace MagniReader.UI;

public partial class VisionResultWindow : Window
{
    private readonly SpeechEngine? _speechEngine;
    private readonly LanguageDetector? _langDetector;
    private string _resultText = "";

    // Stored for "More Details" follow-up request
    private byte[]? _imageBytes;
    private VisionAnalyzer.Provider _provider;
    private string _apiKey = "";
    private VisionAnalyzer? _analyzer;
    private bool _hasDetailedResult;

    public VisionResultWindow(SpeechEngine? speechEngine = null, LanguageDetector? langDetector = null)
    {
        InitializeComponent();
        _speechEngine = speechEngine;
        _langDetector = langDetector;

        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
        if (System.IO.File.Exists(iconPath))
            Icon = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
    }

    /// <summary>
    /// Stores the image + API info so "More Details" can make a follow-up call.
    /// </summary>
    public void SetImageContext(byte[] imageBytes, VisionAnalyzer.Provider provider,
        string apiKey, VisionAnalyzer analyzer)
    {
        _imageBytes = imageBytes;
        _provider = provider;
        _apiKey = apiKey;
        _analyzer = analyzer;
    }

    /// <summary>
    /// Shows the analyzing state.
    /// </summary>
    public void ShowLoading()
    {
        Dispatcher.Invoke(() =>
        {
            ResultText.Text = "🔄 Analyzing the selected area...";
            StatusText.Text = "Sending to VLM...";
            ReadAloudBtn.IsEnabled = false;
            CopyBtn.IsEnabled = false;
            MoreDetailsBtn.IsEnabled = false;
        });
    }

    /// <summary>
    /// Shows the result (brief summary).
    /// </summary>
    public void ShowResult(string text)
    {
        _resultText = text;
        _hasDetailedResult = false;

        Dispatcher.Invoke(() =>
        {
            ResultText.Text = text;
            StatusText.Text = text.StartsWith("⚠") ? "Error" : "Quick summary";
            ReadAloudBtn.IsEnabled = !text.StartsWith("⚠");
            CopyBtn.IsEnabled = true;
            MoreDetailsBtn.IsEnabled = !text.StartsWith("⚠") && _imageBytes != null;
        });
    }

    private async void OnMoreDetails(object sender, RoutedEventArgs e)
    {
        if (_imageBytes == null || _analyzer == null || _hasDetailedResult) return;

        _speechEngine?.Cancel();

        MoreDetailsBtn.IsEnabled = false;
        MoreDetailsBtn.Content = "🔄 Loading...";
        StatusText.Text = "Requesting detailed analysis...";

        var detailedResult = await _analyzer.AnalyzeImageAsync(
            _imageBytes, _provider, _apiKey, detailed: true);

        _resultText = detailedResult;
        _hasDetailedResult = true;

        ResultText.Text = detailedResult;
        StatusText.Text = detailedResult.StartsWith("⚠") ? "Error" : "Detailed analysis";
        MoreDetailsBtn.Content = "✅ Details loaded";
        ReadAloudBtn.IsEnabled = !detailedResult.StartsWith("⚠");
        CopyBtn.IsEnabled = true;

        // Auto-read the detailed result
        if (!detailedResult.StartsWith("⚠") && _speechEngine != null && _langDetector != null)
        {
            var culture = _langDetector.DetectLanguage(detailedResult);
            _speechEngine.Speak(detailedResult, culture);
        }
    }

    private void OnReadAloud(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_resultText) || _speechEngine == null) return;

        var culture = _langDetector?.DetectLanguage(_resultText) ?? CultureInfo.CurrentUICulture;
        _speechEngine.Speak(_resultText, culture);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_resultText))
        {
            Clipboard.SetText(_resultText);
            StatusText.Text = "Copied to clipboard!";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _speechEngine?.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _speechEngine?.Cancel();
        base.OnClosed(e);
    }
}
