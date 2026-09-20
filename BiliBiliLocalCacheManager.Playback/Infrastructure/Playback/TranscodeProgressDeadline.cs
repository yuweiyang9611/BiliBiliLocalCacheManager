using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal sealed class TranscodeProgressDeadline : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (double Percentage, double Seconds, long Bytes)> _maximums = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation;
    private readonly ITimer _timer;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private long _lastProgress;
    private bool _disposed;
    public bool TimedOut { get; private set; }
    public CancellationToken Token => _cancellation.Token;

    public TranscodeProgressDeadline(CancellationToken cancellationToken, TimeSpan timeout, TimeProvider? clock = null)
    {
        _timeout = timeout;
        _clock = clock ?? TimeProvider.System;
        _lastProgress = _clock.GetTimestamp();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = _clock.CreateTimer(OnTimeout, null, timeout, Timeout.InfiniteTimeSpan);
    }

    public void Observe(PlaybackPreparationProgress progress)
    {
        if (progress.Phase is not ("concat" or "mux" or "fallback" or "probe" or "download")) return;
        lock (_sync)
        {
            if (_disposed || TimedOut) return;
            var previous = _maximums.GetValueOrDefault(progress.Phase);
            var percentage = progress.Percentage is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
            var seconds = progress.ProcessedSeconds is { } processed && double.IsFinite(processed) ? Math.Max(0, processed) : 0;
            var bytes = Math.Max(0, progress.ProcessedBytes ?? 0);
            if (percentage <= previous.Percentage && seconds <= previous.Seconds && bytes <= previous.Bytes) return;
            _maximums[progress.Phase] = (Math.Max(percentage, previous.Percentage), Math.Max(seconds, previous.Seconds), Math.Max(bytes, previous.Bytes));
            _lastProgress = _clock.GetTimestamp();
            _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimeout(object? state)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var remaining = _timeout - _clock.GetElapsedTime(_lastProgress);
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            TimedOut = true;
        }
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        _timer.Dispose();
        _cancellation.Dispose();
    }
}
