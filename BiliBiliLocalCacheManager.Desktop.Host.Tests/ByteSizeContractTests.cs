using System.Text.Json;
using System.Text.Json.Nodes;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    private const long MaximumSafeWireBytes = 9_007_199_254_740_991;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_LegacyUnknownTimestampRemainsExplicitlyNullOnTheWire(bool omitTimestamp)
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Legacy cache");
        var entryPath = SetEntryByteField(workspace, 101, 1, "time_update_stamp", 0);
        if (omitTimestamp)
        {
            var entry = JsonNode.Parse(File.ReadAllText(entryPath))!.AsObject();
            entry.Remove("time_update_stamp");
            File.WriteAllText(entryPath, entry.ToJsonString());
        }

        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));
        Assert.Equal(JsonValueKind.Null, scan.GetProperty("items")[0].GetProperty("lastUpdated").ValueKind);
        var token = scan.GetProperty("indexToken").GetString();
        var search = await DispatchAsync(application, "search", JsonSerializer.Serialize(new { indexToken = token, keyword = "Legacy" }));
        Assert.Equal(JsonValueKind.Null, search.GetProperty("items")[0].GetProperty("lastUpdated").ValueKind);
        var details = await DispatchAsync(application, "cache.details", JsonSerializer.Serialize(new { indexToken = token, avid = "101" }));
        Assert.Equal(JsonValueKind.Null, details.GetProperty("item").GetProperty("lastUpdated").ValueKind);
    }

    [Theory]
    [InlineData("total_bytes", MaximumSafeWireBytes + 1)]
    [InlineData("downloaded_bytes", MaximumSafeWireBytes + 1)]
    [InlineData("guessed_total_bytes", MaximumSafeWireBytes + 1)]
    [InlineData("total_bytes", long.MaxValue)]
    [InlineData("downloaded_bytes", long.MaxValue)]
    [InlineData("guessed_total_bytes", long.MaxValue)]
    public async Task Scan_UnsafeByteMetadataBecomesAnIssueWithoutBlockingHealthyPages(string field, long value)
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Healthy cache");
        workspace.CreateCache(202, "Invalid byte metadata");
        var invalidPath = SetEntryByteField(workspace, 202, 1, field, value);
        var application = workspace.CreateApplication();

        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, persistSettings = false, pageSize = 1
        }));

        Assert.Equal(1, scan.GetProperty("invalidEntries").GetInt32());
        Assert.Equal(1, scan.GetProperty("includedEntries").GetInt32());
        Assert.Equal(1, scan.GetProperty("totalItems").GetInt32());
        Assert.False(scan.GetProperty("hasMore").GetBoolean());
        Assert.True(scan.GetProperty("hasWarnings").GetBoolean());
        var issue = Assert.Single(scan.GetProperty("issues").EnumerateArray());
        Assert.Equal("InvalidEntry", issue.GetProperty("kind").GetString());
        Assert.Equal(invalidPath, issue.GetProperty("path").GetString());
        var item = Assert.Single(scan.GetProperty("items").EnumerateArray());
        Assert.Equal("101", item.GetProperty("avid").GetString());
        Assert.Equal(1000, item.GetProperty("sizeBytes").GetInt64());

        var token = scan.GetProperty("indexToken").GetString();
        var search = await DispatchAsync(application, "search", JsonSerializer.Serialize(new
        {
            indexToken = token, keyword = "Healthy", pageSize = 1
        }));
        Assert.Equal(1, search.GetProperty("totalItems").GetInt32());
        Assert.Equal("101", Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("avid").GetString());
        var details = await DispatchAsync(application, "cache.details", JsonSerializer.Serialize(new
        {
            indexToken = token, avid = "101"
        }));
        Assert.Equal(1000, Assert.Single(details.GetProperty("segments").EnumerateArray()).GetProperty("sizeBytes").GetInt64());
        var location = await DispatchAsync(application, "scan.issueLocation", JsonSerializer.Serialize(new
        {
            indexToken = token, issueId = 0
        }));
        Assert.Equal(Path.GetDirectoryName(invalidPath), location.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Scan_MaximumSafeByteSizeRemainsExactInSearchAndDetails()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Boundary cache");
        foreach (var field in new[] { "total_bytes", "downloaded_bytes", "guessed_total_bytes" })
            SetEntryByteField(workspace, 101, 1, field, MaximumSafeWireBytes);
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));

        Assert.Equal(0, scan.GetProperty("invalidEntries").GetInt32());
        Assert.Empty(scan.GetProperty("issues").EnumerateArray());
        Assert.Equal(MaximumSafeWireBytes, scan.GetProperty("items")[0].GetProperty("sizeBytes").GetInt64());
        var token = scan.GetProperty("indexToken").GetString();
        var search = await DispatchAsync(application, "search", JsonSerializer.Serialize(new { indexToken = token, keyword = "Boundary" }));
        Assert.Equal(MaximumSafeWireBytes, search.GetProperty("items")[0].GetProperty("sizeBytes").GetInt64());
        var details = await DispatchAsync(application, "cache.details", JsonSerializer.Serialize(new { indexToken = token, avid = "101" }));
        Assert.Equal(MaximumSafeWireBytes, details.GetProperty("item").GetProperty("sizeBytes").GetInt64());
        Assert.Equal(MaximumSafeWireBytes, details.GetProperty("segments")[0].GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Scan_UnsafeAggregateRejectsOnlyTheOffendingSegment()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Aggregate cache", 2);
        workspace.CreateCache(202, "Healthy cache");
        SetEntryByteField(workspace, 101, 1, "total_bytes", MaximumSafeWireBytes);
        var invalidPath = SetEntryByteField(workspace, 101, 2, "total_bytes", 1);
        var application = workspace.CreateApplication();
        var scan = await DispatchAsync(application, "scan", JsonSerializer.Serialize(new { rootPath = workspace.CacheRoot }));

        Assert.Equal(1, scan.GetProperty("invalidEntries").GetInt32());
        Assert.Equal(2, scan.GetProperty("includedEntries").GetInt32());
        Assert.Equal(2, scan.GetProperty("totalItems").GetInt32());
        Assert.Equal(invalidPath, Assert.Single(scan.GetProperty("issues").EnumerateArray()).GetProperty("path").GetString());
        Assert.All(scan.GetProperty("items").EnumerateArray(), item =>
            Assert.InRange(item.GetProperty("sizeBytes").GetInt64(), 0, MaximumSafeWireBytes));
        var details = await DispatchAsync(application, "cache.details", JsonSerializer.Serialize(new
        {
            indexToken = scan.GetProperty("indexToken").GetString(), avid = "101"
        }));
        Assert.Equal(1, details.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, Assert.Single(details.GetProperty("segments").EnumerateArray()).GetProperty("pageIndex").GetInt32());
        Assert.Equal(MaximumSafeWireBytes, details.GetProperty("item").GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Playback_ImplicitScanAlsoExcludesUnsafeByteMetadata()
    {
        using var workspace = new HostTestWorkspace();
        workspace.CreateCache(101, "Healthy cache");
        workspace.CreateCache(202, "Invalid byte metadata");
        SetEntryByteField(workspace, 202, 1, "total_bytes", long.MaxValue);
        var launcher = new QueueRecorder();
        var application = new DesktopHostApplication(launcher);
        var result = await DispatchAsync(application, "play", JsonSerializer.Serialize(new
        {
            rootPath = workspace.CacheRoot, targets = new[] { new { avid = "101" }, new { avid = "202" } }
        }));

        Assert.Equal(1, result.GetProperty("queued").GetInt32());
        Assert.Equal("202", Assert.Single(result.GetProperty("failures").EnumerateArray()).GetProperty("avid").GetString());
        Assert.Equal(1, launcher.Calls);
    }

    private static string SetEntryByteField(HostTestWorkspace workspace, long avid, int page, string field, long value)
    {
        var entryPath = Path.Combine(workspace.CacheRoot, avid.ToString(System.Globalization.CultureInfo.InvariantCulture), $"c_{page}", "entry.json");
        var entry = JsonNode.Parse(File.ReadAllText(entryPath))!.AsObject();
        entry[field] = value;
        File.WriteAllText(entryPath, entry.ToJsonString());
        return entryPath;
    }
}
