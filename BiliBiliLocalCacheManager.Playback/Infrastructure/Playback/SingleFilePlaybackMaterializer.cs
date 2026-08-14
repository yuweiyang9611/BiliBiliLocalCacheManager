using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class SingleFilePlaybackMaterializer : IPlaybackMaterializer
{
    private readonly IFfmpegTranscoder _transcoder;
    private readonly IPlaybackArtifactStore _artifactStore;

    public SingleFilePlaybackMaterializer()
        : this(new FfmpegCoreTranscoder(), PlaybackArtifactStore.Shared)
    {
    }

    public SingleFilePlaybackMaterializer(IFfmpegTranscoder transcoder)
        : this(transcoder, PlaybackArtifactStore.Shared)
    {
    }

    public SingleFilePlaybackMaterializer(
        IFfmpegTranscoder transcoder,
        IPlaybackArtifactStore artifactStore)
    {
        _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    }

    public bool CanHandle(CachePlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.MaterialKind == CachePlaybackMaterialKind.SingleFile;
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
            return PlaybackMaterializationResult.Failure(plan.Message ?? "当前分段不可播放。", nameof(SingleFilePlaybackMaterializer));
        }

        var inputPath = plan.MediaFiles[0];
        var extension = Path.GetExtension(inputPath);

        if (string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackMaterializationResult.Success(
                inputPath,
                isTemporary: false,
                $"已直接使用现有文件：{Path.GetFileName(inputPath)}",
                nameof(SingleFilePlaybackMaterializer));
        }

        try
        {
            var artifact = _artifactStore.GetOrCreate(
                plan,
                ".mp4",
                outputPath => _transcoder.ConcatToMp4(
                    new[] { inputPath },
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
                    : $"已将 {Path.GetExtension(inputPath)} 片段转换为可播放文件。",
                nameof(SingleFilePlaybackMaterializer));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlaybackMaterializationResult.Failure(
                $"单文件素材准备失败：{ex.Message}",
                nameof(SingleFilePlaybackMaterializer));
        }
    }
}
