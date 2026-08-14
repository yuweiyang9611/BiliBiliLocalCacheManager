namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed class PlaybackArtifactCleanupOptions
{
    public static readonly TimeSpan DefaultCapacityEvictionGracePeriod = TimeSpan.FromMinutes(5);

    public const int DefaultRetentionDays = 30;
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 5 * 365;
    public const int DefaultMaxSizeGigabytes = 20;
    public const int MinimumMaxSizeGigabytes = 1;
    public const int MaximumMaxSizeGigabytes = 128;

    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(DefaultRetentionDays);

    public long MaxTotalBytes { get; set; } = DefaultMaxSizeGigabytes * BytesPerGigabyte;

    /// <summary>
    /// Prevents capacity-based eviction of artifacts that were created or reused very recently.
    /// </summary>
    public TimeSpan CapacityEvictionGracePeriod { get; set; } = DefaultCapacityEvictionGracePeriod;

    /// <summary>
    /// Gets or sets exact managed artifact paths that policy cleanup must preserve.
    /// </summary>
    public IReadOnlyCollection<string> ProtectedPaths { get; set; } = Array.Empty<string>();

    public static bool IsValidRetentionDays(int value)
    {
        return value is >= MinimumRetentionDays and <= MaximumRetentionDays;
    }

    public static bool IsValidMaxSizeGigabytes(int value)
    {
        return value is >= MinimumMaxSizeGigabytes and <= MaximumMaxSizeGigabytes;
    }

    public static PlaybackArtifactCleanupOptions FromUserLimits(
        int retentionDays,
        int maxSizeGigabytes)
    {
        if (!IsValidRetentionDays(retentionDays))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                retentionDays,
                $"Retention days must be between {MinimumRetentionDays} and {MaximumRetentionDays}.");
        }

        if (!IsValidMaxSizeGigabytes(maxSizeGigabytes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSizeGigabytes),
                maxSizeGigabytes,
                $"Maximum cache size must be between {MinimumMaxSizeGigabytes} and {MaximumMaxSizeGigabytes} GB.");
        }

        return new PlaybackArtifactCleanupOptions
        {
            MaxAge = TimeSpan.FromDays(retentionDays),
            MaxTotalBytes = checked(maxSizeGigabytes * BytesPerGigabyte)
        };
    }
}
