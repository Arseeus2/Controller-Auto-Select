namespace ControllerAutoSelect;

// One scheduled callback at a time. Cancellation prevents queued callbacks on unload.
internal sealed class ScheduledMonitor
{
    private readonly CancellationTokenSource stop = new();
    private readonly Func<Action, TimeSpan, CancellationToken, Task> schedule;
    private readonly Action tick;
    private readonly Action<Exception> onError;
    private int stopped;
    public Task Completion { get; }

    public ScheduledMonitor(Func<Action, TimeSpan, CancellationToken, Task> schedule,
        Action tick, Action<Exception> onError)
    {
        this.schedule = schedule;
        this.tick = tick;
        this.onError = onError;
        Completion = Task.Run(RunAsync);
    }

    public void Stop()
    {
        lock (stop)
        {
            if (Interlocked.Exchange(ref stopped, 1) == 0) stop.Cancel();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            var delay = TimeSpan.Zero;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await schedule(() =>
                    {
                        if (!stop.IsCancellationRequested) tick();
                    }, delay, stop.Token).ConfigureAwait(false);
                }
                // Dalamud may also cancel work while its own framework is shutting down.
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    try { onError(ex); }
                    catch { /* Error reporting must not fault an unobserved background task. */ }
                    // A failed scheduler may return immediately. Never spin in a retry loop.
                    try { await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                delay = TimeSpan.FromSeconds(1);
            }
        }
        finally
        {
            // Stop() and completion can race. Keep cancellation and disposal under one lock.
            lock (stop)
            {
                Interlocked.Exchange(ref stopped, 1);
                stop.Dispose();
            }
        }
    }
}
