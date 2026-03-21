using System.Globalization;
using System.IO;
using System.Speech.Synthesis;
using Microsoft.Win32;
using MagniReader.Infrastructure;
using WinRTSynth = Windows.Media.SpeechSynthesis;

namespace MagniReader.Core;

/// <summary>
/// Voice info that can come from either WinRT or registry scanning.
/// </summary>
public class MagniVoice
{
    public string DisplayName { get; set; } = "";
    public string Language { get; set; } = "";   // e.g. "en-US", "es-ES"
    public string LangCode { get; set; } = "";   // e.g. "en", "es"
    public string Gender { get; set; } = "";
    public bool IsNatural { get; set; }
    public string Id { get; set; } = "";         // internal token name
    public WinRTSynth.VoiceInformation? WinRTVoice { get; set; } // null if from registry
    public string? SapiVoiceName { get; set; }   // set if voice is available via SAPI5 (e.g. from NaturalVoiceSAPIAdapter)
    public bool IsPlayable => WinRTVoice != null || SapiVoiceName != null;
}

/// <summary>
/// Manages voices from multiple sources:
///   1. WinRT SpeechSynthesizer.AllVoices (standard + some OneCore)
///   2. OneCore registry scan (catches Natural voices we registered)
///   3. Deduplicates by display name
/// </summary>
public class VoiceManager : IDisposable
{
    private readonly List<MagniVoice> _allVoices = new();
    private Dictionary<string, List<string>> _priorities = new();
    private int _unavailableNaturalVoiceCount;

    // Cache: avoids re-running LINQ scoring on every Speak() call
    private readonly Dictionary<string, MagniVoice?> _voiceSelectionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of Natural/Neural voices that are installed on the system but
    /// cannot be accessed by the standard WinRT SpeechSynthesizer API.
    /// These voices require NaturalVoiceSAPIAdapter to be usable.
    /// </summary>
    public int UnavailableNaturalVoiceCount => _unavailableNaturalVoiceCount;

    public VoiceManager()
    {
        DiscoverVoices();
    }

    private void DiscoverVoices()
    {
        _allVoices.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source 1: WinRT voices (these are the voices we can actually use for synthesis)
        try
        {
            foreach (var v in WinRTSynth.SpeechSynthesizer.AllVoices)
            {
                if (seen.Contains(v.DisplayName)) continue;
                seen.Add(v.DisplayName);

                var langParts = v.Language.Split('-');
                _allVoices.Add(new MagniVoice
                {
                    DisplayName = v.DisplayName,
                    Language = v.Language,
                    LangCode = langParts[0].ToLowerInvariant(),
                    Gender = v.Gender.ToString(),
                    IsNatural = v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase),
                    Id = v.Id,
                    WinRTVoice = v
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceManager] WinRT enum error: {ex.Message}");
        }

        // Source 2: SAPI5 voices (catches NaturalVoiceSAPIAdapter voices if installed)
        // NaturalVoiceSAPIAdapter bridges Windows Neural voices into SAPI5,
        // making them accessible to desktop apps.
        try
        {
            using var sapiKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Speech\Voices\Tokens");
            if (sapiKey != null)
            {
                foreach (var tokenName in sapiKey.GetSubKeyNames())
                {
                    using var tokenKey = sapiKey.OpenSubKey(tokenName);
                    if (tokenKey == null) continue;

                    var displayName = tokenKey.GetValue("")?.ToString();
                    if (string.IsNullOrEmpty(displayName)) continue;
                    if (seen.Contains(displayName)) continue;

                    using var attrsKey = tokenKey.OpenSubKey("Attributes");
                    var language = attrsKey?.GetValue("Language")?.ToString() ?? "";
                    var gender = attrsKey?.GetValue("Gender")?.ToString() ?? "";

                    // Check if this is a NaturalVoiceSAPIAdapter voice
                    var clsid = tokenKey.GetValue("CLSID")?.ToString() ?? "";
                    bool isAdapterVoice = clsid.Contains("a12bdfa1", StringComparison.OrdinalIgnoreCase) ||
                                          displayName.Contains("Natural", StringComparison.OrdinalIgnoreCase);

                    // Convert language code
                    string langCode = "";
                    string langTag = "";
                    try
                    {
                        // SAPI5 language may be a hex LCID or a culture tag
                        if (language.Length <= 4 && language.All(c => "0123456789abcdefABCDEF".Contains(c)))
                        {
                            int lcid = Convert.ToInt32(language, 16);
                            var culture = CultureInfo.GetCultureInfo(lcid);
                            langCode = culture.TwoLetterISOLanguageName;
                            langTag = culture.Name;
                        }
                        else
                        {
                            var culture = CultureInfo.GetCultureInfo(language);
                            langCode = culture.TwoLetterISOLanguageName;
                            langTag = culture.Name;
                        }
                    }
                    catch
                    {
                        langCode = "en";
                        langTag = "en-US";
                    }

                    seen.Add(displayName);

                    // Try to find a matching WinRT voice for playback
                    WinRTSynth.VoiceInformation? winRTMatch = null;
                    try
                    {
                        winRTMatch = WinRTSynth.SpeechSynthesizer.AllVoices
                            .FirstOrDefault(wv => wv.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
                    }
                    catch { }

                    _allVoices.Add(new MagniVoice
                    {
                        DisplayName = displayName,
                        Language = langTag,
                        LangCode = langCode,
                        Gender = gender,
                        IsNatural = isAdapterVoice,
                        Id = tokenName,
                        WinRTVoice = winRTMatch
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceManager] SAPI5 registry scan error: {ex.Message}");
        }

        // Source 2b: System.Speech.Synthesis runtime voices
        // NaturalVoiceSAPIAdapter injects voices dynamically at runtime — they don't
        // appear in the registry but DO appear in GetInstalledVoices().
        try
        {
            using var sapiSynth = new SpeechSynthesizer();
            foreach (var voice in sapiSynth.GetInstalledVoices().Where(v => v.Enabled))
            {
                var info = voice.VoiceInfo;
                if (seen.Contains(info.Name)) continue;
                seen.Add(info.Name);

                bool isNeural = info.Id.StartsWith("Edge-", StringComparison.OrdinalIgnoreCase) ||
                                info.Name.Contains("Online", StringComparison.OrdinalIgnoreCase) ||
                                info.Name.Contains("Natural", StringComparison.OrdinalIgnoreCase);

                _allVoices.Add(new MagniVoice
                {
                    DisplayName = info.Name,
                    Language = info.Culture?.Name ?? "en-US",
                    LangCode = info.Culture?.TwoLetterISOLanguageName ?? "en",
                    Gender = info.Gender.ToString(),
                    IsNatural = isNeural,
                    Id = info.Id,
                    SapiVoiceName = info.Name  // mark as SAPI5-playable
                });

                System.Diagnostics.Debug.WriteLine(
                    $"[VoiceManager] SAPI5 runtime voice: {info.Name} [{info.Culture}] (Neural={isNeural})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceManager] SAPI5 runtime scan error: {ex.Message}");
        }

        // Source 3: OneCore registry — ONLY add non-Neural voices that aren't yet seen.
        // Neural/Natural voices from OneCore use a private TTS engine that the standard
        // WinRT SpeechSynthesizer cannot access. They require NaturalVoiceSAPIAdapter
        // to be installed. We track them separately for the UI info message.
        _unavailableNaturalVoiceCount = 0;
        try
        {
            using var oneCoreKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens");
            if (oneCoreKey != null)
            {
                foreach (var tokenName in oneCoreKey.GetSubKeyNames())
                {
                    using var tokenKey = oneCoreKey.OpenSubKey(tokenName);
                    if (tokenKey == null) continue;

                    var displayName = tokenKey.GetValue("")?.ToString();
                    if (string.IsNullOrEmpty(displayName)) continue;

                    using var attrsKey = tokenKey.OpenSubKey("Attributes");
                    var voiceType = attrsKey?.GetValue("VoiceType")?.ToString() ?? "";
                    bool isNeural = displayName.Contains("Natural", StringComparison.OrdinalIgnoreCase)
                                 || voiceType.Equals("Neural", StringComparison.OrdinalIgnoreCase);

                    if (isNeural && !seen.Contains(displayName))
                    {
                        // This Neural voice is installed but not usable — count it
                        _unavailableNaturalVoiceCount++;
                        System.Diagnostics.Debug.WriteLine(
                            $"[VoiceManager] Neural voice installed but not usable: {displayName}");
                        continue;
                    }

                    if (seen.Contains(displayName)) continue;
                    seen.Add(displayName);

                    var language = attrsKey?.GetValue("Language")?.ToString() ?? "";
                    var gender = attrsKey?.GetValue("Gender")?.ToString() ?? "";

                    string langCode = "";
                    string langTag = "";
                    try
                    {
                        int lcid = Convert.ToInt32(language, 16);
                        var culture = CultureInfo.GetCultureInfo(lcid);
                        langCode = culture.TwoLetterISOLanguageName;
                        langTag = culture.Name;
                    }
                    catch
                    {
                        langCode = "en";
                        langTag = "en-US";
                    }

                    // Try to find a matching WinRT voice
                    WinRTSynth.VoiceInformation? winRTMatch = null;
                    try
                    {
                        var allWinRT = WinRTSynth.SpeechSynthesizer.AllVoices;
                        winRTMatch = allWinRT.FirstOrDefault(wv =>
                            wv.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));

                        // Also try matching by token ID
                        if (winRTMatch == null)
                        {
                            winRTMatch = allWinRT.FirstOrDefault(wv =>
                                wv.Id.EndsWith(tokenName, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    catch { }

                    _allVoices.Add(new MagniVoice
                    {
                        DisplayName = displayName,
                        Language = langTag,
                        LangCode = langCode,
                        Gender = gender,
                        IsNatural = false, // Non-neural OneCore voices
                        Id = tokenName,
                        WinRTVoice = winRTMatch
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceManager] OneCore scan error: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine($"[VoiceManager] Usable voices: {_allVoices.Count}");
        System.Diagnostics.Debug.WriteLine($"[VoiceManager] Unavailable Neural voices: {_unavailableNaturalVoiceCount}");
        foreach (var v in _allVoices)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  {(v.IsNatural ? "⭐" : "  ")} {v.DisplayName} [{v.Language}] " +
                $"(WinRT: {v.WinRTVoice != null})");
        }
    }

    public void LoadPriorities(AppSettings settings)
    {
        _priorities = settings.VoicePriorities ?? new();
        _voiceSelectionCache.Clear(); // Invalidate cache when priorities change
    }

    /// <summary>
    /// Gets the best voice for synthesis, preferring Natural/SAPI5 voices.
    /// Returns the MagniVoice so the caller can decide WinRT vs SAPI5.
    /// </summary>
    public MagniVoice? GetBestVoiceForLanguage(CultureInfo culture)
    {
        var langCode = culture.TwoLetterISOLanguageName;

        // Check cache first — voice list and priorities don't change during runtime
        if (_voiceSelectionCache.TryGetValue(langCode, out var cached))
            return cached;

        // Check user priority first
        if (_priorities.TryGetValue(langCode, out var priorityList) && priorityList.Count > 0)
        {
            foreach (var name in priorityList)
            {
                var voice = _allVoices.FirstOrDefault(v =>
                    v.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    v.IsPlayable);
                if (voice != null)
                {
                    _voiceSelectionCache[langCode] = voice;
                    return voice;
                }
            }
        }

        // Auto-select: prefer voices matching the user's regional variant.
        // The language detector returns neutral cultures like "es", so we need to
        // determine the best regional variant for voice selection.
        var systemLocale = CultureInfo.CurrentUICulture.Name; // e.g. "ca-ES" or "es-ES"
        var systemLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        string preferredLocale;
        if (langCode.Equals(systemLang, StringComparison.OrdinalIgnoreCase))
        {
            // Detected language matches system language → use system's full locale
            // e.g., system is "es-ES", detected "es" → prefer "es-ES"
            preferredLocale = systemLocale;
        }
        else
        {
            // Detected language differs from system language (e.g., system=ca-ES, detected=es)
            // Use the system's REGION to infer the best variant.
            // A user in Spain (region=ES) reading Spanish should get es-ES, not es-MX.
            preferredLocale = culture.Name; // starts as "es" (neutral)
            if (!preferredLocale.Contains('-'))
            {
                try
                {
                    var regionCode = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
                    var candidateLocale = $"{langCode}-{regionCode}"; // e.g. "es-ES"
                    // Validate this is a real culture
                    _ = new CultureInfo(candidateLocale);
                    preferredLocale = candidateLocale;
                    System.Diagnostics.Debug.WriteLine(
                        $"[VoiceManager] Inferred locale {preferredLocale} from region {regionCode}");
                }
                catch
                {
                    // Invalid combination (e.g., "ja-ES") — leave as neutral
                    System.Diagnostics.Debug.WriteLine(
                        $"[VoiceManager] Could not infer locale for '{langCode}', using neutral");
                }
            }
        }

        // Scoring: locale match is MORE important than Natural quality.
        // A standard es-ES voice is better than a Natural es-MX voice for a user in Spain.
        //   3 = matching locale + Natural (best)
        //   2 = matching locale, standard voice
        //   1 = wrong locale but Natural
        //   0 = wrong locale, standard voice
        var candidates = _allVoices
            .Where(v => v.LangCode.Equals(langCode, StringComparison.OrdinalIgnoreCase) &&
                        v.IsPlayable)
            .Select(v =>
            {
                bool localeMatch = v.Language.Equals(preferredLocale, StringComparison.OrdinalIgnoreCase);
                int score = 0;
                if (localeMatch) score += 2;
                if (v.IsNatural) score += 1;
                return (voice: v, score, localeMatch);
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.voice.DisplayName)
            .ToList();

        // Log all candidates so we can diagnose voice selection issues
        System.Diagnostics.Debug.WriteLine(
            $"[VoiceManager] Selecting voice for lang='{langCode}', preferredLocale='{preferredLocale}', " +
            $"systemUI='{CultureInfo.CurrentUICulture.Name}', candidates={candidates.Count}:");
        foreach (var c in candidates)
        {
            System.Diagnostics.Debug.WriteLine(
                $"  score={c.score} locale={c.localeMatch} natural={c.voice.IsNatural} " +
                $"lang={c.voice.Language} name={c.voice.DisplayName} " +
                $"WinRT={c.voice.WinRTVoice != null} SAPI={c.voice.SapiVoiceName != null}");
        }

        var match = candidates.FirstOrDefault().voice;

        if (match != null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[VoiceManager] ★ Selected: {match.DisplayName} [{match.Language}]");
            _voiceSelectionCache[langCode] = match;
            return match;
        }

        // Fallback 1: Multilingual voice (can speak any language)
        var multilingual = _allVoices
            .FirstOrDefault(v => v.IsPlayable && v.IsNatural &&
                v.DisplayName.Contains("Multilingual", StringComparison.OrdinalIgnoreCase));
        if (multilingual != null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[VoiceManager] No Neural voice for '{langCode}', falling back to Multilingual: {multilingual.DisplayName}");
            _voiceSelectionCache[langCode] = multilingual;
            return multilingual;
        }

        // Fallback 2: Any Neural/Natural voice (better than robotic)
        var anyNeural = _allVoices
            .FirstOrDefault(v => v.IsPlayable && v.IsNatural);
        if (anyNeural != null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[VoiceManager] No Multilingual voice, falling back to Neural: {anyNeural.DisplayName}");
            _voiceSelectionCache[langCode] = anyNeural;
            return anyNeural;
        }

        // Fallback 3: Any playable voice (last resort)
        var fallback = _allVoices.FirstOrDefault(v => v.IsPlayable);
        _voiceSelectionCache[langCode] = fallback;
        return fallback;
    }

    /// <summary>
    /// Gets the best WinRT voice for synthesis (legacy compatibility).
    /// </summary>
    public WinRTSynth.VoiceInformation? GetWinRTVoiceForLanguage(CultureInfo culture)
    {
        return GetBestVoiceForLanguage(culture)?.WinRTVoice;
    }

    /// <summary>
    /// Gets all voices grouped by language for the settings UI.
    /// </summary>
    public Dictionary<string, List<MagniVoice>> GetVoicesGroupedByLanguage()
    {
        var grouped = new Dictionary<string, List<MagniVoice>>();

        var byLang = _allVoices
            .GroupBy(v => v.LangCode)
            .OrderBy(g => g.Key);

        foreach (var group in byLang)
        {
            var langCode = group.Key;
            var voices = group.ToList();

            if (_priorities.TryGetValue(langCode, out var priorityList) && priorityList.Count > 0)
            {
                voices = voices
                    .OrderBy(v =>
                    {
                        var idx = priorityList.IndexOf(v.DisplayName);
                        return idx >= 0 ? idx : int.MaxValue;
                    })
                    .ThenByDescending(v => v.IsNatural)
                    .ToList();
            }
            else
            {
                voices = voices
                    .OrderByDescending(v => v.IsNatural)
                    .ThenBy(v => v.DisplayName)
                    .ToList();
            }

            grouped[langCode] = voices;
        }

        return grouped;
    }

    public static string GetLanguageDisplayName(string langCode)
    {
        try { return new CultureInfo(langCode).EnglishName; }
        catch { return langCode; }
    }

    /// <summary>
    /// Preview a voice using the best available synthesis engine.
    /// Uses SAPI5 for adapter voices, WinRT for native voices.
    /// </summary>
    public async void PreviewVoice(string voiceDisplayName, string langCode)
    {
        try
        {
            string sample = langCode switch
            {
                "es" => "Hola, soy tu asistente de lectura.",
                "fr" => "Bonjour, je suis votre assistant de lecture.",
                "de" => "Hallo, ich bin Ihr Leseassistent.",
                "it" => "Ciao, sono il tuo assistente di lettura.",
                "pt" => "Olá, sou seu assistente de leitura.",
                "nl" => "Hallo, ik ben uw leesassistent.",
                "ja" => "こんにちは、読み上げアシスタントです。",
                "ko" => "안녕하세요, 읽기 도우미입니다.",
                "zh" => "你好，我是你的阅读助手。",
                "ca" => "Hola, sóc el teu assistent de lectura.",
                _ => "Hello, I am your reading assistant."
            };

            // Check if this voice should be played via SAPI5 (adapter voice)
            var voice = _allVoices.FirstOrDefault(v =>
                v.DisplayName.Equals(voiceDisplayName, StringComparison.OrdinalIgnoreCase));

            if (voice?.SapiVoiceName != null)
            {
                // Use SAPI5 for adapter voices (NaturalVoiceSAPIAdapter / Edge Online voices)
                System.Diagnostics.Debug.WriteLine($"[VoiceManager] Preview via SAPI5: {voice.SapiVoiceName}");
                await Task.Run(() =>
                {
                    using var sapiSynth = new SpeechSynthesizer();
                    sapiSynth.SelectVoice(voice.SapiVoiceName);
                    sapiSynth.SetOutputToDefaultAudioDevice();
                    sapiSynth.Speak(sample);
                });
            }
            else
            {
                // Use WinRT for native voices
                var synth = new WinRTSynth.SpeechSynthesizer();
                var winRTVoice = ResolveWinRTVoice(voiceDisplayName);
                if (winRTVoice != null)
                {
                    synth.Voice = winRTVoice;
                    System.Diagnostics.Debug.WriteLine($"[VoiceManager] Preview via WinRT: {winRTVoice.DisplayName}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[VoiceManager] Preview using default voice (no match for '{voiceDisplayName}')");
                }

                var result = await synth.SynthesizeTextToStreamAsync(sample);
                var stream = result.AsStreamForRead();
                var reader = new NAudio.Wave.StreamMediaFoundationReader(stream);
                var waveOut = new NAudio.Wave.WaveOutEvent();
                waveOut.Init(reader);
                waveOut.PlaybackStopped += (s, e) =>
                {
                    waveOut.Dispose(); reader.Dispose(); stream.Dispose();
                };
                waveOut.Play();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceManager] Preview error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a voice display name to a WinRT VoiceInformation using multiple strategies.
    /// </summary>
    private WinRTSynth.VoiceInformation? ResolveWinRTVoice(string voiceDisplayName)
    {
        // 1. Check our cached MagniVoice first
        var cached = _allVoices.FirstOrDefault(v =>
            v.DisplayName.Equals(voiceDisplayName, StringComparison.OrdinalIgnoreCase));
        if (cached?.WinRTVoice != null)
            return cached.WinRTVoice;

        // 2. Search WinRT AllVoices directly (in case our cache missed it)
        try
        {
            var allWinRT = WinRTSynth.SpeechSynthesizer.AllVoices;

            // Exact display name
            var match = allWinRT.FirstOrDefault(v =>
                v.DisplayName.Equals(voiceDisplayName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // Containment match (handles partial name differences)
            match = allWinRT.FirstOrDefault(v =>
                v.DisplayName.Contains(voiceDisplayName, StringComparison.OrdinalIgnoreCase) ||
                voiceDisplayName.Contains(v.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // ID-based match using cached voice's Id
            if (cached != null && !string.IsNullOrEmpty(cached.Id))
            {
                match = allWinRT.FirstOrDefault(v =>
                    v.Id.Contains(cached.Id, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
        }
        catch { }

        return null;
    }

    public bool HasNaturalVoices() =>
        _allVoices.Any(v => v.IsNatural);

    /// <summary>
    /// Returns a diagnostic report of all discovered voices.
    /// </summary>
    public string GetVoiceReport()
    {
        var lines = new List<string>
        {
            $"=== MagniReader Voice Report ===",
            $"Total voices: {_allVoices.Count}",
            $"Natural voices: {_allVoices.Count(v => v.IsNatural)}",
            $"WinRT voices: {_allVoices.Count(v => v.WinRTVoice != null)}",
            ""
        };

        foreach (var v in _allVoices)
        {
            lines.Add(
                $"  {(v.IsNatural ? "⭐" : "  ")} {v.DisplayName} [{v.Language}] " +
                $"Gender={v.Gender} WinRT={v.WinRTVoice != null}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose() { }
}
