using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface ICachePlaybackService
{
    CachePlaybackPlan CreatePlan(BiliSegment segment);

    CachePlaybackPlan CreatePlan(BiliVideoCache cache, string? segmentKey = null);

    CachePlaybackPagePlan CreatePagePlan(BiliVideoCache cache, string? segmentKey = null);

    IReadOnlyList<CachePlaybackPagePlan> CreatePagePlans(BiliVideoCache cache);

    PlaybackLaunchResult Play(BiliSegment segment, PlaybackLaunchOptions? launchOptions = null);

    PlaybackLaunchResult Play(BiliVideoCache cache, string? segmentKey = null, PlaybackLaunchOptions? launchOptions = null);

    Task<PlaybackLaunchResult> PlayAsync(
        BiliSegment segment,
        PlaybackLaunchOptions? launchOptions = null,
        IProgress<PlaybackPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Play(segment, launchOptions),
            cancellationToken);
    }

    Task<PlaybackLaunchResult> PlayAsync(
        BiliVideoCache cache,
        string? segmentKey = null,
        PlaybackLaunchOptions? launchOptions = null,
        IProgress<PlaybackPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Play(cache, segmentKey, launchOptions),
            cancellationToken);
    }
}
