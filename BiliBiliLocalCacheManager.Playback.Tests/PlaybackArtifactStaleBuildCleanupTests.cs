using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactStaleBuildCleanupTests
{
    [Fact]
    public void Cleanup_ShouldSkipStaleBuildArtifactLockedByAnotherProcess()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_build_cleanup_lock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var plan = CachePlaybackPlan.Playable(
                100,
                "Title",
                1,
                "P1",
                "c_1",
                root,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { sourcePath });
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            var artifact = store.GetOrCreate(
                plan,
                ".mp4",
                path => File.WriteAllBytes(path, new byte[32]));
            File.Delete(artifact.OutputPath);

            var buildPath = Path.Combine(
                Path.GetDirectoryName(artifact.OutputPath)!,
                $"{Path.GetFileNameWithoutExtension(artifact.OutputPath)}.building-{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(buildPath, new byte[32]);
            File.SetLastWriteTimeUtc(buildPath, DateTime.UtcNow - TimeSpan.FromHours(2));

            using (new FileStream(
                       artifact.OutputPath + ".lock",
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                var lockedResult = store.Cleanup();
                Assert.Equal(0, lockedResult.DeletedFileCount);
                Assert.Equal(0, lockedResult.FailedFileCount);
                Assert.True(File.Exists(buildPath));
            }

            var unlockedResult = store.Cleanup();
            Assert.Equal(1, unlockedResult.DeletedFileCount);
            Assert.False(File.Exists(buildPath));
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
