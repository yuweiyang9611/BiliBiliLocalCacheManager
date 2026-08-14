using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactConcurrentManagementTests
{
    [Fact]
    public void Statistics_ShouldRetryTransientUnauthorizedAccess()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_transient_access_{Guid.NewGuid():N}");
        var store = new PlaybackArtifactStore(cacheRoot);
        var attemptCount = 0;
        store.BeforeStrictSnapshotAttemptForTesting = attempt =>
        {
            attemptCount = attempt;
            if (attempt == 1)
            {
                throw new UnauthorizedAccessException("Simulated delete-pending directory.");
            }
        };

        var statistics = store.GetStatistics();

        Assert.Equal(2, attemptCount);
        Assert.Equal(0, statistics.FileCount);
    }

    [Fact]
    public void Statistics_ShouldNotHidePersistentUnauthorizedAccess()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_persistent_access_{Guid.NewGuid():N}");
        var store = new PlaybackArtifactStore(cacheRoot)
        {
            BeforeStrictSnapshotAttemptForTesting = _ =>
                throw new UnauthorizedAccessException("Simulated persistent ACL failure.")
        };

        Assert.Throws<UnauthorizedAccessException>(store.GetStatistics);
    }

    [Fact]
    public async Task StatisticsCleanupAndClear_ShouldTolerateConcurrentInstances()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_concurrent_management_{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        var cacheRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var producerStore = new PlaybackArtifactStore(cacheRoot);
            for (var index = 0; index < 64; index++)
            {
                var sourcePath = Path.Combine(sourceRoot, $"source-{index}.blv");
                File.WriteAllText(sourcePath, $"source-{index}");
                var plan = CachePlaybackPlan.Playable(
                    100,
                    "Title",
                    1,
                    "P1",
                    $"c_{index}",
                    sourceRoot,
                    "LegacyBlv",
                    CachePlaybackMaterialKind.SingleFile,
                    new[] { sourcePath });
                producerStore.GetOrCreate(
                    plan,
                    ".mp4",
                    path => File.WriteAllBytes(path, new byte[128]));
            }

            var tasks = Enumerable.Range(0, 12).Select(worker => Task.Run(() =>
            {
                var store = new PlaybackArtifactStore(cacheRoot);
                for (var iteration = 0; iteration < 20; iteration++)
                {
                    switch ((worker + iteration) % 3)
                    {
                        case 0:
                            store.GetStatistics();
                            break;
                        case 1:
                            store.Cleanup(new PlaybackArtifactCleanupOptions
                            {
                                MaxAge = TimeSpan.FromDays(30),
                                MaxTotalBytes = long.MaxValue
                            });
                            break;
                        default:
                            store.Clear();
                            break;
                    }
                }
            }));

            await Task.WhenAll(tasks);
            Assert.Equal(0, producerStore.GetStatistics().FileCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
