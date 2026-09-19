namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed record PlaybackArtifactCleanupResult(
    int DeletedFileCount,
    long FreedBytes,
    int FailedFileCount,
    long RemainingBytes,
    PlaybackArtifactCacheStatistics? Statistics = null,
    PlaybackArtifactCleanupPreview? Preview = null,
    bool Cancelled = false,
    int UnprocessedFileCount = 0,
    bool RemainingBytesEstimated = false);
