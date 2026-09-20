using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface IPlaybackMaterializer
{
    bool CanHandle(CachePlaybackPlan plan);

    PlaybackMaterializationResult Materialize(CachePlaybackPlan plan);

    Task<PlaybackMaterializationResult> MaterializeAsync(CachePlaybackPlan plan,
        IProgress<PlaybackPreparationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Materialize(plan, progress, cancellationToken), cancellationToken);

    PlaybackMaterializationResult Materialize(
        CachePlaybackPlan plan,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Materialize(plan);
    }
}
