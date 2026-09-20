using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    [Fact]
    public async Task TrashPages_ReuseSnapshotAndBoundLegacyResponse()
    {
        using var workspace = new HostTestWorkspace();
        var service = new SnapshotTrashService(workspace.CacheRoot, 11_000);
        var application = new DesktopHostApplication(trashService: service);
        var first = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, pageSize = 200 }));
        Assert.Equal(11_000, first.GetProperty("totalItems").GetInt32());
        Assert.Equal(11_000, first.GetProperty("totalSizeBytes").GetInt64());
        Assert.Equal(200, first.GetProperty("items").GetArrayLength());
        var token = first.GetProperty("snapshotToken").GetString();
        var second = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, snapshotToken = token, offset = 200, pageSize = 200 }));
        Assert.Equal(1, service.ListCalls);
        Assert.Equal("201", second.GetProperty("items")[0].GetProperty("avid").GetString());
        var last = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, snapshotToken = token, offset = 10_999 }));
        Assert.False(last.GetProperty("hasMore").GetBoolean());
        Assert.Single(last.GetProperty("items").EnumerateArray());
        var legacy = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.list", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot })));
        Assert.Equal("pagination_required", legacy.Code);
    }

    [Fact]
    public async Task TrashSnapshots_RejectDifferentRootAndInvalidateAfterMutation()
    {
        using var workspace = new HostTestWorkspace();
        var service = new SnapshotTrashService(workspace.CacheRoot, 3);
        var application = new DesktopHostApplication(trashService: service);
        var first = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var token = first.GetProperty("snapshotToken").GetString();
        var other = Directory.CreateDirectory(Path.Combine(workspace.Root, "other")).FullName;
        var changedRoot = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = other, snapshotToken = token })));
        Assert.Equal("stale_trash", changedRoot.Code);
        await DispatchAsync(application, "trash.restore", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds = new[] { service.Entries[0].TrashPath } }));
        var stale = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, snapshotToken = token })));
        Assert.Equal("stale_trash", stale.Code);
    }

    [Fact]
    public async Task TrashSnapshotPurge_ResolvesTheWholeLargeSnapshotAndRejectsExternalChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new HostTestWorkspace();
        var service = new SnapshotTrashService(workspace.CacheRoot, 11_000);
        var application = new DesktopHostApplication(trashService: service);
        var first = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, pageSize = 1 }));
        var token = first.GetProperty("snapshotToken").GetString();
        service.Entries.RemoveAt(0);
        var stale = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.purgeSnapshot", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, snapshotToken = token, confirmed = true })));
        Assert.Equal("stale_trash", stale.Code);
        Assert.Equal(10_999, service.Entries.Count);
        var refreshed = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, pageSize = 1 }));
        var result = await DispatchAsync(application, "trash.purgeSnapshot", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, snapshotToken = refreshed.GetProperty("snapshotToken").GetString(), confirmed = true }));
        Assert.Equal(10_999, result.GetProperty("purged").GetArrayLength());
        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task TrashPages_DoNotTruncateOversizedPathIdentities()
    {
        using var workspace = new HostTestWorkspace();
        var service = new SnapshotTrashService(workspace.CacheRoot, 1);
        service.Entries[0] = service.Entries[0] with { TrashPath = new string('x', 4097) };
        var application = new DesktopHostApplication(trashService: service);
        foreach (var method in new[] { "trash.page", "trash.list" })
        {
            var error = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, method, JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot })));
            Assert.Equal("unsafe_path", error.Code);
        }
    }

    private sealed class SnapshotTrashService(string root, int count) : ICacheTrashService
    {
        public List<CacheTrashEntry> Entries { get; } = Enumerable.Range(1, count).Select(avid => new CacheTrashEntry(
            avid, Path.Combine(root, ".trash", avid.ToString()), Path.Combine(root, avid.ToString()), DateTimeOffset.UnixEpoch, 1, 1, true, null)).ToList();
        public int ListCalls { get; private set; }
        public string GetTrashDirectory(string rootDirectory) => Path.Combine(rootDirectory, ".trash");
        public IReadOnlyList<CacheTrashEntry> ListEntries(string rootDirectory, CancellationToken cancellationToken = default)
        { ListCalls++; cancellationToken.ThrowIfCancellationRequested(); return Entries.ToArray(); }
        public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath)
        {
            Entries.RemoveAll(entry => entry.TrashPath == trashPath);
            return new(avid, true, true, Path.Combine(rootDirectory, avid.ToString()), trashPath, null);
        }
        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid) => throw new NotSupportedException();
        public CacheTrashStatistics GetStatistics(string rootDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public CacheTrashPurgeResult Purge(string rootDirectory, bool includeUntrustedLegacyEntries = false, IReadOnlyCollection<string>? expectedEntryIds = null)
        {
            if (expectedEntryIds is null || !Entries.Select(entry => entry.TrashPath).ToHashSet().SetEquals(expectedEntryIds))
                throw new CacheTrashSnapshotMismatchException(expectedEntryIds?.Count ?? 0, Entries.Count);
            var count = Entries.Count;
            Entries.Clear();
            return new(count, count, 0, 0, null);
        }
    }
}
