using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    // A scan reaches the cache manager through Task.Run, so it starts only when a thread-pool thread is free.
    // Earlier tests in this class each start an FFmpeg prewarm. On Windows without FFmpeg installed those hold
    // four pool threads while the bundle downloads, and the scan waits for the pool to grow. Five seconds was
    // not always enough for that on CI.
    private static readonly TimeSpan ScanWaitTimeout = TimeSpan.FromSeconds(30);

    private static async Task WaitForScanToStartAsync(BlockingCacheManager manager, Task scan)
    {
        var started = manager.Started.Task;
        if (await Task.WhenAny(started, scan).WaitAsync(ScanWaitTimeout) == started) return;
        await scan; // The scan ended first: report its own failure instead of a timeout.
        Assert.Fail("The scan completed without calling the cache manager.");
    }

    [Fact]
    public async Task IndexBuildGate_SerializesScansAndCancelsQueuedWorkWithoutRebuilding()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "first");
        using var manager = new BlockingCacheManager();
        var app = new DesktopHostApplication(cacheManager: manager);
        var parameters = JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot });
        var first = DispatchAsync(app, "scan", parameters);
        await WaitForScanToStartAsync(manager, first);
        using var cancellation = new CancellationTokenSource();
        var second = DispatchAsync(app, "scan", parameters, cancellation.Token);
        Assert.Equal(1, manager.BuildCalls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        manager.Release.Set();
        var result = await first.WaitAsync(ScanWaitTimeout);
        Assert.Equal(1, result.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, manager.BuildCalls);
    }

    [Fact]
    public async Task IndexBuildGate_DoesNotPublishOrPersistScanAfterSettingsInvalidation()
    {
        using var workspace = new HostTestWorkspace();
        using var manager = new BlockingCacheManager();
        var app = new DesktopHostApplication(cacheManager: manager);
        var scan = DispatchAsync(app, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, includeIncomplete = false }));
        await WaitForScanToStartAsync(manager, scan);
        await DispatchAsync(app, "settings.update", "{\"includeIncomplete\":true}");
        manager.Release.Set();
        var stale = await Assert.ThrowsAsync<RpcException>(() => scan);
        Assert.Equal("stale_index", stale.Code);
        var settings = await DispatchAsync(app, "settings.get", "{}");
        Assert.True(settings.GetProperty("includeIncomplete").GetBoolean());
        var fresh = await DispatchAsync(app, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.True(fresh.GetProperty("includeIncomplete").GetBoolean());
    }

    [Fact]
    public async Task IndexBuildGate_DoesNotRepublishAnIndexInvalidatedByTrashMutation()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "first");
        using var manager = new BlockingCacheManager();
        var app = new DesktopHostApplication(cacheManager: manager);
        var parameters = JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot });
        var scan = DispatchAsync(app, "scan", parameters);
        await WaitForScanToStartAsync(manager, scan);
        await DispatchAsync(app, "trash.move", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, avids = new[] { "101" } }));
        manager.Release.Set();
        var stale = await Assert.ThrowsAsync<RpcException>(() => scan);
        Assert.Equal("stale_index", stale.Code);
        var fresh = await DispatchAsync(app, "scan", parameters);
        Assert.Equal(0, fresh.GetProperty("totalItems").GetInt32());
    }

    private sealed class BlockingCacheManager : ICacheManager, IDisposable
    {
        private readonly CacheManager _inner = new();
        private int _calls;
        public int BuildCalls => Volatile.Read(ref _calls);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public CacheIndexBuildResult BuildIndexWithReport(string rootDirectory, CacheIndexBuildOptions? options,
            CancellationToken cancellationToken, IProgress<CacheScanProgress>? progress = null)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                Release.Wait(cancellationToken);
            }
            return _inner.BuildIndexWithReport(rootDirectory, options, cancellationToken, progress);
        }
        public CacheIndex BuildIndex(string rootDirectory, CacheIndexBuildOptions? options = null) => _inner.BuildIndex(rootDirectory, options);
        public CacheIndex BuildIndex(string rootDirectory, bool includeIncomplete) => _inner.BuildIndex(rootDirectory, includeIncomplete);
        public IReadOnlyCollection<BiliVideoCache> Search(string rootDirectory, CacheIndexBuildOptions? buildOptions, CacheSearchOptions searchOptions) => _inner.Search(rootDirectory, buildOptions, searchOptions);
        public IReadOnlyCollection<BiliVideoCache> Search(string rootDirectory, bool includeIncomplete, CacheSearchOptions searchOptions) => _inner.Search(rootDirectory, includeIncomplete, searchOptions);
        public BiliVideoCache? FindByAvid(string rootDirectory, CacheIndexBuildOptions? buildOptions, long avid) => _inner.FindByAvid(rootDirectory, buildOptions, avid);
        public BiliVideoCache? FindByAvid(string rootDirectory, bool includeIncomplete, long avid) => _inner.FindByAvid(rootDirectory, includeIncomplete, avid);
        public CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false) => _inner.DeleteByAvid(rootDirectory, avid, dryRun);
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
