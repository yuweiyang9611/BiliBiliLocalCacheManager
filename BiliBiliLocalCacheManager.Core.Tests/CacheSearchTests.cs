using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using Xunit;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class CacheSearchTests
{
    [Fact]
    public void Search_ShouldMatchOwnerName_WhenScopeIncludesOwner()
    {
        // 1) 构造两个缓存：一个 Owner=Alice，一个 Owner=Bob
        var cacheA = CreateCache(100, "视频A", "P1", ownerName: "Alice", bvid: "BV1");
        var cacheB = CreateCache(200, "视频B", "P2", ownerName: "Bob", bvid: "BV2");
        var index = new CacheIndex(new[] { cacheA, cacheB });

        // 2) 仅在 OwnerName 范围内搜索 Alice
        var options = new CacheSearchOptions
        {
            Keyword = "Alice",
            SplitKeywords = false,
            Scope = CacheSearchScope.OwnerName
        };

        var results = index.Search(options);

        // 3) 断言：只匹配到 Avid=100
        Assert.Single(results);
        Assert.Equal(100, results.First().Avid);
    }

    [Fact]
    public void Search_ShouldMatchBvid_WhenScopeIncludesBvid()
    {
        // 1) 构造两个缓存，Bvid 分别为 BV1 / BV2
        var cacheA = CreateCache(100, "视频A", "P1", ownerName: "Alice", bvid: "BV1");
        var cacheB = CreateCache(200, "视频B", "P2", ownerName: "Bob", bvid: "BV2");
        var index = new CacheIndex(new[] { cacheA, cacheB });

        // 2) 只在 Bvid 范围内搜索 BV2
        var options = new CacheSearchOptions
        {
            Keyword = "BV2",
            SplitKeywords = false,
            Scope = CacheSearchScope.Bvid
        };

        var results = index.Search(options);

        // 3) 断言：只匹配到 Avid=200
        Assert.Single(results);
        Assert.Equal(200, results.First().Avid);
    }

    [Fact]
    public void Search_ShouldMatchAvid_WhenScopeIncludesAvid()
    {
        // 1) 构造两个缓存，Avid 分别为 123 / 456
        var cacheA = CreateCache(123, "视频A", "P1", ownerName: "Alice", bvid: "BV1");
        var cacheB = CreateCache(456, "视频B", "P2", ownerName: "Bob", bvid: "BV2");
        var index = new CacheIndex(new[] { cacheA, cacheB });

        // 2) 只在 Avid 范围内搜索 456
        var options = new CacheSearchOptions
        {
            Keyword = "456",
            SplitKeywords = false,
            Scope = CacheSearchScope.Avid
        };

        var results = index.Search(options);

        // 3) 断言：只匹配到 Avid=456
        Assert.Single(results);
        Assert.Equal(456, results.First().Avid);
    }

    [Theory]
    [InlineData(CacheSearchMatchMode.Contains, "lph", true)]
    [InlineData(CacheSearchMatchMode.Equals, "alpha", true)]
    [InlineData(CacheSearchMatchMode.StartsWith, "alp", true)]
    [InlineData(CacheSearchMatchMode.EndsWith, "pha", true)]
    [InlineData(CacheSearchMatchMode.Equals, "alp", false)]
    [InlineData(CacheSearchMatchMode.StartsWith, "pha", false)]
    [InlineData(CacheSearchMatchMode.EndsWith, "alp", false)]
    public void Search_PreservesEveryMatchMode(CacheSearchMatchMode mode, string keyword, bool expected)
    {
        var index = new CacheIndex([CreateCache(1, "Alpha", "P1", null, null)]);
        var results = index.Search(new CacheSearchOptions
        {
            Keyword = keyword, MatchMode = mode, Scope = CacheSearchScope.Title
        });
        Assert.Equal(expected, results.Count == 1);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public void Search_MatchesTokensAcrossFieldsAndPreservesIndexOrder(bool requireAll, int count)
    {
        var index = new CacheIndex([
            CreateCache(30, "Alpha", "P1", null, null),
            CreateCache(10, "Alpha", "Beta", null, null),
            CreateCache(20, "Other", "Beta", null, null)]);
        var results = index.Search(new CacheSearchOptions
        {
            Keyword = "alpha beta", SplitKeywords = true,
            RequireAllKeywords = requireAll, Scope = CacheSearchScope.Title | CacheSearchScope.PartName
        });
        Assert.Equal(count, results.Count);
        Assert.Equal(requireAll ? [10L] : new[] { 30L, 10L, 20L }, results.Select(cache => cache.Avid));
    }

    [Fact]
    public void Search_CancelledEmptyIndexStillReportsCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => new CacheIndex([]).Search(
            new CacheSearchOptions { Keyword = "alpha" }, source.Token));
    }

    [Fact]
    public void Search_CaseSensitiveAndEmptyTokenRulesArePreserved()
    {
        var index = new CacheIndex([CreateCache(1, "Alpha", "P1", null, null)]);
        Assert.Empty(index.Search(new CacheSearchOptions
        {
            Keyword = "alpha", Scope = CacheSearchScope.Title, CaseSensitive = true
        }));
        Assert.Throws<ArgumentException>(() => index.Search(new CacheSearchOptions
        {
            Keyword = ",,,", SplitKeywords = true, KeywordSeparators = [',']
        }));
    }

    private static BiliVideoCache CreateCache(long avid, string title, string part, string? ownerName, string? bvid)
    {
        var segment = new BiliSegment(
            avid: avid,
            cid: 1,
            bvid: bvid,
            pageIndex: 1,
            partName: part,
            title: title,
            version: CacheVersion.Modern,
            typeTag: "type",
            mediaType: null,
            videoQuality: 80,
            qualityDescription: null,
            isCompleted: true,
            totalBytes: 1000,
            downloadedBytes: 1000,
            totalDuration: TimeSpan.FromSeconds(60),
            danmakuCount: 0,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            segmentDirectory: "segment",
            entryJsonPath: "entry.json",
            videoFiles: new[] { "video.mp4" },
            coverUrl: "cover",
            ownerName: ownerName,
            ownerId: 1);

        // 每个 avid 至少需要一个分段
        return new BiliVideoCache(avid, new[] { segment });
    }
}
