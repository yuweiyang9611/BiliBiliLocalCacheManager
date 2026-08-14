using System.Globalization;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IStorageOverviewService? _storageOverviewService;
    private readonly SemaphoreSlim _storageOverviewRefreshGate = new(1, 1);
    private readonly object _storageOverviewRefreshCancellationSync = new();
    private CancellationTokenSource? _storageOverviewRefreshCancellation;
    private long _storageOverviewGeneration;

    public AsyncRelayCommand RefreshStorageOverviewCommand { get; }

    public StorageOverviewSnapshot? StorageOverview
    {
        get;
        private set => SetField(ref field, value);
    }

    public bool IsStorageOverviewBusy
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                RefreshStorageOverviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StorageOverviewSummary
    {
        get;
        private set => SetField(ref field, value);
    } = "受管空间等待后台统计";

    public string OriginalCacheStorageSummary
    {
        get;
        private set => SetField(ref field, value);
    } = "原始缓存等待统计";

    public string TranscodeStorageSummary
    {
        get;
        private set => SetField(ref field, value);
    } = "转码缓存等待统计";

    public string TrashStorageSummary
    {
        get;
        private set => SetField(ref field, value);
    } = "应用回收站等待统计";

    public string LastStorageMaintenanceSummary
    {
        get;
        private set => SetField(ref field, value);
    } = "最近清理：本次运行尚未执行";

    private bool CanRefreshStorageOverview()
    {
        return _storageOverviewService is not null &&
            !IsBusy &&
            !IsPlaybackBusy &&
            !IsStorageOverviewBusy &&
            !IsTranscodeCacheMaintenanceBusy;
    }

    private async Task RefreshStorageOverviewAsync()
    {
        await RefreshStorageOverviewCoreAsync(transcodeOnly: false);
    }

    private async Task RefreshTranscodeStorageOverviewAsync(
        PlaybackArtifactCacheStatistics? knownStatistics = null,
        PlaybackArtifactCleanupPreview? knownPreview = null)
    {
        await RefreshStorageOverviewCoreAsync(
            transcodeOnly: StorageOverview is not null,
            knownStatistics,
            knownPreview);
    }

    private async Task RefreshStorageOverviewCoreAsync(
        bool transcodeOnly,
        PlaybackArtifactCacheStatistics? knownStatistics = null,
        PlaybackArtifactCleanupPreview? knownPreview = null)
    {
        if (_storageOverviewService is null)
        {
            return;
        }

        using var cancellation = BeginStorageOverviewRefresh();
        var cancellationToken = cancellation.Token;
        var requestGeneration = Volatile.Read(ref _storageOverviewGeneration);
        var enteredRefreshGate = false;
        try
        {
            await _storageOverviewRefreshGate.WaitAsync(cancellationToken);
            enteredRefreshGate = true;
            IsStorageOverviewBusy = true;

            var root = RootPath.Trim();
            var cleanupOptions = CreateTranscodeCacheCleanupOptions();
            var currentSnapshot = StorageOverview;
            var snapshot = await Task.Run(() =>
                transcodeOnly && currentSnapshot is not null
                    ? knownStatistics is not null && knownPreview is not null
                        ? _storageOverviewService.ApplyTranscodeResult(
                            currentSnapshot,
                            knownStatistics,
                            knownPreview)
                        : _storageOverviewService.RefreshTranscode(
                            currentSnapshot,
                            cleanupOptions,
                            cancellationToken)
                    : _storageOverviewService.GetSnapshot(
                        root,
                        cleanupOptions,
                        cancellationToken),
                cancellationToken);

            if (requestGeneration != Volatile.Read(ref _storageOverviewGeneration) ||
                !string.Equals(root, RootPath.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StorageOverview = snapshot;
            UpdateStorageOverviewPresentation(snapshot);
        }
        catch (OperationCanceledException)
        {
            // A newer refresh or a root/policy change superseded this request.
        }
        catch (Exception ex)
        {
            if (requestGeneration == Volatile.Read(ref _storageOverviewGeneration))
            {
                StorageOverview = null;
                StorageOverviewSummary = $"存储统计失败：{ex.Message}";
                OriginalCacheStorageSummary = "原始缓存统计不可用";
                TranscodeStorageSummary = "转码缓存统计不可用";
                TrashStorageSummary = "应用回收站统计不可用";
                RecordDiagnosticEvent(
                    "Storage",
                    DiagnosticEventLevel.Error,
                    StorageOverviewSummary,
                    ex);
            }
        }
        finally
        {
            if (enteredRefreshGate)
            {
                IsStorageOverviewBusy = false;
                _storageOverviewRefreshGate.Release();
            }

            CompleteStorageOverviewRefresh(cancellation);
        }
    }

    private void InvalidateStorageOverview()
    {
        Interlocked.Increment(ref _storageOverviewGeneration);
        CancelStorageOverviewRefresh();
        StorageOverview = null;
        StorageOverviewSummary = "缓存根目录已变化，存储统计待刷新";
        OriginalCacheStorageSummary = "原始缓存待刷新";
        TranscodeStorageSummary = "转码缓存待刷新";
        TrashStorageSummary = "应用回收站待刷新";
        RefreshStorageOverviewCommand.RaiseCanExecuteChanged();
    }

    private void MarkStorageOverviewPolicyStale()
    {
        var snapshot = StorageOverview;
        Interlocked.Increment(ref _storageOverviewGeneration);
        CancelStorageOverviewRefresh();
        if (snapshot is null)
        {
            StorageOverviewSummary = "转码缓存策略已变化，预计可释放空间待刷新";
            TranscodeStorageSummary = "转码缓存策略已变化，待刷新";
            return;
        }

        StorageOverview = snapshot with
        {
            TranscodeCleanupPreview = null,
            ReclaimableBytes = snapshot.Trash?.TotalBytes ?? 0
        };
        StorageOverviewSummary = "转码缓存策略已变化，预计可释放空间待刷新";
        TranscodeStorageSummary = "转码缓存策略已变化，待刷新";
    }

    private CancellationTokenSource BeginStorageOverviewRefresh()
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_storageOverviewRefreshCancellationSync)
        {
            previous = _storageOverviewRefreshCancellation;
            _storageOverviewRefreshCancellation = cancellation;
        }

        TryCancelStorageOverviewRefresh(previous);
        return cancellation;
    }

    private void CancelStorageOverviewRefresh()
    {
        CancellationTokenSource? cancellation;
        lock (_storageOverviewRefreshCancellationSync)
        {
            cancellation = _storageOverviewRefreshCancellation;
        }

        TryCancelStorageOverviewRefresh(cancellation);
    }

    private static void TryCancelStorageOverviewRefresh(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded request completed between the snapshot and cancellation attempt.
        }
    }

    private void CompleteStorageOverviewRefresh(CancellationTokenSource cancellation)
    {
        lock (_storageOverviewRefreshCancellationSync)
        {
            if (ReferenceEquals(_storageOverviewRefreshCancellation, cancellation))
            {
                _storageOverviewRefreshCancellation = null;
            }
        }
    }

    private void UpdateStorageOverviewPresentation(StorageOverviewSnapshot snapshot)
    {
        StorageOverviewSummary =
            $"受管空间 {FormatBytes(snapshot.ManagedTotalBytes)}；" +
            $"预计可释放 {FormatBytes(snapshot.ReclaimableBytes)}；" +
            $"刷新于 {snapshot.RefreshedAt.LocalDateTime:HH:mm:ss}" +
            (snapshot.IsComplete
                ? string.Empty
                : $"；统计不完整（{snapshot.Errors.Count} 项提示）");

        if (snapshot.OriginalCache is { } original)
        {
            OriginalCacheStorageSummary =
                $"{original.ManagedEntryCount} 条 / {original.FileCount} 个文件 / " +
                $"{FormatBytes(original.TotalBytes)}" +
                (original.FailedEntryCount > 0
                    ? $"；失败 {original.FailedEntryCount} 条"
                    : string.Empty) +
                (original.SkippedEntryCount > 0
                    ? $"；忽略 {original.SkippedEntryCount} 项"
                    : string.Empty);
        }
        else
        {
            OriginalCacheStorageSummary = "原始缓存未统计，请检查缓存根目录";
        }

        if (snapshot.TranscodeCache is { } transcode)
        {
            var preview = snapshot.TranscodeCleanupPreview;
            TranscodeStorageSummary =
                $"{transcode.FileCount} 个文件 / {FormatBytes(transcode.TotalBytes)}；" +
                (preview is null
                    ? "预计释放不可用"
                    : $"按当前策略预计释放 {FormatBytes(preview.ReclaimableBytes)}（{preview.CandidateFileCount} 个）");
        }
        else
        {
            TranscodeStorageSummary = "转码缓存统计不可用";
        }

        if (snapshot.Trash is { } trash)
        {
            var otherUnknownCount = Math.Max(
                0,
                trash.SkippedEntryCount - trash.UntrustedLegacyEntryCount);
            TrashStorageSummary =
                $"{trash.ManagedEntryCount} 条 / {trash.FileCount} 个文件 / " +
                $"{FormatBytes(trash.TotalBytes)} 可释放" +
                (trash.UntrustedLegacyEntryCount > 0
                    ? $"；旧版未验证 {trash.UntrustedLegacyEntryCount} 条 / " +
                      $"{trash.UntrustedLegacyFileCount} 个文件 / " +
                      $"{FormatBytes(trash.UntrustedLegacyBytes)}，清空时需二次确认"
                    : string.Empty) +
                (trash.PendingPurgeEntryCount > 0
                    ? $"；待重试永久清理 {trash.PendingPurgeEntryCount} 条（不可恢复）"
                    : string.Empty) +
                (trash.FailedEntryCount > 0
                    ? $"；失败 {trash.FailedEntryCount} 条"
                    : string.Empty) +
                (otherUnknownCount > 0
                    ? $"；其他未知 {otherUnknownCount} 项不会清理"
                    : string.Empty);
        }
        else
        {
            TrashStorageSummary = "应用回收站未统计，请检查缓存根目录";
        }
    }

    private void RecordStorageMaintenance(
        string operation,
        long freedBytes,
        int failedCount)
    {
        var completedAt = DateTimeOffset.Now;
        LastStorageMaintenanceSummary =
            $"最近清理：{completedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)} · " +
            $"{operation} · 释放 {FormatBytes(freedBytes)}" +
            (failedCount > 0 ? $" · 失败 {failedCount} 项" : string.Empty);
        RecordDiagnosticEvent(
            "Storage",
            failedCount > 0 ? DiagnosticEventLevel.Warning : DiagnosticEventLevel.Information,
            LastStorageMaintenanceSummary);
    }
}
