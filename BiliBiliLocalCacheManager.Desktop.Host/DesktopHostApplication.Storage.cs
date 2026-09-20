using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private async Task<object> GetStorageAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var activity = CreateDiskActivity(requestId, "storage.get", cancellationToken);
        var store = new PlaybackArtifactStore(_artifactStore.RootDirectory, activity);
        var settings = _settingsStore.GetState().Settings;
        var cleanupOptions = CreateCleanupOptions(settings);
        var rawRoot = parameters.OptionalString("rootPath") ?? settings.RootPath;
        string? root = null;
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawRoot))
        {
            root = Path.GetFullPath(rawRoot);
            if (!Directory.Exists(root))
            {
                errors.Add($"Cache root does not exist: {root}");
            }
        }
        else
        {
            errors.Add("No cache root is configured.");
        }

        CacheStorageStatistics? originalCache = null;
        CacheTrashStatistics? trash = null;
        PlaybackArtifactCacheStatistics? transcodeCache = null;
        PlaybackArtifactCleanupPreview? transcodePreview = null;
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root is not null && Directory.Exists(root))
            {
                try
                {
                    originalCache = new FileSystemCacheStorageStatisticsService().GetStatistics(root, cancellationToken, activity);
                    if (originalCache.FailedEntryCount > 0)
                    {
                        errors.Add($"{originalCache.FailedEntryCount} cache entries could not be measured.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    errors.Add($"Cache statistics failed: {exception.Message}");
                }

                try
                {
                    trash = new FileSystemCacheTrashService().GetStatistics(root, cancellationToken, activity);
                    if (trash.FailedEntryCount > 0)
                    {
                        errors.Add($"{trash.FailedEntryCount} trash entries could not be measured.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    errors.Add($"Trash statistics failed: {exception.Message}");
                }
            }

            try
            {
                transcodeCache = store.GetStatistics();
                cancellationToken.ThrowIfCancellationRequested();
                transcodePreview = store.PreviewCleanup(cleanupOptions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add($"Transcode cache statistics failed: {exception.Message}");
            }
        }, cancellationToken);

        var managedTotal = SaturatingAdd(
            SaturatingAdd(originalCache?.TotalBytes ?? 0, trash?.TotalBytes ?? 0),
            transcodeCache?.TotalBytes ?? 0);
        var reclaimable = SaturatingAdd(
            trash?.TotalBytes ?? 0,
            transcodePreview?.ReclaimableBytes ?? 0);
        return new
        {
            originalCache = new
            {
                bytes = originalCache?.TotalBytes ?? 0,
                itemCount = originalCache?.ManagedEntryCount ?? 0,
                path = root
            },
            transcodeCache = new
            {
                bytes = transcodeCache?.TotalBytes ?? 0,
                itemCount = transcodeCache?.FileCount ?? 0,
                path = transcodeCache?.RootDirectory ?? _artifactStore.RootDirectory
            },
            trash = new
            {
                bytes = trash?.TotalBytes ?? 0,
                itemCount = trash?.ManagedEntryCount ?? 0,
                path = trash?.TrashDirectory
            },
            totalBytes = managedTotal,
            lastMaintenanceSummary = errors.Count == 0
                ? $"Policy can reclaim {reclaimable} bytes."
                : string.Join("; ", errors)
        };
    }

    private async Task<object> CleanupArtifactsAsync(string requestId, CancellationToken cancellationToken)
    {
        var store = new PlaybackArtifactStore(_artifactStore.RootDirectory, CreateDiskActivity(requestId, "artifacts.cleanup", cancellationToken));
        var cleanupOptions = CreateCleanupOptions(_settingsStore.GetState().Settings);
        var result = await RunArtifactMaintenanceAsync(
            () => store.Cleanup(cleanupOptions),
            cancellationToken);
        RecordArtifactMaintenance("Manual policy cleanup", result);
        return ToWireArtifactCleanupResult(result);
    }

    private async Task<object> ClearArtifactsAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.OptionalBoolean("confirmed") != true)
        {
            throw new RpcException(
                "confirmation_required",
                "artifacts.clear requires params.confirmed=true because it is irreversible.");
        }

        var store = new PlaybackArtifactStore(_artifactStore.RootDirectory, CreateDiskActivity(requestId, "artifacts.clear", cancellationToken));
        var result = await RunArtifactMaintenanceAsync(
            () => store.Cleanup(CreateClearOptions()),
            cancellationToken);
        RecordArtifactMaintenance("Manual cache clear", result);
        return ToWireArtifactCleanupResult(result);
    }
}
