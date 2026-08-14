using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class DashPairPlaybackMaterializer : IPlaybackMaterializer
{
    private readonly IFfmpegTranscoder _transcoder;
    private readonly IPlaybackArtifactStore _artifactStore;

    public DashPairPlaybackMaterializer()
        : this(new FfmpegCoreTranscoder(), PlaybackArtifactStore.Shared)
    {
    }

    public DashPairPlaybackMaterializer(IFfmpegTranscoder transcoder)
        : this(transcoder, PlaybackArtifactStore.Shared)
    {
    }

    public DashPairPlaybackMaterializer(
        IFfmpegTranscoder transcoder,
        IPlaybackArtifactStore artifactStore)
    {
        _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    }

    public bool CanHandle(CachePlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.MaterialKind == CachePlaybackMaterialKind.DashPair;
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
            return PlaybackMaterializationResult.Failure(plan.Message ?? "当前分段不可播放。", nameof(DashPairPlaybackMaterializer));
        }

        try
        {
            var artifact = _artifactStore.GetOrCreate(
                plan,
                ".mp4",
                outputPath => _transcoder.MuxDashPairToMp4(
                    plan.MediaFiles[0],
                    plan.MediaFiles[1],
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
                    : "已将 DASH 视频流和音频流合成为可播放文件。",
                nameof(DashPairPlaybackMaterializer));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlaybackMaterializationResult.Failure(
                $"DASH 音视频合成失败：{ex.Message}",
                nameof(DashPairPlaybackMaterializer));
        }
    }
}
