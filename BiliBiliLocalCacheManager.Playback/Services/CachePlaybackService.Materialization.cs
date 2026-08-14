using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Services;

public sealed partial class CachePlaybackService : ICachePlaybackMaterializationService
{
    public async Task<PlaybackMaterializationResult> MaterializeAsync(
        CachePlaybackPlan plan,
        IProgress<PlaybackPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (!plan.IsPlayable)
        {
            return PlaybackMaterializationResult.Failure(
                plan.Message ?? "当前分段无法生成可播放文件。");
        }

        return await Task.Run(
                () => _materializer.Materialize(plan, progress, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
