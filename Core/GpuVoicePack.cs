using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace MagniReader.Core;

/// <summary>
/// The offline GPU voice pack that an IT department builds with scripts\build-gpu-pack.ps1
/// and deploys per machine (by default to "C:\Program Files\MagniReader GPU Voices").
/// It holds a ready Python for each model, the model weights and the word-aligner
/// weights, so nothing is downloaded and nothing runs from a user-writable folder.
///
/// Layout (paths in manifest.json are relative to the pack folder):
///   manifest.json
///   envs\&lt;env&gt;\python.exe       standalone Python 3.11 with the model's packages
///   models\&lt;model&gt;\              Hugging Face model files
///   torch\hub\checkpoints\       word-aligner weights (torchaudio MMS_FA)
/// </summary>
public static class GpuVoicePack
{
    /// <summary>Same policy key as the updater (HKLM\SOFTWARE\Policies\MagniReader).</summary>
    private const string PolicyKeyPath = @"SOFTWARE\Policies\MagniReader";

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MagniReader GPU Voices");

    public sealed record Entry(string PythonPath, string ModelPath, string? RequirementsSha256);

    private static Lazy<(string? Dir, Dictionary<string, Entry> Engines, string? TorchHome)> _pack = new(Load);

    /// <summary>Folder of the pack in use, or null when there is none.</summary>
    public static string? PackDirectory => _pack.Value.Dir;

    public static string? TorchHome => _pack.Value.TorchHome;

    /// <summary>The pack's copy of this engine, or null.</summary>
    public static Entry? Find(string engineId) =>
        _pack.Value.Engines.TryGetValue(engineId, out var e) ? e : null;

    /// <summary>Look for the pack again (after IT deployed it while MagniReader was running).</summary>
    public static void Refresh() => _pack = new(Load);

    /// <summary>
    /// Policy DWORD <c>DisableGpuVoiceDownload</c> = 1: users can't download GPU voices
    /// themselves; only the pack IT deploys is used.
    /// </summary>
    public static bool DownloadDisabledByPolicy => ReadPolicy("DisableGpuVoiceDownload") is int i && i != 0;

    /// <summary>True when the pack was built from other requirements than this MagniReader ships.</summary>
    public static bool IsOutdated(string engineId, string requirementsFile)
    {
        var entry = Find(engineId);
        if (entry?.RequirementsSha256 is not { Length: > 0 } expected || !File.Exists(requirementsFile)) return false;
        try
        {
            // Same normalisation as build-gpu-pack.ps1: hash of the text with \r removed
            var text = File.ReadAllText(requirementsFile).Replace("\r", "");
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            return !hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static object? ReadPolicy(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
            return key?.GetValue(name);
        }
        catch { return null; }
    }

    private static (string?, Dictionary<string, Entry>, string?) Load()
    {
        var engines = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        // Policy string GpuVoicePackDirectory points at another folder (e.g. D:\MagniReader GPU Voices)
        var dir = ReadPolicy("GpuVoicePackDirectory") as string;
        if (string.IsNullOrWhiteSpace(dir)) dir = DefaultDirectory;
        dir = Environment.ExpandEnvironmentVariables(dir.Trim());
        var manifest = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest)) return (null, engines, null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            string? torch = root.TryGetProperty("torchHome", out var t) && t.GetString() is { Length: > 0 } ts
                ? Path.Combine(dir, ts) : null;
            if (root.TryGetProperty("engines", out var list))
            {
                foreach (var e in list.EnumerateObject())
                {
                    var python = Path.Combine(dir, e.Value.GetProperty("python").GetString()!);
                    var model = Path.Combine(dir, e.Value.GetProperty("model").GetString()!);
                    var req = e.Value.TryGetProperty("requirementsSha256", out var r) ? r.GetString() : null;
                    if (File.Exists(python) && Directory.Exists(model))
                        engines[e.Name] = new Entry(python, model, req);
                    else
                        Log($"[GPU] Pack entry {e.Name} is incomplete ({python}, {model})");
                }
            }
            Log($"[GPU] Voice pack found in {dir}: {string.Join(", ", engines.Keys)}");
            return (dir, engines, torch);
        }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError($"[GPU] Could not read {manifest}", ex);
            return (null, engines, null);
        }
    }

    // ─── Network and policy helpers for the online install ──────────

    /// <summary>
    /// uv and Python only read proxy settings from environment variables, not from
    /// Windows. Pass on the system proxy (including one from a PAC script) so the
    /// install works on networks where everything has to go through the proxy.
    /// </summary>
    public static void ApplySystemProxy(ProcessStartInfo psi)
    {
        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
            if (!string.IsNullOrEmpty(psi.Environment.TryGetValue(name, out var v) ? v : null)) return;
        try
        {
            var target = new Uri("https://pypi.org/simple/");
            var proxy = HttpClient.DefaultProxy.GetProxy(target);
            if (proxy == null || proxy == target || proxy.Host == target.Host) return;
            var url = $"{proxy.Scheme}://{proxy.Authority}";
            psi.Environment["HTTPS_PROXY"] = url;
            psi.Environment["HTTP_PROXY"] = url;
            psi.Environment["NO_PROXY"] = "localhost,127.0.0.1";
            Log($"[GPU] Using the system proxy {url}");
        }
        catch (Exception ex)
        {
            Log($"[GPU] Could not read the system proxy: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns "Windows blocked this program" errors (AppLocker, WDAC, Software Restriction
    /// Policies) into a message that says what to do.
    /// </summary>
    public static Exception ExplainStartFailure(string exe, Exception ex)
    {
        if (ex is System.ComponentModel.Win32Exception w &&
            (w.NativeErrorCode == 1260   // ERROR_ACCESS_DISABLED_BY_POLICY (AppLocker, SRP)
             || w.NativeErrorCode == 4551 // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION (WDAC)
             || w.NativeErrorCode == 5))  // access denied
        {
            return new InvalidOperationException(
                $"Windows blocked {Path.GetFileName(exe)} (a company policy such as AppLocker). " +
                "Ask IT to install the GPU voice pack for this PC (see docs/gpu-voices-it.md on the MagniReader GitHub page).", ex);
        }
        return ex;
    }

    private static void Log(string message) => Infrastructure.CrashLogger.LogTrace(message);
}
