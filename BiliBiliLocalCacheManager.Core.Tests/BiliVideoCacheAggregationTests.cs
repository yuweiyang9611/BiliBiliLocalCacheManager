using BiliBiliLocalCacheManager.Core.Domain.Models;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class BiliVideoCacheAggregationTests
{
    [Fact]
    public void Totals_ShouldSaturateInsteadOfOverflowing()
    {
        var cache = new BiliVideoCache(1,
        [
            CreateSegment(long.MaxValue, TimeSpan.MaxValue, pageIndex: 1),
            CreateSegment(1, TimeSpan.FromTicks(1), pageIndex: 2)
        ]);

        Assert.Equal(long.MaxValue, cache.TotalSize);
        Assert.Equal(TimeSpan.MaxValue, cache.TotalDuration);
    }

    private static BiliSegment CreateSegment(
        long totalBytes,
        TimeSpan duration,
        int pageIndex)
    {
        return new BiliSegment(
            avid: 1,
            cid: pageIndex,
            bvid: null,
            pageIndex,
            partName: $"P{pageIndex}",
            title: "test",
            CacheVersion.Modern,
            typeTag: "80",
            mediaType: null,
            videoQuality: 80,
            qualityDescription: null,
            isCompleted: true,
            totalBytes,
            downloadedBytes: totalBytes,
            totalDuration: duration,
            danmakuCount: 0,
            createdAt: DateTimeOffset.MinValue,
            updatedAt: DateTimeOffset.MinValue,
            segmentDirectory: $"segment-{pageIndex}",
            entryJsonPath: $"entry-{pageIndex}.json",
            videoFiles: Array.Empty<string>(),
            coverUrl: string.Empty,
            ownerName: null,
            ownerId: null);
    }
}
