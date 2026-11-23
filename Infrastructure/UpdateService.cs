using System.Reflection;
using Microsoft.Win32;
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

    public enum CheckResult { NotInstalled, UpToDate, UpdateReady, Failed, Managed }

    /// <summary>
    /// Registry key an administrator can set through Group Policy or Intune:
    /// DWORD <c>DisableUpdates</c> = 1 turns updates off, 0 turns them on even for a
    /// per-machine install. Without it, see <see cref="UpdatesManagedByAdmin"/>.
    /// </summary>
    public const string PolicyKeyPath = @"SOFTWARE\Policies\MagniReader";
    public const string DisableUpdatesValue = "DisableUpdates";

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

    private bool? _managed;

    /// <summary>
    /// True when updates are left to the organisation: an admin policy turned them off,
    /// or the app is installed where this user can't write (the per-machine MSI in
    /// Program Files). Velopack would need admin rights to replace those files, so a
    /// standard user would only get a UAC prompt they can't approve. IT deploys the
    /// new MSI instead.
    /// </summary>
    public bool UpdatesManagedByAdmin => _managed ??= DetectManaged();

    private bool DetectManaged()
    {
        if (!IsInstalled) return false;
        var policy = ReadUpdatePolicy();
        if (policy == true) { CrashLogger.LogInfo("[Update] Turned off by policy"); return true; }
        if (policy == false) return false;
        if (!InstallFolderIsWritable())
        {
            CrashLogger.LogInfo("[Update] Per-machine install: updates are deployed by the administrator");
            return true;
        }
        return false;
    }

    /// <summary>true = updates disabled, false = explicitly allowed, null = no policy.</summary>
    private static bool? ReadUpdatePolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
            return key?.GetValue(DisableUpdatesValue) switch
            {
                int i => i != 0,
                string str when int.TryParse(str, out var n) => n != 0,
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// The install root holds Update.exe and the "current" folder (where this exe runs).
    /// Updating rewrites both, so that root must be writable by this user.
    /// </summary>
    private static bool InstallFolderIsWritable()
    {
        try
        {
            var current = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(root)) return true;
            var probe = Path.Combine(root, $".write-test-{Environment.ProcessId}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch { return true; } // unknown: keep the normal behaviour
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
        if (UpdatesManagedByAdmin) return CheckResult.Managed;
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
