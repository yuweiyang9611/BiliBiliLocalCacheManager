using System.Text.Json;
using System.Text.Json.Nodes;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    private static Task<JsonElement> RefreshDispatchAsync(DesktopHostApplication application, string method, object parameters)
        => DispatchAsync(application, method, JsonSerializer.Serialize(parameters));

    [Fact]
    public async Task MutationRefresh_ReusesUnchangedVideosUntilExplicitFullScan()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "changed", 2);
        workspace.CreateCache(202, "unchanged");
        var application = workspace.CreateApplication();
        var first = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        var token = first.GetProperty("indexToken").GetString();
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = token, targets = new[] { new { avid = "101", pageIndexes = new[] { 1 } } } });
        Assert.Single(moved.GetProperty("moved").EnumerateArray());
        await Assert.ThrowsAsync<RpcException>(() => RefreshDispatchAsync(application, "search", new { indexToken = token }));
        workspace.CreateCache(202, "external change");
        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, incremental = true });
        Assert.Equal("unchanged", refreshed.GetProperty("items").EnumerateArray().Single(v => v.GetProperty("avid").GetString() == "202").GetProperty("title").GetString());
        Assert.Equal(2, refreshed.GetProperty("includedEntries").GetInt32());
        Assert.NotEqual(token, refreshed.GetProperty("indexToken").GetString());
        var full = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        Assert.Equal("external change", full.GetProperty("items").EnumerateArray().Single(v => v.GetProperty("avid").GetString() == "202").GetProperty("title").GetString());
    }

    [Fact]
    public async Task MutationRefresh_MoveAndRestoreMatchFullScan()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "parts", 2);
        workspace.CreateCache(202, "other");
        var application = workspace.CreateApplication();
        var first = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = first.GetProperty("indexToken").GetString(), targets = new[] { new { avid = "101", pageIndexes = new[] { 1 } } } });
        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, incremental = true });
        var full = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        Assert.Equal(full.GetProperty("items").GetRawText(), refreshed.GetProperty("items").GetRawText());
        await RefreshDispatchAsync(application, "trash.restore", new { rootPath = workspace.CacheRoot,
            entryIds = moved.GetProperty("entryIds").EnumerateArray().Select(v => v.GetString()).ToArray() });
        refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, incremental = true });
        full = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        Assert.Equal(full.GetProperty("items").GetRawText(), refreshed.GetProperty("items").GetRawText());
        Assert.Equal(full.GetProperty("includedEntries").GetInt32(), refreshed.GetProperty("includedEntries").GetInt32());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing-metadata")]
    [InlineData("incomplete")]
    public async Task MutationRefresh_IncompleteBaselineFallsBackToFullScan(string defect)
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "changed", 2);
        workspace.CreateCache(202, "other");
        workspace.CreateCache(303, "excluded");
        var entry = Path.Combine(workspace.CacheRoot, "303", "c_1", "entry.json");
        if (defect == "invalid") File.WriteAllText(entry, "{broken");
        else if (defect == "missing-metadata") File.Delete(entry);
        else
        {
            var metadata = JsonNode.Parse(File.ReadAllText(entry))!;
            metadata["is_completed"] = false;
            File.WriteAllText(entry, metadata.ToJsonString());
        }
        var application = workspace.CreateApplication();
        var first = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, includeIncomplete = false });
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = first.GetProperty("indexToken").GetString(), targets = new[] { new { avid = "101", pageIndexes = new[] { 1 } } } });
        Assert.Single(moved.GetProperty("moved").EnumerateArray());
        workspace.CreateCache(202, "external update");

        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, includeIncomplete = false, incremental = true });
        Assert.Equal("external update", refreshed.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("avid").GetString() == "202").GetProperty("title").GetString());
        var full = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, includeIncomplete = false });
        AssertMutationRefreshReportEqual(full, refreshed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationRefresh_WholeVideoAndLastPartRemovalAndRestoreMatchFullScan(bool lastPart)
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "removed");
        workspace.CreateCache(202, "retained");
        var application = workspace.CreateApplication();
        var first = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        object target = lastPart ? new { avid = "101", pageIndexes = new[] { 1 } } : new { avid = "101" };
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = first.GetProperty("indexToken").GetString(), targets = new[] { target } });
        Assert.Single(moved.GetProperty("moved").EnumerateArray());
        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, incremental = true });
        Assert.Equal(1, refreshed.GetProperty("includedEntries").GetInt32());
        var full = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        AssertMutationRefreshReportEqual(full, refreshed);

        var restored = await RefreshDispatchAsync(application, "trash.restore", new { rootPath = workspace.CacheRoot,
            entryIds = moved.GetProperty("entryIds").EnumerateArray().Select(value => value.GetString()).ToArray() });
        Assert.Single(restored.GetProperty("restored").EnumerateArray());
        refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, incremental = true });
        full = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        AssertMutationRefreshReportEqual(full, refreshed);
        Assert.Equal(2, refreshed.GetProperty("includedEntries").GetInt32());
    }

    [Fact]
    public async Task MutationRefresh_ReusedPartIdentityRejectsPhysicalDirectoryReplacement()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "changed", 2);
        workspace.CreateCache(202, "unchanged");
        var application = workspace.CreateApplication();
        var first = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = first.GetProperty("indexToken").GetString(), targets = new[] { new { avid = "101", pageIndexes = new[] { 1 } } } });
        Assert.Single(moved.GetProperty("moved").EnumerateArray());
        var partPath = Path.Combine(workspace.CacheRoot, "202", "c_1");
        var originalPath = Path.Combine(workspace.Root, "preserved-original-202");
        var originalMetadata = File.ReadAllBytes(Path.Combine(partPath, "entry.json"));
        Directory.Move(partPath, originalPath);
        workspace.CreateCache(202, "replacement");
        // Keep entry contents identical so the physical identity, rather than a
        // metadata hash change, is what must prevent moving the replacement.
        File.WriteAllBytes(Path.Combine(partPath, "entry.json"), originalMetadata);

        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, incremental = true });
        var rejected = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = refreshed.GetProperty("indexToken").GetString(), targets = new[] { new { avid = "202", pageIndexes = new[] { 1 } } } });
        Assert.Empty(rejected.GetProperty("moved").EnumerateArray());
        Assert.Single(rejected.GetProperty("failed").EnumerateArray());
        Assert.Contains("replaced", rejected.GetProperty("items")[0].GetProperty("error").GetString());
        Assert.True(File.Exists(Path.Combine(partPath, "entry.json")));
        Assert.True(File.Exists(Path.Combine(originalPath, "entry.json")));
    }

    [Fact]
    public async Task MutationRefresh_CancelledRefreshKeepsThePendingMutationScope()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "changed", 2);
        workspace.CreateCache(202, "unchanged");
        var application = workspace.CreateApplication();
        var first = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot });
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = first.GetProperty("indexToken").GetString(), targets = new[] { new { avid = "101", pageIndexes = new[] { 1 } } } });
        Assert.Single(moved.GetProperty("moved").EnumerateArray());
        workspace.CreateCache(202, "external update");
        using var cancellation = new CancellationTokenSource();
        application.ProgressReported += (_, progress) =>
        {
            if (progress.Operation == "scan" && progress.Stage == "scanning") cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DispatchAsync(application, "scan",
            JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot, incremental = true }), cancellation.Token));
        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, incremental = true });
        Assert.Equal("unchanged", refreshed.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("avid").GetString() == "202").GetProperty("title").GetString());
        Assert.Equal(2, refreshed.GetProperty("includedEntries").GetInt32());
    }

    [Fact]
    public async Task MutationRefresh_SettingsChangeDuringRefreshRejectsOldGeneration()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "changed", 2);
        workspace.CreateCache(202, "unchanged");
        var application = workspace.CreateApplication();
        var first = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, includeIncomplete = false });
        var moved = await RefreshDispatchAsync(application, "trash.move", new { rootPath = workspace.CacheRoot,
            indexToken = first.GetProperty("indexToken").GetString(), targets = new[] { new { avid = "101", pageIndexes = new[] { 1 } } } });
        Assert.Single(moved.GetProperty("moved").EnumerateArray());
        workspace.CreateCache(202, "external update");
        var settingsChanged = false;
        application.ProgressReported += (_, progress) =>
        {
            if (settingsChanged || progress.Operation != "scan" || progress.Stage != "scanning") return;
            settingsChanged = true;
            RefreshDispatchAsync(application, "settings.update", new { includeIncomplete = true }).GetAwaiter().GetResult();
        };
        var stale = await Assert.ThrowsAsync<RpcException>(() => RefreshDispatchAsync(application, "scan",
            new { rootPath = workspace.CacheRoot, includeIncomplete = false, incremental = true }));
        Assert.Equal("stale_index", stale.Code);
        Assert.True(settingsChanged);
        var refreshed = await RefreshDispatchAsync(application, "scan", new { rootPath = workspace.CacheRoot, includeIncomplete = true, incremental = true });
        Assert.Equal("external update", refreshed.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("avid").GetString() == "202").GetProperty("title").GetString());
    }

    private static void AssertMutationRefreshReportEqual(JsonElement expected, JsonElement actual)
    {
        foreach (var property in new[] { "items", "scannedAvidDirectories", "scannedSegmentDirectories", "includedEntries",
                     "skippedIncompleteEntries", "invalidEntries", "inaccessibleDirectories", "hasWarnings", "issues", "issuesTruncated" })
            Assert.Equal(expected.GetProperty(property).GetRawText(), actual.GetProperty(property).GetRawText());
    }
}
