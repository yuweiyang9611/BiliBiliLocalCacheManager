namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal sealed class TranscodeConcurrencyGate(int maximumConcurrency)
{
    private readonly SemaphoreSlim _semaphore = new(maximumConcurrency, maximumConcurrency);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
