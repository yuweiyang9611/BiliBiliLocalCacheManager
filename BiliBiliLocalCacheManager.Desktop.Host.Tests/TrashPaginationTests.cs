using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    [Fact]
    public async Task TrashPages_ShowRestoreAvailabilityAndRevalidateBeforeRestoring()
    {
        using var workspace = new HostTestWorkspace();
        var service = new SnapshotTrashService(workspace.CacheRoot, 2);
        service.Entries[1] = service.Entries[1] with { IsRestorable = false, UnavailableReason = "清理日志待恢复。" };
        var application = new DesktopHostApplication(trashService: service);
        var parameters = JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot });
        var page = await DispatchAsync(application, "trash.page", parameters);
        var entries = page.GetProperty("items");
        Assert.Equal(2, page.GetProperty("totalItems").GetInt32());
        Assert.True(entries[0].GetProperty("isRestorable").GetBoolean());
        Assert.False(entries[1].GetProperty("isRestorable").GetBoolean());
        Assert.Equal("清理日志待恢复。", entries[1].GetProperty("unavailableReason").GetString());

        service.Entries[0] = service.Entries[0] with { IsRestorable = false, UnavailableReason = "身份已变化。" };
        var restore = await DispatchAsync(application, "trash.restore", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, entryIds = new[] { entries[0].GetProperty("id").GetString() }
        }));
        Assert.Empty(restore.GetProperty("restored").EnumerateArray());
        Assert.Equal("身份已变化。", restore.GetProperty("items")[0].GetProperty("error").GetString());
    }

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
        var stale = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.purgeSnapshot", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, snapshotToken = token, confirmed = true, confirmationText = "永久删除" })));
        Assert.Equal("stale_trash", stale.Code);
        Assert.Equal(10_999, service.Entries.Count);
        var refreshed = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, pageSize = 1 }));
        var result = await DispatchAsync(application, "trash.purgeSnapshot", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, snapshotToken = refreshed.GetProperty("snapshotToken").GetString(), confirmed = true, confirmationText = "永久删除" }));
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

    [Fact]
    public async Task TrashPages_OverBudgetAllowsBoundedRestoreAndRefusesPermanentPurgeUntilItFits()
    {
        using var workspace = new HostTestWorkspace();
        var rootPath = workspace.CacheRoot;
        var service = CreateOverBudgetTrash(rootPath);
        var application = new DesktopHostApplication(trashService: service);
        var first = await RefreshDispatchAsync(application, "trash.page", new { rootPath, pageSize = 1 });
        var snapshotToken = first.GetProperty("snapshotToken").GetString();
        Assert.False(first.GetProperty("canPurgeSnapshot").GetBoolean());
        Assert.Equal(1000, first.GetProperty("totalItems").GetInt32());
        var last = await RefreshDispatchAsync(application, "trash.page", new { rootPath, snapshotToken, offset = 999, pageSize = 5 });
        var lastEntry = Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("id").GetString();
        Assert.Equal(service.Entries[^1].TrashPath, lastEntry);
        Assert.False(last.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, service.ListCalls);
        if (OperatingSystem.IsWindows())
        {
            var rejected = await Assert.ThrowsAsync<RpcException>(() => RefreshDispatchAsync(application, "trash.purgeSnapshot",
                new { rootPath, snapshotToken, confirmed = true, confirmationText = "永久删除" }));
            Assert.Equal("trash_too_large", rejected.Code);
            var legacy = await Assert.ThrowsAsync<RpcException>(() => RefreshDispatchAsync(application, "trash.purge",
                new { rootPath, entryIds = service.Entries.Select(entry => entry.TrashPath), confirmed = true, confirmationText = "永久删除" }));
            Assert.Equal("trash_too_large", legacy.Code);
            Assert.Equal(0, service.PurgeCalls);
        }
        var restored = await RefreshDispatchAsync(application, "trash.restore", new { rootPath, entryIds = new[] { lastEntry } });
        Assert.Equal(lastEntry, Assert.Single(restored.GetProperty("restored").EnumerateArray()).GetString());
        var stale = await Assert.ThrowsAsync<RpcException>(() => RefreshDispatchAsync(application, "trash.page", new { rootPath, snapshotToken }));
        Assert.Equal("stale_trash", stale.Code);
        var updated = await RefreshDispatchAsync(application, "trash.page", new { rootPath, pageSize = 1 });
        Assert.True(updated.GetProperty("canPurgeSnapshot").GetBoolean());
        Assert.Equal(999, updated.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task TrashPages_BrowseOnlySnapshotRejectsDifferentRootAndChangedRestoreStatus()
    {
        using var workspace = new HostTestWorkspace();
        var rootPath = workspace.CacheRoot;
        var service = CreateOverBudgetTrash(rootPath);
        var application = new DesktopHostApplication(trashService: service);
        var page = await RefreshDispatchAsync(application, "trash.page", new { rootPath, pageSize = 1 });
        var snapshotToken = page.GetProperty("snapshotToken").GetString();
        var otherRoot = Directory.CreateDirectory(Path.Combine(workspace.Root, "other")).FullName;
        var wrongRoot = await Assert.ThrowsAsync<RpcException>(() => RefreshDispatchAsync(application, "trash.page", new { rootPath = otherRoot, snapshotToken }));
        Assert.Equal("stale_trash", wrongRoot.Code);
        service.Entries[^1] = service.Entries[^1] with { IsRestorable = false };
        var stale = await Assert.ThrowsAsync<RpcException>(() => RefreshDispatchAsync(application, "trash.page", new { rootPath, snapshotToken, offset = 1 }));
        Assert.Equal("stale_trash", stale.Code);
        Assert.Equal(2, service.ListCalls);
    }

    private static SnapshotTrashService CreateOverBudgetTrash(string root)
    {
        var service = new SnapshotTrashService(root, 1000);
        // 1,000 entries exceed the 16 MiB escaped identity budget; restoring one puts the rest under it.
        for (var index = 0; index < service.Entries.Count; index++)
        {
            var entry = service.Entries[index];
            service.Entries[index] = entry with { TrashPath = entry.TrashPath.PadRight(2797, 'x') };
        }
        return service;
    }

    [Fact]
    public async Task PreparedRestore_ConsumesExactMoveIdsOnceAndRechecksLiveRestoreStatus()
    {
        using var workspace = new HostTestWorkspace();
        var rootPath = workspace.CacheRoot;
        var application = workspace.CreateApplication();
        foreach (var avid in new[] { 10, 20, 30, 40 }) workspace.CreateCache(avid, $"Video {avid}");
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath, avids = new[] { "10", "20", "30", "40" } });
        var ids = moved.GetProperty("entryIds").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
        Assert.Equal(4, ids.Length);
        var restoreRequestId = Guid.NewGuid().ToString("D");
        Task<JsonElement> Prepare(int offset, string[] entryIds) => RefreshDispatchAsync(application, "trash.prepareRestore",
            new { rootPath, restoreRequestId, offset, total = ids.Length, entryIds });
        Task<object?> Commit(string id, string root, CancellationToken token = default) => application.DispatchAsync(id, "trash.restore",
            JsonSerializer.SerializeToElement(new { rootPath = root, preparedCount = ids.Length }), token);

        Assert.Equal(2, (await Prepare(0, ids[..2])).GetProperty("receivedCount").GetInt32());
        await Assert.ThrowsAsync<RpcException>(() => Commit(restoreRequestId, rootPath));
        await Assert.ThrowsAsync<RpcException>(() => Prepare(0, ids[..2]));
        Assert.Equal(4, (await Prepare(2, ids[2..])).GetProperty("receivedCount").GetInt32());
        await Assert.ThrowsAsync<RpcException>(() => Commit(Guid.NewGuid().ToString("D"), rootPath));
        await Assert.ThrowsAsync<RpcException>(() => Commit(restoreRequestId, workspace.Root));
        Assert.All(ids, id => Assert.True(Directory.Exists(id)));

        Directory.CreateDirectory(Path.Combine(rootPath, "20")); // A destination became occupied after preparation.
        using var cancellation = new CancellationTokenSource();
        application.ProgressReported += (_, value) =>
        {
            if (value.RequestId == restoreRequestId && value.Current == 3) cancellation.Cancel();
        };
        var result = JsonSerializer.SerializeToElement(await Commit(restoreRequestId, rootPath, cancellation.Token), WireOptions);
        string?[] Ids(string field) => result.GetProperty(field).EnumerateArray().Select(entry => entry.GetString()).ToArray();
        Assert.Equal(new[] { ids[0], ids[2] }, Ids("restored"));
        Assert.Equal(new[] { ids[1] }, Ids("failed"));
        Assert.Equal(new[] { ids[3] }, Ids("unprocessed"));
        Assert.True(result.GetProperty("cancelled").GetBoolean());
        await Assert.ThrowsAsync<RpcException>(() => Commit(restoreRequestId, rootPath));
        Assert.True(File.Exists(Path.Combine(rootPath, "10", "c_1", "entry.json")));
        Assert.True(File.Exists(Path.Combine(rootPath, "30", "c_1", "entry.json")));
        Assert.True(Directory.Exists(ids[1]));
        Assert.True(Directory.Exists(ids[3]));
    }

    private sealed class SnapshotTrashService(string root, int count) : ICacheTrashService
    {
        public List<CacheTrashEntry> Entries { get; } = Enumerable.Range(1, count).Select(avid => new CacheTrashEntry(
            avid, Path.Combine(root, ".trash", avid.ToString()), Path.Combine(root, avid.ToString()), DateTimeOffset.UnixEpoch, 1, 1, true, null)).ToList();
        public int ListCalls { get; private set; }
        public int PurgeCalls { get; private set; }
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
            PurgeCalls++;
            if (expectedEntryIds is null || !Entries.Select(entry => entry.TrashPath).ToHashSet().SetEquals(expectedEntryIds))
                throw new CacheTrashSnapshotMismatchException(expectedEntryIds?.Count ?? 0, Entries.Count);
            var count = Entries.Count;
            Entries.Clear();
            return new(count, count, 0, 0, null);
        }
    }
}
