using System.Diagnostics;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using Xunit.Abstractions;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed class IndexSnapshotTests(ITestOutputHelper output)
{
    [Fact]
    public void QueriesReuseResultsAndEvictTheLeastRecentlyUsedOfEight()
    {
        var snapshot = Snapshot(100);
        var options = new CacheSearchOptions { Keyword = "video" };
        var first = snapshot.Search(options, default);
        Assert.Same(first, snapshot.Search(options, default));
        Assert.Equal(1, snapshot.SearchExecutions);
        for (var i = 0; i < 7; i++) snapshot.Search(new CacheSearchOptions { Keyword = i.ToString() }, default);
        Assert.Same(first, snapshot.Search(options, default));
        snapshot.Search(new CacheSearchOptions { Keyword = "new query" }, default);
        Assert.Same(first, snapshot.Search(options, default));
        var before = snapshot.SearchExecutions;
        snapshot.Search(new CacheSearchOptions { Keyword = "0" }, default);
        Assert.Equal(before + 1, snapshot.SearchExecutions);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => snapshot.Search(new CacheSearchOptions { Keyword = "cancel" }, cancellation.Token));
        Assert.Equal(before + 1, snapshot.SearchExecutions);
        snapshot.Invalidate();
        Assert.Throws<Rpc.RpcException>(() => snapshot.Search(options, default));
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void BenchmarkRepeatedPagesAndFirstSearch(int count)
    {
        var snapshot = Snapshot(count);
        var options = new CacheSearchOptions { Keyword = "video" };
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var first = snapshot.Search(options, default);
        timer.Stop();
        output.WriteLine($"items={count} first-search-ms={timer.Elapsed.TotalMilliseconds:F2} allocated={GC.GetAllocatedBytesForCurrentThread() - allocation}");
        allocation = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        for (var i = 0; i < 50; i++)
        {
            var previous = snapshot.Index.Search(options).OrderByDescending(cache => cache.Segments.Max(segment => segment.UpdatedAt))
                .ThenBy(cache => cache.Avid).ToArray().Skip(i * 100).Take(100).ToArray();
            Assert.Equal(100, previous.Length);
        }
        timer.Stop();
        output.WriteLine($"items={count} previous-50-pages-ms={timer.Elapsed.TotalMilliseconds:F2} allocated={GC.GetAllocatedBytesForCurrentThread() - allocation}");
        allocation = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        for (var i = 0; i < 50; i++)
        {
            var matches = snapshot.Search(options, default);
            Assert.Same(first, matches);
            Assert.Equal(100, matches.Skip(i * 100).Take(100).Count());
        }
        timer.Stop();
        output.WriteLine($"items={count} cached-50-pages-ms={timer.Elapsed.TotalMilliseconds:F2} allocated={GC.GetAllocatedBytesForCurrentThread() - allocation}");
        Assert.Equal(1, snapshot.SearchExecutions);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        timer.Restart();
        Assert.Throws<OperationCanceledException>(() => snapshot.Search(options, cancellation.Token));
        timer.Stop();
        output.WriteLine($"items={count} cancelled-query-ms={timer.Elapsed.TotalMilliseconds:F2}");
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public async Task BenchmarkCancellationDuringUncachedSearch(int count)
    {
        var snapshot = Snapshot(count);
        var options = new CacheSearchOptions
        {
            Keyword = string.Join(' ', Enumerable.Range(0, 60).Select(i => "absent" + i)),
            SplitKeywords = true,
            RequireAllKeywords = false
        };
        using var cancellation = new CancellationTokenSource();
        var timer = Stopwatch.StartNew();
        long requested = 0;
        using var registration = cancellation.Token.Register(() => Interlocked.Exchange(ref requested, Stopwatch.GetTimestamp()));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));
        try
        {
            await Task.Run(() => snapshot.Search(options, cancellation.Token));
            output.WriteLine($"items={count} uncached-search-finished-before-cancellation-ms={timer.Elapsed.TotalMilliseconds:F2}");
        }
        catch (OperationCanceledException)
        {
            var observed = Stopwatch.GetElapsedTime(Interlocked.Read(ref requested));
            output.WriteLine($"items={count} mid-query-cancellation-ms={observed.TotalMilliseconds:F2} total-ms={timer.Elapsed.TotalMilliseconds:F2}");
            Assert.Equal(0, snapshot.SearchExecutions);
        }
    }

    private static DesktopHostApplication.CurrentIndexSnapshot Snapshot(int count)
    {
        var epoch = DateTimeOffset.UnixEpoch;
        var index = new CacheIndex(Enumerable.Range(1, count).Select(avid => new BiliVideoCache(avid, [
            new BiliSegment(avid, avid, "BV" + avid, 1, "Part", "video " + avid, (CacheVersion)0, "", null, null, null,
                true, 1, 1, TimeSpan.FromMinutes(1), 0, epoch, epoch, "", "", [], "", "owner", null)
        ])));
        return new DesktopHostApplication.CurrentIndexSnapshot(index, "test", Path.GetTempPath(), []);
    }

    [Fact]
    public void DetailsPagesReusePlansAndSortingAndInvalidateWithTheIndex()
    {
        var snapshot = Snapshot(10);
        var cache = snapshot.Index.VideoCaches.First();
        var mapped = 0;
        SegmentDto Map(BiliSegment segment)
        {
            mapped++;
            return new("id", "1", segment.PageIndex, segment.PartName, "Unknown", "Unavailable", segment.TotalBytes,
                segment.TotalDuration.TotalSeconds, false, segment.SegmentDirectory);
        }
        var first = snapshot.GetDetailsPage(cache, 0, 100, Map, default);
        var second = snapshot.GetDetailsPage(cache, 0, 100, Map, default);
        Assert.Same(first.Segments, second.Segments);
        Assert.Same(snapshot.Summaries[cache.Avid], second.Item);
        Assert.Equal(1, mapped);
        Assert.Equal(1, snapshot.DetailSortExecutions);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => snapshot.GetDetailsPage(cache, 0, 100, Map, cancellation.Token));
        snapshot.Invalidate();
        Assert.Throws<Rpc.RpcException>(() => snapshot.GetDetailsPage(cache, 0, 100, Map, default));
        Assert.Throws<Rpc.RpcException>(() => snapshot.Search(new CacheSearchOptions { Keyword = "" }, default));
    }

    [Fact]
    public void DetailsPagesBoundThePerVideoCacheAndDoNotCacheCancelledWork()
    {
        var snapshot = Snapshot(1);
        var cache = snapshot.Index.VideoCaches.Single();
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        SegmentDto Map(BiliSegment segment)
        {
            attempts++;
            return new("id", "1", 1, "part", "Unknown", "Unavailable", 0, 0, false, "");
        }
        Assert.Throws<OperationCanceledException>(() => snapshot.GetDetailsPage(cache, 0, 100, segment =>
        {
            cancellation.Cancel();
            return Map(segment);
        }, cancellation.Token));
        snapshot.GetDetailsPage(cache, 0, 100, Map, default);
        Assert.Equal(2, attempts);
        for (var offset = 1; offset <= 8; offset++) snapshot.GetDetailsPage(cache, offset, 100, Map, default);
        snapshot.GetDetailsPage(cache, 0, 100, Map, default);
        Assert.Equal(3, attempts);
        Assert.Equal(1, snapshot.DetailSortExecutions);
    }
}
