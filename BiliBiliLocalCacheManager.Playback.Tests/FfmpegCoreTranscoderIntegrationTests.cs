using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

[Collection(FfmpegIntegrationCollection.Name)]
public sealed class FfmpegCoreTranscoderIntegrationTests(
    FfmpegIntegrationFixture fixture)
{
    [FfmpegIntegrationFact]
    [Trait("Category", "FFmpegIntegration")]
    public void MuxDashPairToMp4_ShouldCopyAacPacketsWithoutReencoding()
    {
        using var workspace = fixture.CreateWorkspace();
        var outputPath = Path.Combine(workspace.RootDirectory, "aac-output.mp4");
        var inputAudioHashes = fixture.GetPacketHashes(
            workspace.AacAudioPath,
            "a:0");
        var inputVideoHashes = fixture.GetPacketHashes(
            workspace.VideoPath,
            "v:0");

        using var cancellationSource =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));
        new FfmpegCoreTranscoder().MuxDashPairToMp4(
            workspace.VideoPath,
            workspace.AacAudioPath,
            outputPath,
            TimeSpan.FromSeconds(2),
            progress: null,
            cancellationSource.Token);

        Assert.True(File.Exists(outputPath));
        Assert.Equal("mpeg4", fixture.GetCodecName(outputPath, "v:0"));
        Assert.Equal("aac", fixture.GetCodecName(outputPath, "a:0"));
        Assert.NotEmpty(inputAudioHashes);
        Assert.NotEmpty(inputVideoHashes);
        var outputAudioHashes = fixture.GetPacketHashes(outputPath, "a:0");
        Assert.Equal(inputAudioHashes.ToArray(), outputAudioHashes.ToArray());
        Assert.Equal(
            inputVideoHashes.ToArray(),
            fixture.GetPacketHashes(outputPath, "v:0").ToArray());
    }

    [FfmpegIntegrationFact]
    [Trait("Category", "FFmpegIntegration")]
    public void MuxDashPairToMp4_ShouldTranscodeNonAacAudioAndCopyVideo()
    {
        using var workspace = fixture.CreateWorkspace();
        var outputPath = Path.Combine(workspace.RootDirectory, "fallback-output.mp4");
        var inputVideoHashes = fixture.GetPacketHashes(
            workspace.VideoPath,
            "v:0");

        Assert.Equal("wmav2", fixture.GetCodecName(workspace.WmaAudioPath, "a:0"));

        using var cancellationSource =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));
        new FfmpegCoreTranscoder().MuxDashPairToMp4(
            workspace.VideoPath,
            workspace.WmaAudioPath,
            outputPath,
            TimeSpan.FromSeconds(2),
            progress: null,
            cancellationSource.Token);

        Assert.True(File.Exists(outputPath));
        Assert.Equal("mpeg4", fixture.GetCodecName(outputPath, "v:0"));
        Assert.Equal("aac", fixture.GetCodecName(outputPath, "a:0"));
        Assert.NotEmpty(inputVideoHashes);
        Assert.Equal(
            inputVideoHashes.ToArray(),
            fixture.GetPacketHashes(outputPath, "v:0").ToArray());
    }

    [FfmpegIntegrationFact]
    [Trait("Category", "FFmpegIntegration")]
    public void Materialize_ShouldRemoveFinalAndPartialArtifacts_WhenRealFfmpegIsCancelled()
    {
        using var workspace = fixture.CreateWorkspace(includeCancellationAudio: true);
        var cancellationAudioPath = Assert.IsType<string>(
            workspace.CancellationAudioPath);
        var store = new PlaybackArtifactStore(workspace.ArtifactRoot);
        var materializer = new DashPairPlaybackMaterializer(
            new FfmpegCoreTranscoder(),
            store);
        var plan = CreateDashPlan(
            workspace,
            cancellationAudioPath,
            TimeSpan.FromHours(1));
        using var cancellationSource = new CancellationTokenSource(
            TimeSpan.FromSeconds(30));
        var cancelledDuringActiveProcessing = 0;
        var progress = new InlineProgress<PlaybackPreparationProgress>(report =>
        {
            if (report.Stage.Contains("兼容转码", StringComparison.Ordinal) &&
                report.Percentage is > 0d and < 100d &&
                Interlocked.Exchange(ref cancelledDuringActiveProcessing, 1) == 0)
            {
                cancellationSource.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => materializer.Materialize(
            plan,
            progress,
            cancellationSource.Token));

        Assert.Equal(1, Volatile.Read(ref cancelledDuringActiveProcessing));
        Assert.Equal(0, store.GetStatistics().FileCount);
        var remainingFiles = Directory.Exists(workspace.ArtifactRoot)
            ? Directory.GetFiles(
                workspace.ArtifactRoot,
                "*",
                SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.DoesNotContain(
            remainingFiles,
            path => string.Equals(
                Path.GetExtension(path),
                ".mp4",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            remainingFiles,
            path => Path.GetFileName(path).Contains(
                ".building-",
                StringComparison.OrdinalIgnoreCase));
    }

    [FfmpegIntegrationFact]
    [Trait("Category", "FFmpegIntegration")]
    public void Materialize_ShouldReuseArtifactWithoutRunningFfmpegAgain()
    {
        using var workspace = fixture.CreateWorkspace();
        var store = new PlaybackArtifactStore(workspace.ArtifactRoot);
        var materializer = new DashPairPlaybackMaterializer(
            new FfmpegCoreTranscoder(),
            store);
        var plan = CreateDashPlan(
            workspace,
            workspace.AacAudioPath,
            TimeSpan.FromSeconds(2));
        var firstProgress = new List<PlaybackPreparationProgress>();
        var cachedProgress = new List<PlaybackPreparationProgress>();
        using var cancellationSource =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var first = materializer.Materialize(
            plan,
            new InlineProgress<PlaybackPreparationProgress>(firstProgress.Add),
            cancellationSource.Token);
        var cached = materializer.Materialize(
            plan,
            new InlineProgress<PlaybackPreparationProgress>(cachedProgress.Add),
            cancellationSource.Token);

        Assert.True(first.Succeeded, first.Message);
        Assert.True(cached.Succeeded, cached.Message);
        Assert.Equal(first.OutputPath, cached.OutputPath);
        Assert.NotEmpty(firstProgress);
        Assert.Empty(cachedProgress);
        Assert.Contains("复用", cached.Message, StringComparison.Ordinal);
        Assert.Equal(1, store.GetStatistics().FileCount);
    }

    [FfmpegIntegrationFact]
    [Trait("Category", "FFmpegIntegration")]
    public void Materialize_ShouldInvalidateArtifact_WhenSourceChanges()
    {
        using var workspace = fixture.CreateWorkspace();
        var store = new PlaybackArtifactStore(workspace.ArtifactRoot);
        var materializer = new DashPairPlaybackMaterializer(
            new FfmpegCoreTranscoder(),
            store);
        var plan = CreateDashPlan(
            workspace,
            workspace.AacAudioPath,
            TimeSpan.FromSeconds(2));
        using var cancellationSource =
            new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var first = materializer.Materialize(
            plan,
            progress: null,
            cancellationSource.Token);
        Assert.True(first.Succeeded, first.Message);

        var originalTimestamp = File.GetLastWriteTimeUtc(workspace.VideoPath);
        File.SetLastWriteTimeUtc(
            workspace.VideoPath,
            originalTimestamp.AddMinutes(1));
        Assert.NotEqual(
            originalTimestamp,
            File.GetLastWriteTimeUtc(workspace.VideoPath));
        var refreshedProgress = new List<PlaybackPreparationProgress>();

        var refreshed = materializer.Materialize(
            plan,
            new InlineProgress<PlaybackPreparationProgress>(refreshedProgress.Add),
            cancellationSource.Token);

        Assert.True(refreshed.Succeeded, refreshed.Message);
        Assert.NotEqual(first.OutputPath, refreshed.OutputPath);
        Assert.NotEmpty(refreshedProgress);
        Assert.Equal(2, store.GetStatistics().FileCount);
    }

    private static CachePlaybackPlan CreateDashPlan(
        FfmpegIntegrationWorkspace workspace,
        string audioPath,
        TimeSpan duration)
    {
        return CachePlaybackPlan.Playable(
            100,
            "FFmpeg integration",
            1,
            "P1",
            "c_1",
            workspace.RootDirectory,
            "IntegrationDash",
            CachePlaybackMaterialKind.DashPair,
            new[] { workspace.VideoPath, audioPath },
            duration: duration);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
