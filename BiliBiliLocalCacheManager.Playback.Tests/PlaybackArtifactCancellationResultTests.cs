using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactCancellationResultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Maintenance_CancellationAfterDeletionPreservesCommittedCounts(bool clear)
    {
        using var fixture = new Fixture();
        var paths = fixture.CreateArtifacts(2);
        using var cancellation = new CancellationTokenSource();
        var store = new PlaybackArtifactStore(fixture.CacheRoot, () =>
        {
            if (paths.Any(path => !File.Exists(path))) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });

        var result = clear ? store.Clear() : store.Cleanup(fixture.Options);

        Assert.True(result.Cancelled);
        Assert.True(result.RemainingBytesEstimated);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.Equal(1, result.UnprocessedFileCount);
        Assert.Equal(32, result.FreedBytes);
        Assert.Equal(32, result.RemainingBytes);
        Assert.Equal(0, result.FailedFileCount);
        Assert.Single(paths, File.Exists);
        Assert.Null(result.Statistics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Maintenance_CancellationDuringFinalMeasurementDoesNotDiscardAllDeletions(bool clear)
    {
        using var fixture = new Fixture();
        var paths = fixture.CreateArtifacts(1);
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "unmanaged.txt"), "not managed");
        using var cancellation = new CancellationTokenSource();
        var store = new PlaybackArtifactStore(fixture.CacheRoot, () =>
        {
            if (!File.Exists(paths[0])) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });

        var result = clear ? store.Clear() : store.Cleanup(fixture.Options);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.Equal(32, result.FreedBytes);
        Assert.Equal(0, result.UnprocessedFileCount);
        Assert.Equal(0, result.RemainingBytes);
        Assert.True(File.Exists(Path.Combine(fixture.CacheRoot, "unmanaged.txt")));
    }

    [Fact]
    public void Cleanup_CancellationBeforePlanningDoesNotMutate()
    {
        using var fixture = new Fixture();
        var paths = fixture.CreateArtifacts(2);
        var store = new PlaybackArtifactStore(fixture.CacheRoot, () => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => store.Cleanup(fixture.Options));
        Assert.All(paths, path => Assert.True(File.Exists(path)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"bili_cleanup_cancellation_{Guid.NewGuid():N}");
        public string CacheRoot => Path.Combine(_root, "cache");
        public PlaybackArtifactCleanupOptions Options => new() { MaxAge = TimeSpan.Zero, MaxTotalBytes = 0, CapacityEvictionGracePeriod = TimeSpan.Zero };
        public string[] CreateArtifacts(int count)
        {
            Directory.CreateDirectory(_root);
            var source = Path.Combine(_root, "source.blv");
            File.WriteAllText(source, "source");
            return Enumerable.Range(1, count).Select(page =>
            {
                var plan = CachePlaybackPlan.Playable(100, "Title", page, $"P{page}", $"c_{page}",
                    _root, "LegacyBlv", CachePlaybackMaterialKind.SingleFile, new[] { source });
                var path = new PlaybackArtifactStore(CacheRoot).GetOrCreate(plan, ".mp4",
                    output => File.WriteAllBytes(output, new byte[32])).OutputPath;
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                return path;
            }).ToArray();
        }
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
