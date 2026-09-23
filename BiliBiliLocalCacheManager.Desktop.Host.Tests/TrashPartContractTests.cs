using System.Text.Json;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    [Fact]
    public async Task PartTrash_MovesCrossVideoSelectionAndReturnsExactUndoEntries()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(100, "first", 2);
        workspace.CreateCache(200, "second", 2);
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var moved = await DispatchAsync(application, "trash.move", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, indexToken = scan.GetProperty("indexToken").GetString(),
            targets = new[] { new { avid = "100", pageIndexes = new[] { 1 } }, new { avid = "200", pageIndexes = new[] { 2 } } }
        }));
        Assert.Equal(2, moved.GetProperty("moved").GetArrayLength());
        Assert.Equal(2, moved.GetProperty("entryIds").GetArrayLength());
        Assert.True(Directory.Exists(Path.Combine(workspace.CacheRoot, "100", "c_2")));
        Assert.True(Directory.Exists(Path.Combine(workspace.CacheRoot, "200", "c_1")));
        Assert.False(Directory.Exists(Path.Combine(workspace.CacheRoot, "100", "c_1")));
        var restored = await DispatchAsync(application, "trash.restore", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot,
            entryIds = moved.GetProperty("entryIds").EnumerateArray().Select(item => item.GetString()).ToArray()
        }));
        Assert.Equal(2, restored.GetProperty("restored").GetArrayLength());
    }

    [Fact]
    public async Task PartTrash_RejectsChangedSourceAndStaleIndex()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(100, "first", 2);
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var request = JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, indexToken = scan.GetProperty("indexToken").GetString(),
            targets = new[] { new { avid = "100", pageIndexes = new[] { 1 } } }
        });
        File.AppendAllText(Path.Combine(workspace.CacheRoot, "100", "c_1", "entry.json"), " ");
        var result = await DispatchAsync(application, "trash.move", request);
        Assert.Empty(result.GetProperty("moved").EnumerateArray());
        Assert.Single(result.GetProperty("failed").EnumerateArray());
        Assert.True(Directory.Exists(Path.Combine(workspace.CacheRoot, "100", "c_1")));
        await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var stale = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.move", request));
        Assert.Equal("stale_index", stale.Code);
    }

    [Fact]
    public async Task PartTrash_RejectsAmbiguousPhysicalDirectoriesWithoutMovingEither()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(100, "first");
        var duplicate = Directory.CreateDirectory(Path.Combine(workspace.CacheRoot, "100", "duplicate")).FullName;
        File.Copy(Path.Combine(workspace.CacheRoot, "100", "c_1", "entry.json"), Path.Combine(duplicate, "entry.json"));
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.Equal(2, scan.GetProperty("items")[0].GetProperty("segmentCount").GetInt32());
        Assert.Equal(1, scan.GetProperty("items")[0].GetProperty("pageCount").GetInt32());
        var result = await DispatchAsync(application, "trash.move", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, indexToken = scan.GetProperty("indexToken").GetString(),
            targets = new[] { new { avid = "100", pageIndexes = new[] { 1 } } }
        }));
        Assert.Empty(result.GetProperty("moved").EnumerateArray());
        Assert.Single(result.GetProperty("failed").EnumerateArray());
        Assert.True(Directory.Exists(duplicate));
        Assert.True(Directory.Exists(Path.Combine(workspace.CacheRoot, "100", "c_1")));
    }

    [Fact]
    public async Task PartTrash_RestoreReportsConflictAndStillRestoresOtherPart()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(100, "first", 2);
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var result = await DispatchAsync(application, "trash.move", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, indexToken = scan.GetProperty("indexToken").GetString(),
            targets = new[] { new { avid = "100", pageIndexes = new[] { 1, 2, 1 } } }
        }));
        Assert.Equal(2, result.GetProperty("entryIds").GetArrayLength());
        Directory.CreateDirectory(Path.Combine(workspace.CacheRoot, "100", "c_1"));
        var restored = await DispatchAsync(application, "trash.restore", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot,
            entryIds = result.GetProperty("entryIds").EnumerateArray().Select(item => item.GetString()).ToArray()
        }));
        Assert.Single(restored.GetProperty("restored").EnumerateArray());
        Assert.Single(restored.GetProperty("failed").EnumerateArray());
        var failure = restored.GetProperty("items").EnumerateArray().Single(item => !item.GetProperty("succeeded").GetBoolean());
        Assert.Equal(1, failure.GetProperty("pageIndex").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("error").GetString()));
        var remaining = await DispatchAsync(application, "trash.page", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.Equal(1, remaining.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, remaining.GetProperty("items")[0].GetProperty("pageIndex").GetInt32());
        Assert.True(Directory.Exists(Path.Combine(workspace.CacheRoot, "100", "c_2")));
    }

    [Theory]
    [InlineData("trash.purge")]
    [InlineData("trash.purgeSnapshot")]
    public async Task TrashPurge_RequiresExactTypedConfirmation(string method)
    {
        using var workspace = new HostTestWorkspace();
        var application = workspace.CreateApplication();
        foreach (var confirmationText in new string?[] { null, "", "永久", "永久删除 " })
        {
            var error = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, method,
                JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, confirmed = true, confirmationText,
                    entryIds = new[] { "unused" }, snapshotToken = "unused" })));
            Assert.Equal(OperatingSystem.IsWindows() ? "confirmation_required" : "unsupported_platform", error.Code);
        }
    }

    [Fact]
    public async Task TrashRestore_RejectsOversizedIdentityResultBeforeInvokingService()
    {
        using var workspace = new HostTestWorkspace();
        var service = new SnapshotTrashService(workspace.CacheRoot, 1);
        var application = new DesktopHostApplication(trashService: service);
        var error = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.restore",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot,
                entryIds = Enumerable.Range(0, 2000).Select(index => new string('x', 4000) + index).ToArray() })));
        Assert.Equal("invalid_params", error.Code);
        Assert.Equal(0, service.ListCalls);
        Assert.Single(service.Entries);
    }

    [Fact]
    public async Task PartTrash_RejectsDifferentRootAndMixedSelection()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(100, "first", 2);
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        var token = scan.GetProperty("indexToken").GetString();
        var other = Directory.CreateDirectory(Path.Combine(workspace.Root, "other")).FullName;
        var wrongRoot = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.move", JsonSerializer.Serialize(new
        {
            rootPath = other, indexToken = token, targets = new[] { new { avid = "100", pageIndexes = new[] { 1 } } }
        })));
        Assert.Equal("stale_index", wrongRoot.Code);
        var mixed = await Assert.ThrowsAsync<RpcException>(() => DispatchAsync(application, "trash.move", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, indexToken = token,
            targets = new object[] { new { avid = "100", pageIndexes = new[] { 1 } }, new { avid = "100" } }
        })));
        Assert.Equal("invalid_params", mixed.Code);
        Assert.True(Directory.Exists(Path.Combine(workspace.CacheRoot, "100", "c_1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrashMutation_ReportsOnlyCompletedItemCounts(bool restore)
    {
        using var workspace = new HostTestWorkspace();
        foreach (var avid in new[] { 101L, 202L, 303L }) workspace.CreateCache(avid, avid.ToString());
        var service = new ObservedTrashService();
        var requested = PrepareTrashMutation(service, workspace.CacheRoot, restore);
        var attempts = 0;
        service.BeforeMutation = () =>
        {
            if (++attempts == 2) throw new IOException("simulated per-item failure");
        };
        var method = restore ? "trash.restore" : "trash.move";
        var application = new DesktopHostApplication(trashService: service);
        var counts = new List<int>();
        application.ProgressReported += (_, value) =>
        {
            if (value.Operation != method) return;
            Assert.Equal("measure", value.Phase);
            Assert.Equal(attempts, value.Current);
            counts.Add(value.Current!.Value);
            Assert.Equal(attempts, JsonSerializer.SerializeToElement(value.Details).GetProperty("itemsProcessed").GetInt32());
        };
        var parameters = restore
            ? JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, entryIds = requested })
            : JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, avids = requested });
        await DispatchAsync(application, method, parameters);
        Assert.Equal(new[] { 1, 2, 3 }, counts);
    }
}
