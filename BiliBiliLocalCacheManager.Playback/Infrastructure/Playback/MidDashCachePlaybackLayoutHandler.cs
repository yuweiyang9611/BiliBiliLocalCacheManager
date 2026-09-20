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
        return DashLayoutPlanBuilder.Build(probe, "MidDash");
    }
}
