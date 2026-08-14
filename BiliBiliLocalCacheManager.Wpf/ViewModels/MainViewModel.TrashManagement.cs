using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Wpf.Commands;

namespace BiliBiliLocalCacheManager.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    public AsyncRelayCommand PurgeTrashCommand { get; }

    private bool CanPurgeTrash()
    {
        return _trashService is not null && !IsBusy && !IsPlaybackBusy;
    }

    private async Task PurgeTrashAsync()
    {
        if (_trashService is null || !TryGetRoot(out var root))
        {
            return;
        }

        string trashDirectory;
        CacheTrashStatistics trashStatistics;
        try
        {
            trashDirectory = _trashService.GetTrashDirectory(root);
            trashStatistics = await Task.Run(() => _trashService.GetStatistics(root));
        }
        catch (Exception ex)
        {
            SetStatus($"读取应用回收站失败：{ex.Message}", isError: true);
            return;
        }

        var confirmed = _dialogService.Confirm(
            $"确定彻底清空当前应用回收站吗？{Environment.NewLine}" +
            $"路径：{trashDirectory}{Environment.NewLine}" +
            $"已验证条目：{trashStatistics.ManagedEntryCount} 条，" +
            $"可释放 {FormatBytes(trashStatistics.TotalBytes)}。{Environment.NewLine}" +
            (trashStatistics.UntrustedLegacyEntryCount > 0
                ? $"另有旧版未验证条目 {trashStatistics.UntrustedLegacyEntryCount} 条，稍后可单独确认。{Environment.NewLine}"
                : string.Empty) +
            "删除后无法撤销。只会删除本应用管理的回收站条目，不会删除原始 B 站缓存或转码缓存。",
            "彻底清空应用回收站");
        if (!confirmed)
        {
            return;
        }

        var includeUntrustedLegacyEntries = false;
        if (trashStatistics.UntrustedLegacyEntryCount > 0)
        {
            includeUntrustedLegacyEntries = _dialogService.Confirm(
                $"检测到 {trashStatistics.UntrustedLegacyEntryCount} 条旧版未验证条目，" +
                $"共 {trashStatistics.UntrustedLegacyFileCount} 个文件，" +
                $"占用 {FormatBytes(trashStatistics.UntrustedLegacyBytes)}。{Environment.NewLine}" +
                "它们的目录名符合旧版格式，但缺少身份元数据，可能来自旧版本写入中断。" +
                $"{Environment.NewLine}是否一并永久删除？选择“否”会保留这些条目。",
                "确认清理旧版未验证条目");
        }

        CancelActiveOperation();
        BeginOperation();
        IsBusy = true;
        var shouldRefreshStorageOverview = false;
        CacheTrashPurgeResult? result = null;
        try
        {
            SetStatus("正在彻底清理应用回收站，请稍候...", isError: false);
            result = await Task.Run(() =>
                _trashService.Purge(root, includeUntrustedLegacyEntries));
            var message =
                $"应用回收站清理完成：彻底删除 {result.DeletedEntryCount} 条，" +
                $"释放 {FormatBytes(result.FreedBytes)}，失败 {result.FailedEntryCount} 条，" +
                $"跳过 {result.SkippedEntryCount} 条。";
            if (result.PartiallyDeletedEntryCount > 0)
            {
                message +=
                    $" 其中 {result.PartiallyDeletedEntryCount} 条已部分删除，实际释放空间已计入。";
            }

            if (result.PendingPurgeEntryCount > 0)
            {
                message +=
                    $" {result.PendingPurgeEntryCount} 条已进入永久清理并等待重试，不能撤销或恢复。";
            }

            if (!string.IsNullOrWhiteSpace(result.FirstErrorMessage))
            {
                message += $" 首个失败原因：{result.FirstErrorMessage}";
            }

            SetStatus(
                message,
                isError: result.FailedEntryCount > 0 || result.SkippedEntryCount > 0);
            RecordStorageMaintenance(
                "应用回收站彻底清理",
                result.FreedBytes,
                result.FailedEntryCount);
            shouldRefreshStorageOverview = true;
        }
        catch (Exception ex)
        {
            SetStatus($"彻底清理应用回收站失败：{ex.Message}", isError: true);
        }
        finally
        {
            SynchronizeUndoStateAfterPurge(root, result?.NonRestorableTrashPaths);
            IsBusy = false;
        }

        if (shouldRefreshStorageOverview)
        {
            _ = RefreshStorageOverviewAsync();
        }
    }

    /// <summary>
    /// 清空回收站后同步撤销状态。只移除本次确实进入永久清理的条目，
    /// 未被清理（跳过或未开始）的条目仍然可以撤销。
    /// </summary>
    /// <param name="nonRestorableTrashPaths">
    /// 本次已永久删除或已进入清理无法回头的回收站路径；
    /// 为 null 表示清理过程异常中断、结果未知，此时保守地清空整个撤销列表。
    /// </param>
    private void SynchronizeUndoStateAfterPurge(
        string root,
        IReadOnlyList<string>? nonRestorableTrashPaths)
    {
        if (string.IsNullOrWhiteSpace(_lastTrashRoot) ||
            !PathsEqual(_lastTrashRoot, root))
        {
            return;
        }

        if (nonRestorableTrashPaths is null)
        {
            _lastTrashOperations.Clear();
        }
        else
        {
            var purged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in nonRestorableTrashPaths)
            {
                var normalized = TryNormalizePath(path);
                if (normalized is not null)
                {
                    purged.Add(normalized);
                }
            }

            _lastTrashOperations.RemoveAll(operation =>
            {
                var normalized = TryNormalizePath(operation.TrashPath);
                return normalized is not null && purged.Contains(normalized);
            });
        }

        if (_lastTrashOperations.Count == 0)
        {
            _lastTrashRoot = null;
        }

        UndoDeleteCommand.RaiseCanExecuteChanged();
    }

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = TryNormalizePath(left);
        var normalizedRight = TryNormalizePath(right);
        return normalizedLeft is not null &&
            string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 回收站条目路径若已损坏，按“无法匹配”处理，避免连累其余可撤销条目。
            return null;
        }
    }
}
