using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MagniReader.Core;

/// <summary>A Kokoro voice the user can pick in Settings.</summary>
public record KokoroVoiceInfo(string Id, string LangCode, string DisplayName);

/// <summary>
/// Offline neural text-to-speech with Kokoro (82M parameters, ONNX Runtime on the CPU).
///
///   - The model (~330 MB) is downloaded once, only when the user asks for it in
///     Settings, to %APPDATA%\MagniReader\kokoro\kokoro.onnx.
///   - Voices (.npy style vectors) ship with the KokoroSharp package and are copied
///     next to the exe in "voices".
///   - Text is synthesized sentence by sentence: playback starts as soon as the first
///     sentence is ready while the next ones are generated in the background.
///   - Word timings come from the durations Kokoro predicts for each phoneme, so the
///     word-highlight box follows the voice.
///
/// Only English and Spanish voices are offered. Other languages (Catalan, …) keep
/// using the Windows voices.
/// </summary>
public sealed class KokoroSpeaker : IDisposable
{
    public const int SampleRate = 24000;

    /// <summary>The fp32 model used by KokoroSharp 0.8.x (has the "durations" output we need).</summary>
    public const string ModelUrl =
        "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";

    /// <summary>Shown to the user before downloading.</summary>
    public const int ApproxModelSizeMb = 330;

    public static string ModelDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagniReader", "kokoro");

    public static string ModelPath { get; } = Path.Combine(ModelDirectory, "kokoro.onnx");

    public static string VoicesDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voices");

    /// <summary>A real model is well over 50 MB; anything smaller is a broken download.</summary>
    public static bool IsModelDownloaded
    {
        get
        {
            try { return File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 50_000_000; }
            catch { return false; }
        }
    }

    public static KokoroSpeaker Shared { get; } = new();

    private KokoroModel? _model;
    private Task<bool>? _loadTask;
    private readonly object _loadLock = new();
    // Tokenizer (MisakiSharp) and the model are used by one reading at a time
    private readonly object _synthLock = new();
    private readonly ConcurrentDictionary<string, KokoroVoice> _voiceCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _previewCts;

    /// <summary>Volume 0..1, applied live to whatever is playing.</summary>
    public float Volume { get; set; } = 1f;

    public bool IsLoaded => _model != null;

    private KokoroSpeaker() { }

    // ─── Model download / load ──────────────────────────────────

    /// <summary>
    /// Downloads the model to <see cref="ModelPath"/>. Progress is 0..1.
    /// Writes to a .part file first so a cancelled download never looks complete.
    /// </summary>
    public static async Task DownloadModelAsync(IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(ModelDirectory);
        var partPath = ModelPath + ".part";

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? ApproxModelSizeMb * 1_000_000L;

        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            var lastReport = Stopwatch.StartNew();
            while ((n = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), token);
                read += n;
                if (lastReport.ElapsedMilliseconds > 150)
                {
                    progress?.Report(Math.Min(1.0, (double)read / total));
                    lastReport.Restart();
                }
            }
            if (read < 50_000_000)
                throw new IOException($"Download incomplete ({read / 1_000_000} MB).");
        }

        File.Move(partPath, ModelPath, overwrite: true);
        progress?.Report(1.0);
        Infrastructure.CrashLogger.LogInfo($"[Kokoro] Model downloaded to {ModelPath}");

        // A previous load attempt may have failed because the file was missing
        lock (Shared._loadLock) { if (Shared._model == null) Shared._loadTask = null; }
    }

    /// <summary>
    /// Loads the model in the background (once). Returns false if the model is not
    /// downloaded or failed to load. Safe to call repeatedly.
    /// </summary>
    public Task<bool> EnsureLoadedAsync()
    {
        if (!IsModelDownloaded) return Task.FromResult(false);
        lock (_loadLock)
        {
            _loadTask ??= Task.Run(Load);
            return _loadTask;
        }
    }

    private bool Load()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            var model = new KokoroModel(ModelPath, options);
            Log($"[Kokoro] Model loaded in {sw.ElapsedMilliseconds}ms");

            // Warm up the tokenizer (loads the English lexicon) and the model, so the
            // first real reading starts quickly.
            lock (_synthLock)
            {
                var voice = GetVoice("af_heart") ?? GetVoices().Select(v => GetVoice(v.Id)).FirstOrDefault(v => v != null);
                var tokens = Tokenizer.Tokenize("Hello.", "en-us");
                if (voice != null && tokens.Length > 0)
                    model.Infer(tokens, voice.Features, 1f);
            }
            _model = model;
            Log($"[Kokoro] Ready after {sw.ElapsedMilliseconds}ms (warm-up included)");
            return true;
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError("[Kokoro] Failed to load the model; Windows voices will be used", ex);
            return false;
        }
    }

    // ─── Voices ─────────────────────────────────────────────────

    /// <summary>
    /// English (US/UK) and Spanish voices found in the voices folder, best ones first.
    /// </summary>
    public static IReadOnlyList<KokoroVoiceInfo> GetVoices()
    {
        var list = new List<KokoroVoiceInfo>();
        try
        {
            if (!Directory.Exists(VoicesDirectory)) return list;
            foreach (var file in Directory.GetFiles(VoicesDirectory, "*.npy", SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (id.Length < 4 || id[2] != '_') continue;
                string? lang = id[0] switch { 'a' or 'b' => "en", 'e' => "es", _ => null };
                if (lang == null) continue;
                list.Add(new KokoroVoiceInfo(id, lang, MakeDisplayName(id)));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kokoro] Voice scan failed: {ex.Message}");
        }

        return list
            .OrderBy(v => v.LangCode)
            .ThenBy(v => Array.IndexOf(RecommendedOrder, v.Id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(v => v.Id)
            .ToList();
    }

    // Highest rated voices in the Kokoro v1.0 voice list come first
    private static readonly string[] RecommendedOrder =
    {
        "af_heart", "af_bella", "af_nicole", "am_michael", "am_fenrir", "bf_emma", "bm_george", "bm_fable",
        "ef_dora", "em_alex", "em_santa",
    };

    public const string DefaultEnglishVoice = "af_heart";
    public const string DefaultSpanishVoice = "ef_dora";

    private static string MakeDisplayName(string id)
    {
        string accent = id[0] switch { 'a' => "US", 'b' => "UK", 'e' => "Spanish", _ => "?" };
        string gender = id[1] == 'f' ? "female" : id[1] == 'm' ? "male" : "";
        var name = id[3..];
        name = name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : id;
        return $"{name} ({accent}, {gender})";
    }

    private KokoroVoice? GetVoice(string id)
    {
        if (_voiceCache.TryGetValue(id, out var v)) return v;
        var path = Path.Combine(VoicesDirectory, id + ".npy");
        if (!File.Exists(path)) return null;
        try
        {
            v = KokoroVoice.FromPath(path);
            _voiceCache[id] = v;
            return v;
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError($"[Kokoro] Could not load voice '{id}'", ex);
            return null;
        }
    }

    public static bool HasVoice(string? id) =>
        !string.IsNullOrEmpty(id) && File.Exists(Path.Combine(VoicesDirectory, id + ".npy"));

    // ─── Speaking ───────────────────────────────────────────────

    /// <summary>
    /// Speaks <paramref name="text"/> and returns when playback has finished.
    /// Throws OperationCanceledException when cancelled, or another exception if
    /// synthesis fails (check whether <paramref name="onAudioStarted"/> was called to
    /// know if anything was heard).
    /// </summary>
    /// <param name="onWord">(charPos, charLen) in <paramref name="text"/>, called from a timer thread.</param>
    /// <param name="onAudioStarted">Called when audio starts, with an estimated total duration in ms.</param>
    public async Task<bool> SpeakAsync(string text, string voiceId, float speed,
        Action<int, int>? onWord, Action<double>? onAudioStarted, CancellationToken token)
    {
        var model = _model ?? throw new InvalidOperationException("Kokoro model is not loaded.");
        var voice = GetVoice(voiceId) ?? throw new FileNotFoundException($"Kokoro voice '{voiceId}' not found in {VoicesDirectory}");
        string langCode = voice.GetLangCode();
        speed = Math.Clamp(speed, 0.5f, 2.0f);
        var sw = Stopwatch.StartNew();

        var chunks = SplitIntoChunks(text);
        if (chunks.Count == 0) return false;

        var provider = new StreamingSampleProvider(SampleRate, () => Volume);
        // (sample offset in the content timeline, charPos, charLen)
        var timings = new List<(long sample, int pos, int len)>();
        var timingsLock = new object();

        using var waveOut = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waveOut.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) finished.TrySetException(e.Exception);
            else finished.TrySetResult();
        };

        System.Threading.Timer? wordTimer = null;
        using var registration = token.Register(() =>
        {
            try { waveOut.Stop(); } catch { }
            finished.TrySetCanceled(token);
        });

        try
        {
            bool started = false;
            await Task.Run(() =>
            {
                long enqueued = 0;
                for (int c = 0; c < chunks.Count; c++)
                {
                    token.ThrowIfCancellationRequested();
                    var (start, length) = chunks[c];

                    float[] samples;
                    List<(int pos, int len, double sec)> words;
                    try
                    {
                        lock (_synthLock)
                        {
                            token.ThrowIfCancellationRequested();
                            (samples, words) = SynthesizeChunk(model, text, start, length, voice, langCode, speed);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One sentence the phonemizer can't handle must not stop the whole
                        // reading (it used to cut the audio and jump to the next paragraph).
                        Infrastructure.CrashLogger.LogError(
                            $"[Kokoro] Skipped chunk {c + 1}/{chunks.Count} it could not synthesize: " +
                            $"\"{Shorten(text.Substring(start, length))}\"", ex);
                        continue;
                    }
                    if (samples.Length == 0) continue;

                    lock (timingsLock)
                    {
                        foreach (var (pos, len, sec) in words)
                            timings.Add((enqueued + (long)(sec * SampleRate), pos, len));
                    }
                    provider.Enqueue(samples);
                    enqueued += samples.Length;

                    if (!started)
                    {
                        token.ThrowIfCancellationRequested();
                        started = true;
                        waveOut.Init(new SampleToWaveProvider16(provider));
                        waveOut.Play();
                        Log($"[Kokoro] Playback started at {sw.ElapsedMilliseconds}ms " +
                            $"(first of {chunks.Count} sentence chunks, {samples.Length * 1000 / SampleRate}ms of audio)");

                        // Rough total for the progress animation: this chunk's pace × whole text
                        double estMs = (double)samples.Length / SampleRate * 1000.0 * text.Length / Math.Max(1, length);
                        onAudioStarted?.Invoke(estMs);
                        if (onWord != null)
                            wordTimer = StartWordTimer(waveOut, provider, timings, timingsLock, onWord);
                    }
                }
                provider.Complete();
                Log($"[Kokoro] All {chunks.Count} chunks synthesized at {sw.ElapsedMilliseconds}ms");
            }, token);

            if (!started)
            {
                Log($"[Kokoro] No speakable audio in {chunks.Count} chunks");
                return false;
            }
            await finished.Task;
            Log($"[Kokoro] Playback finished at {sw.ElapsedMilliseconds}ms");
            return true;
        }
        finally
        {
            wordTimer?.Dispose();
        }
    }

    /// <summary>Plays a short sample with the given voice (Settings ▶ button).</summary>
    public async Task PreviewAsync(string voiceId, string langCode, float speed = 1f)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        if (!await EnsureLoadedAsync()) throw new InvalidOperationException("The Kokoro model could not be loaded.");
        string sample = langCode == "es"
            ? "Hola, soy tu asistente de lectura. ¿Qué te parece esta voz?"
            : "Hello, I am your reading assistant. How does this voice sound?";
        try { await SpeakAsync(sample, voiceId, speed, null, null, cts.Token); }
        catch (OperationCanceledException) { }
    }

    private static System.Threading.Timer StartWordTimer(WaveOutEvent waveOut, StreamingSampleProvider provider,
        List<(long sample, int pos, int len)> timings, object timingsLock, Action<int, int> onWord)
    {
        int current = -1;
        long maxPos = 0;
        return new System.Threading.Timer(_ =>
        {
            try
            {
                // Bytes actually played (16-bit mono) minus silence inserted while waiting
                // for synthesis = position in the speech timeline.
                long pos = waveOut.GetPosition() / 2 - provider.SilenceSamples;
                if (pos < maxPos) pos = maxPos; // never move backwards
                maxPos = pos;

                int index = current;
                (long sample, int pos, int len) word = default;
                lock (timingsLock)
                {
                    while (index + 1 < timings.Count && timings[index + 1].sample <= pos) index++;
                    if (index >= 0) word = timings[index];
                }
                if (index == current || index < 0) return;
                current = index;
                onWord(word.pos, word.len);
            }
            catch { /* device gone or stopped; ignore */ }
        }, null, 0, 30);
    }

    // ─── Synthesis + word timing ────────────────────────────────

    private static readonly HashSet<int> SeparatorTokens = BuildSeparatorTokens();

    private static HashSet<int> BuildSeparatorTokens()
    {
        var set = new HashSet<int>();
        foreach (var ch in " ;:,.!?¡¿—…\"()“”«»\n")
            if (Tokenizer.Vocab.TryGetValue(ch, out var t) && t >= 0) set.Add(t);
        return set;
    }

    private static (float[] samples, List<(int pos, int len, double sec)> words) SynthesizeChunk(
        KokoroModel model, string text, int start, int length, KokoroVoice voice, string langCode, float speed)
    {
        var chunkText = text.Substring(start, length);
        // KokoroSharp turns curly apostrophes into quotes, which splits contractions
        // ("wouldn’t" was read "wouldn tee"). Use a straight apostrophe instead.
        int[] tokens = Tokenizer.Tokenize(NormalizeForSpeech(chunkText, langCode), langCode, preprocess: true);
        if (tokens.Length == 0) return (Array.Empty<float>(), new());
        if (tokens.Length > KokoroModel.maxTokens) tokens = tokens[..KokoroModel.maxTokens];

        float[] raw = model.Infer(tokens, voice.Features, speed, out int[] durations);
        if (raw.Length == 0) return (Array.Empty<float>(), new());

        var stamps = PhonemeTimestamp.FromModelOutput(tokens, durations, raw.Length);
        var trimmed = KokoroPlayback.PostProcessSamples(raw, out int skipped);
        double skippedSec = (double)skipped / SampleRate;
        double audioSec = (double)trimmed.Length / SampleRate;

        var words = AlignWords(chunkText, start, tokens, stamps, skippedSec, audioSec);

        // Natural pause after the chunk, like a reader would make
        char last = chunkText[^1];
        double pause = last is '.' or '!' or '?' or '…' or ':' ? 0.35
                     : last is ',' or ';' or '—' ? 0.15
                     : 0.08;
        int pauseSamples = (int)(pause / speed * SampleRate);
        var samples = new float[trimmed.Length + pauseSamples];
        Array.Copy(trimmed, samples, trimmed.Length);
        return (samples, words);
    }

    /// <summary>
    /// Maps each written word to the time it is spoken. The phonemes Kokoro speaks are
    /// split into word groups at spaces/punctuation; when there are as many groups as
    /// written words they match one to one. Otherwise (numbers, abbreviations and
    /// symbols get expanded) each word is placed proportionally by letter count.
    /// </summary>
    private static List<(int pos, int len, double sec)> AlignWords(
        string chunkText, int chunkStart, int[] tokens, PhonemeTimestamp[]? stamps, double skippedSec, double audioSec)
    {
        // Written words: runs of non-whitespace that contain a letter or digit
        var words = new List<(int start, int length, int letters)>();
        int p = 0;
        while (p < chunkText.Length)
        {
            while (p < chunkText.Length && char.IsWhiteSpace(chunkText[p])) p++;
            if (p >= chunkText.Length) break;
            int s = p, letters = 0;
            while (p < chunkText.Length && !char.IsWhiteSpace(chunkText[p]))
            {
                if (char.IsLetterOrDigit(chunkText[p])) letters++;
                p++;
            }
            if (letters > 0) words.Add((s, p - s, letters));
        }

        var result = new List<(int, int, double)>(words.Count);
        if (words.Count == 0) return result;

        bool haveStamps = stamps != null && stamps.Length == tokens.Length;
        if (haveStamps)
        {
            var groupStarts = new List<int>();
            var phonemeIdx = new List<int>();
            bool inGroup = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                bool sep = SeparatorTokens.Contains(tokens[i]);
                if (!sep)
                {
                    phonemeIdx.Add(i);
                    if (!inGroup) groupStarts.Add(i);
                }
                inGroup = !sep;
            }

            if (phonemeIdx.Count > 0)
            {
                if (groupStarts.Count == words.Count)
                {
                    for (int w = 0; w < words.Count; w++)
                        result.Add((chunkStart + words[w].start, words[w].length,
                            Math.Max(0, stamps![groupStarts[w]].StartSecond - skippedSec)));
                    return result;
                }

                int totalLetters = words.Sum(w => w.letters);
                int before = 0;
                foreach (var w in words)
                {
                    int k = (int)((double)before / totalLetters * phonemeIdx.Count);
                    k = Math.Clamp(k, 0, phonemeIdx.Count - 1);
                    result.Add((chunkStart + w.start, w.length,
                        Math.Max(0, stamps![phonemeIdx[k]].StartSecond - skippedSec)));
                    before += w.letters;
                }
                return result;
            }
        }

        // No timings from the model: spread over the audio by letter count
        {
            int totalLetters = words.Sum(w => w.letters);
            int before = 0;
            foreach (var w in words)
            {
                result.Add((chunkStart + w.start, w.length, audioSec * before / totalLetters));
                before += w.letters;
            }
        }
        return result;
    }

    /// <summary>
    /// Splits text into sentence-sized chunks (start, length). The first chunk is kept
    /// short so playback starts fast; long sentences are split at commas, then spaces.
    /// </summary>
    internal static List<(int start, int length)> SplitIntoChunks(string text, int firstMax = 120, int max = 260)
    {
        var chunks = new List<(int, int)>();
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= text.Length) break;

            int limit = chunks.Count == 0 ? firstMax : max;
            int end = FindSentenceEnd(text, pos);
            if (end - pos > limit) end = FindSoftBreak(text, pos, limit);

            int e = end;
            while (e > pos && char.IsWhiteSpace(text[e - 1])) e--;
            if (e > pos && text.AsSpan(pos, e - pos).IndexOfAnyExcept(" \t\r\n.,;:!?…-—\"'()[]«»“”‘’") >= 0)
                chunks.Add((pos, e - pos));
            pos = Math.Max(end, pos + 1);
        }
        return chunks;
    }

    private static int FindSentenceEnd(string text, int pos)
    {
        for (int i = pos; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n') return i;
            if (ch is '.' or '!' or '?' or '…')
            {
                int j = i + 1;
                while (j < text.Length && text[j] is '.' or '!' or '?' or '…' or '"' or '\'' or '”' or '’' or '»' or ')' or ']') j++;
                if (j >= text.Length || char.IsWhiteSpace(text[j])) return j;
            }
        }
        return text.Length;
    }

    private static int FindSoftBreak(string text, int pos, int limit)
    {
        int hardEnd = Math.Min(text.Length, pos + limit);
        // Never cut an emoji (surrogate pair) in half
        if (hardEnd < text.Length && hardEnd > pos + 1 && char.IsLowSurrogate(text[hardEnd])) hardEnd--;
        int minEnd = pos + limit / 3;
        for (int i = hardEnd - 1; i > minEnd; i--)
            if (text[i] is ',' or ';' or ':' or '—' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                return i + 1;
        for (int i = hardEnd - 1; i > minEnd; i--)
            if (char.IsWhiteSpace(text[i])) return i;
        return hardEnd;
    }

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "...";

    private static string NormalizeApostrophes(string s) =>
        s.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02BC', '\'')
         .Replace('\u00B4', '\'').Replace('`', '\'');

    /// <summary>
    /// Fixes text the phonemizers handle badly. Same length as the input, so word
    /// positions for the highlight don't move.
    ///   - Curly apostrophes → straight ("wouldn’t").
    ///   - ALL-CAPS words: Spanish dropped them silently ("EDITAR" → nothing) and English
    ///     spelled them letter by letter ("MUNDO" → "M-U-N-D-O"). They are lower-cased,
    ///     except short English ones (≤ 4 letters) that are likely acronyms (USA, NASA).
    ///   - Emoji and other characters outside the basic plane become spaces. The English
    ///     phonemizer splits them into half characters and crashes ("String contains
    ///     invalid Unicode code points"), which made Kokoro drop the rest of the text.
    /// </summary>
    internal static string NormalizeForSpeech(string s, string langCode)
    {
        var chars = NormalizeApostrophes(s).ToCharArray();
        for (int k = 0; k < chars.Length; k++)
            if (IsUnspeakableSymbol(chars[k])) chars[k] = ' ';
        bool english = langCode.StartsWith("en", StringComparison.Ordinal);
        int i = 0;
        while (i < chars.Length)
        {
            if (!char.IsLetter(chars[i])) { i++; continue; }
            int start = i;
            bool allUpper = true;
            while (i < chars.Length && (char.IsLetter(chars[i]) || chars[i] == '\''))
            {
                if (char.IsLower(chars[i])) allUpper = false;
                i++;
            }
            int letters = i - start;
            if (allUpper && letters >= 2 && (!english || letters > 4))
                for (int k = start; k < i; k++) chars[k] = char.ToLowerInvariant(chars[k]);
        }
        return new string(chars);
    }

    /// <summary>
    /// Surrogates (emoji and other astral characters, paired or broken), plus the BMP
    /// pieces emoji are built from: variation selectors, zero-width joiner, keycap mark
    /// and the dingbat/misc-symbol blocks (☀ ✅ ❤ …).
    /// </summary>
    internal static bool IsUnspeakableSymbol(char c) =>
        char.IsSurrogate(c)
        || c is '\uFE0E' or '\uFE0F' or '\u200D' or '\u20E3'
        || (c >= '\u2600' && c <= '\u27BF')
        || (c >= '\u2B00' && c <= '\u2BFF');

    private static void Log(string message)
    {
        Debug.WriteLine(message);
        Infrastructure.CrashLogger.LogInfo(message);
    }

    public void Dispose()
    {
        _previewCts?.Cancel();
        try { _model?.Dispose(); } catch { }
        _model = null;
    }

    /// <summary>
    /// Feeds synthesized sentences to the sound card as they become ready. While the
    /// next sentence is still being generated it plays silence (and counts it, so word
    /// timings stay in sync). Ends once <see cref="Complete"/> was called and all audio
    /// has been read.
    /// </summary>
    private sealed class StreamingSampleProvider : ISampleProvider
    {
        private readonly ConcurrentQueue<float[]> _queue = new();
        private readonly Func<float> _volume;
        private float[]? _current;
        private int _currentPos;
        private volatile bool _completed;
        private long _silenceSamples;

        public StreamingSampleProvider(int sampleRate, Func<float> volume)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            _volume = volume;
        }

        public WaveFormat WaveFormat { get; }

        public long SilenceSamples => Interlocked.Read(ref _silenceSamples);

        public void Enqueue(float[] samples) => _queue.Enqueue(samples);

        public void Complete() => _completed = true;

        public int Read(float[] buffer, int offset, int count)
        {
            float vol = Math.Clamp(_volume(), 0f, 1f);
            int written = 0;
            while (written < count)
            {
                if (_current == null || _currentPos >= _current.Length)
                {
                    if (!_queue.TryDequeue(out _current)) { _current = null; break; }
                    _currentPos = 0;
                }
                int n = Math.Min(count - written, _current.Length - _currentPos);
                for (int i = 0; i < n; i++)
                    buffer[offset + written + i] = _current[_currentPos + i] * vol;
                _currentPos += n;
                written += n;
            }

            if (written < count)
            {
                bool done = _completed && _queue.IsEmpty;
                if (done) return written; // 0 here stops playback

                // Underrun: next sentence not ready yet
                Array.Clear(buffer, offset + written, count - written);
                Interlocked.Add(ref _silenceSamples, count - written);
            }
            return count;
        }
    }
}
