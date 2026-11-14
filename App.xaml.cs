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
    private UpdateService? _updateService;
    private DispatcherTimer? _updateTimer;
    private System.Windows.Controls.MenuItem? _restartToUpdateItem;

    // Held for the app's lifetime: a local would be garbage-collected and release the lock.
    private static System.Threading.Mutex? _singleInstanceMutex;
    private ControlBarWindow? _controlBar;
    private bool _toldAboutTray;

    // Pause / back 10 s / forward 10 s, floating on top of the box being read
    private PlaybackStripWindow? _playbackStrip;
    private bool _playbackStripShown;
    // Texts shorter than this (about 5 seconds of speech) don't get the controls,
    // unless the reading goes on to more paragraphs.
    private const int MinCharsForPlaybackControls = 80;

    private bool _isReading;
    private string _currentReadingText = string.Empty;

    // Finds the on-screen position of each word of the element being read
    private WordLocator? _wordLocator;

    // Continuous reading queue
    private List<TextExtractor.TextElement> _readingQueue = new();
    private int _readingIndex;
    // Language chosen for the current reading; short fragments keep it instead of
    // being re-detected (a link or a Latin word used to switch voices mid-reading).
    private System.Globalization.CultureInfo? _readingCulture;

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
            if (!args.Handled) _speechEngine?.Ducker.RestoreNow();
        };

        // 2. Non-UI thread exceptions (ThreadPool, manual threads)
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                CrashLogger.LogCrash(ex, $"Background Thread (IsTerminating={args.IsTerminating})");
            // Don't leave other apps' audio lowered if we're going down.
            if (args.IsTerminating) _speechEngine?.Ducker.RestoreNow();
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
        _singleInstanceMutex = new System.Threading.Mutex(true, "MagniReader_SingleInstance", out bool isNew);
        if (!isNew)
        {
            // Windows auto-start finding the app already running: nothing to say.
            if (e.Args.Contains(StartupManager.AutostartArg))
            {
                Shutdown();
                return;
            }
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

        // If a previous run crashed while music was lowered, turn it back up.
        _speechEngine.Ducker.RecoverFromCrash();

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
        _inputManager.PickModeChanged += armed => _controlBar?.SetPickArmed(armed);
        _inputManager.IsOwnUiAt = (x, y) =>
            _controlBar?.ContainsScreenPoint(x, y) == true ||
            _playbackStrip?.ContainsScreenPoint(x, y) == true;
        _speechEngine.WordReached += OnWordReached;
        _speechEngine.SpeechCompleted += OnSpeechCompleted;
        SetupPlaybackStrip();

        // Initialize vision analyzer
        _visionAnalyzer = new VisionAnalyzer();

        // Install global hooks (always active)
        _inputManager.Install();

        // Setup tray icon
        SetupTrayIcon();

        // Compact control bar (like the Magnifier toolbar)
        SetupControlBar();

        // Keep the Windows startup entry in line with the setting
        StartupManager.Sync(_settingsStore.Settings.LaunchOnStartup);

        // Auto-updates from GitHub Releases (installed app only)
        _updateService = new UpdateService();
        _updateService.UpdateReady += version => Dispatcher.BeginInvoke(() => OnUpdateReady(version));
        StartUpdateChecks();

        // Show voice diagnostic on startup (skip the balloon when Windows auto-starts us)
        if (!e.Args.Contains(StartupManager.AutostartArg))
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    private void SetupTrayIcon()
    {
        // Load tray icon from embedded resource
        var iconPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "app.ico");
        Icon? trayIcon = null;
        if (System.IO.File.Exists(iconPath))
        {
            // Ask for the small-icon size (16 px at 100 %, larger at higher DPI) so Windows
            // uses the hand-tuned small frame of app.ico instead of shrinking the 32 px one.
            int w = GetSystemMetrics(SM_CXSMICON);
            int h = GetSystemMetrics(SM_CYSMICON);
            trayIcon = w > 0 && h > 0 ? new Icon(iconPath, w, h) : new Icon(iconPath);
        }

        _trayIcon = new TaskbarIcon
        {
            Icon = trayIcon,
            ToolTipText = "MagniReader — Ctrl+Alt+Click to read",
        };

        // Create context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();

        // Shown once an update has been downloaded
        _restartToUpdateItem = new System.Windows.Controls.MenuItem
        {
            Header = "⬆ Restart to update",
            FontWeight = FontWeights.Bold,
            Visibility = Visibility.Collapsed,
        };
        _restartToUpdateItem.Click += (s, e) => RestartToUpdate();
        contextMenu.Items.Add(_restartToUpdateItem);

        var barItem = new System.Windows.Controls.MenuItem { Header = "▭ Show control bar" };
        barItem.Click += (s, e) => ShowControlBar();
        contextMenu.Items.Add(barItem);

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "⚙ Settings" };
        settingsItem.Click += (s, e) => OpenSettings();
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var updatesItem = new System.Windows.Controls.MenuItem { Header = "🔄 Check for updates" };
        updatesItem.Click += async (s, e) => await CheckForUpdatesNowAsync();
        contextMenu.Items.Add(updatesItem);

        var aboutItem = new System.Windows.Controls.MenuItem { Header = "ℹ About MagniReader" };
        aboutItem.Click += (s, e) => MessageBox.Show(
            $"MagniReader — {SettingsWindow.DescribeVersion(_updateService)}\n\n" +
            "An intelligent screen reader that reads text aloud\n" +
            "with automatic language detection.\n\n" +
            "How to use:\n" +
            $"• {(_settingsStore?.Settings.ReadHotkey ?? "Ctrl+Alt").Replace("+", " + ")} + Click — Read text at cursor\n" +
            $"• {(_settingsStore?.Settings.SnipHotkey ?? "Ctrl+Alt+S").Replace("+", " + ")} — Describe a screen area\n" +
            "• Controls above the box — Pause, back/forward 10 s\n" +
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
        _trayIcon.TrayMouseDoubleClick += (s, e) => ShowControlBar();
    }

    // ─── Playback controls over the reading box ─────────────────

    private void SetupPlaybackStrip()
    {
        _playbackStrip = new PlaybackStripWindow();
        _playbackStrip.PlayPauseRequested += () => { if (_isReading) _speechEngine?.TogglePause(); };
        _playbackStrip.SkipRequested += seconds => { if (_isReading) _speechEngine?.SkipSeconds(seconds); };
        _playbackStrip.StopRequested += () =>
        {
            _inputManager?.ArmPickMode(false);
            StopReading();
        };
        _speechEngine!.PauseChanged += paused =>
        {
            _playbackStrip?.SetPaused(paused);
            if (_trayIcon != null && _isReading && paused)
                _trayIcon.ToolTipText = "MagniReader — Paused";
        };
        _overlay!.HighlightBoxChanged += box =>
        {
            if (!_isReading || _playbackStrip == null) return;
            if (!_playbackStripShown &&
                _currentReadingText.Length < MinCharsForPlaybackControls && _readingQueue.Count <= 1)
                return;
            _playbackStripShown = true;
            // Owned by the overlay, so Windows always keeps it above the dimmed screen
            // (both are topmost, and the overlay is activated when a reading starts,
            // which used to put the dim layer over the strip and swallow its clicks).
            if (_playbackStrip.Owner == null && _overlay!.IsLoaded)
            {
                try { _playbackStrip.Owner = _overlay; }
                catch (InvalidOperationException) { /* overlay not shown yet; z-order fallback below */ }
            }
            _playbackStrip.SetPaused(_speechEngine?.IsPaused == true);
            _playbackStrip.ShowAt(box);
        };
    }

    private void HidePlaybackStrip()
    {
        _playbackStripShown = false;
        _playbackStrip?.Hide();
    }

    // ─── Control Bar ────────────────────────────────────────────

    private void SetupControlBar()
    {
        var s = _settingsStore!.Settings;
        _controlBar = new ControlBarWindow(s.ControlBarLeft, s.ControlBarTop);
        RefreshControlBar();

        _controlBar.ReadRequested += () =>
            _inputManager!.ArmPickMode(!_inputManager.IsPickArmed);
        _controlBar.StopRequested += () =>
        {
            _inputManager?.ArmPickMode(false);
            StopReading();
        };
        _controlBar.RateChangeRequested += delta =>
        {
            var settings = _settingsStore!.Settings;
            settings.SpeechRate = Math.Clamp(settings.SpeechRate + delta, -10, 10);
            _speechEngine?.SetRate(settings.SpeechRate);
            _settingsStore.Save();
            RefreshControlBar();
        };
        _controlBar.EngineToggleRequested += ToggleSpeechEngine;
        _controlBar.SnipRequested += OnSnipTriggered;
        _controlBar.SettingsRequested += OpenSettings;
        _controlBar.MinimizeRequested += HideControlBar;
        _controlBar.ExitRequested += ExitApplication;
        _controlBar.Moved += (left, top) =>
        {
            _settingsStore!.Settings.ControlBarLeft = left;
            _settingsStore.Settings.ControlBarTop = top;
            _settingsStore.Save();
        };

        if (s.ShowControlBar)
            _controlBar.Show();
    }

    private void RefreshControlBar()
    {
        if (_controlBar == null || _settingsStore == null) return;
        var s = _settingsStore.Settings;
        _controlBar.SetRate(s.SpeechRate);
        _controlBar.SetEngine(string.Equals(s.SpeechEngine, "Kokoro", StringComparison.OrdinalIgnoreCase));
        _controlBar.SetReading(_isReading);
    }

    private void ShowControlBar()
    {
        if (_controlBar == null) return;
        _controlBar.Show();
        _settingsStore!.Settings.ShowControlBar = true;
        _settingsStore.Save();
    }

    private void HideControlBar()
    {
        if (_controlBar == null) return;
        _inputManager?.ArmPickMode(false);
        _controlBar.Hide();
        _settingsStore!.Settings.ShowControlBar = false;
        _settingsStore.Save();

        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _trayIcon?.ShowBalloonTip(
                "MagniReader is still running",
                "Ctrl+Alt+Click still reads text.\nDouble-click the tray icon to bring the bar back.",
                BalloonIcon.Info);
        }
    }

    private void ToggleSpeechEngine()
    {
        var s = _settingsStore!.Settings;
        bool toKokoro = !string.Equals(s.SpeechEngine, "Kokoro", StringComparison.OrdinalIgnoreCase);

        if (toKokoro && !KokoroSpeaker.IsModelDownloaded)
        {
            // The model has to be downloaded first; that lives in Settings.
            _trayIcon?.ShowBalloonTip(
                "Kokoro voices not downloaded",
                "Pick Kokoro in Settings → Speech and download its model first.",
                BalloonIcon.Info);
            OpenSettings();
            return;
        }

        s.SpeechEngine = toKokoro ? "Kokoro" : "Windows";
        _speechEngine?.ConfigureKokoro(s);
        _settingsStore.Save();
        RefreshControlBar();
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

        // Don't highlight the control bar's own buttons while pick mode is armed.
        if (_controlBar?.ContainsScreenPoint(x, y) == true)
        {
            _lastHoverRect = Rect.Empty;
            _hoverHighlight?.HideHighlight();
            return;
        }

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
            _playbackStripShown = false;
            _readingQueue = new List<TextExtractor.TextElement> { firstElement };
            _readingCulture = null;
            _readingIndex = 0;

            _isReading = true;
            _inputManager!.SetReadingState(true);
            _controlBar?.SetReading(true);

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
        _controlBar?.SetReading(false);

        // Cancel speech FIRST (this may trigger SpeechCompleted callback,
        // but _isReading is already false so it'll be a no-op)
        _speechEngine?.Cancel();
        _overlay?.HideOverlay();
        HidePlaybackStrip();

        _wordLocator?.Dispose();
        _wordLocator = null;

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
        if (!_isReading || _wordLocator == null) return;
        _overlay?.HighlightWord(_wordLocator.GetWordRect(charPosition));
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

        var culture = ChooseReadingLanguage(_currentReadingText);
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

        // Start locating each word on screen. This must happen before the overlay
        // is drawn over the element (the OCR fallback takes a screenshot now).
        _wordLocator?.Dispose();
        _wordLocator = WordLocator.Start(_currentReadingText, element.BoundingRect, element.Source, culture, element.Range);

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

    /// <summary>
    /// Detects the language once per reading and keeps it: later elements only switch
    /// language when they hold enough text (80+ letters) to be sure.
    /// </summary>
    private System.Globalization.CultureInfo ChooseReadingLanguage(string text)
    {
        int letters = text.Count(char.IsLetter);
        if (_readingCulture != null && letters < 80)
            return _readingCulture;

        var detected = _langDetector!.DetectLanguage(text);
        if (_readingCulture == null || detected.TwoLetterISOLanguageName != _readingCulture.TwoLetterISOLanguageName)
            CrashLogger.LogInfo($"[App] Reading language: {detected.Name} ({letters} letters)");
        _readingCulture = detected;
        return detected;
    }

    private void OpenSettings()
    {
        var settingsWindow = new SettingsWindow(_settingsStore!, _voiceManager!, ApplySettings, _updateService!);
        settingsWindow.RestartRequested += () => Dispatcher.BeginInvoke(() => RestartToUpdate());
        settingsWindow.ShowDialog();
    }

    private void ApplySettings(AppSettings settings)
    {
        _speechEngine?.SetRate(settings.SpeechRate);
        _speechEngine?.SetVolume(settings.Volume);
        _speechEngine?.ConfigureKokoro(settings);
        _speechEngine?.ConfigureDucking(settings);
        _overlay?.UpdateSettings(settings.DimOpacity, settings.HighlightColor);
        RefreshControlBar();
        if (_updateService != null) StartUpdateChecks();
    }

    // ─── Auto-update ─────────────────────────────────────────

    private void StartUpdateChecks()
    {
        _updateTimer?.Stop();
        _updateTimer = null;
        if (_updateService == null || !_updateService.IsInstalled) return;
        if (_settingsStore?.Settings.AutoCheckUpdates != true) return;

        // First check shortly after startup (don't slow the launch), then every 6 hours.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += async (s, e) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(6);
            await _updateService.CheckAndDownloadAsync();
        };
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesNowAsync()
    {
        if (_updateService == null) return;
        var result = await _updateService.CheckAndDownloadAsync();
        switch (result)
        {
            case UpdateService.CheckResult.NotInstalled:
                _trayIcon?.ShowBalloonTip("MagniReader",
                    "Updates only work in the installed version (run MagniReader-win-Setup.exe).",
                    BalloonIcon.Info);
                break;
            case UpdateService.CheckResult.UpToDate:
                _trayIcon?.ShowBalloonTip("MagniReader",
                    $"You have the latest version ({_updateService.CurrentVersion}).", BalloonIcon.Info);
                break;
            case UpdateService.CheckResult.Failed:
                _trayIcon?.ShowBalloonTip("MagniReader",
                    "Couldn't check for updates. Are you online?", BalloonIcon.Warning);
                break;
            // UpdateReady shows its own balloon via OnUpdateReady
        }
    }

    private void OnUpdateReady(string version)
    {
        if (_restartToUpdateItem != null)
        {
            _restartToUpdateItem.Header = $"⬆ Restart to update (v{version})";
            _restartToUpdateItem.Visibility = Visibility.Visible;
        }
        _trayIcon?.ShowBalloonTip(
            "MagniReader update ready",
            $"Version {version} has been downloaded. It installs next time MagniReader starts, " +
            "or right-click the tray icon → Restart to update.",
            BalloonIcon.Info);
    }

    private void RestartToUpdate()
    {
        if (_updateService?.PendingVersion == null) return;
        // Release hooks, audio ducking and the tray icon before the process is replaced.
        StopReading();
        _speechEngine?.Ducker.RestoreNow();
        _trayIcon?.Dispose();
        _inputManager?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        try
        {
            _updateService.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("[Update] Restart to update failed", ex);
            ExitApplication();
        }
    }

    private void ExitApplication()
    {
        StopReading();
        _updateService?.ApplyOnExitIfPending();
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
        // The user is typing a new shortcut in Settings, not asking for a snip.
        if (SettingsWindow.IsRecordingShortcut) return;

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
