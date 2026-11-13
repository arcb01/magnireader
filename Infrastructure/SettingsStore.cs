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

    /// <summary>Speech volume: 0 to 100.</summary>
    public int Volume { get; set; } = 100;

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

    /// <summary>Whether to launch on Windows startup.</summary>
    public bool LaunchOnStartup { get; set; } = false;

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

    // ─── Vision / Screenshot Analysis ────────────────────────────

    /// <summary>Vision API provider: "Gemini" or "OpenAI".</summary>
    public string VisionProvider { get; set; } = "Gemini";

    /// <summary>API key for the selected vision provider.</summary>
    public string VisionApiKey { get; set; } = "";
}
