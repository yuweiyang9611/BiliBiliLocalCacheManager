using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public interface IStorageOverviewService
{
    StorageOverviewSnapshot GetSnapshot(
        string? cacheRoot,
        PlaybackArtifactCleanupOptions cleanupOptions,
        CancellationToken cancellationToken = default);

    StorageOverviewSnapshot RefreshTranscode(
        StorageOverviewSnapshot snapshot,
        PlaybackArtifactCleanupOptions cleanupOptions,
        CancellationToken cancellationToken = default);

    StorageOverviewSnapshot ApplyTranscodeResult(
        StorageOverviewSnapshot snapshot,
        PlaybackArtifactCacheStatistics statistics,
        PlaybackArtifactCleanupPreview preview) =>
        throw new NotSupportedException("Applying a known transcode result is not supported.");
}
