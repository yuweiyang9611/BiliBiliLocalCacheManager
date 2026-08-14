using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactCleanupRegressionTests
{
    [Fact]
    public void Cleanup_ShouldPreserveUnmanagedBuildingLikeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_building_cleanup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var buildingPath = Path.Combine(root, "orphan.building-test.mp4");
            File.WriteAllText(buildingPath, "partial");
            File.SetLastWriteTimeUtc(buildingPath, DateTime.UtcNow.AddHours(-2));

            var result = new PlaybackArtifactStore(root).Cleanup(new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.FromDays(7),
                MaxTotalBytes = long.MaxValue
            });

            Assert.True(File.Exists(buildingPath));
            Assert.Equal(0, result.DeletedFileCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
