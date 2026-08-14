using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class MidDashCachePlaybackLayoutHandler : ICachePlaybackLayoutHandler
{
    public string Name => nameof(MidDashCachePlaybackLayoutHandler);

    public int Priority => 200;

    public bool CanHandle(CachePlaybackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return PlaybackPathHelpers.IsNumericName(probe.SegmentName) &&
               !PlaybackPathHelpers.HasLuaChild(probe);
    }

    public CachePlaybackPlan BuildPlan(CachePlaybackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var segment = probe.Segment;

        foreach (var qualityDirectory in PlaybackPathHelpers.GetQualityDirectories(probe))
        {
            var videoPath = PlaybackPathHelpers.GetFileInChildDirectory(qualityDirectory, "video.m4s");
            var audioPath = PlaybackPathHelpers.GetFileInChildDirectory(qualityDirectory, "audio.m4s");

            if (videoPath is not null && audioPath is not null)
            {
                return CachePlaybackPlan.Playable(
                    segment.Avid,
                    segment.Title,
                    segment.PageIndex,
                    segment.PartName,
                    probe.SegmentName,
                    segment.SegmentDirectory,
                    "MidDash",
                    CachePlaybackMaterialKind.DashPair,
                    new[] { videoPath, audioPath },
                    duration: segment.TotalDuration);
            }
        }

        return CachePlaybackPlan.Unavailable(
            segment.Avid,
            segment.Title,
            segment.PageIndex,
            segment.PartName,
            probe.SegmentName,
            segment.SegmentDirectory,
            "MidDash",
            "未找到完整的 DASH 音视频文件。");
    }
}
