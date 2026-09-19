using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task TrashMutation_CancellationPreservesCommittedItemsAndInvalidatesIndex(
        bool restore, int cancelAfter)
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First");
        workspace.CreateCache(202, "Second");
        using var cancellation = new CancellationTokenSource();
        var service = new ObservedTrashService();
        var application = new DesktopHostApplication(trashService: service);
        var requested = new[] { "101", "202" };
        if (restore)
        {
            foreach (var avid in requested) service.MoveToTrash(workspace.CacheRoot, long.Parse(avid));
            requested = service.ListEntries(workspace.CacheRoot).Select(entry => entry.TrashPath).ToArray();
        }
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var mutationCount = 0;
        service.AfterMutation = () => { if (++mutationCount == cancelAfter) cancellation.Cancel(); };

        var parameters = restore
            ? JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds = requested })
            : JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, avids = requested });
        var result = await DispatchAsync(application, restore ? "trash.restore" : "trash.move", parameters, cancellation.Token);

        Assert.Equal(requested.Take(cancelAfter), result.GetProperty(restore ? "restored" : "moved").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(result.GetProperty("failed").EnumerateArray());
        Assert.Equal(requested.Skip(cancelAfter), result.GetProperty("unprocessed").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(cancelAfter < requested.Length, result.GetProperty("cancelled").GetBoolean());
        Assert.True(cancellation.IsCancellationRequested);
        var stale = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "search",
            JsonSerializer.Serialize(new { indexToken = scan.GetProperty("indexToken").GetString(), keyword = "" })));
        Assert.Equal("stale_index", stale.Code);
        Assert.Equal(restore ? cancelAfter : 2 - cancelAfter,
            new[] { "101", "202" }.Count(avid => Directory.Exists(Path.Combine(workspace.CacheRoot, avid))));
    }

    [Fact]
    public async Task TrashPurge_LateCancellationReturnsActualCommittedResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First");
        using var cancellation = new CancellationTokenSource();
        var service = new ObservedTrashService();
        service.MoveToTrash(workspace.CacheRoot, 101);
        var entryIds = service.ListEntries(workspace.CacheRoot).Select(entry => entry.TrashPath).ToArray();
        service.AfterMutation = cancellation.Cancel;
        var application = new DesktopHostApplication(trashService: service);

        var result = await DispatchAsync(application, "trash.purge",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds, confirmed = true }), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.GetProperty("cancelled").GetBoolean());
        Assert.Equal(entryIds, result.GetProperty("purged").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(result.GetProperty("failed").EnumerateArray());
        Assert.Empty(result.GetProperty("unprocessed").EnumerateArray());
        Assert.Empty(service.ListEntries(workspace.CacheRoot));
    }

    [Fact]
    public async Task TrashMove_PreCancelledRequestDoesNotMutate()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "First");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DispatchAsync(workspace.CreateApplication(), "trash.move",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, avids = new[] { "101" } }), cancellation.Token));
        Assert.True(Directory.Exists(Path.Combine(workspace.CacheRoot, "101")));
    }

    private sealed class ObservedTrashService : ICacheTrashService
    {
        private readonly FileSystemCacheTrashService _inner = new();
        public Action? AfterMutation { get; set; }
        public string GetTrashDirectory(string rootDirectory) => _inner.GetTrashDirectory(rootDirectory);
        public CacheTrashStatistics GetStatistics(string rootDirectory, CancellationToken cancellationToken = default) =>
            _inner.GetStatistics(rootDirectory, cancellationToken);
        public IReadOnlyList<CacheTrashEntry> ListEntries(string rootDirectory, CancellationToken cancellationToken = default) =>
            _inner.ListEntries(rootDirectory, cancellationToken);
        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid)
        {
            var result = _inner.MoveToTrash(rootDirectory, avid);
            AfterMutation?.Invoke();
            return result;
        }
        public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath)
        {
            var result = _inner.Restore(rootDirectory, avid, trashPath);
            AfterMutation?.Invoke();
            return result;
        }
        public CacheTrashPurgeResult Purge(string rootDirectory, bool includeUntrustedLegacyEntries = false,
            IReadOnlyCollection<string>? expectedEntryIds = null)
        {
            var result = _inner.Purge(rootDirectory, includeUntrustedLegacyEntries, expectedEntryIds);
            AfterMutation?.Invoke();
            return result;
        }
    }
}
