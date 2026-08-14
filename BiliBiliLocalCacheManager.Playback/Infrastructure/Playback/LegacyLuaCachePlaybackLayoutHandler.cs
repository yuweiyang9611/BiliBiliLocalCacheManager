using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class LegacyLuaCachePlaybackLayoutHandler : ICachePlaybackLayoutHandler
{
    public string Name => nameof(LegacyLuaCachePlaybackLayoutHandler);

    public int Priority => 100;

    public bool CanHandle(CachePlaybackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return PlaybackPathHelpers.IsNumericName(probe.SegmentName) &&
               PlaybackPathHelpers.HasLuaChild(probe);
    }

    public CachePlaybackPlan BuildPlan(CachePlaybackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return LegacyLayoutPlanBuilder.Build(probe, isHybrid: false);
    }
}
