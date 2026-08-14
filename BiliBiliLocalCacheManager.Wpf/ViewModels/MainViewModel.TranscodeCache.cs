using System.IO;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    internal const int MaximumProtectedTranscodeArtifactCount = 8;
    internal static readonly TimeSpan ProtectedTranscodeArtifactLifetime = TimeSpan.FromHours(24);

    private readonly IPlaybackArtifactStore? _playbackArtifactStore;
    private readonly SemaphoreSlim _transcodeCacheMaintenanceGate = new(1, 1);
    private readonly object _transcodeCacheStateSync = new();
    private readonly LinkedList<ProtectedTranscodeArtifact> _protectedTranscodeArtifacts = new();
    private readonly Dictionary<string, LinkedListNode<ProtectedTranscodeArtifact>>
        _protectedTranscodeArtifactsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private Task _automaticTranscodeCacheMaintenanceTask = Task.CompletedTask;
    private PlaybackArtifactCacheStatistics? _lastTranscodeCacheStatistics;
    private bool _automaticTranscodeCacheMaintenancePending;
    private bool _automaticTranscodeCacheMaintenanceActive;
    private volatile bool _canRunAutomaticTranscodeCacheMaintenance = true;
    private bool _isTranscodeCacheMaintenanceBusy;

    public RelayCommand OpenTranscodeCacheCommand { get; }

    public AsyncRelayCommand CleanupTranscodeCacheCommand { get; }

    public AsyncRelayCommand ClearTranscodeCacheCommand { get; }

    public bool IsTranscodeCacheMaintenanceBusy
    {
        get => _isTranscodeCacheMaintenanceBusy;
        private set
        {
            if (!SetField(ref _isTranscodeCacheMaintenanceBusy, value))
            {
                return;
            }

            OpenTranscodeCacheCommand.RaiseCanExecuteChanged();
            CleanupTranscodeCacheCommand.RaiseCanExecuteChanged();
            ClearTranscodeCacheCommand.RaiseCanExecuteChanged();
            RefreshStorageOverviewCommand.RaiseCanExecuteChanged();
        }
    }

    public string TranscodeCacheSummary
    {
        get;
        private set => SetField(ref field, value);
    } = "\u8f6c\u7801\u7f13\u5b58\u7b49\u5f85\u540e\u53f0\u68c0\u67e5";

    /// <summary>
    /// Requests the one-time startup cleanup after the main window has rendered.
    /// Multiple automatic requests are coalesced by the shared maintenance coordinator.
    /// </summary>
    public Task StartBackgroundTranscodeCacheMaintenance()
    {
        if (!_canRunAutomaticTranscodeCacheMaintenance ||
            _playbackArtifactStore is null)
        {
            return RefreshStorageOverviewAsync();
        }

        return RequestAutomaticTranscodeCacheMaintenance();
    }

    private bool CanManageTranscodeCache()
    {
        return _playbackArtifactStore is not null &&
            !IsBusy &&
            !IsPlaybackBusy &&
            !IsTranscodeCacheMaintenanceBusy;
    }

    private void OpenTranscodeCache()
    {
        if (_playbackArtifactStore is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_playbackArtifactStore.RootDirectory);
            _explorerService.OpenFolder(_playbackArtifactStore.RootDirectory);
            SetStatus(
                $"\u5df2\u6253\u5f00\u8f6c\u7801\u7f13\u5b58\u76ee\u5f55\uff1a{_playbackArtifactStore.RootDirectory}",
                isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"\u6253\u5f00\u8f6c\u7801\u7f13\u5b58\u5931\u8d25\uff1a{ex.Message}", isError: true);
        }
    }

    private async Task CleanupTranscodeCacheAsync()
    {
        if (_playbackArtifactStore is null)
        {
            return;
        }

        var cleanupOptions = CreateTranscodeCacheCleanupOptions();

        await RunTranscodeCacheOperationAsync(
            () => _playbackArtifactStore.Cleanup(cleanupOptions),
            "\u8f6c\u7801\u7f13\u5b58\u6e05\u7406\u5b8c\u6210",
            "\u8f6c\u7801\u7f13\u5b58\u6309\u7b56\u7565\u6e05\u7406");
    }

    private async Task ClearTranscodeCacheAsync()
    {
        if (_playbackArtifactStore is null)
        {
            return;
        }

        PlaybackArtifactCacheStatistics statistics;
        try
        {
            statistics = await Task.Run(_playbackArtifactStore.GetStatistics);
            _lastTranscodeCacheStatistics = statistics;
            RefreshTranscodeCacheSummary();
        }
        catch (Exception ex)
        {
            SetStatus($"\u8bfb\u53d6\u8f6c\u7801\u7f13\u5b58\u7edf\u8ba1\u5931\u8d25\uff1a{ex.Message}", isError: true);
            return;
        }

        if (statistics.FileCount == 0)
        {
            SetStatus("\u8f6c\u7801\u7f13\u5b58\u5df2\u7ecf\u662f\u7a7a\u7684\u3002", isError: false);
            await RefreshTranscodeStorageOverviewAsync();
            return;
        }

        var confirmed = _dialogService.Confirm(
            $"\u786e\u5b9a\u6e05\u7a7a {statistics.FileCount} \u4e2a\u8f6c\u7801\u7f13\u5b58\u6587\u4ef6\uff08{FormatBytes(statistics.TotalBytes)}\uff09\u5417\uff1f\n" +
            "\u8be5\u64cd\u4f5c\u53ea\u5220\u9664\u672c\u5e94\u7528\u751f\u6210\u7684\u64ad\u653e\u6587\u4ef6\uff0c\u4e0d\u4f1a\u5220\u9664 B \u7ad9\u539f\u59cb\u7f13\u5b58\u3002",
            "\u6e05\u7a7a\u8f6c\u7801\u7f13\u5b58");
        if (!confirmed)
        {
            return;
        }

        await RunTranscodeCacheOperationAsync(
            _playbackArtifactStore.Clear,
            "\u8f6c\u7801\u7f13\u5b58\u5df2\u6e05\u7a7a",
            "\u8f6c\u7801\u7f13\u5b58\u4e00\u952e\u6e05\u7a7a",
            requireEmptyResult: true);
    }

    private async Task RunTranscodeCacheOperationAsync(
        Func<PlaybackArtifactCleanupResult> operation,
        string completedMessage,
        string maintenanceOperation,
        bool requireEmptyResult = false)
    {
        IsBusy = true;
        await _transcodeCacheMaintenanceGate.WaitAsync();
        IsTranscodeCacheMaintenanceBusy = true;
        PlaybackArtifactCleanupResult? completedResult = null;
        try
        {
            var outcome = await Task.Run(() =>
            {
                var result = operation();
                var statistics = result.Statistics ?? _playbackArtifactStore?.GetStatistics();
                return (Result: result, Statistics: statistics);
            });
            var result = outcome.Result;
            completedResult = result;
            _lastTranscodeCacheStatistics = outcome.Statistics;
            RefreshTranscodeCacheSummary();
            var wasNotFullyCleared = requireEmptyResult && result.RemainingBytes > 0;
            RecordStorageMaintenance(
                maintenanceOperation,
                result.FreedBytes,
                result.FailedFileCount + (wasNotFullyCleared ? 1 : 0));
            var resultHeadline = wasNotFullyCleared
                ? "\u8f6c\u7801\u7f13\u5b58\u672a\u5b8c\u5168\u6e05\u7a7a"
                : completedMessage;
            var remainingMessage = wasNotFullyCleared
                ? $"\u5269\u4f59\u5bb9\u91cf {FormatBytes(result.RemainingBytes)}\uff0c"
                : string.Empty;
            SetStatus(
                $"{resultHeadline}\uff1a\u5220\u9664 {result.DeletedFileCount} \u4e2a\uff0c" +
                $"\u91ca\u653e {FormatBytes(result.FreedBytes)}\uff0c{remainingMessage}" +
                $"\u5931\u8d25 {result.FailedFileCount} \u4e2a\u3002",
                isError: result.FailedFileCount > 0 || wasNotFullyCleared);
        }
        catch (Exception ex)
        {
            SetStatus($"\u8f6c\u7801\u7f13\u5b58\u64cd\u4f5c\u5931\u8d25\uff1a{ex.Message}", isError: true);
        }
        finally
        {
            IsTranscodeCacheMaintenanceBusy = false;
            _transcodeCacheMaintenanceGate.Release();
            IsBusy = false;
        }

        await RefreshTranscodeStorageOverviewAsync(
            completedResult?.Statistics,
            completedResult?.Preview);
    }

    private Task RequestAutomaticTranscodeCacheMaintenance()
    {
        if (_playbackArtifactStore is null ||
            !_canRunAutomaticTranscodeCacheMaintenance)
        {
            return Task.CompletedTask;
        }

        lock (_transcodeCacheStateSync)
        {
            _automaticTranscodeCacheMaintenancePending = true;
            if (_automaticTranscodeCacheMaintenanceActive)
            {
                return _automaticTranscodeCacheMaintenanceTask;
            }

            _automaticTranscodeCacheMaintenanceActive = true;
            _automaticTranscodeCacheMaintenanceTask = RunAutomaticTranscodeCacheMaintenanceLoopAsync();
            return _automaticTranscodeCacheMaintenanceTask;
        }
    }

    private async Task RunAutomaticTranscodeCacheMaintenanceLoopAsync()
    {
        // Ensure callers such as OnContentRendered are never synchronously delayed by filesystem work.
        await Task.Yield();

        while (TryTakeAutomaticTranscodeCacheMaintenanceRequest())
        {
            await RunAutomaticTranscodeCacheMaintenanceOnceAsync();
        }
    }

    private bool TryTakeAutomaticTranscodeCacheMaintenanceRequest()
    {
        lock (_transcodeCacheStateSync)
        {
            if (!_automaticTranscodeCacheMaintenancePending)
            {
                _automaticTranscodeCacheMaintenanceActive = false;
                return false;
            }

            _automaticTranscodeCacheMaintenancePending = false;
            return true;
        }
    }

    private async Task RunAutomaticTranscodeCacheMaintenanceOnceAsync()
    {
        if (_playbackArtifactStore is null)
        {
            return;
        }

        await _transcodeCacheMaintenanceGate.WaitAsync();
        IsTranscodeCacheMaintenanceBusy = true;
        PlaybackArtifactCleanupResult? completedResult = null;
        try
        {
            var eligibility = await RevalidateAutomaticMaintenanceEligibilityAsync();
            if (eligibility?.IsEligible != false)
            {
                TranscodeCacheSummary = "\u6b63\u5728\u540e\u53f0\u68c0\u67e5\u8f6c\u7801\u7f13\u5b58\u2026";
                var cleanupOptions = CreateAutomaticTranscodeCacheCleanupOptions(
                    eligibility?.Settings);
                var outcome = await Task.Run(() =>
                {
                    var result = _playbackArtifactStore.Cleanup(cleanupOptions);
                    var statistics = result.Statistics ?? _playbackArtifactStore.GetStatistics();
                    return (Result: result, Statistics: statistics);
                });

                _lastTranscodeCacheStatistics = outcome.Statistics;
                completedResult = outcome.Result;
                RefreshTranscodeCacheSummary();
                RecordStorageMaintenance(
                    "\u8f6c\u7801\u7f13\u5b58\u81ea\u52a8\u7b56\u7565\u6e05\u7406",
                    outcome.Result.FreedBytes,
                    outcome.Result.FailedFileCount);
            }
        }
        catch (Exception ex)
        {
            TranscodeCacheSummary =
                $"\u8f6c\u7801\u7f13\u5b58\u540e\u53f0\u7ef4\u62a4\u5931\u8d25\uff1a{ex.Message}\uff1b" +
                $"\u4fdd\u7559 {TranscodeCacheRetentionDays} \u5929\uff0c" +
                $"\u4e0a\u9650 {TranscodeCacheMaxSizeGigabytes} GB";
            RecordDiagnosticEvent(
                "Storage",
                BiliBiliLocalCacheManager.Wpf.Models.DiagnosticEventLevel.Error,
                TranscodeCacheSummary,
                ex);
        }
        finally
        {
            IsTranscodeCacheMaintenanceBusy = false;
            _transcodeCacheMaintenanceGate.Release();
        }

        await RefreshTranscodeStorageOverviewAsync(
            completedResult?.Statistics,
            completedResult?.Preview);
    }

    private async Task<AutomaticMaintenanceEligibility?>
        RevalidateAutomaticMaintenanceEligibilityAsync()
    {
        if (_settingsService is null)
        {
            return null;
        }

        AutomaticMaintenanceEligibility eligibility;
        try
        {
            eligibility = await Task.Run(
                _settingsService.CheckAutomaticMaintenanceEligibility);
        }
        catch (Exception ex)
        {
            eligibility = new AutomaticMaintenanceEligibility(
                IsEligible: false,
                AppSettingsLoadKind.ReadError,
                SourceSchemaVersion: null,
                $"只读复核设置失败：{ex.Message}");
        }

        if (eligibility.IsEligible &&
            eligibility.LoadKind == AppSettingsLoadKind.MissingFile &&
            _settingsLoadKind is AppSettingsLoadKind.CurrentVersion or
                AppSettingsLoadKind.LegacyVersion)
        {
            eligibility = eligibility with
            {
                IsEligible = false,
                Reason = "设置文件在本次启动后被删除，已停止自动维护以避免套用默认清理策略。"
            };
        }

        if (eligibility.IsEligible)
        {
            return eligibility;
        }

        _canRunAutomaticTranscodeCacheMaintenance = false;
        _settingsCanSave = false;
        _settingsLoadKind = eligibility.LoadKind;
        _sourceSettingsSchemaVersion = eligibility.SourceSchemaVersion;
        _settingsSaveBlockedMessage = eligibility.Reason ??
            "设置文件不再适合由当前版本执行自动维护。";
        TranscodeCacheSummary =
            $"已跳过自动转码缓存维护：{_settingsSaveBlockedMessage}";
        RecordDiagnosticEvent(
            "Settings",
            BiliBiliLocalCacheManager.Wpf.Models.DiagnosticEventLevel.Error,
            TranscodeCacheSummary);
        return eligibility;
    }

    private PlaybackArtifactCleanupOptions CreateTranscodeCacheCleanupOptions()
    {
        return CreateTranscodeCacheCleanupOptionsCore(
            TranscodeCacheRetentionDays,
            TranscodeCacheMaxSizeGigabytes);
    }

    private PlaybackArtifactCleanupOptions CreateAutomaticTranscodeCacheCleanupOptions(
        AppSettings? latestSettings)
    {
        return CreateTranscodeCacheCleanupOptionsCore(
            latestSettings?.TranscodeCacheRetentionDays ?? TranscodeCacheRetentionDays,
            latestSettings?.TranscodeCacheMaxSizeGigabytes ??
                TranscodeCacheMaxSizeGigabytes);
    }

    private PlaybackArtifactCleanupOptions CreateTranscodeCacheCleanupOptionsCore(
        int retentionDays,
        int maxSizeGigabytes)
    {
        var options = PlaybackArtifactCleanupOptions.FromUserLimits(
            retentionDays,
            maxSizeGigabytes);
        lock (_transcodeCacheStateSync)
        {
            var now = DateTimeOffset.UtcNow;
            PruneProtectedTranscodeArtifacts(now);
            var candidates = new List<TranscodeArtifactProtectionCandidate>(
                _protectedTranscodeArtifacts.Count);
            var node = _protectedTranscodeArtifacts.First;
            while (node is not null)
            {
                var next = node.Next;
                var fileLength = TryGetExistingFileLength(node.Value.Path);
                if (fileLength is null)
                {
                    RemoveProtectedTranscodeArtifact(node);
                }
                else
                {
                    candidates.Add(new TranscodeArtifactProtectionCandidate(
                        node.Value.Path,
                        node.Value.ProtectedAtUtc,
                        fileLength.Value));
                }

                node = next;
            }

            options.ProtectedPaths = SelectProtectedTranscodePathsForCleanup(
                candidates,
                now,
                options.MaxTotalBytes);
        }

        return options;
    }

    private bool ProtectManagedTranscodeArtifact(PlaybackLaunchResult result)
    {
        if (_playbackArtifactStore is null ||
            !result.Succeeded ||
            string.IsNullOrWhiteSpace(result.ManagedArtifactPath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(result.ManagedArtifactPath);
            var relativePath = Path.GetRelativePath(_playbackArtifactStore.RootDirectory, fullPath);
            if (relativePath == ".." ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                return false;
            }

            lock (_transcodeCacheStateSync)
            {
                var now = DateTimeOffset.UtcNow;
                PruneProtectedTranscodeArtifacts(now);
                if (_protectedTranscodeArtifactsByPath.Remove(fullPath, out var existingNode))
                {
                    _protectedTranscodeArtifacts.Remove(existingNode);
                }

                var node = _protectedTranscodeArtifacts.AddFirst(
                    new ProtectedTranscodeArtifact(fullPath, now));
                _protectedTranscodeArtifactsByPath.Add(fullPath, node);
                while (_protectedTranscodeArtifacts.Count > MaximumProtectedTranscodeArtifactCount)
                {
                    RemoveProtectedTranscodeArtifact(_protectedTranscodeArtifacts.Last!);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> SelectProtectedTranscodePathsForCleanup(
        IEnumerable<TranscodeArtifactProtectionCandidate> candidatesMostRecentFirst,
        DateTimeOffset now,
        long maxProtectedBytes)
    {
        ArgumentNullException.ThrowIfNull(candidatesMostRecentFirst);
        var selected = new List<string>(MaximumProtectedTranscodeArtifactCount);
        long selectedBytes = 0;
        foreach (var candidate in candidatesMostRecentFirst)
        {
            if (selected.Count >= MaximumProtectedTranscodeArtifactCount ||
                candidate.FileLength < 0 ||
                now - candidate.ProtectedAtUtc > ProtectedTranscodeArtifactLifetime)
            {
                continue;
            }

            if (selected.Count == 0)
            {
                // The most recently played artifact is the only capacity exception.
                selected.Add(candidate.Path);
                selectedBytes = candidate.FileLength;
                continue;
            }

            if (selectedBytes > maxProtectedBytes ||
                candidate.FileLength > maxProtectedBytes - selectedBytes)
            {
                break;
            }

            selected.Add(candidate.Path);
            selectedBytes += candidate.FileLength;
        }

        return selected;
    }

    private void PruneProtectedTranscodeArtifacts(DateTimeOffset now)
    {
        var node = _protectedTranscodeArtifacts.Last;
        while (node is not null)
        {
            var previous = node.Previous;
            if (now - node.Value.ProtectedAtUtc > ProtectedTranscodeArtifactLifetime)
            {
                RemoveProtectedTranscodeArtifact(node);
            }

            node = previous;
        }
    }

    private void RemoveProtectedTranscodeArtifact(
        LinkedListNode<ProtectedTranscodeArtifact> node)
    {
        _protectedTranscodeArtifacts.Remove(node);
        _protectedTranscodeArtifactsByPath.Remove(node.Value.Path);
    }

    private static long? TryGetExistingFileLength(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return null;
        }
    }

    internal sealed record TranscodeArtifactProtectionCandidate(
        string Path,
        DateTimeOffset ProtectedAtUtc,
        long FileLength);

    private sealed record ProtectedTranscodeArtifact(
        string Path,
        DateTimeOffset ProtectedAtUtc);

    private void RefreshTranscodeCacheSummary()
    {
        if (_playbackArtifactStore is null)
        {
            TranscodeCacheSummary = "\u8f6c\u7801\u7f13\u5b58\u4e0d\u53ef\u7528";
            return;
        }

        if (_lastTranscodeCacheStatistics is null)
        {
            TranscodeCacheSummary =
                "\u8f6c\u7801\u7f13\u5b58\u7b49\u5f85\u540e\u53f0\u68c0\u67e5\uff1b" +
                $"\u4fdd\u7559 {TranscodeCacheRetentionDays} \u5929\uff0c" +
                $"\u4e0a\u9650 {TranscodeCacheMaxSizeGigabytes} GB";
            return;
        }

        TranscodeCacheSummary =
            $"\u8f6c\u7801\u7f13\u5b58 {_lastTranscodeCacheStatistics.FileCount} \u4e2a / " +
            $"{FormatBytes(_lastTranscodeCacheStatistics.TotalBytes)}\uff1b" +
            $"\u4fdd\u7559 {TranscodeCacheRetentionDays} \u5929\uff0c" +
            $"\u4e0a\u9650 {TranscodeCacheMaxSizeGigabytes} GB";
    }
}
