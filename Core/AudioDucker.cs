using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace MagniReader.Core;

/// <summary>
/// Lowers the volume of other apps (music, videos, calls…) while MagniReader speaks,
/// and puts it back when speech ends.
///
/// Works on Windows Core Audio sessions: every app that plays sound has a session per
/// output device with its own volume (the one in the Volume Mixer). Only sessions that are
/// actually playing are lowered. On restore, a session is put back to its original volume
/// unless the user changed it by hand in the meantime.
///
/// The original volumes are also written to disk while ducked, so if MagniReader crashes
/// or is killed mid-sentence the next start puts other apps back to normal.
///
/// All Core Audio calls run one at a time on a background (MTA) thread, never on the UI thread.
/// </summary>
public sealed class AudioDucker : IDisposable
{
    /// <summary>Wait this long after speech ends before restoring, so that consecutive
    /// sentences/paragraphs don't make the music jump up and down between them.</summary>
    private const int RestoreDelayMs = 700;

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagniReader", "ducked-sessions.json");

    private readonly object _lock = new();
    private readonly int _ownPid = Environment.ProcessId;
    private Task _queue = Task.CompletedTask;
    private Timer? _restoreTimer;
    private int _restoreGeneration;

    // Only touched from the serial queue.
    private readonly List<DuckedSession> _ducked = new();

    /// <summary>Whether ducking is on (Settings → "Lower other sounds while reading").</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Volume other apps are lowered to, as a fraction of their own volume (0.0–1.0).</summary>
    public float Level { get; set; } = 0.3f;

    /// <summary>Lowers other apps now (cancels any pending restore). Safe to call repeatedly.</summary>
    public void Duck()
    {
        lock (_lock)
        {
            _restoreGeneration++;          // invalidates a restore that is waiting to fire
            _restoreTimer?.Dispose();
            _restoreTimer = null;
        }
        if (!Enabled) return;
        float level = Math.Clamp(Level, 0f, 1f);
        Enqueue(() => DuckCore(level));
    }

    /// <summary>Restores other apps after a short delay, unless <see cref="Duck"/> is called first.</summary>
    public void ScheduleRestore()
    {
        lock (_lock)
        {
            int gen = ++_restoreGeneration;
            _restoreTimer?.Dispose();
            _restoreTimer = new Timer(_ =>
            {
                lock (_lock) { if (gen != _restoreGeneration) return; }
                Enqueue(RestoreCore);
            }, null, RestoreDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>Restores other apps immediately and waits for it (used on exit/crash).</summary>
    public void RestoreNow()
    {
        Task t;
        lock (_lock)
        {
            _restoreGeneration++;
            _restoreTimer?.Dispose();
            _restoreTimer = null;
            t = _queue = _queue.ContinueWith(_ => SafeRun(RestoreCore), TaskScheduler.Default);
        }
        try { t.Wait(2000); } catch { }
    }

    /// <summary>
    /// If a previous run crashed while other apps were lowered, puts them back.
    /// Call once at startup.
    /// </summary>
    public void RecoverFromCrash()
    {
        Enqueue(() =>
        {
            List<DuckedSession>? saved = null;
            try
            {
                if (!File.Exists(StatePath)) return;
                saved = JsonSerializer.Deserialize<List<DuckedSession>>(File.ReadAllText(StatePath));
            }
            catch { }
            if (saved is { Count: > 0 })
            {
                Infrastructure.CrashLogger.LogInfo(
                    $"[Ducker] Restoring {saved.Count} app volume(s) left lowered by a previous run");
                _ducked.AddRange(saved);
            }
            RestoreCore();
        });
    }

    // ─── Core (serial background queue) ──────────────────────────

    private void DuckCore(float level)
    {
        var sw = Stopwatch.StartNew();
        int before = _ducked.Count;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager;
                sessions.RefreshSessions();
                var list = sessions.Sessions;
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        var s = list[i];
                        if (s.IsSystemSoundsSession) continue;
                        if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                        int pid = (int)s.GetProcessID;
                        if (pid == _ownPid || pid == 0) continue;

                        string id = s.GetSessionInstanceIdentifier;
                        if (_ducked.Any(d => d.DeviceId == device.ID && d.SessionId == id)) continue; // already lowered

                        float original = s.SimpleAudioVolume.Volume;
                        float lowered = original * level;
                        if (original <= 0f) continue;
                        s.SimpleAudioVolume.Volume = lowered;
                        _ducked.Add(new DuckedSession
                        {
                            DeviceId = device.ID,
                            SessionId = id,
                            ProcessId = pid,
                            Original = original,
                            Lowered = lowered,
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Ducker] skip session: {ex.Message}");
                    }
                }
            }
        }
        SaveState();
        int added = _ducked.Count - before;
        if (added > 0)
            Infrastructure.CrashLogger.LogInfo(
                $"[Ducker] Lowered {added} app(s) to {level:P0} in {sw.ElapsedMilliseconds}ms: " +
                string.Join(", ", _ducked.Skip(before).Select(d => ProcessName(d.ProcessId))));
    }

    private void RestoreCore()
    {
        if (_ducked.Count == 0) { DeleteState(); return; }

        var pending = _ducked.ToList();
        _ducked.Clear();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var byDevice in pending.GroupBy(d => d.DeviceId))
        {
            MMDevice? device = null;
            try { device = enumerator.GetDevice(byDevice.Key); } catch { }
            if (device == null) continue;
            using (device)
            {
                var sessions = device.AudioSessionManager;
                sessions.RefreshSessions();
                var list = sessions.Sessions;
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        var s = list[i];
                        string id = s.GetSessionInstanceIdentifier;
                        var d = byDevice.FirstOrDefault(x => x.SessionId == id);
                        if (d == null) continue;
                        // Leave it alone if the user moved the slider while we were reading.
                        float now = s.SimpleAudioVolume.Volume;
                        if (Math.Abs(now - d.Lowered) > 0.02f) continue;
                        s.SimpleAudioVolume.Volume = d.Original;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Ducker] restore failed: {ex.Message}");
                    }
                }
            }
        }
        DeleteState();
    }

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return pid.ToString(); }
    }

    private void SaveState()
    {
        try
        {
            if (_ducked.Count == 0) { DeleteState(); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_ducked));
        }
        catch { }
    }

    private static void DeleteState()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
    }

    // ─── Plumbing ────────────────────────────────────────────────

    private void Enqueue(Action action)
    {
        lock (_lock)
            _queue = _queue.ContinueWith(_ => SafeRun(action), TaskScheduler.Default);
    }

    private static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Infrastructure.CrashLogger.LogError("Audio ducking failed", ex);
        }
    }

    public void Dispose() => RestoreNow();

    private sealed class DuckedSession
    {
        public string DeviceId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public int ProcessId { get; set; }
        public float Original { get; set; }
        public float Lowered { get; set; }
    }
}
