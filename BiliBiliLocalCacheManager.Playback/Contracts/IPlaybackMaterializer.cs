using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface IPlaybackMaterializer
{
    bool CanHandle(CachePlaybackPlan plan);

    PlaybackMaterializationResult Materialize(CachePlaybackPlan plan);

    PlaybackMaterializationResult Materialize(
        CachePlaybackPlan plan,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Materialize(plan);
    }
}
