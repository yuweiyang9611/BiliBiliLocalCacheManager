using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class StorageOverviewService(
    ICacheStorageStatisticsService cacheStorageStatisticsService,
    ICacheTrashService trashService,
    IPlaybackArtifactStore playbackArtifactStore) : IStorageOverviewService
{
    public StorageOverviewSnapshot GetSnapshot(
        string? cacheRoot,
        PlaybackArtifactCleanupOptions cleanupOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanupOptions);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<string>();
        CacheStorageStatistics? originalCache = null;
        CacheTrashStatistics? trash = null;
        PlaybackArtifactCacheStatistics? transcodeCache = null;
        PlaybackArtifactCleanupPreview? transcodeCleanupPreview = null;
        string? normalizedCacheRoot = null;

        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            errors.Add("尚未选择有效的 B 站缓存根目录。");
        }
        else
        {
            try
            {
                normalizedCacheRoot = Path.GetFullPath(cacheRoot);
                originalCache = cacheStorageStatisticsService.GetStatistics(
                    normalizedCacheRoot,
                    cancellationToken);
                if (originalCache.FailedEntryCount > 0)
                {
                    errors.Add($"原始缓存有 {originalCache.FailedEntryCount} 个条目统计失败。");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"原始缓存统计失败：{ex.Message}");
            }

            if (normalizedCacheRoot is not null)
            {
                try
                {
                    trash = trashService.GetStatistics(normalizedCacheRoot, cancellationToken);
                    if (trash.FailedEntryCount > 0)
                    {
                        errors.Add($"应用回收站有 {trash.FailedEntryCount} 个条目统计失败。");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"应用回收站统计失败：{ex.Message}");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            transcodeCache = playbackArtifactStore.GetStatistics();
            cancellationToken.ThrowIfCancellationRequested();
            transcodeCleanupPreview = playbackArtifactStore.PreviewCleanup(cleanupOptions);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"转码缓存统计失败：{ex.Message}");
        }

        return CreateSnapshot(
            normalizedCacheRoot,
            originalCache,
            transcodeCache,
            transcodeCleanupPreview,
            trash,
            errors.ToArray());
    }

    public StorageOverviewSnapshot RefreshTranscode(
        StorageOverviewSnapshot snapshot,
        PlaybackArtifactCleanupOptions cleanupOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(cleanupOptions);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = snapshot.Errors
            .Where(error => !error.StartsWith("转码缓存", StringComparison.Ordinal))
            .ToList();
        PlaybackArtifactCacheStatistics? transcodeCache = null;
        PlaybackArtifactCleanupPreview? transcodeCleanupPreview = null;
        try
        {
            transcodeCache = playbackArtifactStore.GetStatistics();
            cancellationToken.ThrowIfCancellationRequested();
            transcodeCleanupPreview = playbackArtifactStore.PreviewCleanup(cleanupOptions);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"转码缓存统计失败：{ex.Message}");
        }

        if (transcodeCache is not null && transcodeCleanupPreview is not null)
        {
            return ApplyTranscodeResult(snapshot, transcodeCache, transcodeCleanupPreview);
        }

        return CreateSnapshot(
            snapshot.CacheRoot,
            snapshot.OriginalCache,
            transcodeCache,
            transcodeCleanupPreview,
            snapshot.Trash,
            errors.ToArray());
    }

    public StorageOverviewSnapshot ApplyTranscodeResult(
        StorageOverviewSnapshot snapshot,
        PlaybackArtifactCacheStatistics statistics,
        PlaybackArtifactCleanupPreview preview)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(preview);
        var errors = snapshot.Errors
            .Where(error => !error.StartsWith("转码缓存", StringComparison.Ordinal))
            .ToArray();
        return CreateSnapshot(
            snapshot.CacheRoot,
            snapshot.OriginalCache,
            statistics,
            preview,
            snapshot.Trash,
            errors);
    }

    private static StorageOverviewSnapshot CreateSnapshot(
        string? cacheRoot,
        CacheStorageStatistics? originalCache,
        PlaybackArtifactCacheStatistics? transcodeCache,
        PlaybackArtifactCleanupPreview? transcodeCleanupPreview,
        CacheTrashStatistics? trash,
        IReadOnlyList<string> errors)
    {
        var managedTotalBytes = 0L;
        managedTotalBytes = SaturatingAdd(
            managedTotalBytes,
            originalCache?.TotalBytes ?? 0);
        managedTotalBytes = SaturatingAdd(
            managedTotalBytes,
            transcodeCache?.TotalBytes ?? 0);
        managedTotalBytes = SaturatingAdd(
            managedTotalBytes,
            trash?.TotalBytes ?? 0);

        var reclaimableBytes = SaturatingAdd(
            transcodeCleanupPreview?.ReclaimableBytes ?? 0,
            trash?.TotalBytes ?? 0);

        return new StorageOverviewSnapshot(
            cacheRoot,
            originalCache,
            transcodeCache,
            transcodeCleanupPreview,
            trash,
            managedTotalBytes,
            reclaimableBytes,
            DateTimeOffset.Now,
            errors);
    }

    private static long SaturatingAdd(long left, long right)
    {
        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }
}
