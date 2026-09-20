namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class PlaybackPreparationProtection : IAsyncDisposable
{
    private readonly PlaybackArtifactStore _store;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly HashSet<string> _paths = new(PlaybackFileSystem.PathComparer);
    private readonly CancellationTokenSource _failure = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenSource _renewal;
    private readonly ITimer _timer;
    private Exception? _error;
    private bool _stopped;
    private int _refreshing;

    public PlaybackPreparationProtection(PlaybackArtifactStore store, CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _clock = timeProvider ?? TimeProvider.System;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _failure.Token);
        _renewal = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _timer = _clock.CreateTimer(_ => Refresh(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public CancellationToken Token => _lifetime.Token;
    public Exception? Failure { get { lock (_sync) return _error; } }

    public void Register(string path)
    {
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                Token.ThrowIfCancellationRequested();
            }
            _store.ProtectUntilIfManaged(path, _clock.GetUtcNow().AddHours(6), _renewal.Token);
            lock (_sync)
            {
                _renewal.Token.ThrowIfCancellationRequested();
                _paths.Add(path);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(exception);
            throw;
        }
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            string[] paths;
            lock (_sync)
            {
                if (_stopped || Token.IsCancellationRequested) return;
                paths = _paths.ToArray();
            }
            foreach (var path in paths)
                _store.ProtectUntilIfManaged(path, _clock.GetUtcNow().AddHours(6), _renewal.Token);
        }
        catch (OperationCanceledException) when (_renewal.IsCancellationRequested) { }
        catch (Exception exception) { Fail(exception); }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    private void Fail(Exception exception)
    {
        lock (_sync) _error ??= exception;
        _failure.Cancel();
    }

    public async ValueTask StopAsync()
    {
        // Cancel lock waits before joining the timer callback.
        lock (_sync) _stopped = true;
        _renewal.Cancel();
        await _timer.DisposeAsync();
        lock (_sync) { _stopped = true; _paths.Clear(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Cancel();
        _renewal.Dispose();
        _lifetime.Dispose();
        _failure.Dispose();
    }
}
