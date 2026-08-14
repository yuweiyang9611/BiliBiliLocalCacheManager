using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class OrderedPairPlaybackMaterializer : IPlaybackMaterializer
{
    private readonly IFfmpegTranscoder _transcoder;
    private readonly IPlaybackArtifactStore _artifactStore;

    public OrderedPairPlaybackMaterializer()
        : this(new FfmpegCoreTranscoder(), PlaybackArtifactStore.Shared)
    {
    }

    public OrderedPairPlaybackMaterializer(IFfmpegTranscoder transcoder)
        : this(transcoder, PlaybackArtifactStore.Shared)
    {
    }

    public OrderedPairPlaybackMaterializer(
        IFfmpegTranscoder transcoder,
        IPlaybackArtifactStore artifactStore)
    {
        _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    }

    public bool CanHandle(CachePlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.MaterialKind == CachePlaybackMaterialKind.OrderedPair;
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

        if (!plan.IsPlayable)
        {
            return PlaybackMaterializationResult.Failure(plan.Message ?? "当前分段不可播放。", nameof(OrderedPairPlaybackMaterializer));
        }

        try
        {
            var artifact = _artifactStore.GetOrCreate(
                plan,
                ".mp4",
                outputPath => _transcoder.ConcatToMp4(
                    plan.MediaFiles,
                    outputPath,
                    plan.Duration,
                    progress,
                    cancellationToken),
                cancellationToken,
                PlaybackArtifactWaitProgress.Create(progress));
            cancellationToken.ThrowIfCancellationRequested();
            return PlaybackMaterializationResult.Success(
                artifact.OutputPath,
                isTemporary: true,
                artifact.WasReused
                    ? "已复用现有播放文件。"
                    : $"已按顺序合并 {plan.MediaFiles.Count} 个媒体片段。",
                nameof(OrderedPairPlaybackMaterializer));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlaybackMaterializationResult.Failure(
                $"顺序片段合并失败：{ex.Message}",
                nameof(OrderedPairPlaybackMaterializer));
        }
    }
}
