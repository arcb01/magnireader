using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MagniReader.Core;

/// <summary>
/// Tells when this app's own audio really starts coming out of the speakers, by
/// watching the peak meter of our audio sessions (the same meter the Windows volume
/// mixer shows).
///
/// SAPI voices give no "playback started" event: SpeakAsync returns at once, but an
/// Online voice (NaturalVoiceSAPIAdapter) first has to fetch its audio from the
/// internet, which can take seconds. The word highlight waits for this signal, so it
/// no longer runs ahead of the voice.
/// </summary>
internal sealed class AudioStartDetector
{
    // Peak (0..1) that counts as sound. Leading silence of a voice is digital zero.
    private const float SoundThreshold = 0.01f;
    private const int PollMs = 15;
    // How long the meter must read silence first when waiting for a NEW prompt
    // after a rewind / forward (the previous audio is still fading out).
    private const int SilenceMs = 60;

    private static readonly int OwnPid = Environment.ProcessId;

    /// <summary>
    /// Watches in the background and calls <paramref name="onSound"/> once (on a
    /// background thread) when our audio starts, with the time it took. If the meter
    /// can't be read, or nothing is heard within <paramref name="timeout"/>, calls
    /// <paramref name="onSound"/> anyway with <c>detected = false</c>, so the caller
    /// never waits forever. Nothing is called if <paramref name="token"/> is cancelled.
    /// </summary>
    public static AudioStartDetector Watch(Action<TimeSpan, bool> onSound, TimeSpan timeout,
        bool requireSilenceFirst, Func<bool> isPaused, CancellationToken token)
    {
        var detector = new AudioStartDetector();
        var thread = new Thread(() => detector.Run(onSound, timeout, requireSilenceFirst, isPaused, token))
        {
            IsBackground = true,
            Name = "MagniReader audio start detector",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return detector;
    }

    /// <summary>
    /// True once the meter is watching one of our audio sessions. While false the
    /// meter can't hear anything yet, so other signals (the voice's first word
    /// event) are trusted as "audio started" instead.
    /// </summary>
    public bool IsListening => _listening;
    private volatile bool _listening;

    private void Run(Action<TimeSpan, bool> onSound, TimeSpan timeout,
        bool requireSilenceFirst, Func<bool> isPaused, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var waited = Stopwatch.StartNew(); // time not paused, for the timeout
        bool detected = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = new List<MMDevice>();
            var sessions = new List<AudioSessionControl>();
            long lastRefresh = -1000;
            int silentMs = requireSilenceFirst ? 0 : SilenceMs;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (isPaused())
                    {
                        waited.Stop();
                        silentMs = requireSilenceFirst ? 0 : silentMs;
                    }
                    else if (!waited.IsRunning) waited.Start();

                    if (waited.Elapsed > timeout) break;

                    // Our session appears only when the voice opens the audio device,
                    // so look for it again until it shows up (and now and then after).
                    if (sessions.Count == 0 ? sw.ElapsedMilliseconds - lastRefresh > 100
                                            : sw.ElapsedMilliseconds - lastRefresh > 1000)
                    {
                        lastRefresh = sw.ElapsedMilliseconds;
                        Refresh(enumerator, devices, sessions);
                        _listening = sessions.Count > 0;
                    }

                    float peak = 0f;
                    foreach (var s in sessions)
                    {
                        try { peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue); }
                        catch { lastRefresh = -1000; } // session gone: look again
                    }

                    if (peak < SoundThreshold) silentMs += PollMs;
                    else if (silentMs >= SilenceMs && !isPaused())
                    {
                        detected = true;
                        break;
                    }

                    token.WaitHandle.WaitOne(PollMs);
                }
            }
            finally
            {
                foreach (var s in sessions) try { s.Dispose(); } catch { }
                foreach (var d in devices) try { d.Dispose(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioStart] meter unavailable: {ex.Message}");
        }

        if (token.IsCancellationRequested) return;
        try { onSound(sw.Elapsed, detected); } catch { }
    }

    private static void Refresh(MMDeviceEnumerator enumerator, List<MMDevice> devices, List<AudioSessionControl> sessions)
    {
        foreach (var s in sessions) try { s.Dispose(); } catch { }
        foreach (var d in devices) try { d.Dispose(); } catch { }
        sessions.Clear();
        devices.Clear();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(device);
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var list = manager.Sessions;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    try
                    {
                        if (!s.IsSystemSoundsSession && (int)s.GetProcessID == OwnPid)
                        {
                            sessions.Add(s);
                            continue;
                        }
                    }
                    catch { }
                    try { s.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioStart] skip device: {ex.Message}");
            }
        }
    }
}
