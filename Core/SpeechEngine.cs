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
    private List<(int charPos, int charLen, TimeSpan offset)> _wordTimings = new();
    private System.Threading.Timer? _wordTimer;
    private bool _useMediaPlayerForWordTimer; // tracks which player is active

    // Volume (0-100)
    private int _volume = 100;

    public event Action<int, int>? WordReached;
    public event Action? SpeechCompleted;
    public event Action? SpeechStarted;

    public bool IsSpeaking => _isSpeaking;

    public SpeechEngine(VoiceManager voiceManager)
    {
        _voiceManager = voiceManager;
        _synth = new SpeechSynthesizer();
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

        // Fallback: warm up SAPI5 path
        if (bestVoice?.SapiVoiceName != null)
        {
            await Task.Run(() =>
            {
                lock (_sapiLock)
                {
                    _persistentSapiSynth = new SapiSynth.SpeechSynthesizer();
                    _persistentSapiSynth.SelectVoice(bestVoice.SapiVoiceName);
                    _persistentSapiSynth.SetOutputToDefaultAudioDevice();
                    _currentSapiVoiceName = bestVoice.SapiVoiceName;
                    _persistentSapiSynth.Speak(" ");
                }
            });
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
    public async void Speak(string text, CultureInfo culture)
    {
        if (_isSpeaking) Cancel();

        _currentText = text;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var sw = Stopwatch.StartNew();

        try
        {
            var bestVoice = _voiceManager.GetBestVoiceForLanguage(culture);

            // STRATEGY: Prefer WinRT for lower latency.
            // WinRT synthesis goes through the Windows OneCore speech service —
            // the same engine used by Narrator / "Acceso directo a lectura".
            // SAPI5 + NaturalVoiceSAPIAdapter adds an adapter layer with higher latency.

            // 1. If the voice has a direct WinRT handle, use WinRT
            if (bestVoice?.WinRTVoice != null)
            {
                System.Diagnostics.Debug.WriteLine(
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
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] Route: WinRT equivalent ({winrtEquivalent.DisplayName}) for SAPI5 voice ({bestVoice.SapiVoiceName})");
                    await SpeakViaWinRT(text, winrtEquivalent, token, sw);
                    return;
                }

                // No WinRT equivalent → use SAPI5
                System.Diagnostics.Debug.WriteLine(
                    $"[Speech] Route: SAPI5 (no WinRT equivalent) → {bestVoice.SapiVoiceName}");
                await SpeakViaSapi(text, bestVoice, token, sw);
                return;
            }

            // 3. Fallback: use default WinRT voice
            System.Diagnostics.Debug.WriteLine("[Speech] Route: WinRT default voice");
            await SpeakViaWinRT(text, null, token, sw);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError($"Speech failed for '{text[..Math.Min(50, text.Length)]}...'", ex);
            _isSpeaking = false;
            SpeechCompleted?.Invoke();
        }
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

        // Extract word timings without blocking playback
        _ = Task.Run(() => ExtractWordTimings(text, result));

        // Try native MediaPlayer first (lower latency than NAudio)
        try
        {
            PlayViaNativeMediaPlayer(result, token, sw);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Speech] MediaPlayer failed ({ex.Message}), falling back to NAudio");
            PlayViaNAudio(result, token, sw);
        }
    }

    /// <summary>
    /// Plays SpeechSynthesisStream via native Windows MediaPlayer.
    /// This is the same playback pipeline Windows Narrator uses.
    /// </summary>
    private void PlayViaNativeMediaPlayer(SpeechSynthesisStream stream, CancellationToken token, Stopwatch sw)
    {
        CleanupMediaPlayer();

        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.Volume = _volume / 100.0;
        _useMediaPlayerForWordTimer = true;

        var source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _mediaPlayer.Source = source;

        _mediaPlayer.MediaEnded += (s, e) =>
        {
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
            System.Diagnostics.Debug.WriteLine(
                $"[Speech] MediaPlayer playback failed: {e.Error}");
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

        if (token.IsCancellationRequested) return;

        System.Diagnostics.Debug.WriteLine(
            $"[Speech] MediaPlayer starting playback at {sw.ElapsedMilliseconds}ms");

        StartWordTimer();
        _mediaPlayer.Play();
    }

    /// <summary>
    /// Fallback: plays SpeechSynthesisStream via NAudio WaveOutEvent.
    /// </summary>
    private void PlayViaNAudio(SpeechSynthesisStream result, CancellationToken token, Stopwatch sw)
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

        _waveOut.Volume = _volume / 100f;
        _waveOut.Init(_currentReader);

        if (token.IsCancellationRequested) return;

        System.Diagnostics.Debug.WriteLine(
            $"[Speech] NAudio starting playback at {sw.ElapsedMilliseconds}ms");

        StartWordTimer();
        _waveOut.Play();
    }

    private void OnWaveOutPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_isSpeaking)
            {
                _isSpeaking = false;
                StopWordTimer();
                SpeechCompleted?.Invoke();
            }
        });
    }

    // ─── SAPI5 Synthesis ────────────────────────────────────────

    private async Task SpeakViaSapi(string text, MagniVoice bestVoice, CancellationToken token, Stopwatch sw)
    {
        _isSpeaking = true;
        SpeechStarted?.Invoke();

        await Task.Run(() =>
        {
            // Brief lock only to access/create the synth
            lock (_sapiLock)
            {
                if (_persistentSapiSynth == null || _currentSapiVoiceName != bestVoice.SapiVoiceName)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] SAPI5 creating new synth ({_currentSapiVoiceName} → {bestVoice.SapiVoiceName})");
                    _persistentSapiSynth?.Dispose();
                    _persistentSapiSynth = new SapiSynth.SpeechSynthesizer();
                    _persistentSapiSynth.SelectVoice(bestVoice.SapiVoiceName!);
                    _persistentSapiSynth.SetOutputToDefaultAudioDevice();
                    _currentSapiVoiceName = bestVoice.SapiVoiceName;
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] SAPI5 synth ready in {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Speech] SAPI5 reusing synth ({sw.ElapsedMilliseconds}ms)");
                }
            }

            // NOTE: We intentionally do NOT subscribe to SpeakProgress.
            // NaturalVoiceSAPIAdapter can report negative CharacterPosition values,
            // which crashes System.Speech internally (String.Substring with negative index).
            // This crash happens on a ThreadPool thread and is unrecoverable.
            // Instead, we use timer-based word tracking (same as WinRT path).
            _persistentSapiSynth!.SpeakCompleted += OnSapiSpeakCompleted;

            if (token.IsCancellationRequested) return;

            // Build estimated word timings for timer-based highlighting
            BuildEstimatedWordTimings(text);

            System.Diagnostics.Debug.WriteLine(
                $"[Speech] SAPI5 SpeakAsync at {sw.ElapsedMilliseconds}ms");
            _persistentSapiSynth.SpeakAsync(text);

            // Start timer-based word tracking
            _useMediaPlayerForWordTimer = false;
            StartWordTimer();

            // Event-based wait (responds instantly vs Thread.Sleep polling)
            using var doneEvent = new ManualResetEventSlim(false);
            void onDone(object? s, SapiSynth.SpeakCompletedEventArgs e) => doneEvent.Set();
            _persistentSapiSynth.SpeakCompleted += onDone;

            try
            {
                while (_isSpeaking && !token.IsCancellationRequested)
                {
                    if (doneEvent.Wait(100, token)) break;
                }
            }
            catch (OperationCanceledException) { }

            _persistentSapiSynth.SpeakCompleted -= onDone;

            if (token.IsCancellationRequested)
                _persistentSapiSynth.SpeakAsyncCancelAll();

            _persistentSapiSynth.SpeakCompleted -= OnSapiSpeakCompleted;
            StopWordTimer();
        }, token);
    }


    private void OnSapiSpeakCompleted(object? sender, SapiSynth.SpeakCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_isSpeaking)
            {
                _isSpeaking = false;
                SpeechCompleted?.Invoke();
            }
        });
    }

    // ─── Cancel / Cleanup ────────────────────────────────────────

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        StopWordTimer();

        // Stop MediaPlayer
        try { _mediaPlayer?.Pause(); }
        catch { }

        // Stop NAudio WaveOut
        try { _waveOut?.Stop(); }
        catch { }

        CleanupStreamResources();

        // Cancel SAPI5
        try { _persistentSapiSynth?.SpeakAsyncCancelAll(); }
        catch { }

        if (_isSpeaking)
        {
            _isSpeaking = false;
            SpeechCompleted?.Invoke();
        }
    }

    private void CleanupMediaPlayer()
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

    public void SetRate(int rate)
    {
        // WinRT SpeechSynthesizer doesn't have a direct Rate property.
        // Could use SSML prosody rate for WinRT, or SAPI5 Rate property.
    }

    public void SetVolume(int volume)
    {
        _volume = volume;
        if (_waveOut != null)
            _waveOut.Volume = volume / 100f;
        if (_mediaPlayer != null)
            _mediaPlayer.Volume = volume / 100.0;
    }

    // ─── Word Timing ────────────────────────────────────────────

    /// <summary>
    /// Builds estimated word timings for SAPI5 path (no stream markers available).
    /// Assumes ~60ms per character as a rough speech rate estimate.
    /// </summary>
    private void BuildEstimatedWordTimings(string text)
    {
        _wordTimings.Clear();
        var totalDuration = TimeSpan.FromSeconds(text.Length * 0.06);
        int totalChars = text.Length;
        int pos = 0;
        int charsSoFar = 0;

        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) { pos++; charsSoFar++; }
            if (pos >= text.Length) break;

            int wordStart = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
            int wordLen = pos - wordStart;

            double fraction = (double)charsSoFar / Math.Max(1, totalChars);
            var offset = TimeSpan.FromTicks((long)(totalDuration.Ticks * fraction));
            _wordTimings.Add((wordStart, wordLen, offset));
            charsSoFar = pos;
        }
    }

    private void ExtractWordTimings(string text, SpeechSynthesisStream result)
    {
        _wordTimings.Clear();

        var totalDuration = result.Markers.Count > 0
            ? result.Markers.Max(m => m.Time)
            : TimeSpan.FromSeconds(text.Length * 0.06);

        if (totalDuration == TimeSpan.Zero)
            totalDuration = TimeSpan.FromSeconds(text.Length * 0.06);

        int totalChars = text.Length;
        int pos = 0;
        int charsSoFar = 0;

        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) { pos++; charsSoFar++; }
            if (pos >= text.Length) break;

            int wordStart = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
            int wordLen = pos - wordStart;

            double fraction = (double)charsSoFar / Math.Max(1, totalChars);
            var offset = TimeSpan.FromTicks((long)(totalDuration.Ticks * fraction));

            _wordTimings.Add((wordStart, wordLen, offset));
            charsSoFar = pos;
        }
    }

    private void StartWordTimer()
    {
        if (_wordTimings.Count == 0) return;

        var startTime = DateTime.UtcNow;
        int currentIndex = 0;

        _wordTimer = new System.Threading.Timer(_ =>
        {
            if (!_isSpeaking) return;
            // Check if the active player is still alive
            if (_useMediaPlayerForWordTimer && _mediaPlayer == null) return;
            if (!_useMediaPlayerForWordTimer && _waveOut == null) return;

            var elapsed = DateTime.UtcNow - startTime;

            while (currentIndex < _wordTimings.Count - 1 &&
                   _wordTimings[currentIndex + 1].offset <= elapsed)
            {
                currentIndex++;
            }

            if (currentIndex < _wordTimings.Count)
            {
                var (charPos, charLen, _) = _wordTimings[currentIndex];
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    WordReached?.Invoke(charPos, charLen);
                });
            }
        }, null, 0, 80);
    }

    private void StopWordTimer()
    {
        _wordTimer?.Dispose();
        _wordTimer = null;
    }

    public void Dispose()
    {
        Cancel();
        CleanupMediaPlayer();

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

        _synth = null!;
    }
}
