using System.Globalization;
using System.IO;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.ViewModels;

/// <summary>
/// 把缓存导出成可以直接播放、拷贝、分享的标准 MP4。
/// 复用播放管线的物化流程，命中转码缓存时可以秒出。
/// </summary>
public sealed partial class MainViewModel
{
    public AsyncRelayCommand ExportMp4Command { get; }

    private bool CanExportMp4()
    {
        return _materializationService is not null &&
            !IsBusy &&
            !IsPlaybackBusy &&
            (SelectedItems.Count > 0 || SelectedSegmentDetails.Count > 0);
    }

    private async Task ExportMp4Async()
    {
        if (_materializationService is null)
        {
            SetStatus("当前构建未启用导出功能。", isError: true);
            return;
        }

        if (!TryBuildBatchTargets(out var targets, out var failureMessage))
        {
            SetStatus(failureMessage, isError: true);
            return;
        }

        if (!TryResolveExportDestination(targets, out var destination))
        {
            return;
        }

        var (operationId, cancellation) = BeginCancelableOperation();
        IsPlaybackBusy = true;
        var exported = 0;
        var failures = new List<string>();
        var skipped = 0;

        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                var target = targets[index];
                var label = DescribeExportTarget(target, targets.Count > 1);
                SetStatus(
                    $"正在导出 {index + 1}/{targets.Count}：{label}",
                    isError: false);

                var outcome = await ExportSingleTargetAsync(
                    target,
                    destination,
                    // 后缀只取决于这个视频自身是否分 P；不同视频之间的重名交给去重逻辑处理。
                    useMultiPageNaming: HasMultiplePages(target.Cache),
                    progressLabel: $"{index + 1}/{targets.Count} {label}",
                    cancellationToken: cancellation.Token);

                switch (outcome.Kind)
                {
                    case ExportOutcomeKind.Exported:
                        exported++;
                        break;
                    case ExportOutcomeKind.Skipped:
                        skipped++;
                        break;
                    default:
                        failures.Add($"{label}：{outcome.Message}");
                        break;
                }
            }

            ReportExportSummary(exported, skipped, failures, destination, canceled: false);
        }
        catch (OperationCanceledException)
        {
            ReportExportSummary(exported, skipped, failures, destination, canceled: true);
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsPlaybackBusy = false;
            FinishCancelableOperation(operationId, cancellation);
        }
    }

    private async Task<ExportOutcome> ExportSingleTargetAsync(
        BatchPlaybackTarget target,
        ExportDestination destination,
        bool useMultiPageNaming,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        CachePlaybackPlan plan;
        try
        {
            plan = _playbackService
                .CreatePagePlan(
                    target.Cache,
                    target.PageIndex.ToString(CultureInfo.InvariantCulture))
                .SelectedPlan;
        }
        catch (Exception ex)
        {
            return ExportOutcome.Failed($"无法生成导出计划（{ex.Message}）");
        }

        if (!plan.IsPlayable)
        {
            return ExportOutcome.Failed(plan.Message ?? "该页面不可导出");
        }

        var progress = new Progress<PlaybackPreparationProgress>(report =>
        {
            var percentage = report.Percentage is { } value
                ? $" {value:F0}%"
                : string.Empty;
            SetStatus($"正在导出 {progressLabel}：{report.Stage}{percentage}", isError: false);
        });

        PlaybackMaterializationResult materialization;
        try
        {
            materialization = await _materializationService!.MaterializeAsync(
                plan,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExportOutcome.Failed(ex.Message);
        }

        if (!materialization.Succeeded || string.IsNullOrWhiteSpace(materialization.OutputPath))
        {
            return ExportOutcome.Failed(materialization.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var targetPath = ResolveTargetPath(
                destination,
                target,
                materialization.OutputPath,
                useMultiPageNaming);
            if (targetPath is null)
            {
                return ExportOutcome.SkippedTarget;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 产物可能是缓存中的原始文件或转码仓库中的共享产物，只能复制不能移动。
            await Task.Run(
                () => File.Copy(materialization.OutputPath, targetPath, overwrite: true),
                cancellationToken);

            return ExportOutcome.ExportedTo(targetPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExportOutcome.Failed($"写入目标文件失败（{ex.Message}）");
        }
    }

    private string? ResolveTargetPath(
        ExportDestination destination,
        BatchPlaybackTarget target,
        string materializedPath,
        bool useMultiPageNaming)
    {
        if (destination.SingleFilePath is not null)
        {
            return destination.SingleFilePath;
        }

        var extension = Path.GetExtension(materializedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mp4";
        }

        var baseName = ExportFileNaming.BuildBaseName(
            target.Cache.Title,
            target.Cache.Avid,
            target.PageIndex,
            target.PartName,
            useMultiPageNaming);

        return ExportFileNaming.EnsureUniquePath(destination.DirectoryPath!, baseName, extension);
    }

    private bool TryResolveExportDestination(
        IReadOnlyList<BatchPlaybackTarget> targets,
        out ExportDestination destination)
    {
        destination = default!;

        if (targets.Count == 1)
        {
            if (_fileSaveDialogService is null)
            {
                SetStatus("当前构建未提供保存对话框，无法导出。", isError: true);
                return false;
            }

            var target = targets[0];
            var suggestedName = ExportFileNaming.BuildBaseName(
                target.Cache.Title,
                target.Cache.Avid,
                target.PageIndex,
                target.PartName,
                HasMultiplePages(target.Cache));

            var picked = _fileSaveDialogService.PickSavePath(
                "导出为 MP4",
                suggestedName + ".mp4",
                "mp4",
                "MP4 视频|*.mp4|所有文件|*.*");
            if (string.IsNullOrWhiteSpace(picked))
            {
                SetStatus("已取消导出。", isError: false);
                return false;
            }

            destination = ExportDestination.ToFile(picked);
            return true;
        }

        var folder = _dialogService.PickFolder("选择导出目录", RootPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            SetStatus("已取消导出。", isError: false);
            return false;
        }

        destination = ExportDestination.ToDirectory(folder);
        return true;
    }

    private void ReportExportSummary(
        int exported,
        int skipped,
        IReadOnlyList<string> failures,
        ExportDestination destination,
        bool canceled)
    {
        var location = destination.SingleFilePath ?? destination.DirectoryPath ?? string.Empty;
        var prefix = canceled ? "已取消导出" : "导出完成";
        var message = $"{prefix}：成功 {exported} 个";

        if (skipped > 0)
        {
            message += $"，跳过 {skipped} 个";
        }

        if (failures.Count > 0)
        {
            message += $"，失败 {failures.Count} 个。首个失败：{failures[0]}";
        }
        else if (exported > 0)
        {
            message += $"。输出位置：{location}";
        }

        SetStatus(message, isError: failures.Count > 0);
    }

    private static bool HasMultiplePages(BiliVideoCache cache)
    {
        return cache.Segments
            .Select(segment => segment.PageIndex)
            .Distinct()
            .Count() > 1;
    }

    private static string DescribeExportTarget(BatchPlaybackTarget target, bool includePage)
    {
        var title = string.IsNullOrWhiteSpace(target.Cache.Title)
            ? "av" + target.Cache.Avid.ToString(CultureInfo.InvariantCulture)
            : target.Cache.Title;

        return includePage
            ? $"{title} P{target.PageIndex.ToString(CultureInfo.InvariantCulture)}"
            : title;
    }

    private enum ExportOutcomeKind
    {
        Exported,
        Skipped,
        Failed
    }

    private readonly record struct ExportOutcome(
        ExportOutcomeKind Kind,
        string Message)
    {
        public static ExportOutcome ExportedTo(string path) =>
            new(ExportOutcomeKind.Exported, path);

        public static ExportOutcome SkippedTarget => new(ExportOutcomeKind.Skipped, string.Empty);

        public static ExportOutcome Failed(string message) =>
            new(ExportOutcomeKind.Failed, string.IsNullOrWhiteSpace(message) ? "未知错误" : message);
    }

    private sealed record ExportDestination(string? SingleFilePath, string? DirectoryPath)
    {
        public static ExportDestination ToFile(string path) => new(path, null);

        public static ExportDestination ToDirectory(string path) => new(null, path);
    }
}
