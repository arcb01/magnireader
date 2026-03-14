using Microsoft.Win32;

namespace MagniReader.Core;

/// <summary>
/// Makes OneCore voices (used by Narrator) accessible to the System.Speech API (SAPI5).
/// 
/// Windows stores voices in two separate registry locations:
///   - SAPI5:    HKLM\SOFTWARE\Microsoft\Speech\Voices\Tokens       (old, limited)
///   - OneCore:  HKLM\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens (modern, better quality)
/// 
/// System.Speech.SpeechSynthesizer can ONLY see SAPI5 voices. This class creates
/// symbolic registry references from the OneCore path to SAPI5, making all OneCore
/// voices available to our app without modifying system files.
/// 
/// Requires elevated permissions (run as admin) for the first-time setup.
/// Once created, the registry entries persist across reboots.
/// </summary>
public static class VoiceEnabler
{
    private const string SAPI5_TOKENS = @"SOFTWARE\Microsoft\Speech\Voices\Tokens";
    private const string ONECORE_TOKENS = @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens";

    /// <summary>
    /// Checks how many OneCore voices are not yet accessible via SAPI5.
    /// </summary>
    public static (int total, int missing, List<string> missingNames) GetMissingVoices()
    {
        var missingNames = new List<string>();
        int total = 0;

        try
        {
            using var oneCoreKey = Registry.LocalMachine.OpenSubKey(ONECORE_TOKENS);
            if (oneCoreKey == null) return (0, 0, missingNames);

            using var sapiKey = Registry.LocalMachine.OpenSubKey(SAPI5_TOKENS);
            var sapiVoiceNames = sapiKey?.GetSubKeyNames() ?? Array.Empty<string>();

            foreach (var subKeyName in oneCoreKey.GetSubKeyNames())
            {
                total++;

                // Check if a corresponding SAPI5 entry already exists
                if (!sapiVoiceNames.Contains(subKeyName))
                {
                    try
                    {
                        using var voiceKey = oneCoreKey.OpenSubKey(subKeyName);
                        var name = voiceKey?.GetValue("")?.ToString() ?? subKeyName;
                        missingNames.Add(name);
                    }
                    catch
                    {
                        missingNames.Add(subKeyName);
                    }
                }
            }
        }
        catch { }

        return (total, missingNames.Count, missingNames);
    }

    /// <summary>
    /// Enables OneCore voices for SAPI5 by creating registry entries.
    /// Returns (success, message) tuple.
    /// Requires admin privileges.
    /// </summary>
    public static (bool success, string message) EnableOneCoreVoices()
    {
        int enabled = 0;
        int skipped = 0;
        var errors = new List<string>();

        try
        {
            using var oneCoreKey = Registry.LocalMachine.OpenSubKey(ONECORE_TOKENS);
            if (oneCoreKey == null)
                return (false, "OneCore voices registry not found.");

            // Open or create the SAPI5 tokens key
            using var sapiKey = Registry.LocalMachine.OpenSubKey(SAPI5_TOKENS, writable: true);
            if (sapiKey == null)
                return (false, "Cannot open SAPI5 registry for writing. Run as Administrator.");

            foreach (var subKeyName in oneCoreKey.GetSubKeyNames())
            {
                try
                {
                    // Check if already exists
                    using var existingKey = sapiKey.OpenSubKey(subKeyName);
                    if (existingKey != null)
                    {
                        skipped++;
                        continue;
                    }

                    // Read OneCore voice data
                    using var oneCoreVoice = oneCoreKey.OpenSubKey(subKeyName);
                    if (oneCoreVoice == null) continue;

                    // Create SAPI5 entry pointing to the OneCore voice
                    using var newKey = sapiKey.CreateSubKey(subKeyName);
                    if (newKey == null)
                    {
                        errors.Add($"Failed to create key for {subKeyName}");
                        continue;
                    }

                    // Copy the default value (voice display name)
                    var defaultValue = oneCoreVoice.GetValue("");
                    if (defaultValue != null)
                        newKey.SetValue("", defaultValue);

                    // Set the CLSID to point to the OneCore TTS engine
                    newKey.SetValue("CLSID", "{179F3D56-1B0B-42B2-A962-59B7EF59FE1B}");

                    // Point to the actual voice data path in OneCore
                    var voicePath = oneCoreVoice.GetValue("VoicePath")?.ToString();
                    if (!string.IsNullOrEmpty(voicePath))
                        newKey.SetValue("VoicePath", voicePath);

                    // Also set the voice path indirectly
                    newKey.SetValue("LangDataPath",
                        oneCoreVoice.GetValue("LangDataPath")?.ToString() ?? "");
                    newKey.SetValue("VoicePath",
                        oneCoreVoice.GetValue("VoicePath")?.ToString() ?? "");

                    // Copy the Attributes subkey (contains Language, Gender, Name, etc.)
                    using var oneCoreAttrs = oneCoreVoice.OpenSubKey("Attributes");
                    if (oneCoreAttrs != null)
                    {
                        using var newAttrs = newKey.CreateSubKey("Attributes");
                        if (newAttrs != null)
                        {
                            foreach (var valueName in oneCoreAttrs.GetValueNames())
                            {
                                var value = oneCoreAttrs.GetValue(valueName);
                                if (value != null)
                                    newAttrs.SetValue(valueName, value);
                            }
                        }
                    }

                    enabled++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{subKeyName}: {ex.Message}");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied. Please run MagniReader as Administrator to enable OneCore voices.");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }

        var msg = $"Enabled {enabled} new voices. {skipped} already existed.";
        if (errors.Count > 0)
            msg += $"\n{errors.Count} errors: {string.Join(", ", errors.Take(3))}";

        return (enabled > 0 || skipped > 0, msg);
    }

    /// <summary>
    /// Checks if the app is running with admin privileges.
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
