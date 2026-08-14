using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class CompositePlaybackMaterializer : IPlaybackMaterializer
{
    private readonly IReadOnlyList<IPlaybackMaterializer> _materializers;

    public CompositePlaybackMaterializer(IEnumerable<IPlaybackMaterializer> materializers)
    {
        ArgumentNullException.ThrowIfNull(materializers);

        _materializers = materializers.ToList();
        if (_materializers.Count == 0)
        {
            throw new ArgumentException("At least one materializer is required.", nameof(materializers));
        }
    }

    public bool CanHandle(CachePlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return _materializers.Any(materializer => materializer.CanHandle(plan));
    }

    public PlaybackMaterializationResult Materialize(CachePlaybackPlan plan)
    {
        return Materialize(plan, null, CancellationToken.None);
    }

    public PlaybackMaterializationResult Materialize(
        CachePlaybackPlan plan,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var materializer = _materializers.FirstOrDefault(candidate => candidate.CanHandle(plan));
        if (materializer is null)
        {
            return PlaybackMaterializationResult.Failure(
                $"未找到适用于 {plan.MaterialKind} 的素材准备器。",
                nameof(CompositePlaybackMaterializer));
        }

        return materializer.Materialize(plan, progress, cancellationToken);
    }
}
