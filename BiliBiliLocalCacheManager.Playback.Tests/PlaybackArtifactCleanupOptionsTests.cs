using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactCleanupOptionsTests
{
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void Defaults_ShouldRemainThirtyDaysAndTwentyGigabytes()
    {
        var options = new PlaybackArtifactCleanupOptions();

        Assert.Equal(TimeSpan.FromDays(30), options.MaxAge);
        Assert.Equal(20L * BytesPerGigabyte, options.MaxTotalBytes);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultCapacityEvictionGracePeriod,
            options.CapacityEvictionGracePeriod);
        Assert.Empty(options.ProtectedPaths);
    }

    [Fact]
    public void FromUserLimits_ShouldConvertValidatedValues()
    {
        var options = PlaybackArtifactCleanupOptions.FromUserLimits(45, 128);

        Assert.Equal(TimeSpan.FromDays(45), options.MaxAge);
        Assert.Equal(128L * BytesPerGigabyte, options.MaxTotalBytes);
    }

    [Fact]
    public void FromUserLimits_ShouldAcceptBoundaryValues()
    {
        var minimum = PlaybackArtifactCleanupOptions.FromUserLimits(
            PlaybackArtifactCleanupOptions.MinimumRetentionDays,
            PlaybackArtifactCleanupOptions.MinimumMaxSizeGigabytes);
        var maximum = PlaybackArtifactCleanupOptions.FromUserLimits(
            PlaybackArtifactCleanupOptions.MaximumRetentionDays,
            PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes);

        Assert.Equal(
            TimeSpan.FromDays(PlaybackArtifactCleanupOptions.MinimumRetentionDays),
            minimum.MaxAge);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.MinimumMaxSizeGigabytes * BytesPerGigabyte,
            minimum.MaxTotalBytes);
        Assert.Equal(
            TimeSpan.FromDays(PlaybackArtifactCleanupOptions.MaximumRetentionDays),
            maximum.MaxAge);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes * BytesPerGigabyte,
            maximum.MaxTotalBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1826)]
    public void FromUserLimits_ShouldRejectInvalidRetentionDays(int retentionDays)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaybackArtifactCleanupOptions.FromUserLimits(retentionDays, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(129)]
    public void FromUserLimits_ShouldRejectInvalidMaximumSize(int maxSizeGigabytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaybackArtifactCleanupOptions.FromUserLimits(30, maxSizeGigabytes));
    }
}
