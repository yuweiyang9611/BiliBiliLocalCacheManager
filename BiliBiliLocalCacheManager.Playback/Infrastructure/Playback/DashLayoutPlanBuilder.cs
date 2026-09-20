using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal static class DashLayoutPlanBuilder
{
    public static CachePlaybackPlan Build(CachePlaybackProbe probe, string structureKind)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var segment = probe.Segment;
        foreach (var qualityDirectory in PlaybackPathHelpers.GetQualityDirectories(probe))
        {
            var video = PlaybackPathHelpers.GetFileInChildDirectory(qualityDirectory, "video.m4s");
            var audio = PlaybackPathHelpers.GetFileInChildDirectory(qualityDirectory, "audio.m4s");
            if (video is not null && audio is not null)
                return CachePlaybackPlan.Playable(segment.Avid, segment.Title, segment.PageIndex, segment.PartName,
                    probe.SegmentName, segment.SegmentDirectory, structureKind, CachePlaybackMaterialKind.DashPair,
                    [video, audio], duration: segment.TotalDuration);
        }
        return CachePlaybackPlan.Unavailable(segment.Avid, segment.Title, segment.PageIndex, segment.PartName,
            probe.SegmentName, segment.SegmentDirectory, structureKind, "\u672a\u627e\u5230\u5b8c\u6574\u7684 DASH \u97f3\u89c6\u9891\u6587\u4ef6\u3002");
    }
}
