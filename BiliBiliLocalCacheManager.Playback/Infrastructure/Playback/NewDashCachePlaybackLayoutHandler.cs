using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class NewDashCachePlaybackLayoutHandler : ICachePlaybackLayoutHandler
{
    public string Name => nameof(NewDashCachePlaybackLayoutHandler);

    public int Priority => 300;

    public bool CanHandle(CachePlaybackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return probe.SegmentName.StartsWith("c_", StringComparison.OrdinalIgnoreCase) &&
               !PlaybackPathHelpers.HasLuaChild(probe);
    }

    public CachePlaybackPlan BuildPlan(CachePlaybackProbe probe)
    {
        return DashLayoutPlanBuilder.Build(probe, "NewDash");
    }
}
