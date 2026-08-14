using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactProtectedCleanupTests
{
    [Fact]
    public void Cleanup_ShouldPreserveProtectedArtifact_ForRetentionAndCapacityPolicies()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            var protectedArtifact = CreateArtifact(store, root, "protected.blv", pageIndex: 1);
            var removableArtifact = CreateArtifact(store, root, "removable.blv", pageIndex: 2);
            File.SetLastWriteTimeUtc(protectedArtifact, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(removableArtifact, DateTime.UtcNow.AddDays(-10));

            var result = store.Cleanup(new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.FromDays(7),
                MaxTotalBytes = 0,
                CapacityEvictionGracePeriod = TimeSpan.Zero,
                ProtectedPaths = new[] { protectedArtifact }
            });

            Assert.True(File.Exists(protectedArtifact));
            Assert.False(File.Exists(removableArtifact));
            Assert.Equal(1, result.DeletedFileCount);
            Assert.Equal(32, result.RemainingBytes);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Cleanup_ShouldDelayCapacityEviction_ForRecentlyCreatedArtifact()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            var artifact = CreateArtifact(store, root, "recent.blv", pageIndex: 1);

            var protectedByGrace = store.Cleanup(new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.FromDays(365),
                MaxTotalBytes = 0
            });

            Assert.True(File.Exists(artifact));
            Assert.Equal(0, protectedByGrace.DeletedFileCount);
            Assert.Equal(32, protectedByGrace.RemainingBytes);

            File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddMinutes(-10));
            var expiredGrace = store.Cleanup(new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.FromDays(365),
                MaxTotalBytes = 0
            });

            Assert.False(File.Exists(artifact));
            Assert.Equal(1, expiredGrace.DeletedFileCount);
            Assert.Equal(0, expiredGrace.RemainingBytes);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static string CreateArtifact(
        PlaybackArtifactStore store,
        string root,
        string sourceName,
        int pageIndex)
    {
        var sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, sourceName);
        File.WriteAllText(sourcePath, sourceName);
        var plan = CachePlaybackPlan.Playable(
            100,
            "Title",
            pageIndex,
            $"P{pageIndex}",
            $"c_{pageIndex}",
            sourceDirectory,
            "LegacyBlv",
            CachePlaybackMaterialKind.SingleFile,
            new[] { sourcePath });

        return store.GetOrCreate(
            plan,
            ".mp4",
            path => File.WriteAllBytes(path, new byte[32])).OutputPath;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_protected_cleanup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup failures.
        }
    }
}
