using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

/// <summary>
/// 管理播放过程中生成的媒体产物，负责复用、原子写入和受控清理。
/// </summary>
public interface IPlaybackArtifactStore
{
    string RootDirectory { get; }

    PlaybackArtifactMaterialization GetOrCreate(
        CachePlaybackPlan plan,
        string extension,
        Action<string> producer,
        CancellationToken cancellationToken = default);

    PlaybackArtifactMaterialization GetOrCreate(
        CachePlaybackPlan plan,
        string extension,
        Action<string> producer,
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        return GetOrCreate(plan, extension, producer, cancellationToken);
    }

    PlaybackArtifactCacheStatistics GetStatistics();

    PlaybackArtifactCleanupPreview PreviewCleanup(PlaybackArtifactCleanupOptions? options = null);

    PlaybackArtifactCleanupResult Cleanup(PlaybackArtifactCleanupOptions? options = null);

    PlaybackArtifactCleanupResult Clear();
}
