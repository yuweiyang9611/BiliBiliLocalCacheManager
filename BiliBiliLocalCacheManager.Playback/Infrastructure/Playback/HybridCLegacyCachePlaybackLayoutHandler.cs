using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class HybridCLegacyCachePlaybackLayoutHandler : ICachePlaybackLayoutHandler
{
    public string Name => nameof(HybridCLegacyCachePlaybackLayoutHandler);

    public int Priority => 400;

    public bool CanHandle(CachePlaybackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return probe.SegmentName.StartsWith("c_", StringComparison.OrdinalIgnoreCase) &&
               PlaybackPathHelpers.HasLuaChild(probe);
    }

    public CachePlaybackPlan BuildPlan(CachePlaybackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return LegacyLayoutPlanBuilder.Build(probe, isHybrid: true);
    }
}
