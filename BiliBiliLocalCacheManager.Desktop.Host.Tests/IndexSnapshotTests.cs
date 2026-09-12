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

    private static DesktopHostApplication.CurrentIndexSnapshot Snapshot(int count)
    {
        var epoch = DateTimeOffset.UnixEpoch;
        var index = new CacheIndex(Enumerable.Range(1, count).Select(avid => new BiliVideoCache(avid, [
            new BiliSegment(avid, avid, "BV" + avid, 1, "Part", "video " + avid, (CacheVersion)0, "", null, null, null,
                true, 1, 1, TimeSpan.FromMinutes(1), 0, epoch, epoch, "", "", [], "", "owner", null)
        ])));
        return new DesktopHostApplication.CurrentIndexSnapshot(index, "test", Path.GetTempPath(), []);
    }
}
