using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface ICachePlaybackLayoutHandler
{
    string Name { get; }

    int Priority { get; }

    bool CanHandle(CachePlaybackProbe probe);

    CachePlaybackPlan BuildPlan(CachePlaybackProbe probe);
}
