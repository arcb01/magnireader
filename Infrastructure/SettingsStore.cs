using System.IO;
using System.Text.Json;

namespace MagniReader.Infrastructure;

/// <summary>
/// Persists user settings to a JSON file.
/// </summary>
public class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagniReader",
        "settings.json"
    );

    public AppSettings Settings { get; private set; }

    public SettingsStore()
    {
        Settings = Load();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupted settings file, use defaults
        }

        return new AppSettings();
    }
}

public class AppSettings
{
    /// <summary>Speech rate: -10 to 10. Default: 1 (slightly faster than normal).</summary>
    public int SpeechRate { get; set; } = 1;

    /// <summary>Speech volume: 0 to 200 (above 100 boosts Kokoro voices; Windows voices top out at 100).</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Lower other apps' audio (music, videos) while MagniReader is speaking.</summary>
    public bool DuckOtherAudio { get; set; } = true;

    /// <summary>Volume other apps are lowered to while speaking, in % of their normal volume (0–100).
    /// Settings shows this inverted, as "Lower other sounds by" (100 − DuckLevel).</summary>
    public int DuckLevel { get; set; } = 30;

    /// <summary>Overlay dim opacity: 0.0 to 1.0. Default: 0.6 (60%).</summary>
    public double DimOpacity { get; set; } = 0.6;

    /// <summary>Highlight color as hex string.</summary>
    public string HighlightColor { get; set; } = "#FFD700"; // Gold

    /// <summary>Highlight text color as hex string.</summary>
    public string HighlightTextColor { get; set; } = "#000000"; // Black

    /// <summary>Override voice name. If empty, uses auto-detection.</summary>
    public string? PreferredVoice { get; set; }

    /// <summary>Whether to highlight word-by-word or sentence-by-sentence.</summary>
    public bool WordByWordHighlight { get; set; } = true;

    /// <summary>Show the compact control bar (hidden when the user sends it to the tray).</summary>
    public bool ShowControlBar { get; set; } = true;

    /// <summary>Last control bar position (WPF units). Null = top center of the primary screen.</summary>
    public double? ControlBarLeft { get; set; }
    public double? ControlBarTop { get; set; }

    /// <summary>Whether to launch on Windows startup.</summary>
    public bool LaunchOnStartup { get; set; } = false;

    /// <summary>Check GitHub for a new version in the background and install it on restart.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// Voice priority per language. Key = language code (e.g. "en", "es").
    /// Value = ordered list of voice names, highest priority first.
    /// </summary>
    public Dictionary<string, List<string>> VoicePriorities { get; set; } = new();

    // ─── Kokoro (offline neural voices) ──────────────────────────

    /// <summary>Speech engine: "Windows" (SAPI/OneCore voices) or "Kokoro".</summary>
    public string SpeechEngine { get; set; } = "Windows";

    /// <summary>Kokoro voice used for English text (e.g. "af_heart").</summary>
    public string KokoroVoiceEnglish { get; set; } = "af_heart";

    /// <summary>Kokoro voice used for Spanish text (e.g. "ef_dora").</summary>
    public string KokoroVoiceSpanish { get; set; } = "ef_dora";

    // ─── Shortcuts ───────────────────────────────────────────────
    // Text form: modifiers in the order Ctrl, Alt, Shift, Win, then one key (A–Z, 0–9, F1–F12).

    /// <summary>Keys held while clicking to read the text under the cursor (modifiers only).</summary>
    public string ReadHotkey { get; set; } = "Ctrl+Alt";

    /// <summary>Opens the snipping overlay to describe a screen area.</summary>
    public string SnipHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>Reads the text currently selected in any app.</summary>
    public string ReadSelectionHotkey { get; set; } = "Ctrl+Alt+R";

    // ─── Vision / Screenshot Analysis ────────────────────────────

    /// <summary>Vision API provider: "Gemini" or "OpenAI".</summary>
    public string VisionProvider { get; set; } = "Gemini";

    /// <summary>API key for the selected vision provider.</summary>
    public string VisionApiKey { get; set; } = "";
}
