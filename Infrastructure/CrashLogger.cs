using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace MagniReader.Infrastructure;

/// <summary>
/// Persistent file logger for crash reports and runtime diagnostics.
///
/// - Crash logs: written on unhandled exceptions with full stack traces.
/// - Event logs: key operations, warnings, and errors during normal use.
/// - Log rotation: keeps only the last 5 log files (max ~1MB each).
///
/// Logs are stored in: %APPDATA%/MagniReader/logs/
/// </summary>
public static class CrashLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagniReader", "logs");

    private static readonly string LogFile;
    private static readonly object _lock = new();
    private const long MaxLogSize = 1_048_576; // 1 MB
    private const int MaxLogFiles = 5;

    static CrashLogger()
    {
        Directory.CreateDirectory(LogDir);
        LogFile = Path.Combine(LogDir, $"magnireader_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        RotateOldLogs();
    }

    /// <summary>
    /// Returns the directory where log files are stored.
    /// </summary>
    public static string LogDirectory => LogDir;

    /// <summary>
    /// Returns the current log file path.
    /// </summary>
    public static string CurrentLogFile => LogFile;

    // ─── Public API ──────────────────────────────────────────────

    /// <summary>
    /// Logs a crash (unhandled exception) with full context.
    /// </summary>
    public static void LogCrash(Exception ex, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║                    💥 CRASH REPORT                          ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine($"Time:       {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Source:     {source}");
        sb.AppendLine($"App:        MagniReader {GetAppVersion()}");
        sb.AppendLine($"OS:         {Environment.OSVersion}");
        sb.AppendLine($"CLR:        {Environment.Version}");
        sb.AppendLine($"Culture:    {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"64-bit:     {Environment.Is64BitProcess}");
        sb.AppendLine();

        AppendException(sb, ex, "Exception");
        sb.AppendLine();

        Write(sb.ToString());
    }

    /// <summary>
    /// Logs an error that was caught but is noteworthy.
    /// </summary>
    public static void LogError(string message, Exception? ex = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ERROR] {DateTime.Now:HH:mm:ss.fff} {message}");
        if (ex != null)
            AppendException(sb, ex, "Details");
        Write(sb.ToString());
    }

    /// <summary>
    /// Logs a warning.
    /// </summary>
    public static void LogWarning(string message)
    {
        Write($"[WARN]  {DateTime.Now:HH:mm:ss.fff} {message}");
    }

    /// <summary>
    /// Logs an informational event.
    /// </summary>
    public static void LogInfo(string message)
    {
        Write($"[INFO]  {DateTime.Now:HH:mm:ss.fff} {message}");
    }

    /// <summary>
    /// Logs app startup with system diagnostics.
    /// </summary>
    public static void LogStartup()
    {
        var sb = new StringBuilder();
        sb.AppendLine("─── MagniReader Started ─────────────────────────────────────");
        sb.AppendLine($"  Version:  {GetAppVersion()}");
        sb.AppendLine($"  Time:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  OS:       {Environment.OSVersion}");
        sb.AppendLine($"  CLR:      {Environment.Version}");
        sb.AppendLine($"  Culture:  {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"  Region:   {System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName}");
        sb.AppendLine($"  64-bit:   {Environment.Is64BitProcess}");
        sb.AppendLine($"  Log file: {LogFile}");
        sb.AppendLine("─────────────────────────────────────────────────────────────");
        Write(sb.ToString());
    }

    // ─── Internal ────────────────────────────────────────────────

    private static void AppendException(StringBuilder sb, Exception ex, string label)
    {
        sb.AppendLine($"--- {label} ---");
        sb.AppendLine($"Type:    {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.AppendLine("Stack:");
            sb.AppendLine(ex.StackTrace);
        }

        if (ex.InnerException != null)
        {
            sb.AppendLine();
            AppendException(sb, ex.InnerException, "Inner Exception");
        }

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine();
                AppendException(sb, inner, "Aggregate Inner");
            }
        }
    }

    private static void Write(string text)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(LogFile, text + Environment.NewLine);
            }
            catch
            {
                // Last resort: at least get it in Debug output
                Debug.WriteLine($"[CrashLogger] Failed to write to log: {text}");
            }
        }

        // Also echo to Debug output for development
        Debug.WriteLine(text);
    }

    private static void RotateOldLogs()
    {
        try
        {
            var logFiles = Directory.GetFiles(LogDir, "magnireader_*.log")
                .OrderByDescending(f => f)
                .ToArray();

            // Delete old logs beyond the keep limit
            for (int i = MaxLogFiles; i < logFiles.Length; i++)
            {
                try { File.Delete(logFiles[i]); }
                catch { }
            }
        }
        catch { }
    }

    private static string GetAppVersion()
    {
        try
        {
            return Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "1.0.0";
        }
        catch { return "1.0.0"; }
    }
}
