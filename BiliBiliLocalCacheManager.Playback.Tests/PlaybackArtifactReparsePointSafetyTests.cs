using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactReparsePointSafetyTests
{
    [Fact]
    public void Store_ShouldNotTraverseManagedLookingDirectoryLink()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_link_safety_{Guid.NewGuid():N}");
        var cacheRoot = Path.Combine(root, "cache");
        var outsidePage = Path.Combine(root, "outside-page");
        var avidDirectory = Path.Combine(cacheRoot, "999");
        var pageLink = Path.Combine(avidDirectory, "Page_1");
        Directory.CreateDirectory(avidDirectory);
        Directory.CreateDirectory(outsidePage);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(pageLink, outsidePage);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or
                    IOException or
                    PlatformNotSupportedException)
            {
                return;
            }

            var outsideArtifact = Path.Combine(
                outsidePage,
                "0123456789abcdef01234567.mp4");
            File.WriteAllBytes(outsideArtifact, new byte[32]);
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var plan = CachePlaybackPlan.Playable(
                999,
                "Title",
                1,
                "P1",
                "c_1",
                root,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { sourcePath });
            var store = new PlaybackArtifactStore(cacheRoot);

            Assert.Throws<InvalidOperationException>(() => store.GetOrCreate(
                plan,
                ".mp4",
                path => File.WriteAllBytes(path, new byte[32])));
            Assert.Throws<InvalidOperationException>(() => store.GetStatistics());

            var clearResult = store.Clear();
            Assert.Equal(0, clearResult.DeletedFileCount);
            Assert.True(File.Exists(outsideArtifact));
        }
        finally
        {
            try
            {
                if (Directory.Exists(pageLink))
                {
                    Directory.Delete(pageLink);
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Ignore best-effort test cleanup failures.
            }
        }
    }
}
