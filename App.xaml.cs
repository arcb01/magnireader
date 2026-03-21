using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using MagniReader.Core;
using MagniReader.Input;
using MagniReader.UI;
using MagniReader.Infrastructure;
using MagniReader.Helpers;

namespace MagniReader;

/// <summary>
/// Application entry point and main orchestrator.
/// Coordinates text extraction → language detection → voice selection → speech → overlay.
/// 
/// Interaction model:
///   - Ctrl+Alt + Left Click on text → reads aloud
///   - Plain Left Click while reading → stops reading
///   - Escape while reading → stops reading
/// </summary>
public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private OverlayWindow? _overlay;
    private InputManager? _inputManager;
    private TextExtractor? _textExtractor;
    private LanguageDetector? _langDetector;
    private VoiceManager? _voiceManager;
    private SpeechEngine? _speechEngine;
    private SettingsStore? _settingsStore;
    private VisionAnalyzer? _visionAnalyzer;
    private HoverHighlightWindow? _hoverHighlight;

    private bool _isReading;
    private string _currentReadingText = string.Empty;

    // Continuous reading queue
    private List<TextExtractor.TextElement> _readingQueue = new();
    private int _readingIndex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ─── Global exception handlers ──────────────────────────────
        // These catch ALL unhandled exceptions from any thread/context.
        // Without these, crashes vanish silently — no logs, no diagnostics.

        // 1. UI thread exceptions (WPF Dispatcher)
        DispatcherUnhandledException += (s, args) =>
        {
            CrashLogger.LogCrash(args.Exception, "UI Thread (Dispatcher)");
            args.Handled = TryShowCrashNotification(args.Exception);
        };

        // 2. Non-UI thread exceptions (ThreadPool, manual threads)
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                CrashLogger.LogCrash(ex, $"Background Thread (IsTerminating={args.IsTerminating})");
        };

        // 3. Unobserved async Task exceptions
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            CrashLogger.LogCrash(args.Exception, "Unobserved Task");
            args.SetObserved(); // Prevent app from crashing on fire-and-forget tasks
        };

        CrashLogger.LogStartup();

        // Handle --enable-voices flag (used when relaunched as admin)
        if (e.Args.Contains("--enable-voices"))
        {
            var (success, message) = VoiceEnabler.EnableOneCoreVoices();
            MessageBox.Show(
                success ? $"✅ {message}" : $"❌ {message}",
                "Voice Setup",
                MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // Single instance check
        var mutex = new System.Threading.Mutex(true, "MagniReader_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("MagniReader is already running!", "MagniReader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Initialize components
        _settingsStore = new SettingsStore();
        _textExtractor = new TextExtractor();
        _langDetector = new LanguageDetector();
        _voiceManager = new VoiceManager();
        _speechEngine = new SpeechEngine(_voiceManager);
        _inputManager = new InputManager();
        _overlay = new OverlayWindow();
        _hoverHighlight = new HoverHighlightWindow();

        // Apply saved settings
        ApplySettings(_settingsStore.Settings);

        // Load voice priorities
        _voiceManager.LoadPriorities(_settingsStore.Settings);

        // Pre-warm the speech engine in the background
        _ = _speechEngine.WarmUpAsync();

        // Wire up events
        _inputManager.ReadTriggered += OnReadTriggered;
        _inputManager.CancelTriggered += OnCancelTriggered;
        _inputManager.SnipTriggered += OnSnipTriggered;
        _inputManager.HoverPreview += OnHoverPreview;
        _inputManager.HoverEnd += OnHoverEnd;
        _speechEngine.WordReached += OnWordReached;
        _speechEngine.SpeechCompleted += OnSpeechCompleted;
        _speechEngine.PlaybackStarted += (durationMs) =>
        {
            _overlay?.StartProgress(durationMs);
        };

        // Initialize vision analyzer
        _visionAnalyzer = new VisionAnalyzer();

        // Install global hooks (always active)
        _inputManager.Install();

        // Setup tray icon
        SetupTrayIcon();

        // Show voice diagnostic on startup
        ShowVoiceDiagnostic();

        // Show the overlay window (hidden) to initialize it
        _overlay.Show();
        _overlay.Visibility = Visibility.Collapsed;

        // Initialize hover highlight (hidden)
        _hoverHighlight.Show();
        _hoverHighlight.Visibility = Visibility.Collapsed;

        CrashLogger.LogInfo("Startup complete");
    }

    private void ShowVoiceDiagnostic()
    {
        var report = _voiceManager!.GetVoiceReport();
        System.Diagnostics.Debug.WriteLine(report);

        if (!_voiceManager.HasNaturalVoices())
        {
            _trayIcon?.ShowBalloonTip(
                "MagniReader — Voice Setup",
                "No natural voices detected. For better quality:\n" +
                "Settings → Accessibility → Narrator → Add Natural voices\n" +
                "Or: Settings → Time & Language → Speech",
                BalloonIcon.Info);
        }
        else
        {
            _trayIcon?.ShowBalloonTip(
                "MagniReader Ready!",
                "Ctrl+Alt+Click on any text to read it aloud.\n" +
                "Natural voices detected ⭐",
                BalloonIcon.Info);
        }
    }

    private void SetupTrayIcon()
    {
        // Load tray icon from embedded resource
        var iconPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "app.ico");
        Icon? trayIcon = null;
        if (System.IO.File.Exists(iconPath))
        {
            trayIcon = new Icon(iconPath);
        }

        _trayIcon = new TaskbarIcon
        {
            Icon = trayIcon,
            ToolTipText = "MagniReader — Ctrl+Alt+Click to read",
        };

        // Create context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "⚙ Settings" };
        settingsItem.Click += (s, e) => OpenSettings();
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var aboutItem = new System.Windows.Controls.MenuItem { Header = "ℹ About MagniReader" };
        aboutItem.Click += (s, e) => MessageBox.Show(
            "MagniReader v1.0\n\n" +
            "An intelligent screen reader that reads text aloud\n" +
            "with automatic language detection.\n\n" +
            "How to use:\n" +
            "• Ctrl+Alt+Click — Read text at cursor\n" +
            "• Left Click or Esc — Stop reading\n\n" +
            "Built with ❤ using Windows UI Automation.",
            "About MagniReader",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        contextMenu.Items.Add(aboutItem);

        var logsItem = new System.Windows.Controls.MenuItem { Header = "📋 View Crash Logs" };
        logsItem.Click += (s, e) =>
        {
            try
            {
                var logDir = CrashLogger.LogDirectory;
                if (System.IO.Directory.Exists(logDir))
                    Process.Start("explorer.exe", logDir);
                else
                    MessageBox.Show("No log directory found yet.", "MagniReader");
            }
            catch { }
        };
        contextMenu.Items.Add(logsItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "✕ Exit" };
        exitItem.Click += (s, e) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.TrayMouseDoubleClick += (s, e) => OpenSettings();
    }

    /// <summary>
    /// Called when Ctrl+Alt+Click is detected. Receives click coordinates.
    /// </summary>
    private void OnReadTriggered(int clickX, int clickY)
    {
        if (_isReading)
        {
            // If already reading, stop first, then start new reading
            StopReading();
        }

        // Hide hover highlight when starting to read
        _hoverHighlight?.HideHighlight();
        StartReading(clickX, clickY);
    }

    // ─── Hover Preview ──────────────────────────────────────────

    private Rect _lastHoverRect = Rect.Empty;

    private void OnHoverPreview(int x, int y)
    {
        if (_isReading) return;

        try
        {
            var point = new System.Windows.Point(x, y);
            var element = System.Windows.Automation.AutomationElement.FromPoint(point);
            if (element == null)
            {
                _hoverHighlight?.HideHighlight();
                return;
            }

            var rect = element.Current.BoundingRectangle;
            if (rect == Rect.Empty || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            {
                _hoverHighlight?.HideHighlight();
                return;
            }

            // Skip if we're still on the same element (avoid flicker)
            if (rect == _lastHoverRect) return;
            _lastHoverRect = rect;

            // Try to get text from this element
            string text = "";
            try
            {
                text = element.Current.Name ?? "";
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Try ValuePattern
                    if (element.TryGetCurrentPattern(
                        System.Windows.Automation.ValuePattern.Pattern, out var pattern))
                    {
                        text = ((System.Windows.Automation.ValuePattern)pattern).Current.Value ?? "";
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(text))
            {
                _hoverHighlight?.HideHighlight();
                _lastHoverRect = Rect.Empty;
                return;
            }

            // Show highlight
            _hoverHighlight?.HighlightElement(rect, text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Hover error: {ex.Message}");
            _hoverHighlight?.HideHighlight();
        }
    }

    private void OnHoverEnd()
    {
        _lastHoverRect = Rect.Empty;
        _hoverHighlight?.HideHighlight();
    }

    private void StartReading(int clickX, int clickY)
    {
        var pipelineSw = Stopwatch.StartNew();
        try
        {
            // FAST PATH: Extract only the first element (skips sibling traversal)
            var (firstElement, extractionContext) = _textExtractor!.ExtractFirstElementAtPoint(clickX, clickY);
            System.Diagnostics.Debug.WriteLine(
                $"[App] ⏱ Fast text extraction: {pipelineSw.ElapsedMilliseconds}ms");

            if (firstElement == null || string.IsNullOrWhiteSpace(firstElement.Text))
            {
                _trayIcon?.ShowBalloonTip(
                    "MagniReader",
                    "No readable text found at clicked position.",
                    BalloonIcon.Warning);
                return;
            }

            // Start with just the first element — speak immediately
            _readingQueue = new List<TextExtractor.TextElement> { firstElement };
            _readingIndex = 0;

            _isReading = true;
            _inputManager!.SetReadingState(true);

            System.Diagnostics.Debug.WriteLine(
                $"[App] Starting reading first element immediately (siblings deferred)");

            // Start reading the first element NOW — before collecting siblings
            ReadCurrentElement(showOverlay: true, pipelineSw);

            // Collect siblings in the background while speech is already playing
            if (extractionContext != null)
            {
                _ = Task.Run(() =>
                {
                    var siblingsSw = Stopwatch.StartNew();
                    var siblings = _textExtractor.CollectSiblingsFromContext(extractionContext);
                    System.Diagnostics.Debug.WriteLine(
                        $"[App] ⏱ Deferred sibling collection: {siblingsSw.ElapsedMilliseconds}ms, found {siblings.Count} sibling(s)");

                    if (siblings.Count > 0)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (_isReading)
                            {
                                _readingQueue.AddRange(siblings.Where(e => !string.IsNullOrWhiteSpace(e.Text)));
                                System.Diagnostics.Debug.WriteLine(
                                    $"[App] Reading queue updated: {_readingQueue.Count} total element(s)");
                            }
                        });
                    }
                });
            }
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"Reading failed at ({clickX},{clickY})", ex);
            StopReading();
        }
    }

    private void StopReading()
    {
        if (!_isReading) return; // Already stopped — no double-processing

        System.Diagnostics.Debug.WriteLine("[App] StopReading");
        _isReading = false;
        _inputManager?.SetReadingState(false);

        // Cancel speech FIRST (this may trigger SpeechCompleted callback,
        // but _isReading is already false so it'll be a no-op)
        _speechEngine?.Cancel();
        _overlay?.HideOverlay();

        if (_trayIcon != null)
            _trayIcon.ToolTipText = "MagniReader — Ctrl+Alt+Click to read";
    }

    private void OnCancelTriggered()
    {
        System.Diagnostics.Debug.WriteLine("[App] Cancel triggered");
        StopReading();
    }

    private void OnWordReached(int charPosition, int charLength)
    {
        _overlay?.HighlightWord(charPosition, charLength);
    }

    private void OnSpeechCompleted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isReading) return;

            // Advance to next element in queue
            _readingIndex++;

            if (_readingIndex < _readingQueue.Count)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[App] Advancing to element {_readingIndex + 1}/{_readingQueue.Count}");
                ReadCurrentElement(showOverlay: false, null);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[App] Finished all {_readingQueue.Count} element(s)");
                StopReading();
            }
        });
    }

    /// <summary>
    /// Reads the current element in the reading queue.
    /// Speech is fired FIRST for minimal latency; overlay updates follow.
    /// </summary>
    private void ReadCurrentElement(bool showOverlay, Stopwatch? pipelineSw)
    {
        if (_readingIndex >= _readingQueue.Count) return;

        var element = _readingQueue[_readingIndex];
        _currentReadingText = element.Text;

        var culture = _langDetector!.DetectLanguage(_currentReadingText);
        if (pipelineSw != null)
            System.Diagnostics.Debug.WriteLine(
                $"[App] ⏱ Lang detection: {pipelineSw.ElapsedMilliseconds}ms");

        // SPEECH FIRST — this is the primary feedback the user cares about.
        // Overlay will catch up via Dispatcher below.
        _speechEngine!.Speak(_currentReadingText, culture);
        if (pipelineSw != null)
            System.Diagnostics.Debug.WriteLine(
                $"[App] ⏱ Speak() dispatched: {pipelineSw.ElapsedMilliseconds}ms");

        // Overlay and tray update (non-blocking)
        var rect = element.BoundingRect;
        if (rect == Rect.Empty || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            rect = new Rect(
                SystemParameters.VirtualScreenWidth / 4,
                SystemParameters.VirtualScreenHeight / 4,
                SystemParameters.VirtualScreenWidth / 2,
                SystemParameters.VirtualScreenHeight / 2);
        }

        if (showOverlay)
        {
            _overlay!.ShowOverlay(rect, _currentReadingText, culture.EnglishName);
        }
        else
        {
            _overlay!.MoveToElement(rect, _currentReadingText);
        }

        _trayIcon!.ToolTipText = $"MagniReader — Reading ({culture.EnglishName}) [{_readingIndex + 1}/{_readingQueue.Count}]...";

        if (pipelineSw != null)
            System.Diagnostics.Debug.WriteLine(
                $"[App] ⏱ Full pipeline complete: {pipelineSw.ElapsedMilliseconds}ms");
    }

    private void OpenSettings()
    {
        var settingsWindow = new SettingsWindow(_settingsStore!, _voiceManager!, ApplySettings);
        settingsWindow.ShowDialog();
    }

    private void ApplySettings(AppSettings settings)
    {
        _speechEngine?.SetRate(settings.SpeechRate);
        _speechEngine?.SetVolume(settings.Volume);
        _overlay?.UpdateSettings(settings.DimOpacity, settings.HighlightColor);
    }

    private void ExitApplication()
    {
        StopReading();
        _trayIcon?.Dispose();
        _inputManager?.Dispose();
        _speechEngine?.Dispose();
        _voiceManager?.Dispose();
        _visionAnalyzer?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CrashLogger.LogInfo("Application shutting down");
        _trayIcon?.Dispose();
        _inputManager?.Dispose();
        _speechEngine?.Dispose();
        _voiceManager?.Dispose();
        _visionAnalyzer?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Tries to show a tray notification about the crash. Returns true if the app should continue.
    /// </summary>
    private bool TryShowCrashNotification(Exception ex)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(
                "MagniReader Crashed",
                $"{ex.GetType().Name}: {ex.Message}\n" +
                $"Log: {CrashLogger.CurrentLogFile}",
                BalloonIcon.Error);

            // Try to keep the app alive for non-fatal errors
            // (COM exceptions, UI Automation timeouts, etc.)
            if (ex is System.Runtime.InteropServices.COMException ||
                ex is System.Runtime.InteropServices.InvalidComObjectException ||
                ex is TimeoutException ||
                ex is System.ComponentModel.Win32Exception)
            {
                CrashLogger.LogWarning($"Non-fatal exception handled, app continues: {ex.GetType().Name}");
                return true; // Handled — don't crash
            }
        }
        catch { }

        return false; // Let the default crash behavior proceed
    }

    // ─── Vision / Snip Analysis ──────────────────────────────

    private bool _isSnipping;

    private async void OnSnipTriggered()
    {
        if (_isReading || _isSnipping) return;

        try
        {
            _isSnipping = true;
            System.Diagnostics.Debug.WriteLine("[App] Snip triggered");

            // Show snip window
            var snipWindow = new SnipWindow();
            var imageBytes = await snipWindow.CaptureAreaAsync();

            if (imageBytes == null || imageBytes.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine("[App] Snip cancelled");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[App] Captured {imageBytes.Length} bytes");

            // Parse provider
            var settings = _settingsStore!.Settings;
            var provider = settings.VisionProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                ? VisionAnalyzer.Provider.OpenAI
                : VisionAnalyzer.Provider.Gemini;

            // Show result window with loading state
            var resultWindow = new VisionResultWindow(_speechEngine, _langDetector);
            resultWindow.SetImageContext(imageBytes, provider, settings.VisionApiKey, _visionAnalyzer!);
            resultWindow.Show();
            resultWindow.ShowLoading();

            // Analyze (brief summary first)
            var result = await _visionAnalyzer!.AnalyzeImageAsync(
                imageBytes, provider, settings.VisionApiKey, detailed: false);

            System.Diagnostics.Debug.WriteLine($"[App] VLM result: {result[..Math.Min(100, result.Length)]}...");

            // Show result
            resultWindow.ShowResult(result);

            // Auto-read the brief result aloud
            if (!result.StartsWith("⚠") && _speechEngine != null && _langDetector != null)
            {
                var culture = _langDetector.DetectLanguage(result);
                _speechEngine.Speak(result, culture);
            }
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("Snip/Vision analysis failed", ex);
        }
        finally
        {
            _isSnipping = false;
        }
    }
}
