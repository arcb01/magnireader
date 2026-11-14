using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagniReader.Core;
using MagniReader.Infrastructure;

namespace MagniReader.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly VoiceManager _voiceManager;
    private readonly Action<AppSettings> _onSettingsSaved;
    private readonly UpdateService _updateService;
    private readonly Dictionary<string, List<string>> _workingPriorities = new();

    private readonly (string color, string text)[] _themes = new[]
    {
        ("#FFD700", "#000000"),
        ("#3B82F6", "#FFFFFF"),
        ("#22C55E", "#000000"),
        ("#FFFFFF", "#000000"),
        ("#FFFF00", "#000000"),
    };

    public SettingsWindow(SettingsStore settingsStore, VoiceManager voiceManager, Action<AppSettings> onSettingsSaved,
        UpdateService updateService)
    {
        _settingsStore = settingsStore;
        _voiceManager = voiceManager;
        _onSettingsSaved = onSettingsSaved;
        _updateService = updateService;
        InitializeComponent();
        SetWindowIcon();
        LoadSettings();
        PopulateVoiceGroups();
        Closed += (_, _) => _downloadCts?.Cancel();
    }

    private void SetWindowIcon()
    {
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            Icon = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
        }
    }

    private void LoadSettings()
    {
        var s = _settingsStore.Settings;
        RateSlider.Value = s.SpeechRate;
        VolumeSlider.Value = s.Volume;
        DuckCheckBox.IsChecked = s.DuckOtherAudio;
        // The slider shows how much other apps are lowered by; the setting stores what they keep.
        DuckLevelSlider.Value = 100 - Math.Clamp(s.DuckLevel, 0, 80);
        DuckLevelSlider.IsEnabled = s.DuckOtherAudio;
        OpacitySlider.Value = s.DimOpacity;
        for (int i = 0; i < _themes.Length; i++)
        {
            if (_themes[i].color.Equals(s.HighlightColor, StringComparison.OrdinalIgnoreCase))
            { ThemeCombo.SelectedIndex = i; break; }
        }

        LoadKokoroSettings(s);
        LoadAppSettings(s);

        // Vision settings
        VisionProviderCombo.SelectedIndex = s.VisionProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (!string.IsNullOrEmpty(s.VisionApiKey))
        {
            ApiKeyBox.Password = s.VisionApiKey;
            ApiKeyTextBox.Text = s.VisionApiKey;
        }
    }

    private string _apiKeyValue = "";
    private bool _apiKeyVisible;

    private void PopulateVoiceGroups()
    {
        VoiceGroupsPanel.Children.Clear();
        _workingPriorities.Clear();

        var grouped = _voiceManager.GetVoicesGroupedByLanguage();

        foreach (var (langCode, voices) in grouped)
        {
            _workingPriorities[langCode] = voices.Select(v => v.DisplayName).ToList();

            var langName = VoiceManager.GetLanguageDisplayName(langCode);
            var flag = langCode switch
            {
                "en" => "🇬🇧", "es" => "🇪🇸", "fr" => "🇫🇷", "de" => "🇩🇪",
                "it" => "🇮🇹", "pt" => "🇵🇹", "ja" => "🇯🇵", "ko" => "🇰🇷",
                "zh" => "🇨🇳", _ => "🌐"
            };

            // Header
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            header.Children.Add(new TextBlock
            {
                Text = $"{flag} {langName}", FontWeight = FontWeights.Bold, FontSize = 14,
                Foreground = Brush(0xCD, 0xD6, 0xF4), VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = $"  ({voices.Count})", FontSize = 11,
                Foreground = Brush(0x6C, 0x70, 0x86), VerticalAlignment = VerticalAlignment.Center
            });
            VoiceGroupsPanel.Children.Add(header);

            // Voice list
            var border = new Border
            {
                Background = Brush(0x31, 0x32, 0x44), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 4)
            };
            var panel = new StackPanel { Tag = langCode };
            BuildVoiceList(panel, langCode, voices);
            border.Child = panel;
            VoiceGroupsPanel.Children.Add(border);
        }

        if (_voiceManager.HasNaturalVoices())
        {
            VoiceHelpText.Text = "⭐ Natural voices active and will be used first!";
        }
        else if (_voiceManager.UnavailableNaturalVoiceCount > 0)
        {
            VoiceHelpText.Text =
                $"⚠ {_voiceManager.UnavailableNaturalVoiceCount} Natural voice(s) installed but not usable.\n" +
                "Windows restricts Natural voices to system apps (Narrator, Edge).\n" +
                "To unlock them: install NaturalVoiceSAPIAdapter from GitHub,\n" +
                "then restart MagniReader.";
        }
        else
        {
            VoiceHelpText.Text =
                "💡 For better voice quality, install Natural voices:\n" +
                "Settings → Accessibility → Narrator → Add natural voices\n" +
                "Then install NaturalVoiceSAPIAdapter to use them here.";
        }
        EnableOneCoreBtn.Visibility = Visibility.Collapsed;
    }

    private void BuildVoiceList(StackPanel listPanel, string langCode, IList<MagniVoice> voices)
    {
        listPanel.Children.Clear();

        for (int i = 0; i < voices.Count; i++)
        {
            var voice = voices[i];
            int index = i;

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Height = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Priority number
            var numTb = new TextBlock
            {
                Text = $"{i + 1}", FontSize = 12,
                FontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal,
                Foreground = i == 0 ? Brush(0xFF, 0xD7, 0x00) : Brush(0x6C, 0x70, 0x86),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(numTb, 0);
            row.Children.Add(numTb);

            // Voice name + badges
            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (voice.IsNatural)
                namePanel.Children.Add(new TextBlock { Text = "⭐ ", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

            namePanel.Children.Add(new TextBlock
            {
                Text = voice.DisplayName.Replace("Microsoft ", ""),
                FontSize = 13,
                Foreground = voice.IsNatural ? Brush(0xFF, 0xD7, 0x00) : Brush(0xCD, 0xD6, 0xF4),
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (i == 0)
            {
                namePanel.Children.Add(new TextBlock
                {
                    Text = "  PRIMARY", FontSize = 9,
                    Foreground = Brush(0xA6, 0xE3, 0xA1),
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }

            if (voice.WinRTVoice == null)
            {
                namePanel.Children.Add(new TextBlock
                {
                    Text = "  (registry)", FontSize = 9,
                    Foreground = Brush(0x6C, 0x70, 0x86),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }

            Grid.SetColumn(namePanel, 1);
            row.Children.Add(namePanel);

            // Buttons
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (voice.IsPlayable)
            {
                var vName = voice.DisplayName;
                var lc = langCode;
                var playBtn = MakeBtn("▶", "Preview");
                playBtn.Click += (s, e) => _voiceManager.PreviewVoice(vName, lc);
                btnPanel.Children.Add(playBtn);
            }
            else
            {
                var dis = MakeBtn("▶", "Not available — install NaturalVoiceSAPIAdapter");
                dis.IsEnabled = false; dis.Opacity = 0.3;
                btnPanel.Children.Add(dis);
            }

            if (i > 0)
            {
                var upBtn = MakeBtn("▲", "Higher priority");
                upBtn.Click += (s, e) => MoveVoice(langCode, index, -1);
                btnPanel.Children.Add(upBtn);
            }
            else
            {
                var dis = MakeBtn("▲", ""); dis.IsEnabled = false; dis.Opacity = 0.2;
                btnPanel.Children.Add(dis);
            }

            if (i < voices.Count - 1)
            {
                var downBtn = MakeBtn("▼", "Lower priority");
                downBtn.Click += (s, e) => MoveVoice(langCode, index, 1);
                btnPanel.Children.Add(downBtn);
            }
            else
            {
                var dis = MakeBtn("▼", ""); dis.IsEnabled = false; dis.Opacity = 0.2;
                btnPanel.Children.Add(dis);
            }

            Grid.SetColumn(btnPanel, 2);
            row.Children.Add(btnPanel);
            listPanel.Children.Add(row);
        }
    }

    private static Button MakeBtn(string text, string tip) => new()
    {
        Content = text,
        ToolTip = string.IsNullOrEmpty(tip) ? null : tip,
        Width = 28, Height = 28,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Foreground = Brush(0xA6, 0xAD, 0xC8), FontSize = 12,
        Cursor = System.Windows.Input.Cursors.Hand,
        Margin = new Thickness(2, 0, 2, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private void MoveVoice(string langCode, int idx, int dir)
    {
        if (!_workingPriorities.TryGetValue(langCode, out var list)) return;
        int newIdx = idx + dir;
        if (newIdx < 0 || newIdx >= list.Count) return;

        (list[idx], list[newIdx]) = (list[newIdx], list[idx]);

        var allGrouped = _voiceManager.GetVoicesGroupedByLanguage();
        if (!allGrouped.TryGetValue(langCode, out var langVoices)) return;

        var reordered = list
            .Select(name => langVoices.FirstOrDefault(v => v.DisplayName == name))
            .Where(v => v != null)
            .Cast<MagniVoice>()
            .ToList();

        foreach (var child in VoiceGroupsPanel.Children)
        {
            if (child is Border b && b.Child is StackPanel p && p.Tag is string t && t == langCode)
            { BuildVoiceList(p, langCode, reordered); break; }
        }
    }

    private void EnableOneCoreVoices_Click(object sender, RoutedEventArgs e) { }

    private void RateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (RateValueText != null) RateValueText.Text = ((int)e.NewValue).ToString(); }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (VolumeValueText != null) VolumeValueText.Text = $"{(int)e.NewValue}%"; }

    private void DuckCheckBox_Changed(object sender, RoutedEventArgs e)
    { if (DuckLevelSlider != null) DuckLevelSlider.IsEnabled = DuckCheckBox.IsChecked == true; }

    private void DuckLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (DuckLevelValueText != null) DuckLevelValueText.Text = (int)e.NewValue >= 100 ? "100% (muted)" : $"{(int)e.NewValue}%"; }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (OpacityValueText != null) OpacityValueText.Text = $"{(int)(e.NewValue * 100)}%"; }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _settingsStore.Settings;
        s.SpeechRate = (int)RateSlider.Value;
        s.Volume = (int)VolumeSlider.Value;
        s.DuckOtherAudio = DuckCheckBox.IsChecked == true;
        s.DuckLevel = 100 - (int)DuckLevelSlider.Value;
        s.DimOpacity = OpacitySlider.Value;
        int ti = ThemeCombo.SelectedIndex;
        if (ti >= 0 && ti < _themes.Length) { s.HighlightColor = _themes[ti].color; s.HighlightTextColor = _themes[ti].text; }
        s.VoicePriorities = new Dictionary<string, List<string>>(_workingPriorities);

        // Voice engine
        s.SpeechEngine = EngineCombo.SelectedIndex == 1 ? "Kokoro" : "Windows";
        if (KokoroEnglishCombo.SelectedItem is KokoroVoiceInfo en) s.KokoroVoiceEnglish = en.Id;
        if (KokoroSpanishCombo.SelectedItem is KokoroVoiceInfo es) s.KokoroVoiceSpanish = es.Id;

        // Vision settings
        s.VisionProvider = VisionProviderCombo.SelectedIndex == 1 ? "OpenAI" : "Gemini";
        s.VisionApiKey = _apiKeyValue;

        // Startup + updates
        s.LaunchOnStartup = LaunchOnStartupCheckBox.IsChecked == true;
        s.AutoCheckUpdates = AutoUpdateCheckBox.IsChecked == true;
        StartupManager.Set(s.LaunchOnStartup);

        _settingsStore.Save();
        _onSettingsSaved(s);
        _voiceManager.LoadPriorities(s);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ─── Kokoro voice engine ─────────────────────────────────

    private CancellationTokenSource? _downloadCts;

    private void LoadKokoroSettings(AppSettings s)
    {
        var voices = KokoroSpeaker.GetVoices();
        var english = voices.Where(v => v.LangCode == "en").ToList();
        var spanish = voices.Where(v => v.LangCode == "es").ToList();
        KokoroEnglishCombo.ItemsSource = english;
        KokoroSpanishCombo.ItemsSource = spanish;
        KokoroEnglishCombo.SelectedItem = english.FirstOrDefault(v => v.Id == s.KokoroVoiceEnglish) ?? english.FirstOrDefault();
        KokoroSpanishCombo.SelectedItem = spanish.FirstOrDefault(v => v.Id == s.KokoroVoiceSpanish) ?? spanish.FirstOrDefault();

        EngineCombo.SelectedIndex = string.Equals(s.SpeechEngine, "Kokoro", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        UpdateKokoroPanel();
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateKokoroPanel();

    private void UpdateKokoroPanel()
    {
        if (KokoroPanel == null || EngineCombo == null) return;
        KokoroPanel.Visibility = EngineCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

        if (_downloadCts != null) return; // download in progress owns the status line

        if (KokoroEnglishCombo.Items.Count == 0 && KokoroSpanishCombo.Items.Count == 0)
        {
            KokoroStatusText.Text = $"⚠ Kokoro voices not found in {KokoroSpeaker.VoicesDirectory}. Rebuild MagniReader.";
            KokoroDownloadBtn.Visibility = Visibility.Collapsed;
        }
        else if (KokoroSpeaker.IsModelDownloaded)
        {
            KokoroStatusText.Text = KokoroSpeaker.Shared.IsLoaded
                ? "✓ Kokoro model installed and loaded."
                : "✓ Kokoro model installed. It loads in the background when Kokoro is selected.";
            KokoroDownloadBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            KokoroStatusText.Text =
                $"The Kokoro model is not installed yet. It is a one-time download of about {KokoroSpeaker.ApproxModelSizeMb} MB, " +
                "saved in %APPDATA%\\MagniReader\\kokoro. Until then Windows voices are used.";
            KokoroDownloadBtn.Content = $"⬇ Download Kokoro model (~{KokoroSpeaker.ApproxModelSizeMb} MB)";
            KokoroDownloadBtn.Visibility = Visibility.Visible;
        }
    }

    private async void KokoroDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null)
        {
            _downloadCts.Cancel(); // button acts as "Cancel" while downloading
            return;
        }

        var cts = _downloadCts = new CancellationTokenSource();
        KokoroDownloadBtn.Content = "✖ Cancel download";
        KokoroProgress.Visibility = Visibility.Visible;
        KokoroProgress.Value = 0;
        KokoroStatusText.Text = "Downloading the Kokoro model…";

        var progress = new Progress<double>(p =>
        {
            KokoroProgress.Value = p;
            KokoroStatusText.Text = $"Downloading the Kokoro model… {p * 100:F0}%";
        });

        try
        {
            await KokoroSpeaker.DownloadModelAsync(progress, cts.Token);
            KokoroStatusText.Text = "Loading the Kokoro model…";
            bool ok = await KokoroSpeaker.Shared.EnsureLoadedAsync();
            _downloadCts = null;
            UpdateKokoroPanel();
            if (!ok)
                KokoroStatusText.Text = "⚠ The model downloaded but could not be loaded. See the log in %APPDATA%\\MagniReader\\logs.";
        }
        catch (OperationCanceledException)
        {
            _downloadCts = null;
            UpdateKokoroPanel();
            KokoroStatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("Kokoro model download failed", ex);
            _downloadCts = null;
            UpdateKokoroPanel();
            KokoroStatusText.Text = $"⚠ Download failed: {ex.Message}";
        }
        finally
        {
            KokoroProgress.Visibility = Visibility.Collapsed;
            cts.Dispose();
        }
    }

    private void PreviewKokoroEnglish_Click(object sender, RoutedEventArgs e) =>
        PreviewKokoro(KokoroEnglishCombo.SelectedItem as KokoroVoiceInfo);

    private void PreviewKokoroSpanish_Click(object sender, RoutedEventArgs e) =>
        PreviewKokoro(KokoroSpanishCombo.SelectedItem as KokoroVoiceInfo);

    private async void PreviewKokoro(KokoroVoiceInfo? voice)
    {
        if (voice == null) return;
        if (!KokoroSpeaker.IsModelDownloaded)
        {
            KokoroStatusText.Text = "Download the Kokoro model first to preview its voices.";
            return;
        }
        try
        {
            if (!KokoroSpeaker.Shared.IsLoaded) KokoroStatusText.Text = "Loading the Kokoro model…";
            float speed = RateSlider.Value >= 0 ? 1f + (float)RateSlider.Value * 0.1f : 1f + (float)RateSlider.Value * 0.05f;
            var preview = KokoroSpeaker.Shared.PreviewAsync(voice.Id, voice.LangCode, speed);
            await KokoroSpeaker.Shared.EnsureLoadedAsync();
            UpdateKokoroPanel();
            await preview;
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("Kokoro preview failed", ex);
            KokoroStatusText.Text = $"⚠ Preview failed: {ex.Message}";
        }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    // ─── Startup & updates ───────────────────────────────────

    private void LoadAppSettings(AppSettings s)
    {
        // The registry is the truth: the user may have turned it off in Task Manager → Startup apps.
        LaunchOnStartupCheckBox.IsChecked = s.LaunchOnStartup && StartupManager.IsEnabled;
        AutoUpdateCheckBox.IsChecked = s.AutoCheckUpdates;
        VersionText.Text = $"MagniReader {_updateService.CurrentVersion}";

        if (!_updateService.IsInstalled)
        {
            AutoUpdateCheckBox.IsEnabled = false;
            CheckUpdatesBtn.IsEnabled = false;
            UpdateStatusText.Text = "Updates are only available in the installed version (MagniReader-win-Setup.exe from the GitHub releases page).";
        }
        else if (_updateService.PendingVersion is string pending)
        {
            ShowUpdateReady(pending);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesBtn.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";
        var result = await _updateService.CheckAndDownloadAsync();
        CheckUpdatesBtn.IsEnabled = true;
        UpdateStatusText.Text = result switch
        {
            UpdateService.CheckResult.UpToDate => $"✓ You have the latest version ({_updateService.CurrentVersion}).",
            UpdateService.CheckResult.Failed => "⚠ Couldn't reach GitHub. Check your connection and try again.",
            UpdateService.CheckResult.NotInstalled => "Updates are only available in the installed version.",
            _ => UpdateStatusText.Text,
        };
        if (result == UpdateService.CheckResult.UpdateReady && _updateService.PendingVersion is string v)
            ShowUpdateReady(v);
    }

    private void ShowUpdateReady(string version)
    {
        UpdateStatusText.Text = $"⬆ Version {version} is downloaded and ready to install.";
        RestartToUpdateBtn.Content = $"⬆ Restart to update (v{version})";
        RestartToUpdateBtn.Visibility = Visibility.Visible;
    }

    private void RestartToUpdate_Click(object sender, RoutedEventArgs e)
    {
        Close();
        // Same path as the tray menu item, which also releases hooks and the tray icon first.
        RestartRequested?.Invoke();
    }

    /// <summary>Raised when the user clicks "Restart to update".</summary>
    public event Action? RestartRequested;

    // ─── Vision settings handlers ────────────────────────────

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _apiKeyValue = ApiKeyBox.Password;
    }

    private void ApiKeyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _apiKeyValue = ApiKeyTextBox.Text;
    }

    private void ToggleApiKeyVisibility(object sender, RoutedEventArgs e)
    {
        _apiKeyVisible = !_apiKeyVisible;
        if (_apiKeyVisible)
        {
            ApiKeyTextBox.Text = _apiKeyValue;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyTextBox.Visibility = Visibility.Visible;
        }
        else
        {
            ApiKeyBox.Password = _apiKeyValue;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Visibility = Visibility.Visible;
        }
    }

    private async void TestApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_apiKeyValue))
        {
            TestKeyStatus.Text = "⚠ Enter an API key first.";
            TestKeyStatus.Foreground = Brush(0xF3, 0x8B, 0xA8);
            return;
        }

        TestKeyBtn.IsEnabled = false;
        TestKeyStatus.Text = "Testing...";
        TestKeyStatus.Foreground = Brush(0x6C, 0x70, 0x86);

        var provider = VisionProviderCombo.SelectedIndex == 1
            ? VisionAnalyzer.Provider.OpenAI
            : VisionAnalyzer.Provider.Gemini;

        using var analyzer = new VisionAnalyzer();
        var (success, message) = await analyzer.ValidateKeyAsync(provider, _apiKeyValue);

        TestKeyStatus.Text = message;
        TestKeyStatus.Foreground = success
            ? Brush(0xA6, 0xE3, 0xA1) // green
            : Brush(0xF3, 0x8B, 0xA8); // red
        TestKeyBtn.IsEnabled = true;
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }
}
