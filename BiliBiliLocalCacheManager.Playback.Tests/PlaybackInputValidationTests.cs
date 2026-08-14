using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackInputValidationTests
{
    [Fact]
    public void CreatePlan_ShouldRejectZeroByteDashStream()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_zero_dash_test_{Guid.NewGuid():N}");
        try
        {
            var segment = Path.Combine(root, "100", "c_1");
            var quality = Path.Combine(segment, "80");
            Directory.CreateDirectory(quality);
            File.WriteAllText(Path.Combine(segment, "entry.json"), BuildEntryJson());
            File.WriteAllBytes(Path.Combine(quality, "video.m4s"), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(quality, "audio.m4s"), "audio");

            var cache = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(root).VideoCaches);
            var plan = new CachePlaybackService().CreatePlan(cache.Segments.Single());

            Assert.False(plan.IsPlayable);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Ignore test cleanup failures.
            }
        }
    }

    private static string BuildEntryJson() =>
        """
        {
          "is_completed": true,
          "total_bytes": 10,
          "downloaded_bytes": 10,
          "title": "Zero DASH",
          "type_tag": "80",
          "cover": "cover",
          "prefered_video_quality": 80,
          "guessed_total_bytes": 10,
          "total_time_milli": 1000,
          "danmaku_count": 0,
          "time_update_stamp": 0,
          "time_create_stamp": 0,
          "avid": 100,
          "spid": 0,
          "seasion_id": 0,
          "page_data": { "cid": 1, "page": 1, "from": "local", "part": "P1", "vid": "", "has_alias": false, "tid": 0 }
        }
        """;
}
