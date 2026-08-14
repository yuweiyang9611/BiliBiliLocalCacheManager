using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackMaterializerWaitProgressTests
{
    [Theory]
    [InlineData(CachePlaybackMaterialKind.SingleFile)]
    [InlineData(CachePlaybackMaterialKind.OrderedPair)]
    [InlineData(CachePlaybackMaterialKind.DashPair)]
    public void Materializer_ShouldForwardArtifactLockWaitProgress(
        CachePlaybackMaterialKind materialKind)
    {
        var mediaFiles = materialKind == CachePlaybackMaterialKind.SingleFile
            ? new[] { "source.blv" }
            : new[] { "video.m4s", "audio.m4s" };
        var plan = CachePlaybackPlan.Playable(
            100,
            "Title",
            1,
            "P1",
            "c_1",
            Path.GetTempPath(),
            "Test",
            materialKind,
            mediaFiles);
        var artifactStore = new WaitReportingArtifactStore();
        var transcoder = new UnexpectedTranscoder();
        IPlaybackMaterializer materializer = materialKind switch
        {
            CachePlaybackMaterialKind.SingleFile =>
                new SingleFilePlaybackMaterializer(transcoder, artifactStore),
            CachePlaybackMaterialKind.OrderedPair =>
                new OrderedPairPlaybackMaterializer(transcoder, artifactStore),
            CachePlaybackMaterialKind.DashPair =>
                new DashPairPlaybackMaterializer(transcoder, artifactStore),
            _ => throw new ArgumentOutOfRangeException(nameof(materialKind))
        };
        var reported = new List<PlaybackPreparationProgress>();
        var progress = new SynchronousProgress(reported.Add);

        var result = materializer.Materialize(plan, progress, CancellationToken.None);

        Assert.True(result.Succeeded);
        var wait = Assert.Single(reported);
        Assert.Equal("正在等待其他实例生成播放缓存", wait.Stage);
        Assert.Null(wait.Percentage);
        Assert.Null(wait.EstimatedRemaining);
    }

    private sealed class WaitReportingArtifactStore : IPlaybackArtifactStore
    {
        public string RootDirectory => Path.GetTempPath();

        public PlaybackArtifactMaterialization GetOrCreate(
            CachePlaybackPlan plan,
            string extension,
            Action<string> producer,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The progress-aware overload was not used.");
        }

        public PlaybackArtifactMaterialization GetOrCreate(
            CachePlaybackPlan plan,
            string extension,
            Action<string> producer,
            CancellationToken cancellationToken,
            Action<string, double?>? reportProgress)
        {
            reportProgress?.Invoke("正在等待其他实例生成播放缓存", null);
            return new PlaybackArtifactMaterialization(
                Path.Combine(Path.GetTempPath(), "reused.mp4"),
                WasReused: true);
        }

        public PlaybackArtifactCacheStatistics GetStatistics() =>
            new(RootDirectory, 0, 0);

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null) =>
            new(0, 0, 0);

        public PlaybackArtifactCleanupResult Cleanup(
            PlaybackArtifactCleanupOptions? options = null) =>
            new(0, 0, 0, 0);

        public PlaybackArtifactCleanupResult Clear() =>
            new(0, 0, 0, 0);
    }

    private sealed class UnexpectedTranscoder : IFfmpegTranscoder
    {
        public void ConcatToMp4(
            IReadOnlyList<string> inputFiles,
            string outputPath,
            TimeSpan expectedDuration,
            IProgress<PlaybackPreparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("A reused artifact must not run FFmpeg.");
        }

        public void MuxDashPairToMp4(
            string videoPath,
            string audioPath,
            string outputPath,
            TimeSpan expectedDuration,
            IProgress<PlaybackPreparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("A reused artifact must not run FFmpeg.");
        }
    }

    private sealed class SynchronousProgress(Action<PlaybackPreparationProgress> report)
        : IProgress<PlaybackPreparationProgress>
    {
        public void Report(PlaybackPreparationProgress value) => report(value);
    }
}
