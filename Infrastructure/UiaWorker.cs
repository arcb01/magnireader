using System.Collections.Concurrent;

namespace MagniReader.Infrastructure;

/// <summary>
/// Runs UI Automation calls (and other cross-process work such as screenshots)
/// on one dedicated background thread, off the WPF UI thread.
///
/// UI Automation calls go into the target app's process and wait for it. When the
/// target is busy or frozen, a call can take seconds. Keeping them here means the
/// UI thread (overlay, control bar) stays responsive. If a job takes longer than its
/// timeout, the caller gets a <see cref="TimeoutException"/> and the stuck thread is
/// abandoned: a fresh one takes the next jobs.
///
/// The thread is MTA, which is what Microsoft recommends for UI Automation clients.
/// </summary>
public sealed class UiaWorker : IDisposable
{
    private Lane _lane;
    private readonly object _lock = new();
    private bool _disposed;

    public UiaWorker()
    {
        _lane = new Lane();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the worker thread. Throws <see cref="TimeoutException"/>
    /// if it doesn't finish within <paramref name="timeout"/>.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout)
    {
        Lane lane;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lane = _lane;
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.Post(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });

        try
        {
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The target app is hanging. Replace the thread so later work isn't stuck behind it.
            lock (_lock)
            {
                if (ReferenceEquals(_lane, lane) && !_disposed)
                {
                    lane.Retire();
                    _lane = new Lane();
                    CrashLogger.LogWarning($"[UIA] A UI Automation call took over {timeout.TotalSeconds:0.#}s; worker restarted");
                }
            }
            throw;
        }
    }

    public Task RunAsync(Action work, TimeSpan timeout) =>
        RunAsync(() => { work(); return true; }, timeout);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _lane.Retire();
        }
    }

    /// <summary>One worker thread with its own queue.</summary>
    private sealed class Lane
    {
        private readonly BlockingCollection<Action> _queue = new();

        public Lane()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "MagniReader UI Automation" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Post(Action action)
        {
            try { _queue.Add(action); }
            catch (InvalidOperationException) { /* retired; the caller's timeout reports it */ }
        }

        /// <summary>Stops taking work. The thread exits once its current job returns.</summary>
        public void Retire()
        {
            try { _queue.CompleteAdding(); } catch { }
        }

        private void Loop()
        {
            try
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[UIA] Job failed: {ex.Message}"); }
                }
            }
            catch { }
        }
    }
}
