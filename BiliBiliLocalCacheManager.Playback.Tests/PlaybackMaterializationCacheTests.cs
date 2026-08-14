using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackMaterializationCacheTests
{
    [Fact]
    public void Materialize_ShouldNotInvokeFfmpegOrReportProgress_OnCacheHit()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_materialization_cache_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var plan = CachePlaybackPlan.Playable(
                100,
                "Title",
                1,
                "P1",
                "c_1",
                root,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { sourcePath },
                duration: TimeSpan.FromSeconds(10));
            var transcoder = new CountingTranscoder();
            var materializer = new SingleFilePlaybackMaterializer(
                transcoder,
                new PlaybackArtifactStore(Path.Combine(root, "artifacts")));
            var firstProgress = new List<PlaybackPreparationProgress>();
            var cachedProgress = new List<PlaybackPreparationProgress>();

            var first = materializer.Materialize(
                plan,
                new InlineProgress<PlaybackPreparationProgress>(firstProgress.Add),
                CancellationToken.None);
            var cached = materializer.Materialize(
                plan,
                new InlineProgress<PlaybackPreparationProgress>(cachedProgress.Add),
                CancellationToken.None);

            Assert.True(first.Succeeded);
            Assert.True(cached.Succeeded);
            Assert.Equal(first.OutputPath, cached.OutputPath);
            Assert.Equal(1, transcoder.CallCount);
            Assert.Single(firstProgress);
            Assert.Empty(cachedProgress);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class CountingTranscoder : IFfmpegTranscoder
    {
        public int CallCount { get; private set; }

        public void ConcatToMp4(
            IReadOnlyList<string> inputFiles,
            string outputPath,
            TimeSpan expectedDuration,
            IProgress<PlaybackPreparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            progress?.Report(new PlaybackPreparationProgress(
                "Preparing",
                50,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)));
            File.WriteAllBytes(outputPath, new byte[32]);
        }

        public void MuxDashPairToMp4(
            string videoPath,
            string audioPath,
            string outputPath,
            TimeSpan expectedDuration,
            IProgress<PlaybackPreparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
