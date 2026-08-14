using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed record StorageOverviewSnapshot(
    string? CacheRoot,
    CacheStorageStatistics? OriginalCache,
    PlaybackArtifactCacheStatistics? TranscodeCache,
    PlaybackArtifactCleanupPreview? TranscodeCleanupPreview,
    CacheTrashStatistics? Trash,
    long ManagedTotalBytes,
    long ReclaimableBytes,
    DateTimeOffset RefreshedAt,
    IReadOnlyList<string> Errors)
{
    public bool IsComplete =>
        Errors.Count == 0 &&
        OriginalCache?.FailedEntryCount is not > 0 &&
        Trash?.FailedEntryCount is not > 0;
}
