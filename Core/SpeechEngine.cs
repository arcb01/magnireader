using System.Diagnostics;
using System.Globalization;
using System.IO;
using NAudio.Wave;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using SapiSynth = System.Speech.Synthesis;
using WinRTSynth = Windows.Media.SpeechSynthesis;

namespace MagniReader.Core;

/// <summary>
/// Speech engine with three synthesis paths (in priority order):
///   1. WinRT + native MediaPlayer — fastest, uses same OneCore engine as Windows Narrator
///   2. SAPI5 direct audio — for NaturalVoiceSAPIAdapter voices without WinRT equivalents
///   3. WinRT + NAudio fallback — if MediaPlayer fails
///
/// Performance:
///   - WinRT is preferred because it goes through the Windows speech service directly
///     (same path as "Acceso directo a lectura" / Narrator), avoiding SAPI5 adapter overhead.
///   - SAPI5 synth is kept persistent to avoid re-initialization.
///   - Native MediaPlayer starts playback faster than NAudio's StreamMediaFoundationReader.
/// </summary>
public class SpeechEngine : IDisposable
{
    private readonly VoiceManager _voiceManager;
    private SpeechSynthesizer _synth;
    private CancellationTokenSource? _cts;
    private bool _isSpeaking;

    // Native MediaPlayer for WinRT voice playback (replaces NAudio for lower latency)
    private MediaPlayer? _mediaPlayer;

    // NAudio fallback for WinRT path if MediaPlayer fails
    private WaveOutEvent? _waveOut;
    private NAudio.Wave.StreamMediaFoundationReader? _currentReader;
    private System.IO.Stream? _currentStream;

    // Persistent SAPI5 synth — reused across calls to avoid re-initialization overhead
    private SapiSynth.SpeechSynthesizer? _persistentSapiSynth;
    private string? _currentSapiVoiceName;
    private readonly object _sapiLock = new();

    // Word progress tracking
    private string _currentText = string.Empty;
    // Replaced as a whole (never mutated after publishing) so the timer thread
    // always sees a complete list for the text being spoken now.
    private volatile List<(int charPos, int charLen, TimeSpan offset)> _wordTimings = new();
    private System.Threading.Timer? _wordTimer;
    private bool _useMediaPlayerForWordTimer; // tracks which player is active

    // Volume (0-200) and rate (-10..10, SAPI scale)
    private int _volume = 100;
    private int _rate;

    // The whole text of the current reading. A live speed change restarts it from the
    // word being spoken: _charOffset is where the part being spoken now starts, and
    // _lastWordPos the last word reached, both in positions of _fullText.
    private string _fullText = string.Empty;
    private CultureInfo _speakCulture = CultureInfo.CurrentUICulture;
    private int _charOffset;
    private volatile int _lastWordPos = -1;
    // Rate changes are applied a moment after the last click, so stepping through
    // several speeds restarts the voice once. While paused, on resume.
    private System.Windows.Threading.DispatcherTimer? _restartTimer;
    private bool _restartPending;
    // Set while a speed change restarts the reading: the cancel of the old part must
    // not count as the text having finished (that would move on to the next paragraph).
    private bool _restarting;

    // Whether each SAPI voice actually produces audio (true) or not (false), checked
    // once per session. NaturalVoiceSAPIAdapter "Online" voices can return EMPTY audio
    // and still report success when the online service is unreachable, so a reading
    // was silent with no error. Broken voices are replaced by a local voice.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _sapiVoiceWorks =
        new(StringComparer.OrdinalIgnoreCase);

    // Kokoro (offline neural voices). Used for English/Spanish when selected in
    // Settings; everything else, or any Kokoro failure, falls back to the paths below.
    private readonly KokoroSpeaker _kokoro = KokoroSpeaker.Shared;
    private bool _kokoroEnabled;
    private readonly LanguageDetector _kokoroLangCheck = new();
    private static readonly HashSet<string> LatinScriptLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "ca", "fr", "pt", "it", "de", "nl", "da", "sv", "no", "nb", "fi", "pl", "tr" };
    private string _kokoroVoiceEnglish = KokoroSpeaker.DefaultEnglishVoice;
    private string _kokoroVoiceSpanish = KokoroSpeaker.DefaultSpanishVoice;

    // ─── Pause / seek (reading controls) ────────────────────────
    private enum PlaybackRoute { None, Kokoro, MediaPlayer, NAudio, Sapi }
    private volatile PlaybackRoute _route;
    private volatile bool _isPaused;
    private KokoroPlaybackControl? _kokoroControl;
    // Position for the routes that have no player position (NAudio fallback, SAPI estimate)
    private readonly PlaybackClock _clock = new();
    // SAPI: estimated time of each word of the current reading, and the last word reached
    private List<(int charPos, int charLen, TimeSpan offset)>? _sapiEstimate;
    private volatile int _sapiWordIndex = -1;
    private volatile bool _sapiGotBookmark;
    private int _sapiSeekToWord = -1;
    private ManualResetEventSlim? _sapiWake;
    private bool _sapiPaused;

    public event Action<int, int>? WordReached;
    public event Action? SpeechCompleted;
    public event Action? SpeechStarted;
    /// <summary>Fires when audio physically starts playing, with the real duration in ms.</summary>
    public event Action<double>? PlaybackStarted;

    public bool IsSpeaking => _isSpeaking;

    /// <summary>True while the current reading is paused with <see cref="SetPaused"/>.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Raised (on the UI thread) when the reading is paused or resumed.</summary>
    public event Action<bool>? PauseChanged;

    /// <summary>Lowers other apps' audio while speaking (see Settings → Speech).</summary>
    public AudioDucker Ducker { get; } = new();

    public SpeechEngine(VoiceManager voiceManager)
    {
        _voiceManager = voiceManager;
        _synth = new SpeechSynthesizer();
        // Ask the voice for real word boundaries (TimedMetadataTracks "SpeechWord")
        // so the overlay box follows the words exactly as they are spoken.
        try { _synth.Options.IncludeWordBoundaryMetadata = true; }
        catch { /* older Windows: fall back to estimated timings */ }
        InitPersistentMediaPlayer();

        // Whenever speech ends (finished, failed or stopped), bring other apps back up.
        // The restore waits a moment, so the next paragraph's Duck() cancels it and the
        // music doesn't bounce up between paragraphs.
        SpeechCompleted += Ducker.ScheduleRestore;
    }

    /// <summary>
    /// Creates the persistent MediaPlayer once. Reused across all speech calls.
    /// </summary>
    private void InitPersistentMediaPlayer()
    {
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.Volume = SystemVoiceVolume;

        _mediaPlayer.MediaEnded += (s, e) =>
        {
            LogTrace("[Speech] MediaPlayer playback ended");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_isSpeaking)
                {
                    _isSpeaking = false;
                    StopWordTimer();
                    SpeechCompleted?.Invoke();
                }
            });
        };

        _mediaPlayer.MediaFailed += (s, e) =>
        {
            Infrastructure.CrashLogger.LogError(
                $"MediaPlayer playback failed: {e.Error} ({e.ErrorMessage})", e.ExtendedErrorCode);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_isSpeaking)
                {
                    _isSpeaking = false;
                    StopWordTimer();
                    SpeechCompleted?.Invoke();
                }
            });
        };
    }

    /// <summary>
    /// Pre-warms the speech engine by loading the best voice for the system language.
    /// For WinRT voices: triggers a minimal synthesis to warm up the OneCore service.
    /// For SAPI5 voices: pre-loads the neural model.
    /// </summary>
    public async Task WarmUpAsync()
    {
        var sw = Stopwatch.StartNew();
        var culture = CultureInfo.CurrentUICulture;
        var bestVoice = _voiceManager.GetBestVoiceForLanguage(culture);

        // Warm up WinRT path (preferred)
        var winrtVoice = bestVoice?.WinRTVoice ?? TryFindWinRTEquivalent(bestVoice?.DisplayName);
        if (winrtVoice != null)
        {
            try
            {
                _synth.Voice = winrtVoice;
                // Synthesize a space to warm up the OneCore service
                await _synth.SynthesizeTextToStreamAsync(" ");
                System.Diagnostics.Debug.WriteLine(
                    $"[Speech] WinRT warm-up complete in {sw.ElapsedMilliseconds}ms (voice: {winrtVoice.DisplayName})");
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Speech] WinRT warm-up failed: {ex.Message}, will try SAPI5");
            }
        }

        // Check that the SAPI voices we are likely to use really produce audio
        // (UI language and English), so the first reading doesn't wait on a broken voice.
        var englishVoice = _voiceManager.GetBestVoiceForLanguage(CultureInfo.GetCultureInfo("en"));
        if (englishVoice?.SapiVoiceName != null && englishVoice.WinRTVoice == null)
            _ = EnsureSapiVoiceWorksAsync(englishVoice.SapiVoiceName);

        // Fallback: warm up SAPI5 path
        if (bestVoice?.SapiVoiceName != null)
        {
            if (!await EnsureSapiVoiceWorksAsync(bestVoice.SapiVoiceName))
                return;

            try
            {
                await Task.Run(() =>
                {
                    lock (_sapiLock)
                    {
                        _persistentSapiSynth?.Dispose();
                        _persistentSapiSynth = null;
                        _currentSapiVoiceName = null;
                        var synth = new SapiSynth.SpeechSynthesizer();
                        synth.SelectVoice(bestVoice.SapiVoiceName);
                        synth.SetOutputToDefaultAudioDevice();
                        _persistentSapiSynth = synth;
                        _currentSapiVoiceName = bestVoice.SapiVoiceName;
                        synth.Speak(" ");
                    }
                });
            }
            catch (Exception ex)
            {
                // The voice is broken (typically an "Online" voice that can't reach
                // the service). Remember it so readings go straight to a local voice.
                _sapiVoiceWorks[bestVoice.SapiVoiceName] = false;
                Infrastructure.CrashLogger.LogError(
                    $"SAPI warm-up failed for voice '{bestVoice.SapiVoiceName}'; a local voice will be used instead", ex);
                return;
            }
            System.Diagnostics.Debug.WriteLine(
                $"[Speech] SAPI5 warm-up complete in {sw.ElapsedMilliseconds}ms (voice: {bestVoice.SapiVoiceName})");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[Speech] No voice to warm up");
        }
    }

    /// <summary>
    /// Speaks text using the best available voice for the given language.
    /// Prefers WinRT path (native OneCore, same as Windows Narrator) for lower latency.
    /// Falls back to SAPI5 for voices without WinRT equivalents.
    /// </summary>
    public void Speak(string text, CultureInfo culture)
    {
        _fullText = CleanForSpeech(text);
        _speakCulture = culture;
        _lastWordPos = -1;
        SpeakFrom(0);
    }

    /// <summary>
    /// Speaks <see cref="_fullText"/> from character <paramref name="offset"/> on. Word
    /// positions are reported in full-text positions, so the highlight stays right.
    /// Used by <see cref="Speak"/> (offset 0) and by a live speed change, which restarts
    /// the reading at the word being spoken with the new rate.
    /// </summary>
    private async void SpeakFrom(int offset)
    {
        CancelRestart();
        if (_isSpeaking) Cancel();

        offset = Math.Clamp(offset, 0, _fullText.Length);
        string text = _fullText.Substring(offset);
        var culture = _speakCulture;
        _charOffset = offset;
        _currentText = text;
        _route = PlaybackRoute.None;
        SetPausedFlag(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var sw = Stopwatch.StartNew();

        // Lower music/videos while we talk (restored on SpeechCompleted / Cancel).
        Ducker.Duck();

        try
        {
            // 0. Kokoro, if selected and it has a voice for this language
            // (A restart after a speed change keeps the language picked for the whole text.)
            if (_kokoroEnabled && offset == 0) _speakCulture = culture = RecheckLanguageForKokoro(text, culture);
            if (await TrySpeakViaKokoroAsync(text, culture, token)) return;
            if (token.IsCancellationRequested) return;

            var bestVoice = _voiceManager.GetBestVoiceForLanguage(culture);
            LogTrace($"[Speech] Speak: {text.Length} chars, lang={culture.Name}, voice={bestVoice?.DisplayName ?? "(none)"}");

            // STRATEGY: Prefer WinRT for lower latency.
            // WinRT synthesis goes through the Windows OneCore speech service —
            // the same engine used by Narrator / "Acceso directo a lectura".
            // SAPI5 + NaturalVoiceSAPIAdapter adds an adapter layer with higher latency.

            // 1. If the voice has a direct WinRT handle, use WinRT
            if (bestVoice?.WinRTVoice != null)
            {
                LogTrace(
                    $"[Speech] Route: WinRT direct → {bestVoice.DisplayName}");
                await SpeakViaWinRT(text, bestVoice.WinRTVoice, token, sw);
                return;
            }

            // 2. If it's a SAPI5 voice, try to find a WinRT equivalent first
            if (bestVoice?.SapiVoiceName != null)
            {
                var winrtEquivalent = TryFindWinRTEquivalent(bestVoice.DisplayName);
                if (winrtEquivalent != null)
                {
                    LogTrace(
                        $"[Speech] Route: WinRT equivalent ({winrtEquivalent.DisplayName}) for SAPI5 voice ({bestVoice.SapiVoiceName})");
                    await SpeakViaWinRT(text, winrtEquivalent, token, sw);
                    return;
                }

                // No WinRT equivalent → use SAPI5, but only if the voice really produces audio
                bool sapiWorks = await EnsureSapiVoiceWorksAsync(bestVoice.SapiVoiceName);
                if (token.IsCancellationRequested) return;
                if (sapiWorks)
                {
                    LogTrace(
                        $"[Speech] Route: SAPI5 (no WinRT equivalent) → {bestVoice.SapiVoiceName}");
                    var sapiError = await SpeakViaSapi(text, bestVoice, token, sw);
                    if (sapiError == null || token.IsCancellationRequested) return;

                    _sapiVoiceWorks[bestVoice.SapiVoiceName] = false;
                    Infrastructure.CrashLogger.LogError(
                        $"SAPI voice '{bestVoice.SapiVoiceName}' failed to speak; falling back to a local voice", sapiError);
                }

                // SAPI voice is broken → speak with a local WinRT voice for the same language
                var localVoice = FindLocalWinRTVoice(culture);
                LogTrace(
                    $"[Speech] Route: local fallback → {localVoice?.DisplayName ?? "default voice"}");
                await SpeakViaWinRT(text, localVoice, token, sw);
                return;
            }

            // 3. Fallback: use default WinRT voice
            LogTrace("[Speech] Route: WinRT default voice");
            await SpeakViaWinRT(text, null, token, sw);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError($"Speech failed ({text.Length} chars, lang={culture.Name})", ex);
            _isSpeaking = false;
            SpeechCompleted?.Invoke();
        }
        finally
        {
            // Some routes end without ever starting playback (cancelled while synthesizing,
            // no voice…), so no SpeechCompleted fires. If nothing is playing and no newer
            // Speak() has taken over, make sure other apps are not left lowered.
            if (!_isSpeaking && (_cts == null || _cts.Token == token))
                Ducker.ScheduleRestore();
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CitationMarks = new(
        @"\[\s*(?:\d{1,4}(?:\s*[-–,]\s*\d{1,4})*|[a-z]|[ivxl]{1,5}|(?:note|nota|n\.)\s*\d+|citation needed|cita requerida|cal citació|clarification needed|edit|editar|modifica|modificar|update|actualizar|sources?|fuentes?|fonts?)\s*\]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex StraySymbols = new(
        @"(?<=^|\s)[·•|►▶→←↑↓↗↘■□▪◦●○※†‡¶^~*#>]+(?=\s|$)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Blanks out things that shouldn't be read aloud: Wikipedia citation marks
    /// ([1], [a], [citation needed], [edit]) and stray symbols (bullets, arrows,
    /// separators). Replaced with spaces so character positions stay the same for
    /// the word highlight.
    /// </summary>
    internal static string CleanForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = CitationMarks.Replace(text, m => new string(' ', m.Length));
        text = StraySymbols.Replace(text, m => new string(' ', m.Length));
        return text;
    }

    /// <summary>
    /// Tries to find a WinRT voice that matches a SAPI5 voice by name.
    /// For example, SAPI5 "Microsoft Andrew Online (Natural)" → WinRT "Microsoft Andrew".
    /// </summary>
    private VoiceInformation? TryFindWinRTEquivalent(string? sapiDisplayName)
    {
        if (string.IsNullOrEmpty(sapiDisplayName)) return null;

        try
        {
            var allWinRT = WinRTSynth.SpeechSynthesizer.AllVoices;

            // Extract base name: "Microsoft Andrew Online (Natural)" → "Andrew"
            var baseName = ExtractVoiceBaseName(sapiDisplayName);
            if (string.IsNullOrEmpty(baseName)) return null;

            // Find a WinRT voice with the same base name
            foreach (var v in allWinRT)
            {
                var winrtBase = ExtractVoiceBaseName(v.DisplayName);
                if (baseName.Equals(winrtBase, StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] Found WinRT equivalent: '{v.DisplayName}' for SAPI5 '{sapiDisplayName}'");
                    return v;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Speech] WinRT equivalent search error: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Writes to the debugger and to the MagniReader log file, so a silent reading
    /// can be diagnosed from %APPDATA%\MagniReader\logs without a debugger.
    /// </summary>
    private static void LogTrace(string message)
    {
        Infrastructure.CrashLogger.LogTrace(message);
    }

    /// <summary>
    /// Returns whether a SAPI voice produces real audio, testing it once per session.
    /// </summary>
    private async Task<bool> EnsureSapiVoiceWorksAsync(string voiceName)
    {
        if (_sapiVoiceWorks.TryGetValue(voiceName, out var known)) return known;
        var works = await Task.Run(() => ProbeSapiVoice(voiceName));
        _sapiVoiceWorks[voiceName] = works;
        return works;
    }

    /// <summary>
    /// Synthesizes a short phrase into memory and checks that it contains audio.
    /// A broken "Online" voice returns only a 46-byte WAV header after a ~5 s timeout.
    /// </summary>
    private static bool ProbeSapiVoice(string voiceName)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var synth = new SapiSynth.SpeechSynthesizer();
            synth.SelectVoice(voiceName);
            using var ms = new MemoryStream();
            synth.SetOutputToWaveStream(ms);
            synth.Speak("Hello.");
            synth.SetOutputToNull();

            // Half a second of 16 kHz 16-bit audio is ~16 KB; an empty result is 46 bytes.
            bool works = ms.Length > 2000;
            if (works)
                LogTrace($"[Speech] Voice check OK: '{voiceName}' ({ms.Length} bytes in {sw.ElapsedMilliseconds}ms)");
            else
                Infrastructure.CrashLogger.LogWarning(
                    $"Voice '{voiceName}' produced no audio ({ms.Length} bytes in {sw.ElapsedMilliseconds}ms). " +
                    "If it is an Online voice, its service is unreachable or NaturalVoiceSAPIAdapter needs updating. " +
                    "A local voice will be used instead.");
            return works;
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError($"Voice check failed for '{voiceName}'", ex);
            return false;
        }
    }

    /// <summary>
    /// Finds an installed WinRT (OneCore) voice for the culture's language, preferring
    /// the user's regional variant (es-ES over es-MX for a user in Spain).
    /// Returns null if there is none, which makes WinRT use its default voice.
    /// </summary>
    private static VoiceInformation? FindLocalWinRTVoice(CultureInfo culture)
    {
        try
        {
            var lang = culture.TwoLetterISOLanguageName;
            var sameLang = WinRTSynth.SpeechSynthesizer.AllVoices
                .Where(v => v.Language.Equals(lang, StringComparison.OrdinalIgnoreCase) ||
                            v.Language.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameLang.Count == 0) return null;

            var ui = CultureInfo.CurrentUICulture;
            var preferred = culture.Name.Contains('-')
                ? culture.Name
                : lang.Equals(ui.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)
                    ? ui.Name
                    : $"{lang}-{RegionInfo.CurrentRegion.TwoLetterISORegionName}";

            return sameLang.FirstOrDefault(v => v.Language.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                   ?? sameLang[0];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Speech] Local voice lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts the voice's given name from display strings like
    /// "Microsoft Andrew Online (Natural)" → "Andrew"
    /// </summary>
    private static string? ExtractVoiceBaseName(string displayName)
    {
        // Remove "Microsoft " prefix
        var name = displayName;
        if (name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
            name = name["Microsoft ".Length..];

        // Remove suffixes like "Online", "(Natural)", "Desktop"
        var idx = name.IndexOfAny(['(', '-']);
        if (idx > 0) name = name[..idx];

        // Remove known suffixes
        foreach (var suffix in new[] { "Online", "Desktop", "Mobile", "Natural", "HD" })
        {
            name = name.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
        }

        return name.Trim().Length > 0 ? name.Trim() : null;
    }

    // ─── Background audio ducking ───────────────────────────────

    /// <summary>Applies "Lower other sounds while reading" from Settings.</summary>
    public void ConfigureDucking(Infrastructure.AppSettings settings)
    {
        Ducker.Enabled = settings.DuckOtherAudio;
        Ducker.Level = Math.Clamp(settings.DuckLevel, 0, 100) / 100f;
        if (!Ducker.Enabled) Ducker.ScheduleRestore();
    }

    // ─── Kokoro ─────────────────────────────────────────────────

    /// <summary>
    /// Applies the engine/voice choice from Settings. Starts loading the Kokoro model
    /// in the background so the first reading doesn't wait for it.
    /// </summary>
    public void ConfigureKokoro(Infrastructure.AppSettings settings)
    {
        _kokoroEnabled = string.Equals(settings.SpeechEngine, "Kokoro", StringComparison.OrdinalIgnoreCase);
        _kokoroVoiceEnglish = KokoroSpeaker.HasVoice(settings.KokoroVoiceEnglish)
            ? settings.KokoroVoiceEnglish : KokoroSpeaker.DefaultEnglishVoice;
        _kokoroVoiceSpanish = KokoroSpeaker.HasVoice(settings.KokoroVoiceSpanish)
            ? settings.KokoroVoiceSpanish : KokoroSpeaker.DefaultSpanishVoice;

        if (_kokoroEnabled)
        {
            if (KokoroSpeaker.IsModelDownloaded)
                _ = _kokoro.EnsureLoadedAsync();
            else
                LogTrace("[Speech] Kokoro selected but its model is not downloaded; using Windows voices");
        }
    }

    /// <summary>
    /// The trigram detector often takes short English/Spanish text for French,
    /// Portuguese, Dutch or Danish, which sent it to a slow Online SAPI voice (silent
    /// for seconds while the highlight moved). With Kokoro selected, Latin-script
    /// results are re-checked against English, Spanish and Catalan.
    /// </summary>
    private CultureInfo RecheckLanguageForKokoro(string text, CultureInfo culture)
    {
        var lang = culture.TwoLetterISOLanguageName;
        if (lang is "en" or "es" || !LatinScriptLanguages.Contains(lang)) return culture;
        var recheck = _kokoroLangCheck.DetectAmong(text, "en", "es", "ca");
        if (recheck == lang) return culture;
        LogTrace($"[Speech] Language re-check: '{lang}' → '{recheck}'");
        try { return CultureInfo.GetCultureInfo(recheck); }
        catch { return culture; }
    }

    /// <summary>Kokoro speed for the settings rate -10..10 (1.0 = normal).</summary>
    private float KokoroSpeed => _rate >= 0 ? 1f + _rate * 0.1f : 1f + _rate * 0.05f;

    /// <summary>
    /// Speaks with Kokoro. Returns false (nothing was heard) when Kokoro is off, has no
    /// voice for the language, isn't ready, or failed before any audio, so the caller
    /// uses a Windows voice instead.
    /// </summary>
    private async Task<bool> TrySpeakViaKokoroAsync(string text, CultureInfo culture, CancellationToken token)
    {
        if (!_kokoroEnabled) return false;

        string lang = culture.TwoLetterISOLanguageName;
        string? voiceId = lang switch
        {
            "en" => _kokoroVoiceEnglish,
            "es" => _kokoroVoiceSpanish,
            _ => null,
        };
        if (voiceId == null)
        {
            LogTrace($"[Speech] Kokoro has no '{lang}' voice; using a Windows voice");
            return false;
        }
        if (!KokoroSpeaker.IsModelDownloaded) return false;

        if (!_kokoro.IsLoaded)
        {
            // Model still loading (right after startup): wait a little, else use Windows voice
            var load = _kokoro.EnsureLoadedAsync();
            var winner = await Task.WhenAny(load, Task.Delay(4000, token));
            if (token.IsCancellationRequested) return true;
            if (winner != load || !load.Result)
            {
                LogTrace("[Speech] Kokoro model not ready; using a Windows voice for this reading");
                return false;
            }
        }

        LogTrace($"[Speech] Route: Kokoro → {voiceId} (lang={lang}, speed={KokoroSpeed:F2})");
        var control = new KokoroPlaybackControl();
        if (_isPaused) control.SetPaused(true);
        _kokoroControl = control;
        _route = PlaybackRoute.Kokoro;
        _isSpeaking = true;
        SpeechStarted?.Invoke();
        bool audioStarted = false;

        bool spoke;
        int offset = _charOffset;
        try
        {
            spoke = await _kokoro.SpeakAsync(text, voiceId, KokoroSpeed,
                onWord: (pos, len) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_isSpeaking && !token.IsCancellationRequested)
                        RaiseWordReached(offset + pos, len);
                }),
                onAudioStarted: estMs =>
                {
                    audioStarted = true;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => PlaybackStarted?.Invoke(estMs));
                },
                token, control);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex) when (!audioStarted && !token.IsCancellationRequested)
        {
            Infrastructure.CrashLogger.LogError("Kokoro failed before speaking; falling back to a Windows voice", ex);
            _isSpeaking = false;
            _route = PlaybackRoute.None;
            return false;
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError("Kokoro playback failed", ex);
            spoke = true;
        }

        if (!spoke && !token.IsCancellationRequested)
        {
            // Kokoro found nothing it could pronounce: let a Windows voice try
            LogTrace("[Speech] Kokoro produced no audio for this text; using a Windows voice");
            _isSpeaking = false;
            _route = PlaybackRoute.None;
            return false;
        }

        if (!token.IsCancellationRequested && _isSpeaking)
        {
            _isSpeaking = false;
            SpeechCompleted?.Invoke();
        }
        return true;
    }

    // ─── WinRT Synthesis ─────────────────────────────────────────

    private async Task SpeakViaWinRT(string text, VoiceInformation? voice, CancellationToken token, Stopwatch sw)
    {
        if (voice != null)
        {
            _synth.Voice = voice;
        }

        _isSpeaking = true;
        SpeechStarted?.Invoke();

        var result = await _synth.SynthesizeTextToStreamAsync(text);
        System.Diagnostics.Debug.WriteLine(
            $"[Speech] WinRT synthesis done in {sw.ElapsedMilliseconds}ms");
        if (token.IsCancellationRequested) return;

        // Word timings MUST be ready before playback starts: the word timer
        // takes them when it starts. (Running this in the background made the
        // timer use the previous reading's timings.) Reading the cues is fast,
        // they're already in the stream.
        ExtractWordTimings(text, result);

        // Calculate REAL audio duration from the WAV stream
        double realDurationMs = GetStreamDurationMs(result);
        System.Diagnostics.Debug.WriteLine(
            $"[Speech] Real audio duration: {realDurationMs:F0}ms");

        try
        {
            PlayViaNativeMediaPlayer(result, token, sw, realDurationMs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Speech] MediaPlayer failed ({ex.Message}), falling back to NAudio");
            PlayViaNAudio(result, token, sw, realDurationMs);
        }
    }

    /// <summary>
    /// Plays SpeechSynthesisStream via native Windows MediaPlayer.
    /// This is the same playback pipeline Windows Narrator uses.
    /// </summary>
    private void PlayViaNativeMediaPlayer(SpeechSynthesisStream stream, CancellationToken token, Stopwatch sw, double realDurationMs)
    {
        // Reuse the persistent MediaPlayer — just swap the source
        if (_mediaPlayer == null) InitPersistentMediaPlayer();

        _mediaPlayer!.Pause();
        _useMediaPlayerForWordTimer = true;

        var source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _mediaPlayer.Source = source;
        _mediaPlayer.Volume = SystemVoiceVolume;

        if (token.IsCancellationRequested) return;

        LogTrace(
            $"[Speech] MediaPlayer starting playback at {sw.ElapsedMilliseconds}ms");

        _route = PlaybackRoute.MediaPlayer;
        StartWordTimer();
        if (!_isPaused) _mediaPlayer.Play(); // paused while synthesizing: wait for Resume

        // Notify with real duration for accurate progress animation
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => PlaybackStarted?.Invoke(realDurationMs));
    }

    /// <summary>
    /// Gets the real audio duration by reading the WAV byte rate from the stream header.
    /// WAV byte rate is at offset 28 (4 bytes, little-endian).
    /// Duration = (streamSize - 44 header bytes) / byteRate * 1000.
    /// </summary>
    private double GetStreamDurationMs(SpeechSynthesisStream stream)
    {
        try
        {
            var inputStream = stream.GetInputStreamAt(28);
            var reader = new Windows.Storage.Streams.DataReader(inputStream);
            reader.LoadAsync(4).AsTask().GetAwaiter().GetResult();
            uint byteRate = reader.ReadUInt32();
            reader.DetachStream();

            // Reset stream position to start for playback
            stream.Seek(0);

            if (byteRate > 0)
            {
                long dataSize = Math.Max(0, (long)stream.Size - 44);
                return (double)dataSize / byteRate * 1000.0;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Speech] WAV duration parse failed: {ex.Message}");
        }

        // Fallback: assume 32kB/s (16kHz, 16-bit, mono)
        return (double)Math.Max(0, (long)stream.Size - 44) / 32000.0 * 1000.0;
    }

    /// <summary>
    /// Fallback: plays SpeechSynthesisStream via NAudio WaveOutEvent.
    /// </summary>
    private void PlayViaNAudio(SpeechSynthesisStream result, CancellationToken token, Stopwatch sw, double realDurationMs)
    {
        CleanupStreamResources();
        _useMediaPlayerForWordTimer = false;

        _currentStream = result.AsStreamForRead();
        _currentReader = new NAudio.Wave.StreamMediaFoundationReader(_currentStream);

        if (_waveOut == null)
        {
            _waveOut = new WaveOutEvent();
            _waveOut.PlaybackStopped += OnWaveOutPlaybackStopped;
        }
        else
        {
            _waveOut.Stop();
        }

        _waveOut.Volume = (float)SystemVoiceVolume;
        _waveOut.Init(_currentReader);

        if (token.IsCancellationRequested) return;

        LogTrace(
            $"[Speech] NAudio starting playback at {sw.ElapsedMilliseconds}ms");

        _route = PlaybackRoute.NAudio;
        StartWordTimer();
        if (!_isPaused) _waveOut.Play(); // paused while synthesizing: wait for Resume

        // Notify with real duration for accurate progress animation
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => PlaybackStarted?.Invoke(realDurationMs));
    }

    private void OnWaveOutPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Only while NAudio is playing: a stop from Cancel() can arrive after a new
            // reading (or a speed-change restart) has started on another route
            if (_isSpeaking && _route == PlaybackRoute.NAudio)
            {
                _isSpeaking = false;
                StopWordTimer();
                SpeechCompleted?.Invoke();
            }
        });
    }

    // ─── SAPI5 Synthesis ────────────────────────────────────────

    /// <summary>
    /// Speaks via SAPI5. Returns null on success or cancellation, or the error if the
    /// voice failed. On error SpeechCompleted is NOT raised, so the caller can retry
    /// with another voice. SAPI reports errors only through SpeakCompleted.Error, which
    /// used to be ignored: a broken voice finished instantly and nothing was heard.
    /// </summary>
    private async Task<Exception?> SpeakViaSapi(string text, MagniVoice bestVoice, CancellationToken token, Stopwatch sw)
    {
        int sapiOffset = _charOffset;
        _isSpeaking = true;
        SpeechStarted?.Invoke();

        return await Task.Run<Exception?>(() =>
        {
            SapiSynth.SpeechSynthesizer synth;

            // Brief lock only to access/create the synth
            lock (_sapiLock)
            {
                if (_persistentSapiSynth == null || _currentSapiVoiceName != bestVoice.SapiVoiceName)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] SAPI5 creating new synth ({_currentSapiVoiceName} → {bestVoice.SapiVoiceName})");
                    _persistentSapiSynth?.Dispose();
                    _persistentSapiSynth = null;
                    _currentSapiVoiceName = null;
                    _sapiPaused = false;
                    try
                    {
                        var created = new SapiSynth.SpeechSynthesizer();
                        created.SelectVoice(bestVoice.SapiVoiceName!);
                        created.SetOutputToDefaultAudioDevice();
                        _persistentSapiSynth = created;
                        _currentSapiVoiceName = bestVoice.SapiVoiceName;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] SAPI5 synth ready in {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] SAPI5 reusing synth ({sw.ElapsedMilliseconds}ms)");
                }

                synth = _persistentSapiSynth;
                synth.Volume = Math.Clamp(_volume, 0, 100);
                synth.Rate = _rate;
            }

            if (token.IsCancellationRequested) return null;

            // NOTE: We intentionally do NOT subscribe to SpeakProgress.
            // NaturalVoiceSAPIAdapter can report negative CharacterPosition values,
            // which crashes System.Speech internally (String.Substring with negative index).
            // This crash happens on a ThreadPool thread and is unrecoverable.
            // Instead, we use timer-based word tracking (same as WinRT path).

            // Subscribe BEFORE SpeakAsync: a broken voice can complete immediately.
            // Results are keyed by prompt so a cancelled earlier prompt is ignored.
            var completions = new System.Collections.Concurrent.ConcurrentDictionary<SapiSynth.Prompt, Exception?>();
            using var doneEvent = new ManualResetEventSlim(false);
            void onDone(object? s, SapiSynth.SpeakCompletedEventArgs e)
            {
                completions[e.Prompt] = e.Error;
                doneEvent.Set();
            }
            synth.SpeakCompleted += onDone;

            // Real word timings for SAPI voices: a bookmark sits before each word
            // and SAPI raises BookmarkReached when the AUDIO reaches it.
            // (BookmarkReached doesn't have the negative-position crash that
            // SpeakProgress has with NaturalVoiceSAPIAdapter.)
            SapiSynth.Prompt? promptForBookmarks = null;
            List<(int start, int length)>? bookmarkWordsRef = null;
            _sapiGotBookmark = false;
            _sapiWordIndex = -1;
            void onBookmark(object? s, SapiSynth.BookmarkReachedEventArgs e)
            {
                try
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(e.Prompt, promptForBookmarks)) return;
                    var words = bookmarkWordsRef;
                    if (words == null || !e.Bookmark.StartsWith("w", StringComparison.Ordinal)) return;
                    if (!int.TryParse(e.Bookmark.AsSpan(1), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out int i) || i < 0 || i >= words.Count) return;

                    if (!_sapiGotBookmark)
                    {
                        _sapiGotBookmark = true;
                        StopWordTimer(); // real timings from now on
                        LogTrace("[Speech] Word timing: SAPI bookmarks (real)");
                    }
                    _sapiWordIndex = i;
                    var (charPos, charLen) = words[i];
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_isSpeaking && !token.IsCancellationRequested)
                            RaiseWordReached(sapiOffset + charPos, charLen);
                    });
                }
                catch { /* never let an event handler crash the speech thread */ }
            }
            synth.BookmarkReached += onBookmark;

            try
            {
                // Estimated word timings drive the highlight until the voice
                // reports its first bookmark (some SAPI voices don't support them).
                BuildEstimatedWordTimings(text);
                var bookmarkWords = TokenizeWords(text);
                bookmarkWordsRef = bookmarkWords;
                // For rewind / forward: SAPI can't seek, so it restarts from a word
                _sapiEstimate = _wordTimings;
                Interlocked.Exchange(ref _sapiSeekToWord, -1);
                _sapiWake = doneEvent;

                LogTrace(
                    $"[Speech] SAPI5 SpeakAsync at {sw.ElapsedMilliseconds}ms");
                SapiSynth.Prompt prompt;
                try
                {
                    prompt = synth.SpeakAsync(BuildBookmarkedPrompt(text, bookmarkWords, synth));
                }
                catch (Exception ex)
                {
                    return ex;
                }
                promptForBookmarks = prompt;
                _route = PlaybackRoute.Sapi;
                if (_isPaused) SetSapiPaused(true); // paused before the voice started

                // Start timer-based word tracking
                _useMediaPlayerForWordTimer = false;
                StartWordTimer();

                // Notify with estimated duration (no WAV stream for SAPI5)
                double sapiDurationMs = text.Length * 70.0;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () => PlaybackStarted?.Invoke(sapiDurationMs));

                // Event-based wait (responds instantly vs Thread.Sleep polling)
                Exception? error = null;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (completions.TryGetValue(prompt, out error)) break;

                        // Rewind / forward: SAPI can't seek inside a prompt, so the
                        // current prompt is dropped and a new one starts at the target word.
                        int seekTo = Interlocked.Exchange(ref _sapiSeekToWord, -1);
                        if (seekTo >= 0 && seekTo < bookmarkWords.Count)
                        {
                            LogTrace($"[Speech] SAPI5 restarting at word {seekTo + 1}/{bookmarkWords.Count}");
                            bool wasPaused = _sapiPaused;
                            // A paused synthesizer doesn't finish a cancel until it resumes
                            if (wasPaused) SetSapiPaused(false);
                            try { synth.SpeakAsyncCancel(prompt); } catch { }
                            try
                            {
                                prompt = synth.SpeakAsync(BuildBookmarkedPrompt(text, bookmarkWords, synth, seekTo));
                            }
                            catch (Exception ex)
                            {
                                error = ex;
                                break;
                            }
                            promptForBookmarks = prompt;
                            if (wasPaused) SetSapiPaused(true);

                            _sapiWordIndex = seekTo;
                            var estimate = _sapiEstimate;
                            if (estimate != null && seekTo < estimate.Count)
                                _clock.Set(estimate[seekTo].offset); // estimated highlight follows
                            var (seekPos, seekLen) = bookmarkWords[seekTo];
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                if (_isSpeaking && !token.IsCancellationRequested)
                                    RaiseWordReached(sapiOffset + seekPos, seekLen);
                            });
                            continue;
                        }

                        doneEvent.Wait(100, token);
                        doneEvent.Reset();
                    }
                }
                catch (OperationCanceledException) { }

                if (token.IsCancellationRequested)
                {
                    LogTrace($"[Speech] SAPI5 cancelled at {sw.ElapsedMilliseconds}ms");
                    try { synth.SpeakAsyncCancelAll(); } catch { }
                    return null;
                }

                LogTrace($"[Speech] SAPI5 finished at {sw.ElapsedMilliseconds}ms" +
                      (error != null ? $" with error: {error.Message}" : ""));
                if (error != null) return error;

                // Finished normally
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_isSpeaking && !token.IsCancellationRequested)
                    {
                        _isSpeaking = false;
                        SpeechCompleted?.Invoke();
                    }
                });
                return null;
            }
            finally
            {
                _sapiWake = null;
                synth.SpeakCompleted -= onDone;
                synth.BookmarkReached -= onBookmark;
                StopWordTimer();
            }
        }, token);
    }

    // ─── Pause / rewind / forward ───────────────────────────────

    /// <summary>
    /// Pauses or resumes the current reading. Works on every route; a pause asked for
    /// while the voice is still being prepared is applied when audio starts.
    /// Other apps' audio comes back up while paused. Call on the UI thread.
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (!_isSpeaking || paused == _isPaused) return;
        LogTrace($"[Speech] {(paused ? "Pause" : "Resume")} ({_route})");

        // The speed was changed while paused: continue from the same word at the new speed
        if (!paused && _restartPending)
        {
            RestartAtCurrentWord();
            return;
        }

        switch (_route)
        {
            case PlaybackRoute.Kokoro:
                _kokoroControl?.SetPaused(paused);
                break;
            case PlaybackRoute.MediaPlayer:
                try { if (paused) _mediaPlayer?.Pause(); else _mediaPlayer?.Play(); } catch { }
                break;
            case PlaybackRoute.NAudio:
                try { if (paused) _waveOut?.Pause(); else _waveOut?.Play(); } catch { }
                break;
            case PlaybackRoute.Sapi:
                SetSapiPaused(paused);
                break;
        }

        if (paused) _clock.Pause(); else _clock.Resume();
        if (paused) Ducker.ScheduleRestore(); else Ducker.Duck();
        SetPausedFlag(paused);
    }

    public void TogglePause() => SetPaused(!_isPaused);

    /// <summary>
    /// Jumps forward (positive) or back (negative) in the current text.
    ///   - Kokoro, Windows voices through MediaPlayer/NAudio: exact, on the audio itself.
    ///   - SAPI voices can't seek: speech restarts at the word estimated to be that many
    ///     seconds away, so the jump is approximate.
    /// Back stops at the start of the text; forward past its end moves on to the next
    /// paragraph, like when it finishes. Call on the UI thread.
    /// </summary>
    public void SkipSeconds(double seconds)
    {
        if (!_isSpeaking || seconds == 0) return;
        LogTrace($"[Speech] Skip {seconds:+0;-0}s ({_route})");

        switch (_route)
        {
            case PlaybackRoute.Kokoro:
                if (_kokoroControl?.SeekBy(seconds) == true) FinishEarly();
                break;

            case PlaybackRoute.MediaPlayer:
                try
                {
                    var session = _mediaPlayer?.PlaybackSession;
                    if (session == null) return;
                    var target = session.Position + TimeSpan.FromSeconds(seconds);
                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    var duration = session.NaturalDuration;
                    if (duration > TimeSpan.Zero && target >= duration) { FinishEarly(); return; }
                    session.Position = target;
                }
                catch (Exception ex) { LogTrace($"[Speech] MediaPlayer seek failed: {ex.Message}"); }
                break;

            case PlaybackRoute.NAudio:
                try
                {
                    var reader = _currentReader;
                    if (reader == null) return;
                    var target = _clock.Now + TimeSpan.FromSeconds(seconds);
                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    if (target >= reader.TotalTime) { FinishEarly(); return; }
                    reader.CurrentTime = target;
                    _clock.Set(target);
                }
                catch (Exception ex) { LogTrace($"[Speech] NAudio seek failed: {ex.Message}"); }
                break;

            case PlaybackRoute.Sapi:
                SeekSapi(seconds);
                break;
        }
    }

    private void SeekSapi(double seconds)
    {
        var estimate = _sapiEstimate;
        if (estimate == null || estimate.Count == 0) return;

        // Where we are on the estimated timeline: the last bookmark the voice reached,
        // or the estimate clock for voices without bookmarks.
        int current = _sapiGotBookmark ? _sapiWordIndex : -1;
        TimeSpan now = current >= 0 && current < estimate.Count
            ? estimate[current].offset
            : _clock.Now;
        var target = now + TimeSpan.FromSeconds(seconds);

        if (target > estimate[^1].offset)
        {
            if (seconds > 0) FinishEarly();
            return;
        }

        int index = 0;
        for (int i = 0; i < estimate.Count; i++)
        {
            if (estimate[i].offset <= target) index = i;
            else break;
        }
        // The jump must move at least one word, even when the estimate is coarse
        if (seconds < 0 && current >= 0 && index >= current) index = Math.Max(0, current - 1);
        if (seconds > 0 && current >= 0 && index <= current)
        {
            if (current + 1 >= estimate.Count) { FinishEarly(); return; }
            index = current + 1;
        }

        Interlocked.Exchange(ref _sapiSeekToWord, index);
        try { _sapiWake?.Set(); } catch (ObjectDisposedException) { }
    }

    private void SetSapiPaused(bool paused)
    {
        try
        {
            lock (_sapiLock)
            {
                var synth = _persistentSapiSynth;
                if (synth == null || paused == _sapiPaused) return;
                if (paused) synth.Pause(); else synth.Resume();
                _sapiPaused = paused;
            }
        }
        catch { }
    }

    /// <summary>Ends the current text as if it had finished, so the next one is read.</summary>
    private void FinishEarly()
    {
        // Cancel() raises SpeechCompleted, which moves the reading on to the next
        // paragraph (StopReading clears its own flag first, so it isn't affected).
        Cancel();
    }

    private void SetPausedFlag(bool paused)
    {
        if (_isPaused == paused) return;
        _isPaused = paused;
        PauseChanged?.Invoke(paused);
    }

    /// <summary>A stopwatch that can be paused and moved, for routes with no player position.</summary>
    private sealed class PlaybackClock
    {
        private readonly object _lock = new();
        private readonly Stopwatch _sw = new();
        private TimeSpan _base;

        public TimeSpan Now { get { lock (_lock) return _base + _sw.Elapsed; } }

        public void Restart(TimeSpan at, bool paused)
        {
            lock (_lock) { _base = at; _sw.Reset(); if (!paused) _sw.Start(); }
        }

        public void Set(TimeSpan at)
        {
            lock (_lock) { _base = at; bool running = _sw.IsRunning; _sw.Reset(); if (running) _sw.Start(); }
        }

        public void Pause() { lock (_lock) _sw.Stop(); }
        public void Resume() { lock (_lock) _sw.Start(); }
    }

    // ─── Cancel / Cleanup ────────────────────────────────────────

    public void Cancel()
    {
        if (_isSpeaking)
            LogTrace("[Speech] Cancel() while speaking, called from: " +
                  string.Join(" <- ", new StackTrace(1, false).GetFrames().Take(3)
                      .Select(f => f.GetMethod()?.Name ?? "?")));
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        StopWordTimer();

        // Reset MediaPlayer (keep alive for reuse)
        ResetMediaPlayer();

        // Stop NAudio WaveOut
        try { _waveOut?.Stop(); }
        catch { }

        CleanupStreamResources();

        // Cancel SAPI5 (a paused synthesizer only finishes the cancel once resumed)
        try { _persistentSapiSynth?.SpeakAsyncCancelAll(); }
        catch { }
        SetSapiPaused(false);

        _route = PlaybackRoute.None;
        _kokoroControl = null;
        if (!_restarting) CancelRestart();
        SetPausedFlag(false);

        if (_isSpeaking)
        {
            _isSpeaking = false;
            if (!_restarting) SpeechCompleted?.Invoke();
        }

        // Stopped (or replaced by a new Speak, which ducks again right after this).
        Ducker.ScheduleRestore();
    }

    /// <summary>
    /// Resets the persistent MediaPlayer without disposing it (reuse optimization).
    /// </summary>
    private void ResetMediaPlayer()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
            }
        }
        catch { }
    }

    /// <summary>
    /// Fully disposes the MediaPlayer. Only called at app shutdown.
    /// </summary>
    private void DisposeMediaPlayer()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }
        }
        catch { }
    }

    private void CleanupStreamResources()
    {
        try { _currentReader?.Dispose(); } catch { }
        try { _currentStream?.Dispose(); } catch { }
        _currentReader = null;
        _currentStream = null;
    }

    /// <summary>
    /// Sets the speaking rate on the settings scale -10..10 (0 = normal).
    /// WinRT uses SpeakingRate 0.5..3.0; SAPI uses -10..10 directly.
    /// </summary>
    public void SetRate(int rate)
    {
        int old = _rate;
        _rate = Math.Clamp(rate, -10, 10);
        if (_rate != old && _isSpeaking) ScheduleRestart();
        try
        {
            _synth.Options.SpeakingRate = _rate >= 0 ? 1.0 + _rate * 0.2 : 1.0 + _rate * 0.05;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Speech] SetRate (WinRT) failed: {ex.Message}");
        }
        // SAPI rate is applied at the start of each SAPI reading.
    }

    // ─── Live speed change ───────────────────────────────────────

    /// <summary>Speed presets of the reading strip's speed button, in the order it cycles.</summary>
    public static readonly double[] SpeedPresets = { 1.0, 1.25, 1.5, 2.0, 0.75 };

    private enum RateScale { Kokoro, WinRT, Sapi }

    /// <summary>The voice whose rate scale applies: the one reading now, else the selected one.</summary>
    private RateScale CurrentScale => _route switch
    {
        PlaybackRoute.Kokoro => RateScale.Kokoro,
        PlaybackRoute.Sapi => RateScale.Sapi,
        PlaybackRoute.None => _kokoroEnabled ? RateScale.Kokoro : RateScale.WinRT,
        _ => RateScale.WinRT,
    };

    private static double Multiplier(RateScale scale, int rate) => scale switch
    {
        RateScale.Kokoro => rate >= 0 ? 1 + rate * 0.1 : 1 + rate * 0.05,
        RateScale.WinRT => rate >= 0 ? 1 + rate * 0.2 : 1 + rate * 0.05,
        _ => Math.Pow(3.0, rate / 10.0), // SAPI: about 3x at the extremes
    };

    /// <summary>
    /// How much faster than normal the settings <paramref name="rate"/> (-10..10) is with
    /// the current voice, e.g. 1.5 for "1.5x". Each voice type maps the rate differently.
    /// </summary>
    public double SpeedMultiplier(int rate) => Multiplier(CurrentScale, Math.Clamp(rate, -10, 10));

    /// <summary>
    /// The settings rate (-10..10) for the next speed preset after <paramref name="rate"/>
    /// with the current voice (1x → 1.25x → 1.5x → 2x → 0.75x → 1x). Presets the rate scale
    /// can't hit exactly use the nearest rate.
    /// </summary>
    public int NextPresetRate(int rate)
    {
        var scale = CurrentScale;
        rate = Math.Clamp(rate, -10, 10);
        double now = Multiplier(scale, rate);

        // Which preset are we on (nearest), then the one after it
        int at = 0;
        for (int i = 1; i < SpeedPresets.Length; i++)
            if (Math.Abs(SpeedPresets[i] - now) < Math.Abs(SpeedPresets[at] - now)) at = i;

        for (int step = 1; step <= SpeedPresets.Length; step++)
        {
            double target = SpeedPresets[(at + step) % SpeedPresets.Length];
            int best = 0;
            for (int r = -10; r <= 10; r++)
                if (Math.Abs(Multiplier(scale, r) - target) < Math.Abs(Multiplier(scale, best) - target)) best = r;
            if (best != rate) return best;
        }
        return rate;
    }

    private void RaiseWordReached(int pos, int len)
    {
        _lastWordPos = pos;
        WordReached?.Invoke(pos, len);
    }

    /// <summary>
    /// Restarts the reading at the current word a moment after the last rate change
    /// (or on resume, when paused). Windows voices and Kokoro both take the rate when
    /// they synthesize, so a new speed needs the rest of the text spoken again.
    /// </summary>
    private void ScheduleRestart()
    {
        _restartPending = true;
        if (_isPaused) return;
        if (_restartTimer == null)
        {
            _restartTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _restartTimer.Tick += (_, _) =>
            {
                _restartTimer!.Stop();
                if (_restartPending && _isSpeaking && !_isPaused) RestartAtCurrentWord();
            };
        }
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private void CancelRestart()
    {
        _restartPending = false;
        _restartTimer?.Stop();
    }

    private void RestartAtCurrentWord()
    {
        int from = _lastWordPos >= _charOffset ? _lastWordPos : _charOffset;
        LogTrace($"[Speech] Speed changed: restarting at char {from}/{_fullText.Length} (rate {_rate})");
        _restarting = true;
        try { SpeakFrom(from); }
        finally { _restarting = false; }
    }

    /// <summary>
    /// Speech volume, 0–200%. Kokoro can be boosted above 100% (its samples are amplified
    /// with a soft limiter). Windows voices can't go above their own full volume, so for
    /// them anything over 100% plays at 100%.
    /// </summary>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 200);
        _kokoro.Volume = _volume / 100f;
        // SAPI volume is applied at the start of each SAPI reading.
        if (_waveOut != null)
            _waveOut.Volume = (float)SystemVoiceVolume;
        if (_mediaPlayer != null)
            _mediaPlayer.Volume = SystemVoiceVolume;
    }

    /// <summary>Volume for Windows voices (MediaPlayer / NAudio), 0.0–1.0.</summary>
    private double SystemVoiceVolume => Math.Clamp(_volume, 0, 100) / 100.0;

    // ─── Word Timing ────────────────────────────────────────────

    /// <summary>
    /// Builds estimated word timings for the SAPI5 path, used until the voice
    /// reports bookmarks. ~60ms per character at normal rate, scaled by the rate.
    /// </summary>
    private void BuildEstimatedWordTimings(string text)
    {
        // SAPI rate -10..10 is roughly a 3x slowdown/speedup at the extremes
        double rateFactor = Math.Pow(3.0, -_rate / 10.0);
        _wordTimings = EstimateWordTimings(text, TimeSpan.FromMilliseconds(text.Length * 60.0 * rateFactor));
        LogTrace("[Speech] Word timing: estimated (until SAPI bookmarks arrive)");
    }

    /// <summary>Splits text into words (runs of non-whitespace): (start, length).</summary>
    private static List<(int start, int length)> TokenizeWords(string text)
    {
        var words = new List<(int, int)>();
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= text.Length) break;
            int start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
            words.Add((start, pos - start));
        }
        return words;
    }

    /// <summary>
    /// Builds a SAPI prompt with a bookmark "w{index}" before every word.
    /// The whitespace after each word is kept so the voice reads the text
    /// exactly as before. Uses the voice's own culture so System.Speech
    /// doesn't switch to another voice.
    /// </summary>
    private static SapiSynth.PromptBuilder BuildBookmarkedPrompt(
        string text, List<(int start, int length)> words, SapiSynth.SpeechSynthesizer synth, int fromWord = 0)
    {
        CultureInfo culture;
        try { culture = synth.Voice?.Culture ?? CultureInfo.CurrentUICulture; }
        catch { culture = CultureInfo.CurrentUICulture; }

        var pb = new SapiSynth.PromptBuilder(culture);
        for (int i = Math.Max(0, fromWord); i < words.Count; i++)
        {
            int start = words[i].start;
            int end = i + 1 < words.Count ? words[i + 1].start : text.Length;
            pb.AppendBookmark("w" + i.ToString(CultureInfo.InvariantCulture));
            pb.AppendText(text.Substring(start, end - start));
        }
        return pb;
    }

    /// <summary>
    /// Estimates when each word starts when the voice gives no word timings.
    /// Each word "costs" its number of letters, plus a pause after commas and
    /// a longer one after sentence ends, spread over <paramref name="totalDuration"/>.
    /// Much closer than spreading by raw character position.
    /// </summary>
    private static List<(int charPos, int charLen, TimeSpan offset)> EstimateWordTimings(string text, TimeSpan totalDuration)
    {
        var words = TokenizeWords(text);
        var weights = new double[words.Count];
        double total = 0;
        for (int i = 0; i < words.Count; i++)
        {
            var (start, length) = words[i];
            double w = 0;
            for (int k = start; k < start + length; k++)
                if (char.IsLetterOrDigit(text[k])) w++;
            w = Math.Max(1.5, w);

            char last = text[start + length - 1];
            if (last is ',' or ';' or ':' or ')' ) w += 3;
            else if (last is '.' or '!' or '?' or '…') w += 6;

            // Line breaks are read as a pause too
            int next = start + length;
            if (next < text.Length && text.IndexOf('\n', next, Math.Min(4, text.Length - next)) >= 0) w += 4;

            weights[i] = w;
            total += w;
        }

        var timings = new List<(int, int, TimeSpan)>(words.Count);
        double acc = 0;
        for (int i = 0; i < words.Count; i++)
        {
            double fraction = total > 0 ? acc / total : 0;
            timings.Add((words[i].start, words[i].length, TimeSpan.FromTicks((long)(totalDuration.Ticks * fraction))));
            acc += weights[i];
        }
        return timings;
    }

    /// <summary>
    /// Reads the real word boundaries from the WinRT synthesis result
    /// (requires Options.IncludeWordBoundaryMetadata). Each SpeechCue gives the
    /// word's character range in the input text and when it is spoken.
    /// Falls back to an estimate if the voice provides no cues.
    /// </summary>
    private void ExtractWordTimings(string text, SpeechSynthesisStream result)
    {
        var timings = new List<(int charPos, int charLen, TimeSpan offset)>();
        try
        {
            foreach (var track in result.TimedMetadataTracks)
            {
                if (!string.Equals(track.Id, "SpeechWord", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var cue in track.Cues)
                {
                    if (cue is not SpeechCue sc || sc.StartPositionInInput is not int start)
                        continue;
                    int end = sc.EndPositionInInput ?? start;
                    if (start < 0 || start >= text.Length) continue;
                    // EndPositionInInput is inclusive
                    int len = Math.Max(1, Math.Min(text.Length, end + 1) - start);
                    timings.Add((start, len, sc.StartTime));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Speech] Reading word cues failed: {ex.Message}");
            timings.Clear();
        }

        if (timings.Count > 0)
        {
            timings.Sort((a, b) => a.offset.CompareTo(b.offset));
            LogTrace($"[Speech] Word timing: {timings.Count} word cues from the voice (real)");
            _wordTimings = timings;
            return;
        }

        // No cues: estimate from the real audio length
        LogTrace("[Speech] Word timing: voice gave no word cues, estimating from audio length");
        double durationMs = GetStreamDurationMs(result);
        var total = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.FromSeconds(text.Length * 0.06);
        _wordTimings = EstimateWordTimings(text, total);
    }

    /// <summary>
    /// Raises WordReached each time speech moves on to a new word.
    /// With the MediaPlayer path the real playback position is used, so the
    /// highlight stays in sync even if audio output starts late or stutters.
    /// Otherwise a clock that stops while paused and jumps on rewind / forward is used.
    /// The word is looked up from the position every tick, so it can move backwards.
    /// </summary>
    private void StartWordTimer()
    {
        StopWordTimer();
        var timings = _wordTimings;
        _clock.Restart(TimeSpan.Zero, paused: _isPaused);
        if (timings.Count == 0) return;

        bool useMediaPlayer = _useMediaPlayerForWordTimer;
        int offset = _charOffset;
        int currentIndex = -1;

        _wordTimer = new System.Threading.Timer(_ =>
        {
            if (!_isSpeaking) return;

            TimeSpan elapsed = _clock.Now;
            if (useMediaPlayer)
            {
                try
                {
                    var session = _mediaPlayer?.PlaybackSession;
                    if (session != null) elapsed = session.Position;
                }
                catch { /* keep the clock */ }
            }

            // Last word that starts at or before the position
            int lo = 0, hi = timings.Count - 1, index = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (timings[mid].offset <= elapsed) { index = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            // Don't highlight the first word before it is actually spoken
            if (index < 0 || index == currentIndex) return;
            currentIndex = index;

            var (charPos, charLen, _) = timings[index];
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_isSpeaking) RaiseWordReached(offset + charPos, charLen);
            });
        }, null, 0, 40);
    }

    private void StopWordTimer()
    {
        _wordTimer?.Dispose();
        _wordTimer = null;
    }

    public void Dispose()
    {
        Cancel();
        DisposeMediaPlayer();

        try { _waveOut?.Dispose(); } catch { }
        _waveOut = null;

        try
        {
            lock (_sapiLock)
            {
                _persistentSapiSynth?.Dispose();
                _persistentSapiSynth = null;
            }
        }
        catch { }

        try { _kokoro.Dispose(); } catch { }

        Ducker.Dispose(); // puts other apps' volume back right away

        _synth = null!;
    }
}
