using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MagniReader.Core;

/// <summary>A high-quality GPU voice model the user can pick in Settings.</summary>
/// <param name="EnvName">Python environment (and requirements file) it runs in.</param>
/// <param name="DownloadGb">Python packages + model weights, roughly.</param>
/// <param name="VramGb">GPU memory it keeps while MagniReader runs (measured on an RTX 3090).</param>
public record GpuEngineInfo(string Id, string DisplayName, string Description, string EnvName,
    double DownloadGb, double VramGb, string License);

/// <summary>A reference clip the GPU models clone (accent and timbre come from it).</summary>
public record GpuVoiceInfo(string Id, string LangCode, string DisplayName, bool BuiltIn);

/// <summary>What was found about the NVIDIA GPU. <see cref="Problem"/> is null when it can run the GPU voices.</summary>
public record GpuStatus(string? Name, int VramMb, string? Driver, string? Problem)
{
    public bool CanRun => Problem == null;
}

/// <summary>
/// Optional high-quality voices that run on an NVIDIA GPU (CUDA), for English and
/// Castilian Spanish. Kokoro stays the default neural engine; this is an extra mode.
///
///   - The models are PyTorch projects, so they run in a small Python server
///     (GpuTts\magni_tts_server.py) that MagniReader starts and talks to over
///     127.0.0.1 with a random token. The server exits when MagniReader exits.
///   - Everything (uv, Python, PyTorch, model weights) is installed on demand from
///     Settings into %LOCALAPPDATA%\MagniReader\gpu-tts, one environment per model.
///   - The models clone a reference clip, which sets the accent: a public-domain
///     Castilian Spanish clip and an English one ship in GpuTts\voices, and users can
///     add their own clips (.wav + .txt transcript) to %APPDATA%\MagniReader\gpu-voices.
///   - Text is synthesized sentence by sentence through the same player as Kokoro,
///     so pause, seek and the word highlight behave the same.
/// </summary>
public sealed class GpuVoiceSpeaker : IDisposable
{
    public static GpuVoiceSpeaker Shared { get; } = new();

    // Fastest first. Numbers are from the benchmark on an RTX 3090 (see GpuTts\BENCHMARKS.md).
    public static IReadOnlyList<GpuEngineInfo> Engines { get; } = new[]
    {
        new GpuEngineInfo("omnivoice", "OmniVoice (k2-fsa)",
            "Fast and natural, very good Castilian Spanish. Recommended.",
            "omnivoice", 12, 6, "Model: CC BY-NC 4.0 (personal, non-commercial use)"),
        new GpuEngineInfo("qwen3-0.6b", "Qwen3-TTS 0.6B Base (Alibaba)",
            "Alibaba's voice-cloning model, sped up with CUDA graphs (faster-qwen3-tts).",
            "qwen3", 11, 5, "Model: Apache 2.0"),
    };

    public const string DefaultEngine = "omnivoice";
    public const string DefaultEnglishVoice = "en_us_f1";
    public const string DefaultSpanishVoice = "es_castilian_m1";

    /// <summary>uv (Python package manager) release used to set up the environments.</summary>
    private const string UvVersion = "0.13.9";
    private const string UvUrl = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/uv-x86_64-pc-windows-msvc.zip";
    /// <summary>PyTorch cu128 wheels need an NVIDIA driver from the R570 branch or newer.</summary>
    private const int MinDriverMajor = 570;

    public static string RuntimeDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagniReader", "gpu-tts");

    /// <summary>Server script, requirements and the built-in reference clips (next to the exe).</summary>
    public static string BundledDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GpuTts");

    public static string UserVoicesDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagniReader", "gpu-voices");

    private static string UvPath => Path.Combine(RuntimeDirectory, "uv", "uv.exe");
    private static string EnvDirectory(GpuEngineInfo e) => Path.Combine(RuntimeDirectory, "envs", e.EnvName);
    private static string PythonPath(GpuEngineInfo e) => Path.Combine(EnvDirectory(e), "Scripts", "python.exe");
    private static string InstalledMarker(GpuEngineInfo e) => Path.Combine(RuntimeDirectory, $"installed-{e.Id}.txt");

    public static GpuEngineInfo? FindEngine(string? id) =>
        Engines.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True once the environment was set up and the model loaded and spoke on this PC.</summary>
    public static bool IsInstalled(string? engineId) =>
        FindEngine(engineId) is { } e && File.Exists(InstalledMarker(e)) && File.Exists(PythonPath(e));

    // ─── GPU detection ──────────────────────────────────────────

    private static GpuStatus? _gpuStatus;

    /// <summary>
    /// Looks for an NVIDIA GPU with nvidia-smi (installed with the driver). Cached; pass
    /// <paramref name="refresh"/> to look again. Takes ~100 ms, call it off the UI thread.
    /// </summary>
    public static GpuStatus DetectGpu(bool refresh = false)
    {
        if (_gpuStatus != null && !refresh) return _gpuStatus;
        return _gpuStatus = DetectGpuCore();
    }

    private static GpuStatus DetectGpuCore()
    {
        try
        {
            var smi = FindNvidiaSmi();
            if (smi == null)
                return new GpuStatus(null, 0, null, FallbackGpuMessage());

            var psi = new ProcessStartInfo(smi, "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } }
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return new GpuStatus(null, 0, null, "The NVIDIA driver did not answer (nvidia-smi failed). Update or reinstall the NVIDIA driver.");

            // Pick the GPU with the most memory
            GpuStatus? best = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var f = line.Split(',', StringSplitOptions.TrimEntries);
                if (f.Length < 3) continue;
                int.TryParse(f[1], out int mb);
                if (best == null || mb > best.VramMb) best = new GpuStatus(f[0], mb, f[2], null);
            }
            if (best == null) return new GpuStatus(null, 0, null, FallbackGpuMessage());

            int.TryParse(best.Driver?.Split('.')[0], out int major);
            if (major > 0 && major < MinDriverMajor)
                return best with { Problem = $"The NVIDIA driver {best.Driver} is too old. Install driver {MinDriverMajor} or newer." };
            if (best.VramMb < 6000)
                return best with { Problem = $"{best.Name} has {best.VramMb / 1024.0:F0} GB of memory; the GPU voices need at least 6 GB." };
            return best;
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError("[GPU] Detection failed", ex);
            return new GpuStatus(null, 0, null, $"Could not check the GPU: {ex.Message}");
        }
    }

    private static string? FindNvidiaSmi()
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(sys)) return sys;
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        return File.Exists(pf) ? pf : null;
    }

    /// <summary>No nvidia-smi: say which graphics card there is, if Windows knows.</summary>
    private static string FallbackGpuMessage()
    {
        try
        {
            // Display adapters device class
            const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(cls);
            var names = (key?.GetSubKeyNames() ?? Array.Empty<string>())
                .Select(n => { try { using var k = key!.OpenSubKey(n); return k?.GetValue("DriverDesc") as string; } catch { return null; } })
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (names.Any(n => n!.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
                return "An NVIDIA GPU is present but its driver tools were not found. Install the latest NVIDIA driver.";
            if (names.Count > 0)
                return $"No NVIDIA GPU found (this PC has: {string.Join(", ", names)}). The GPU voices need an NVIDIA GPU with CUDA.";
        }
        catch { /* WMI unavailable */ }
        return "No NVIDIA GPU found. The GPU voices need an NVIDIA GPU with CUDA.";
    }

    // ─── Voices ─────────────────────────────────────────────────

    /// <summary>Reference clips: the built-in ones, then the user's own.</summary>
    public static IReadOnlyList<GpuVoiceInfo> GetVoices()
    {
        var list = new List<GpuVoiceInfo>();
        foreach (var (dir, builtIn) in new[] { (Path.Combine(BundledDirectory, "voices"), true), (UserVoicesDirectory, false) })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var wav in Directory.GetFiles(dir, "*.wav"))
                {
                    var id = Path.GetFileNameWithoutExtension(wav);
                    if (list.Any(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                    string lang = id.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
                    string name = id;
                    var meta = Path.ChangeExtension(wav, ".json");
                    if (File.Exists(meta))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                        if (doc.RootElement.TryGetProperty("lang", out var l) && l.GetString() is { Length: > 0 } ls) lang = ls;
                        if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } ns) name = ns;
                    }
                    if (!builtIn) name += " — your clip";
                    list.Add(new GpuVoiceInfo(id, lang, name, builtIn));
                }
            }
            catch (Exception ex)
            {
                Infrastructure.CrashLogger.LogError($"[GPU] Voice scan failed in {dir}", ex);
            }
        }
        return list;
    }

    public static bool HasVoice(string? id) =>
        !string.IsNullOrEmpty(id) && GetVoices().Any(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ─── Install ────────────────────────────────────────────────

    /// <summary>
    /// Sets up the Python environment for <paramref name="engineId"/> and loads the model
    /// once (which downloads its weights and checks it really runs on the GPU).
    /// Progress lines are short status messages for the Settings window.
    /// </summary>
    public async Task InstallAsync(string engineId, IProgress<string> progress, CancellationToken token)
    {
        var engine = FindEngine(engineId) ?? throw new ArgumentException($"Unknown GPU engine '{engineId}'");
        var gpu = DetectGpu(refresh: true);
        if (!gpu.CanRun) throw new InvalidOperationException(gpu.Problem);

        Directory.CreateDirectory(RuntimeDirectory);
        var requirements = Path.Combine(BundledDirectory, "requirements", engine.EnvName + ".txt");
        if (!File.Exists(requirements)) throw new FileNotFoundException("Missing requirements file", requirements);

        // 1. uv
        if (!File.Exists(UvPath))
        {
            progress.Report("Downloading the Python installer (uv)…");
            await DownloadUvAsync(token);
        }

        // 2. Python 3.11 + virtual environment
        if (!File.Exists(PythonPath(engine)))
        {
            progress.Report("Setting up Python…");
            await RunToolAsync(UvPath, $"venv --python 3.11 --python-preference only-managed --seed \"{EnvDirectory(engine)}\"",
                progress, token);
        }

        // 3. PyTorch (CUDA) + the model's packages
        progress.Report("Downloading PyTorch and the model's packages (several GB, this takes a while)…");
        await RunToolAsync(UvPath,
            $"pip install --python \"{PythonPath(engine)}\" -r \"{requirements}\" " +
            "--index-strategy unsafe-best-match",
            progress, token);

        // 4. Load the model once: downloads the weights and proves the GPU works
        progress.Report("Downloading the voice model and testing it on the GPU…");
        Task<bool> start;
        lock (_serverLock)
        {
            StopCore();
            _serverEngine = engine;
            start = _startTask = StartServerAsync(engine, progress, token);
        }
        bool ok = await start;
        if (!ok) throw new InvalidOperationException(_lastError ?? "The model could not start. See the log in %LOCALAPPDATA%\\MagniReader\\gpu-tts\\logs.");

        File.WriteAllText(InstalledMarker(engine), $"{DateTime.Now:O}\n{_ready?.Gpu}\n");
        progress.Report($"✓ {engine.DisplayName} is installed and running on {_ready?.Gpu}.");
    }

    private static async Task DownloadUvAsync(CancellationToken token)
    {
        var dir = Path.GetDirectoryName(UvPath)!;
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "uv.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        await using (var src = await http.GetStreamAsync(UvUrl, token))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst, token);
        ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        File.Delete(zip);
        if (!File.Exists(UvPath)) throw new FileNotFoundException("uv.exe was not in the download", UvPath);
    }

    /// <summary>Common environment for uv and the server: everything stays inside RuntimeDirectory.</summary>
    private static void SetEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["UV_CACHE_DIR"] = Path.Combine(RuntimeDirectory, "uv-cache");
        psi.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(RuntimeDirectory, "python");
        psi.Environment["UV_LINK_MODE"] = "copy";
        psi.Environment["HF_HOME"] = Path.Combine(RuntimeDirectory, "hf");
        psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        psi.Environment["TORCH_HOME"] = Path.Combine(RuntimeDirectory, "torch"); // word aligner model
        psi.Environment["MAGNI_TTS_CACHE"] = Path.Combine(RuntimeDirectory, "cache");
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONNOUSERSITE"] = "1";
        // A user's own Python settings must not leak into our environment
        psi.Environment.Remove("PYTHONPATH");
        psi.Environment.Remove("PYTHONHOME");
        psi.Environment.Remove("VIRTUAL_ENV");
    }

    private static async Task RunToolAsync(string exe, string args, IProgress<string> progress, CancellationToken token)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        SetEnvironment(psi);
        Log($"[GPU] Running: {Path.GetFileName(exe)} {args}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Log("[GPU] " + line);
            lock (tail) { tail.Enqueue(line); if (tail.Count > 8) tail.Dequeue(); }
            var l = line.Trim();
            if (l.StartsWith("Downloading", StringComparison.Ordinal) || l.StartsWith("Installed", StringComparison.Ordinal)
                || l.StartsWith("Prepared", StringComparison.Ordinal) || l.StartsWith("Resolved", StringComparison.Ordinal))
                progress.Report(l);
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (token.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            await p.WaitForExitAsync(token);
        if (p.ExitCode != 0)
        {
            string last;
            lock (tail) last = string.Join(" | ", tail);
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {p.ExitCode}): {last}");
        }
    }

    // ─── Server ─────────────────────────────────────────────────

    private sealed record ReadyInfo(int Port, string Engine, string Gpu, int VramUsedMb, int LoadMs);

    private readonly object _serverLock = new();
    private Process? _server;
    private GpuEngineInfo? _serverEngine;
    private ReadyInfo? _ready;
    private string? _token;
    private string? _lastError;
    private Task<bool>? _startTask;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private CancellationTokenSource? _previewCts;

    /// <summary>Volume 0..2, applied live (same scale as Kokoro).</summary>
    public float Volume { get; set; } = 1f;

    public bool IsReady(string engineId) =>
        _ready != null && _server is { HasExited: false } &&
        string.Equals(_serverEngine?.Id, engineId, StringComparison.OrdinalIgnoreCase);

    public string? LastError => _lastError;

    /// <summary>
    /// Starts the server for <paramref name="engineId"/> in the background (once). A
    /// server for another engine is stopped first. Returns false if it can't run.
    /// </summary>
    public Task<bool> EnsureStartedAsync(string engineId)
    {
        var engine = FindEngine(engineId);
        if (engine == null || !IsInstalled(engineId)) return Task.FromResult(false);
        lock (_serverLock)
        {
            if (_startTask != null && _serverEngine == engine && (_server == null || !_server.HasExited))
                return _startTask;
            StopCore();
            _serverEngine = engine;
            return _startTask = Task.Run(() => StartServerAsync(engine, null, CancellationToken.None));
        }
    }

    private async Task<bool> StartServerAsync(GpuEngineInfo engine, IProgress<string>? progress, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        _lastError = null;
        var gpu = DetectGpu();
        if (!gpu.CanRun) { _lastError = gpu.Problem; return false; }

        string script = Path.Combine(BundledDirectory, "magni_tts_server.py");
        Directory.CreateDirectory(UserVoicesDirectory);
        var logDir = Path.Combine(RuntimeDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var token16 = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        var psi = new ProcessStartInfo(PythonPath(engine))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-u", script, "--engine", engine.Id,
                     "--voices", Path.Combine(BundledDirectory, "voices"), "--voices", UserVoicesDirectory,
                     "--parent-pid", Environment.ProcessId.ToString() })
            psi.ArgumentList.Add(a);
        SetEnvironment(psi);
        psi.Environment["MAGNI_TTS_TOKEN"] = token16;

        Log($"[GPU] Starting {engine.Id} server");
        var p = Process.Start(psi);
        if (p == null) { _lastError = "Could not start Python."; return false; }
        lock (_serverLock) { _server = p; _serverEngine = engine; _token = token16; _ready = null; }

        var logPath = Path.Combine(logDir, $"server-{engine.Id}.log");
        var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        var stderrTail = new Queue<string>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { try { logWriter.Dispose(); } catch { } return; }
            try { logWriter.WriteLine(e.Data); } catch { }
            lock (stderrTail) { stderrTail.Enqueue(e.Data); if (stderrTail.Count > 6) stderrTail.Dequeue(); }
            // Model download progress (huggingface tqdm bars) for the Settings window
            if (progress != null && e.Data.Contains('%')) progress.Report("Model: " + LastTqdm(e.Data));
        };
        p.BeginErrorReadLine();

        try
        {
            // First stdout line: {"event":"ready",...} or {"event":"error",...}
            using var reg = token.Register(() => { try { p.Kill(true); } catch { } });
            while (true)
            {
                var line = await p.StandardOutput.ReadLineAsync(token);
                if (line == null)
                {
                    string tail; lock (stderrTail) tail = string.Join(" | ", stderrTail);
                    _lastError = $"The GPU voice server stopped while loading. {tail}";
                    Log("[GPU] " + _lastError);
                    return false;
                }
                if (!line.TrimStart().StartsWith('{')) { Log("[GPU] " + line); continue; }
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var ev = root.TryGetProperty("event", out var evp) ? evp.GetString() : null;
                if (ev == "error")
                {
                    _lastError = root.GetProperty("message").GetString();
                    Log($"[GPU] Server error: {_lastError}");
                    return false;
                }
                if (ev == "ready")
                {
                    _ready = new ReadyInfo(root.GetProperty("port").GetInt32(), engine.Id,
                        root.TryGetProperty("gpu", out var g) ? g.GetString() ?? "" : "",
                        root.TryGetProperty("vramUsedMb", out var v) ? v.GetInt32() : 0,
                        root.TryGetProperty("load_ms", out var lm) ? lm.GetInt32() : 0);
                    Log($"[GPU] {engine.Id} ready on port {_ready.Port} after {sw.ElapsedMilliseconds}ms ({_ready.Gpu})");
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Infrastructure.CrashLogger.LogError("[GPU] Server start failed", ex);
            return false;
        }
    }

    private static string LastTqdm(string s)
    {
        // tqdm writes many "\r" updates on one line; keep the last one
        int i = s.LastIndexOf('\r');
        var t = (i >= 0 ? s[(i + 1)..] : s).Trim();
        return t.Length > 90 ? t[..90] : t;
    }

    public Task StopAsync() => Task.Run(() => { lock (_serverLock) StopCore(); });

    private void StopCore()
    {
        var p = _server;
        var ready = _ready;
        _server = null; _ready = null; _startTask = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                try
                {
                    // Ask nicely first (frees the GPU memory cleanly), then kill
                    if (ready != null)
                    {
                        using var cts = new CancellationTokenSource(1500);
                        PostAsync(ready, "/shutdown", "{}", cts.Token).GetAwaiter().GetResult().Dispose();
                    }
                }
                catch { }
                if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally { p.Dispose(); }
        Log("[GPU] Server stopped");
    }

    private Task<HttpResponseMessage> PostAsync(ReadyInfo? ready, string path, string json, CancellationToken token)
    {
        if (ready == null) throw new InvalidOperationException("GPU voice server is not running.");
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{ready.Port}{path}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Magni-Token", _token);
        return _http.SendAsync(req, token);
    }

    // ─── Speaking ───────────────────────────────────────────────

    /// <summary>
    /// Speaks <paramref name="text"/> with the running GPU engine. Same contract as
    /// <see cref="KokoroSpeaker.SpeakAsync"/>.
    /// </summary>
    public Task<bool> SpeakAsync(string text, string lang, string voiceId, float speed,
        Action<int, int>? onWord, Action<double>? onAudioStarted, CancellationToken token,
        KokoroPlaybackControl? control = null)
    {
        if (_ready == null || _server is not { HasExited: false })
            throw new InvalidOperationException("GPU voice server is not running.");
        speed = Math.Clamp(speed, 0.5f, 2.0f);
        var chunks = KokoroSpeaker.SplitIntoChunks(text);
        return KokoroSpeaker.PlayChunksAsync("GPU", text, chunks, () => Volume,
            (start, length, ct) => SynthesizeChunkAsync(text, start, length, lang, voiceId, speed, ct),
            onWord, onAudioStarted, token, control);
    }

    private async Task<(float[] samples, List<(int pos, int len, double sec)> words)> SynthesizeChunkAsync(
        string text, int start, int length, string lang, string voiceId, float speed, CancellationToken token)
    {
        var chunkText = text.Substring(start, length);
        var spoken = KokoroSpeaker.NormalizeForSpeech(chunkText, lang);
        var body = JsonSerializer.Serialize(new { text = spoken, lang, voice = voiceId, speed });
        using var resp = await PostAsync(_ready, "/synth", body, token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GPU synth failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(token)}");
        var bytes = await resp.Content.ReadAsByteArrayAsync(token);
        var raw = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, raw, 0, raw.Length * 4);
        double audioSec = (double)raw.Length / KokoroSpeaker.SampleRate;

        // Word start times from the server's aligner when it sent them, else by letter count
        List<(int pos, int len, double sec)> words;
        if (resp.Headers.TryGetValues("X-Word-Times", out var wt) &&
            ParseTimes(wt.FirstOrDefault()) is { } times &&
            WordsOf(chunkText) is var ws && ws.Count == times.Length)
        {
            words = ws.Select((w, i) => (start + w.start, w.length, times[i])).ToList();
        }
        else
        {
            words = KokoroSpeaker.AlignWords(chunkText, start, Array.Empty<int>(), null, 0, audioSec);
        }

        // Same natural pause after the chunk as Kokoro
        char last = chunkText[^1];
        double pause = last is '.' or '!' or '?' or '…' or ':' ? 0.3 : last is ',' or ';' or '—' ? 0.12 : 0.05;
        var samples = new float[raw.Length + (int)(pause / speed * KokoroSpeaker.SampleRate)];
        Array.Copy(raw, samples, raw.Length);
        return (samples, words);
    }

    private static double[]? ParseTimes(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',');
        var r = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out r[i])) return null;
        return r;
    }

    /// <summary>Written words as the server counts them: whitespace runs with a letter or digit.</summary>
    internal static List<(int start, int length)> WordsOf(string s)
    {
        var list = new List<(int, int)>();
        int p = 0;
        while (p < s.Length)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            int b = p; bool any = false;
            while (p < s.Length && !char.IsWhiteSpace(s[p])) { any |= char.IsLetterOrDigit(s[p]); p++; }
            if (any) list.Add((b, p - b));
        }
        return list;
    }

    /// <summary>Plays a short sample (Settings ▶ button). Starts the server if needed.</summary>
    public async Task PreviewAsync(string engineId, string voiceId, string lang, float speed = 1f)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        if (!await EnsureStartedAsync(engineId))
            throw new InvalidOperationException(_lastError ?? "The GPU voice could not start.");
        string sample = lang == "es"
            ? "Hola, soy tu asistente de lectura. ¿Qué te parece esta voz?"
            : "Hello, I am your reading assistant. How does this voice sound?";
        try { await SpeakAsync(sample, lang, voiceId, speed, null, null, cts.Token); }
        catch (OperationCanceledException) { }
    }

    private static void Log(string message) => Infrastructure.CrashLogger.LogTrace(message);

    public void Dispose()
    {
        _previewCts?.Cancel();
        lock (_serverLock) StopCore();
        _http.Dispose();
    }
}
