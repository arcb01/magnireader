using Microsoft.Win32;

namespace MagniReader.Infrastructure;

/// <summary>
/// "Launch at startup" via the per-user Run key (HKCU, no admin needed).
/// The installed app lives at %LocalAppData%\MagniReader\current\MagniReader.exe,
/// a path that stays the same across updates, so the entry keeps working.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MagniReader";

    /// <summary>Command-line flag added to the startup entry, so the app knows it was auto-started.</summary>
    public const string AutostartArg = "--autostart";

    private static string? ExePath => Environment.ProcessPath;

    private static string? ExpectedCommand =>
        ExePath == null ? null : $"\"{ExePath}\" {AutostartArg}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch { return false; }
        }
    }

    public static void Enable()
    {
        var cmd = ExpectedCommand;
        if (cmd == null) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, cmd);
            CrashLogger.LogInfo($"[Startup] Enabled: {cmd}");
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("[Startup] Could not enable launch at startup", ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                CrashLogger.LogInfo("[Startup] Disabled");
            }
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("[Startup] Could not disable launch at startup", ex);
        }
    }

    public static void Set(bool enabled)
    {
        if (enabled) Enable(); else Disable();
    }

    /// <summary>
    /// Makes the Run key match the saved setting, and points it at the current exe
    /// if it was registered from somewhere else (e.g. a dev build before installing).
    /// </summary>
    public static void Sync(bool enabled)
    {
        if (!enabled)
        {
            // Only remove an entry that points at this exe: a dev build must not
            // switch off the installed app's startup entry, or the other way around.
            if (PointsAtThisExe()) Disable();
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is string current &&
                string.Equals(current, ExpectedCommand, StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch { }
        Enable();
    }

    private static bool PointsAtThisExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return ExePath != null && key?.GetValue(ValueName) is string s &&
                   s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
