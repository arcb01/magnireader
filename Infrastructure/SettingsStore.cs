using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public void Save() => SaveTo(Settings);

    private static void SaveTo(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
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
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // Older versions stored the API key in plain text ("VisionApiKey"). Take it
                // and rewrite the file right away so only the encrypted copy stays on disk.
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("VisionApiKey", out var legacy) &&
                    legacy.ValueKind == JsonValueKind.String)
                {
                    var key = legacy.GetString();
                    if (!string.IsNullOrEmpty(key) && string.IsNullOrEmpty(settings.VisionApiKey))
                        settings.VisionApiKey = key;
                    SaveTo(settings);
                }
                return settings;
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

    // ─── Vision / Screenshot Analysis ────────────────────────────

    /// <summary>Vision API provider: "Gemini" or "OpenAI".</summary>
    public string VisionProvider { get; set; } = "Gemini";

    /// <summary>
    /// API key for the selected vision provider, in memory only. On disk it is stored
    /// encrypted with Windows DPAPI for the current user (<see cref="VisionApiKeyProtected"/>),
    /// so copying settings.json to another account or PC doesn't reveal it.
    /// </summary>
    [JsonIgnore]
    public string VisionApiKey { get; set; } = "";

    /// <summary>The API key encrypted with DPAPI (CurrentUser), Base64. What settings.json holds.</summary>
    public string? VisionApiKeyProtected
    {
        get => SecretProtector.Protect(VisionApiKey);
        set => VisionApiKey = SecretProtector.Unprotect(value);
    }

    /// <summary>
    /// The user agreed that snips are uploaded to the vision provider. Asked once, before
    /// the first upload; turning it off again makes MagniReader ask on the next snip.
    /// </summary>
    public bool VisionUploadConsent { get; set; } = false;

    // ─── Shortcuts ───────────────────────────────────────────────
    // Format "Mod+Mod+Key" with modifiers Ctrl, Alt, Shift, Win (see Input/Hotkey.cs).
    // On keyboards with AltGr, "Alt" means the left Alt key only, so typing @ # € with
    // AltGr never triggers MagniReader.

    /// <summary>Modifiers held while left-clicking text to read it (no key). Default "Ctrl+Alt".</summary>
    public string ReadHotkey { get; set; } = "Ctrl+Alt";

    /// <summary>Opens the snipping tool for the vision feature.</summary>
    public string SnipHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>Reads the selected text, or the clipboard when nothing is selected.</summary>
    public string ReadSelectionHotkey { get; set; } = "Ctrl+Alt+R";

    // ─── Diagnostics ─────────────────────────────────────────────

    /// <summary>
    /// Write detailed speech/voice traces to the log file. Off by default; the text being
    /// read is never logged either way.
    /// </summary>
    public bool DiagnosticLogging { get; set; } = false;
}

/// <summary>
/// Encrypts small secrets (API keys) with Windows DPAPI, tied to the current Windows user.
/// </summary>
internal static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MagniReader.VisionApiKey.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }
        catch (Exception ex)
        {
            // Never fall back to plain text: the key just won't be saved.
            CrashLogger.LogError("Could not encrypt the API key; it will not be saved", ex);
            return null;
        }
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            // Settings copied from another user/PC: the key has to be entered again.
            CrashLogger.LogWarning($"Could not decrypt the saved API key ({ex.GetType().Name}); please enter it again");
            return "";
        }
    }
}
