using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactLruCleanupTests
{
    [Fact]
    public void Cleanup_ShouldKeepRecentlyReusedArtifact_WhenCapacityIsExceeded()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_lru_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            var firstPlan = CreatePlan(root, "first.blv", 1);
            var secondPlan = CreatePlan(root, "second.blv", 2);
            var first = store.GetOrCreate(
                firstPlan,
                ".mp4",
                path => File.WriteAllBytes(path, new byte[32]));
            var second = store.GetOrCreate(
                secondPlan,
                ".mp4",
                path => File.WriteAllBytes(path, new byte[32]));
            File.SetLastWriteTimeUtc(first.OutputPath, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(second.OutputPath, DateTime.UtcNow.AddHours(-1));

            var reused = store.GetOrCreate(
                firstPlan,
                ".mp4",
                _ => throw new InvalidOperationException("Cache should have been reused."));
            var result = store.Cleanup(new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.FromDays(365),
                MaxTotalBytes = 32
            });

            Assert.True(reused.WasReused);
            Assert.True(File.Exists(first.OutputPath));
            Assert.False(File.Exists(second.OutputPath));
            Assert.Equal(1, result.DeletedFileCount);
            Assert.Equal(32, result.FreedBytes);
            Assert.Equal(32, result.RemainingBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CachePlaybackPlan CreatePlan(
        string root,
        string sourceFileName,
        int pageIndex)
    {
        var sourcePath = Path.Combine(root, sourceFileName);
        File.WriteAllText(sourcePath, "source");
        return CachePlaybackPlan.Playable(
            100,
            "Title",
            pageIndex,
            $"P{pageIndex}",
            $"c_{pageIndex}",
            root,
            "LegacyBlv",
            CachePlaybackMaterialKind.SingleFile,
            new[] { sourcePath });
    }
}
