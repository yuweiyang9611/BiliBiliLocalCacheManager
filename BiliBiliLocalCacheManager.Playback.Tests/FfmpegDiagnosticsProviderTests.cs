using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class FfmpegDiagnosticsProviderTests
{
    [Fact]
    public void GetSnapshot_BeforeInitialization_IsObservationalAndReturnsNotInitialized()
    {
        var state = new FfmpegDiagnosticState();
        var provider = new BundledFfmpegDiagnosticsProvider(state);

        var snapshot = provider.GetSnapshot();

        Assert.Same(FfmpegDiagnosticSnapshot.NotInitialized, snapshot);
        Assert.False(snapshot.IsInitialized);
        Assert.Equal(FfmpegResolutionSource.NotInitialized, snapshot.Source);
    }

    [Fact]
    public void GetSnapshot_AfterPublish_ReturnsPublishedMetadataWithoutResolutionWork()
    {
        var state = new FfmpegDiagnosticState();
        var provider = new BundledFfmpegDiagnosticsProvider(state);
        var expected = new FfmpegDiagnosticSnapshot(
            true,
            FfmpegResolutionSource.ArchiveOverride,
            @"D:\tools\ffmpeg",
            "test-version");
        state.Publish(expected);

        var snapshot = provider.GetSnapshot();

        Assert.Same(expected, snapshot);
    }
}
