using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(false, "io")]
    [InlineData(false, "access")]
    [InlineData(false, "security")]
    [InlineData(false, "safety")]
    [InlineData(true, "timeout")]
    [InlineData(true, "io")]
    [InlineData(true, "access")]
    [InlineData(true, "security")]
    [InlineData(true, "safety")]
    public async Task TrashMutation_ItemExceptionPreservesCommittedItemsAndContinues(
        bool restore, string failure)
    {
        using var workspace = new HostTestWorkspace();
        foreach (var avid in new[] { 101L, 202L, 303L }) workspace.CreateCache(avid, avid.ToString());
        var service = new ObservedTrashService();
        var application = new DesktopHostApplication(trashService: service);
        var requested = PrepareTrashMutation(service, workspace.CacheRoot, restore);
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var attempts = 0;
        service.BeforeMutation = () =>
        {
            if (++attempts != 2) return;
            throw failure switch
            {
                "timeout" => new TimeoutException("The transaction lock is busy."),
                "access" => new UnauthorizedAccessException("The directory is not accessible."),
                "security" => new System.Security.SecurityException("Filesystem access is restricted."),
                "safety" => new InvalidOperationException("The trash entry is no longer a physical directory."),
                _ => new IOException("The disk is unavailable.")
            };
        };

        var parameters = restore
            ? JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds = requested })
            : JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, avids = requested });
        var result = await DispatchAsync(application, restore ? "trash.restore" : "trash.move", parameters);

        Assert.Equal(new[] { requested[0], requested[2] },
            result.GetProperty(restore ? "restored" : "moved").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { requested[1] }, result.GetProperty("failed").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(result.GetProperty("unprocessed").EnumerateArray());
        Assert.False(result.GetProperty("cancelled").GetBoolean());
        Assert.Equal(3, attempts);
        var stale = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "search",
            JsonSerializer.Serialize(new { indexToken = scan.GetProperty("indexToken").GetString(), keyword = "" })));
        Assert.Equal("stale_index", stale.Code);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task TrashMutation_CancellationAfterItemExceptionPreservesProcessedAndRemainingItems(
        bool restore, int failAfter)
    {
        using var workspace = new HostTestWorkspace();
        foreach (var avid in new[] { 101L, 202L, 303L }) workspace.CreateCache(avid, avid.ToString());
        using var cancellation = new CancellationTokenSource();
        var service = new ObservedTrashService();
        var requested = PrepareTrashMutation(service, workspace.CacheRoot, restore);
        var attempts = 0;
        service.BeforeMutation = () =>
        {
            if (++attempts != failAfter) return;
            cancellation.Cancel();
            throw new IOException("The disk became unavailable.");
        };
        var parameters = restore
            ? JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds = requested })
            : JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, avids = requested });

        var result = await DispatchAsync(new DesktopHostApplication(trashService: service),
            restore ? "trash.restore" : "trash.move", parameters, cancellation.Token);

        Assert.Equal(requested.Take(failAfter - 1),
            result.GetProperty(restore ? "restored" : "moved").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { requested[failAfter - 1] },
            result.GetProperty("failed").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(requested.Skip(failAfter),
            result.GetProperty("unprocessed").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(failAfter < requested.Length, result.GetProperty("cancelled").GetBoolean());
        Assert.Equal(failAfter, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrashMutation_UnexpectedProgrammingExceptionIsNotAnItemFailure(bool restore)
    {
        using var workspace = new HostTestWorkspace();
        foreach (var avid in new[] { 101L, 202L, 303L }) workspace.CreateCache(avid, avid.ToString());
        var service = new ObservedTrashService();
        var requested = PrepareTrashMutation(service, workspace.CacheRoot, restore);
        var parameters = restore
            ? JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds = requested })
            : JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, avids = requested });
        var application = new DesktopHostApplication(trashService: service);
        foreach (var exception in new Exception[]
                 {
                     new NullReferenceException("A required service is missing."),
                     new ArgumentException("An internal argument is invalid."),
                     new ObjectDisposedException("trashService")
                 })
        {
            service.BeforeMutation = () => throw exception;
            var actual = await Record.ExceptionAsync(() => DispatchAsync(application,
                restore ? "trash.restore" : "trash.move", parameters));
            Assert.Same(exception, actual);
        }
    }

    private static string[] PrepareTrashMutation(ObservedTrashService service, string root, bool restore)
    {
        var requested = new[] { "101", "202", "303" };
        if (!restore) return requested;
        foreach (var avid in requested) service.MoveToTrash(root, long.Parse(avid));
        return service.ListEntries(root).OrderBy(entry => entry.Avid).Select(entry => entry.TrashPath).ToArray();
    }

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
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds, confirmed = true, confirmationText = "永久删除" }), cancellation.Token);

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
        public Action? BeforeMutation { get; set; }
        public Action? AfterMutation { get; set; }
        public string GetTrashDirectory(string rootDirectory) => _inner.GetTrashDirectory(rootDirectory);
        public CacheTrashStatistics GetStatistics(string rootDirectory, CancellationToken cancellationToken = default) =>
            _inner.GetStatistics(rootDirectory, cancellationToken);
        public IReadOnlyList<CacheTrashEntry> ListEntries(string rootDirectory, CancellationToken cancellationToken = default) =>
            _inner.ListEntries(rootDirectory, cancellationToken);
        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid)
        {
            BeforeMutation?.Invoke();
            var result = _inner.MoveToTrash(rootDirectory, avid);
            AfterMutation?.Invoke();
            return result;
        }
        public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath)
        {
            BeforeMutation?.Invoke();
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
