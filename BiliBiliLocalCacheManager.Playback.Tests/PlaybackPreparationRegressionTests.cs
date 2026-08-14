using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackPreparationRegressionTests
{
    [Fact]
    public void RequiresFfmpegPreparation_ShouldMatchMaterialKindAndExtension()
    {
        var mp4 = CreatePlan(CachePlaybackMaterialKind.SingleFile, "video.mp4");
        var legacy = CreatePlan(CachePlaybackMaterialKind.SingleFile, "video.blv");
        var dash = CreatePlan(CachePlaybackMaterialKind.DashPair, "video.m4s", "audio.m4s");

        Assert.False(mp4.RequiresFfmpegPreparation);
        Assert.True(legacy.RequiresFfmpegPreparation);
        Assert.True(dash.RequiresFfmpegPreparation);
    }

    [Fact]
    public void MaterializerCancellation_ShouldForwardDurationAndRemovePartialArtifact()
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, "source.blv");
            File.WriteAllText(source, "source");
            var duration = TimeSpan.FromSeconds(42);
            var plan = CachePlaybackPlan.Playable(
                100,
                "Title",
                1,
                "P1",
                "c_1",
                root,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { source },
                duration: duration);
            using var cancellationSource = new CancellationTokenSource();
            var transcoder = new CancellingTranscoder(cancellationSource);
            var store = new PlaybackArtifactStore(Path.Combine(root, "artifacts"));
            var materializer = new SingleFilePlaybackMaterializer(transcoder, store);
            var reports = new List<PlaybackPreparationProgress>();

            Assert.ThrowsAny<OperationCanceledException>(() => materializer.Materialize(
                plan,
                new InlineProgress<PlaybackPreparationProgress>(reports.Add),
                cancellationSource.Token));

            Assert.Equal(duration, transcoder.ExpectedDuration);
            Assert.Single(reports);
            Assert.Empty(Directory.Exists(store.RootDirectory)
                ? Directory.GetFiles(store.RootDirectory, "*", SearchOption.AllDirectories)
                : Array.Empty<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlayAsyncCancellation_ShouldNotLaunchPlayer()
    {
        var root = CreateTempRoot();
        try
        {
            var video = Path.Combine(root, "video.m4s");
            var audio = Path.Combine(root, "audio.m4s");
            File.WriteAllText(video, "video");
            File.WriteAllText(audio, "audio");
            var plan = CachePlaybackPlan.Playable(
                100,
                "Title",
                1,
                "P1",
                "c_1",
                root,
                "NewDash",
                CachePlaybackMaterialKind.DashPair,
                new[] { video, audio },
                duration: TimeSpan.FromSeconds(10));
            using var cancellationSource = new CancellationTokenSource();
            var launcher = new CountingLauncher();
            var service = new CachePlaybackService(
                new[] { new FixedLayoutHandler(plan) },
                new CancellingMaterializer(cancellationSource),
                launcher);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PlayAsync(
                CreateSegment(root),
                progress: new InlineProgress<PlaybackPreparationProgress>(_ => { }),
                cancellationToken: cancellationSource.Token));

            Assert.Equal(0, launcher.CallCount);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static CachePlaybackPlan CreatePlan(
        CachePlaybackMaterialKind materialKind,
        params string[] mediaFiles)
    {
        return CachePlaybackPlan.Playable(
            100,
            "Title",
            1,
            "P1",
            "c_1",
            Path.GetTempPath(),
            "Test",
            materialKind,
            mediaFiles);
    }

    private static BiliSegment CreateSegment(string directory)
    {
        return new BiliSegment(
            100,
            1,
            null,
            1,
            "P1",
            "Title",
            CacheVersion.Modern,
            "80",
            null,
            80,
            null,
            true,
            10,
            10,
            TimeSpan.FromSeconds(10),
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            directory,
            Path.Combine(directory, "entry.json"),
            Array.Empty<string>(),
            string.Empty,
            null,
            null);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bili_playback_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup failures.
        }
    }

    private sealed class CancellingTranscoder(CancellationTokenSource cancellationSource) : IFfmpegTranscoder
    {
        public TimeSpan ExpectedDuration { get; private set; }

        public void ConcatToMp4(
            IReadOnlyList<string> inputFiles,
            string outputPath,
            TimeSpan expectedDuration,
            IProgress<PlaybackPreparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            ExpectedDuration = expectedDuration;
            File.WriteAllText(outputPath, "partial");
            progress?.Report(new PlaybackPreparationProgress(
                "Preparing",
                25,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3)));
            cancellationSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class FixedLayoutHandler(CachePlaybackPlan plan) : ICachePlaybackLayoutHandler
    {
        public string Name => "Fixed";
        public int Priority => 1;
        public bool CanHandle(CachePlaybackProbe probe) => true;
        public CachePlaybackPlan BuildPlan(CachePlaybackProbe probe) => plan;
    }

    private sealed class CancellingMaterializer(CancellationTokenSource cancellationSource) : IPlaybackMaterializer
    {
        public bool CanHandle(CachePlaybackPlan plan) => true;

        public PlaybackMaterializationResult Materialize(CachePlaybackPlan plan)
        {
            throw new NotSupportedException();
        }

        public PlaybackMaterializationResult Materialize(
            CachePlaybackPlan plan,
            IProgress<PlaybackPreparationProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new PlaybackPreparationProgress(
                "Preparing",
                10,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(9)));
            cancellationSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }

    private sealed class CountingLauncher : IPlaybackLauncher
    {
        public int CallCount { get; private set; }

        public PlaybackLaunchResult Launch(
            PlaybackMaterializationResult materializationResult,
            PlaybackLaunchOptions? launchOptions = null)
        {
            CallCount++;
            return PlaybackLaunchResult.Success("Started", "Test");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
