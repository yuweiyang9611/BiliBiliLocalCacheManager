using System.Text.Json;
using System.Text.Json.Nodes;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    [Fact]
    public async Task Scan_MissingCidKeepsOtherPartsUsableAndIdentifiesTheUnmovablePart()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Mixed video", 2);
        workspace.CreateCache(202, "Other video");
        var video = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "one-second.mp4"));
        var healthyPath = Path.Combine(workspace.CacheRoot, "101", "c_1", "lua.flv.bb2api.80", "0.mp4");
        File.WriteAllBytes(healthyPath, video);
        var incompleteIdentityPath = Path.Combine(workspace.CacheRoot, "101", "c_2", "entry.json");
        var metadata = JsonNode.Parse(File.ReadAllText(incompleteIdentityPath))!;
        Assert.True(metadata["page_data"]!.AsObject().Remove("cid"));
        File.WriteAllText(incompleteIdentityPath, metadata.ToJsonString());
        var application = workspace.CreateApplication();
        var rootPath = workspace.CacheRoot;

        var scan = await RefreshDispatchAsync(application, "scan", new { rootPath });
        var indexToken = scan.GetProperty("indexToken").GetString();
        Assert.Equal(3, scan.GetProperty("includedEntries").GetInt32());
        Assert.Equal(1, scan.GetProperty("unavailablePartIdentities").GetInt32());
        Assert.True(scan.GetProperty("hasWarnings").GetBoolean());
        var issue = Assert.Single(scan.GetProperty("issues").EnumerateArray());
        Assert.Equal(incompleteIdentityPath, issue.GetProperty("path").GetString());
        Assert.Equal("PartIdentityUnavailable", issue.GetProperty("kind").GetString());
        Assert.Contains("page_data/cid", issue.GetProperty("message").GetString());
        var search = await RefreshDispatchAsync(application, "search", new { indexToken, keyword = "av101" });
        Assert.Equal("101", Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("avid").GetString());
        var details = await RefreshDispatchAsync(application, "cache.details", new { indexToken, avid = "101" });
        var firstPart = details.GetProperty("segments").EnumerateArray().Single(item => item.GetProperty("pageIndex").GetInt32() == 1);
        Assert.True(firstPart.GetProperty("isPlayable").GetBoolean());

        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath, indexToken,
            targets = new[] { new { avid = "101", pageIndexes = new[] { 1, 2 } } } });
        Assert.Equal("101:P1", Assert.Single(moved.GetProperty("moved").EnumerateArray()).GetString());
        Assert.Equal("101:P2", Assert.Single(moved.GetProperty("failed").EnumerateArray()).GetString());
        Assert.Contains("page_data/cid", moved.GetProperty("items")[1].GetProperty("error").GetString());
        Assert.Equal(metadata.ToJsonString(), File.ReadAllText(incompleteIdentityPath));
        var trashEntry = Assert.Single(moved.GetProperty("entryIds").EnumerateArray()).GetString()!;
        Assert.Equal(video, File.ReadAllBytes(Path.Combine(trashEntry, "lua.flv.bb2api.80", "0.mp4")));

        workspace.CreateCache(202, "External update");
        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath, incremental = true, maxReportedIssues = 0 });
        Assert.Contains(refreshed.GetProperty("items").EnumerateArray(), row => row.GetProperty("title").GetString() == "External update");
        Assert.True(refreshed.GetProperty("hasWarnings").GetBoolean());
        Assert.Empty(refreshed.GetProperty("issues").EnumerateArray());
        Assert.True(refreshed.GetProperty("issuesTruncated").GetBoolean());
        var restored = await RefreshDispatchAsync(application, "trash.restore", new { rootPath, entryIds = new[] { trashEntry } });
        Assert.Equal(trashEntry, Assert.Single(restored.GetProperty("restored").EnumerateArray()).GetString());
        Assert.Equal(video, File.ReadAllBytes(healthyPath));
    }

    [Fact]
    public async Task Scan_ReportsPaddedAvDirectoriesAndMovesOnlyTheCanonicalEntry()
    {
        using var workspace = new HostTestWorkspace();
        var rootPath = workspace.CacheRoot;
        workspace.CreateCache(43, "First alias");
        Directory.Move(Path.Combine(rootPath, "43"), Path.Combine(rootPath, "00043"));
        workspace.CreateCache(44, "Second alias");
        Directory.Move(Path.Combine(rootPath, "44"), Path.Combine(rootPath, "00044"));
        workspace.CreateCache(44, "Canonical");
        var application = workspace.CreateApplication();
        var scan = await RefreshDispatchAsync(application, "scan", new { rootPath });
        Assert.Equal("Canonical", Assert.Single(scan.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(2, scan.GetProperty("invalidEntries").GetInt32());
        Assert.Equal(new[] { Path.Combine(rootPath, "00043"), Path.Combine(rootPath, "00044") },
            scan.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("path").GetString()));
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath, indexToken = scan.GetProperty("indexToken").GetString(),
            targets = new[] { new { avid = "43" }, new { avid = "44" } } });
        Assert.Equal("43", Assert.Single(moved.GetProperty("failed").EnumerateArray()).GetString());
        Assert.Equal("44", Assert.Single(moved.GetProperty("moved").EnumerateArray()).GetString());
        Assert.True(File.Exists(Path.Combine(rootPath, "00043", "c_1", "entry.json")));
        Assert.True(File.Exists(Path.Combine(rootPath, "00044", "c_1", "entry.json")));
        Assert.False(Directory.Exists(Path.Combine(rootPath, "44")));
    }

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
