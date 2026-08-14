using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactCleanupLockTests
{
    [Fact]
    public void Clear_ShouldSkipArtifactLockedByAnotherProcess()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_cleanup_lock_{Guid.NewGuid():N}");
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

            PlaybackArtifactCleanupResult lockedResult;
            using (new FileStream(
                       artifact.OutputPath + ".lock",
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                lockedResult = store.Clear();
            }

            Assert.Equal(0, lockedResult.DeletedFileCount);
            Assert.Equal(1, lockedResult.FailedFileCount);
            Assert.Equal(32, lockedResult.RemainingBytes);
            Assert.True(File.Exists(artifact.OutputPath));

            var unlockedResult = store.Clear();
            Assert.Equal(1, unlockedResult.DeletedFileCount);
            Assert.Equal(0, unlockedResult.RemainingBytes);
            Assert.False(File.Exists(artifact.OutputPath));
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
