using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MagniReader.Infrastructure;

/// <summary>
/// Checks GitHub Releases for a newer version (published by the Release workflow),
/// downloads it in the background, and applies it when the user restarts or quits.
/// Only works in the installed app; a dev build from Visual Studio reports "not installed".
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/arcb01/magnireader";

    private readonly UpdateManager? _manager;
    private VelopackAsset? _pending;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public enum CheckResult { NotInstalled, UpToDate, UpdateReady, Failed }

    /// <summary>Raised (on a background thread) when an update has been downloaded and is ready to apply.</summary>
    public event Action<string>? UpdateReady;

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("[Update] Could not create the update manager", ex);
        }
    }

    /// <summary>True when running from the installer's folder (updates possible).</summary>
    public bool IsInstalled
    {
        get
        {
            try { return _manager?.IsInstalled == true; }
            catch { return false; }
        }
    }

    /// <summary>Version shown to the user, e.g. "1.2.0".</summary>
    public string CurrentVersion
    {
        get
        {
            try
            {
                if (IsInstalled && _manager!.CurrentVersion != null)
                    return _manager.CurrentVersion.ToString();
            }
            catch { }
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // Strip the "+commitsha" suffix the SDK adds
            return info?.Split('+')[0] ?? "dev";
        }
    }

    /// <summary>Version of a downloaded update waiting to be applied, or null.</summary>
    public string? PendingVersion => _pending?.Version?.ToString();

    public async Task<CheckResult> CheckAndDownloadAsync()
    {
        if (!IsInstalled) return CheckResult.NotInstalled;
        if (_pending != null) return CheckResult.UpdateReady;

        if (!await _checkLock.WaitAsync(0)) return CheckResult.UpToDate; // a check is already running
        try
        {
            var info = await _manager!.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null)
            {
                CrashLogger.LogInfo($"[Update] Up to date ({CurrentVersion})");
                return CheckResult.UpToDate;
            }

            var version = info.TargetFullRelease.Version.ToString();
            CrashLogger.LogInfo($"[Update] Downloading {version}…");
            await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _pending = info.TargetFullRelease;
            CrashLogger.LogInfo($"[Update] {version} downloaded and ready");
            UpdateReady?.Invoke(version);
            return CheckResult.UpdateReady;
        }
        catch (Exception ex)
        {
            // Offline, GitHub rate limit, etc. Not worth bothering the user.
            CrashLogger.LogWarning($"[Update] Check failed: {ex.Message}");
            return CheckResult.Failed;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>Applies the downloaded update and restarts the app. Exits this process.</summary>
    public void ApplyAndRestart()
    {
        if (_manager == null || _pending == null) return;
        CrashLogger.LogInfo($"[Update] Restarting into {_pending.Version}");
        _manager.ApplyUpdatesAndRestart(_pending);
    }

    /// <summary>
    /// Called when the app is quitting: if an update was downloaded, install it silently
    /// once this process has exited, so the next launch is already up to date.
    /// </summary>
    public void ApplyOnExitIfPending()
    {
        if (_manager == null || _pending == null) return;
        try
        {
            CrashLogger.LogInfo($"[Update] Will apply {_pending.Version} after exit");
            _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("[Update] Could not schedule the update", ex);
        }
    }
}
