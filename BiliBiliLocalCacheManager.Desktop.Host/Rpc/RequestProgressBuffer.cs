namespace BiliBiliLocalCacheManager.Desktop.Host.Rpc;

internal sealed class RequestProgressBuffer : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<HostProgressEvent, Task> _write;
    private readonly Action<Exception> _onFailure;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private HostProgressEvent? _latest;
    private bool _completed;

    public RequestProgressBuffer(Func<HostProgressEvent, Task> write, Action<Exception> onFailure,
        TimeProvider? timeProvider = null, TimeSpan? interval = null)
    {
        _write = write;
        _onFailure = onFailure;
        _pump = PumpAsync(timeProvider ?? TimeProvider.System, interval ?? TimeSpan.FromMilliseconds(250));
    }

    public void Report(HostProgressEvent progress)
    {
        lock (_sync) { if (!_completed) _latest = progress; }
    }

    private async Task PumpAsync(TimeProvider clock, TimeSpan interval)
    {
        try
        {
            using var timer = new PeriodicTimer(interval, clock);
            while (await timer.WaitForNextTickAsync(_stop.Token)) await FlushAsync();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { _onFailure(exception); throw; }
    }

    private Task FlushAsync()
    {
        HostProgressEvent? progress;
        lock (_sync) { progress = _latest; _latest = null; }
        return progress is null ? Task.CompletedTask : _write(progress);
    }

    public async Task CompleteAsync()
    {
        lock (_sync) _completed = true;
        _stop.Cancel();
        // Join any in-flight write, then emit the latest value before the terminal response.
        await _pump;
        await FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _completed = true; _latest = null; }
        _stop.Cancel();
        try { await _pump; }
        finally { _stop.Dispose(); }
    }
}
