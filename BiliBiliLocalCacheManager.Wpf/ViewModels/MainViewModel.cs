using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using Media = System.Windows.Media;
using CoreContracts = BiliBiliLocalCacheManager.Core.Application.Contracts;
using PlaybackContracts = BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Wpf.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly CoreContracts.ICacheManager _cacheService;
    private readonly PlaybackContracts.ICachePlaybackService _playbackService;
    private readonly IDialogService _dialogService;
    private readonly IHelpService _helpService;
    private readonly IExplorerService _explorerService;
    private readonly CoreContracts.ICacheTrashService? _trashService;
    private readonly IAppSettingsService? _settingsService;
    private readonly Queue<BatchPlaybackTarget> _playbackQueue = new();
    private readonly IPlaybackProgressDialogService? _playbackProgressDialogService;
    private readonly PlaybackContracts.ICachePlaybackMaterializationService? _materializationService;
    private readonly List<CacheTrashOperationResult> _lastTrashOperations = new();
    private string? _lastTrashRoot;
    private CancellationTokenSource? _activeOperationCts;
    private bool _isPlaybackBusy;

    private CacheIndex? _currentIndex;
    private string? _currentRoot;
    private bool _loadingSettings;
    private bool _settingsCanSave = true;
    private string? _settingsSaveBlockedMessage;
    private bool? _currentIncludeIncomplete;
    private long _operationGeneration;

    public MainViewModel(
        CoreContracts.ICacheManager cacheService,
        PlaybackContracts.ICachePlaybackService playbackService,
        IDialogService dialogService,
        IHelpService helpService,
        IExplorerService explorerService,
        CoreContracts.ICacheTrashService? trashService = null,
        IAppSettingsService? settingsService = null,
        IPlaybackProgressDialogService? playbackProgressDialogService = null,
        PlaybackContracts.IPlaybackArtifactStore? playbackArtifactStore = null,
        IStorageOverviewService? storageOverviewService = null,
        IFileSaveDialogService? fileSaveDialogService = null,
        IDiagnosticReportService? diagnosticReportService = null,
        IDiagnosticEventRecorder? diagnosticEventRecorder = null,
        PlaybackContracts.IFfmpegDiagnosticsProvider? ffmpegDiagnosticsProvider = null,
        PlaybackContracts.ICachePlaybackMaterializationService? materializationService = null)
    {
        _cacheService = cacheService;
        _playbackService = playbackService;
        _dialogService = dialogService;
        _helpService = helpService;
        _explorerService = explorerService;
        _trashService = trashService;
        _settingsService = settingsService;

        _playbackProgressDialogService = playbackProgressDialogService;
        _playbackArtifactStore = playbackArtifactStore;
        _storageOverviewService = storageOverviewService;
        _materializationService = materializationService;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        BrowseRootCommand = new RelayCommand(BrowseRoot);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy);
        CancelOperationCommand = new RelayCommand(CancelActiveOperation, () => _activeOperationCts is not null);
        PlaySelectedPageCommand = new AsyncRelayCommand(PlaySelectedPageAsync, CanPlaySelectedPage);
        PlayBatchCommand = new AsyncRelayCommand(PlayBatchAsync, CanPlayBatch);
        PlayNextCommand = new AsyncRelayCommand(PlayNextQueuedAsync, CanPlayNext);
        ClearQueueCommand = new RelayCommand(ClearPlaybackQueue, CanClearQueue);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, CanDelete);
        UndoDeleteCommand = new AsyncRelayCommand(UndoDeleteAsync, CanUndoDelete);
        OpenTrashCommand = new RelayCommand(OpenTrash, () => _trashService is not null);
        PurgeTrashCommand = new AsyncRelayCommand(PurgeTrashAsync, CanPurgeTrash);
        ClearCommand = new RelayCommand(Clear);
        HelpCommand = new RelayCommand(OpenHelp);
        OpenFolderCommand = new RelayCommand<CacheItem?>(OpenFolder);
        OpenSegmentFolderCommand = new RelayCommand<SegmentDetailItem?>(OpenSegmentFolder);
        OpenTranscodeCacheCommand = new RelayCommand(OpenTranscodeCache, CanManageTranscodeCache);
        CleanupTranscodeCacheCommand = new AsyncRelayCommand(CleanupTranscodeCacheAsync, CanManageTranscodeCache);
        ClearTranscodeCacheCommand = new AsyncRelayCommand(ClearTranscodeCacheAsync, CanManageTranscodeCache);
        RefreshStorageOverviewCommand = new AsyncRelayCommand(
            RefreshStorageOverviewAsync,
            CanRefreshStorageOverview);
        ExportMp4Command = new AsyncRelayCommand(ExportMp4Async, CanExportMp4);
        InitializeDiagnostics(
            fileSaveDialogService,
            diagnosticReportService,
            diagnosticEventRecorder,
            ffmpegDiagnosticsProvider);

        LoadSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CacheItem> Items { get; } = new();

    public ObservableCollection<CacheItem> SelectedItems { get; } = new();

    public ObservableCollection<SegmentDetailItem> SegmentDetails { get; } = new();

    public ObservableCollection<SegmentDetailItem> SelectedSegmentDetails { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand BrowseRootCommand { get; }

    public AsyncRelayCommand SearchCommand { get; }

    public RelayCommand CancelOperationCommand { get; }

    public AsyncRelayCommand PlaySelectedPageCommand { get; }

    public AsyncRelayCommand PlayBatchCommand { get; }

    public AsyncRelayCommand PlayNextCommand { get; }

    public RelayCommand ClearQueueCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public AsyncRelayCommand UndoDeleteCommand { get; }

    public RelayCommand OpenTrashCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand HelpCommand { get; }

    public RelayCommand<CacheItem?> OpenFolderCommand { get; }

    public RelayCommand<SegmentDetailItem?> OpenSegmentFolderCommand { get; }

    public string RootPath
    {
        get;
        set
        {
            if (!SetField(ref field, value))
            {
                return;
            }

            _currentIndex = null;
            _currentRoot = null;
            _currentIncludeIncomplete = null;
            InvalidateOperations();
            ResetPlaybackQueue();
            ClearItems();
            InvalidateStorageOverview();
        }
    } = string.Empty;

    public string Keyword
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ScheduleIncrementalSearch();
            }
        }
    } = string.Empty;

    public string StatusMessage
    {
        get;
        private set => SetField(ref field, value);
    } = string.Empty;

    public Media.Brush StatusBrush
    {
        get;
        private set => SetField(ref field, value);
    } = new Media.SolidColorBrush(AppThemePalette.FallbackStatusNormal);

    /// <summary>当前状态是否为错误，供界面选择图标与配色。</summary>
    public bool IsStatusError
    {
        get;
        private set => SetField(ref field, value);
    }

    public bool IsBusy
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
                CancelOperationCommand.RaiseCanExecuteChanged();
                RaiseSelectionCommandStates();
                RefreshStorageOverviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool IsPlaybackBusy
    {
        get => _isPlaybackBusy;
        set
        {
            if (_isPlaybackBusy == value)
            {
                return;
            }

            _isPlaybackBusy = value;
            RaiseSelectionCommandStates();
        }
    }

    public int QueueLength
    {
        get;
        private set => SetField(ref field, value);
    }

    public string StorageSummary
    {
        get;
        private set => SetField(ref field, value);
    } = "列表为空";

    public bool IncludeIncomplete
    {
        get;
        set
        {
            if (!SetField(ref field, value))
            {
                return;
            }

            _currentIndex = null;
            _currentIncludeIncomplete = null;
            InvalidateOperations();
            ResetPlaybackQueue();
            ClearItems();
            SaveSettings();
        }
    }

    public bool SplitKeywords
    {
        get;
        set => SetSettingField(ref field, value);
    } = true;

    public bool AnyKeywords
    {
        get;
        set => SetSettingField(ref field, value);
    }

    public bool IncludePartName
    {
        get;
        set => SetSettingField(ref field, value);
    } = true;

    public bool IncludeOwnerName
    {
        get;
        set => SetSettingField(ref field, value);
    }

    public bool IncludeBvid
    {
        get;
        set => SetSettingField(ref field, value);
    }

    public bool IncludeAvid
    {
        get;
        set => SetSettingField(ref field, value);
    }

    public bool CaseSensitive
    {
        get;
        set => SetSettingField(ref field, value);
    }

    public CacheSearchMatchMode MatchMode
    {
        get;
        set => SetSettingField(ref field, value);
    } = CacheSearchMatchMode.Contains;

    public PlaybackPlayerPreference PreferredPlayer
    {
        get;
        set => SetSettingField(ref field, value);
    } = PlaybackPlayerPreference.SystemDefaultFirst;

    public int TranscodeCacheRetentionDays
    {
        get;
        set
        {
            if (!PlaybackArtifactCleanupOptions.IsValidRetentionDays(value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"\u4fdd\u7559\u5929\u6570\u5fc5\u987b\u5728 {PlaybackArtifactCleanupOptions.MinimumRetentionDays}\u2013" +
                    $"{PlaybackArtifactCleanupOptions.MaximumRetentionDays} \u5929\u4e4b\u95f4\u3002");
            }

            if (SetSettingField(ref field, value))
            {
                RefreshTranscodeCacheSummary();
                MarkStorageOverviewPolicyStale();
            }
        }
    } = PlaybackArtifactCleanupOptions.DefaultRetentionDays;

    public int TranscodeCacheMaxSizeGigabytes
    {
        get;
        set
        {
            if (!PlaybackArtifactCleanupOptions.IsValidMaxSizeGigabytes(value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"\u5bb9\u91cf\u4e0a\u9650\u5fc5\u987b\u5728 {PlaybackArtifactCleanupOptions.MinimumMaxSizeGigabytes}\u2013" +
                    $"{PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes} GB \u4e4b\u95f4\u3002");
            }

            if (SetSettingField(ref field, value))
            {
                RefreshTranscodeCacheSummary();
                MarkStorageOverviewPolicyStale();
            }
        }
    } = PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes;

    public CacheItem? SelectedItem
    {
        get;
        set
        {
            if (!SetField(ref field, value))
            {
                return;
            }

            UpdateSegmentDetails();
            RaiseSelectionCommandStates();
        }
    }

    public SegmentDetailItem? SelectedSegmentDetail
    {
        get;
        set
        {
            if (!SetField(ref field, value))
            {
                return;
            }

            RaiseSelectionCommandStates();
        }
    }

    public void SetSelectedCaches(IEnumerable<CacheItem> items)
    {
        ReplaceSelection(SelectedItems, items);

        if (SelectedItem is not null && !SelectedItems.Contains(SelectedItem))
        {
            SelectedItem = SelectedItems.FirstOrDefault();
        }

        UpdateStorageSummary();
        RaiseSelectionCommandStates();
    }

    public void SetSelectedSegments(IEnumerable<SegmentDetailItem> items)
    {
        ReplaceSelection(SelectedSegmentDetails, items);

        if (SelectedSegmentDetail is not null && !SelectedSegmentDetails.Contains(SelectedSegmentDetail))
        {
            SelectedSegmentDetail = SelectedSegmentDetails.FirstOrDefault();
        }

        RaiseSelectionCommandStates();
    }

    private (long operationId, CancellationTokenSource cancellation) BeginCancelableOperation()
    {
        CancelActiveOperation();
        var cancellation = new CancellationTokenSource();
        _activeOperationCts = cancellation;
        IsBusy = true;
        return (Interlocked.Increment(ref _operationGeneration), cancellation);
    }

    private long BeginOperation() => Interlocked.Increment(ref _operationGeneration);

    private void FinishCancelableOperation(long operationId, CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_activeOperationCts, cancellation))
        {
            _activeOperationCts = null;
            IsBusy = false;
        }

        cancellation.Dispose();
    }

    private void CancelActiveOperation()
    {
        try
        {
            _activeOperationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _activeOperationCts = null;
        }
    }

    private void InvalidateOperations()
    {
        var hadCancelableOperation = _activeOperationCts is not null;
        CancelActiveOperation();
        Interlocked.Increment(ref _operationGeneration);
        if (hadCancelableOperation)
        {
            IsBusy = false;
        }
    }

    private bool IsOperationCurrent(long operationId) => operationId == Volatile.Read(ref _operationGeneration);

    private bool IsCurrentContext(string root, bool includeIncomplete)
    {
        var currentRoot = RootPath.Trim();
        return string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase) &&
               includeIncomplete == IncludeIncomplete;
    }

    private async Task ScanAsync()
    {
        if (!TryGetRoot(out var root))
        {
            return;
        }

        var includeIncomplete = IncludeIncomplete;
        var (operationId, cancellation) = BeginCancelableOperation();
        var shouldRefreshStorageOverview = false;
        var progress = new Progress<CacheScanProgress>(item =>
        {
            if (IsOperationCurrent(operationId))
            {
                SetStatus(
                    $"正在扫描：已处理 {item.ProcessedSegmentDirectories} 个分段，收录 {item.IncludedEntries} 个。{Path.GetFileName(item.CurrentPath)}",
                    isError: false);
            }
        });

        try
        {
            SetStatus("正在扫描，请稍候...", isError: false);
            var report = await Task.Run(() => _cacheService.BuildIndexWithReport(
                root,
                new CacheIndexBuildOptions
                {
                    IncludeIncompleteEntries = includeIncomplete
                },
                cancellation.Token,
                progress), cancellation.Token);

            if (!IsOperationCurrent(operationId) || !IsCurrentContext(root, includeIncomplete))
            {
                return;
            }

            var index = report.Index;
            _currentRoot = root;
            _currentIndex = index;
            _currentIncludeIncomplete = includeIncomplete;

            UpdateItems(index.VideoCaches);
            SaveSettings();
            SetStatus(BuildScanStatus(report), isError: false);
            shouldRefreshStorageOverview = true;
        }
        catch (OperationCanceledException)
        {
            if (IsOperationCurrent(operationId))
            {
                SetStatus("扫描已取消。", isError: false);
            }
        }
        catch (Exception ex)
        {
            if (IsOperationCurrent(operationId))
            {
                SetStatus($"扫描失败：{ex.Message}", isError: true);
            }
        }
        finally
        {
            FinishCancelableOperation(operationId, cancellation);
        }

        if (shouldRefreshStorageOverview)
        {
            _ = RefreshStorageOverviewAsync();
        }
    }

    private void BrowseRoot()
    {
        var selected = _dialogService.PickFolder("选择缓存根目录", RootPath);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        RootPath = selected;
        SaveSettings();
        SetStatus($"已选择目录：{selected}，正在扫描...", isError: false);

        // 选完目录几乎总是要扫描，省掉多余的一次点击。
        ScanCommand.Execute(null);
    }

    private async Task SearchAsync()
    {
        if (!TryGetRoot(out var root))
        {
            return;
        }

        var keyword = Keyword.Trim();
        var showAll = string.IsNullOrWhiteSpace(keyword);
        var includeIncomplete = IncludeIncomplete;
        var (operationId, cancellation) = BeginCancelableOperation();

        try
        {
            SetStatus(showAll ? "正在加载全部缓存..." : "正在搜索，请稍候...", isError: false);

            var index = await EnsureIndexAsync(
                root,
                includeIncomplete,
                operationId,
                cancellation.Token);
            if (index is null)
            {
                if (IsOperationCurrent(operationId))
                {
                    SetStatus("索引未初始化，请先执行扫描。", isError: true);
                }

                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();

            // 关键字为空表示“显示全部”，与删除后刷新时的语义保持一致。
            IReadOnlyCollection<BiliVideoCache> results;
            if (showAll)
            {
                results = index.VideoCaches;
            }
            else
            {
                var options = BuildSearchOptions(keyword);
                results = await Task.Run(() => index.Search(options), cancellation.Token);
            }

            if (!IsOperationCurrent(operationId) || !IsCurrentContext(root, includeIncomplete))
            {
                return;
            }

            UpdateItems(results);
            SetStatus(
                showAll
                    ? $"已显示全部 {results.Count} 条缓存。"
                    : $"搜索完成，命中 {results.Count} 条缓存。",
                isError: false);
        }
        catch (OperationCanceledException)
        {
            if (IsOperationCurrent(operationId))
            {
                SetStatus("搜索已取消。", isError: false);
            }
        }
        catch (Exception ex)
        {
            if (IsOperationCurrent(operationId))
            {
                SetStatus($"搜索失败：{ex.Message}", isError: true);
            }
        }
        finally
        {
            FinishCancelableOperation(operationId, cancellation);
        }
    }

    private async Task<CacheIndex?> EnsureIndexAsync(
        string root,
        bool includeIncomplete,
        long operationId,
        CancellationToken cancellationToken)
    {
        if (!IsOperationCurrent(operationId) || !IsCurrentContext(root, includeIncomplete))
        {
            return null;
        }

        if (_currentIndex is null ||
            !string.Equals(_currentRoot, root, StringComparison.OrdinalIgnoreCase) ||
            _currentIncludeIncomplete != includeIncomplete)
        {
            var progress = new Progress<CacheScanProgress>(item =>
            {
                if (IsOperationCurrent(operationId))
                {
                    SetStatus(
                        $"正在建立搜索索引：已处理 {item.ProcessedSegmentDirectories} 个分段。",
                        isError: false);
                }
            });
            var report = await Task.Run(() => _cacheService.BuildIndexWithReport(
                root,
                new CacheIndexBuildOptions
                {
                    IncludeIncompleteEntries = includeIncomplete
                },
                cancellationToken,
                progress), cancellationToken);

            if (!IsOperationCurrent(operationId) || !IsCurrentContext(root, includeIncomplete))
            {
                return null;
            }

            _currentRoot = root;
            _currentIndex = report.Index;
            _currentIncludeIncomplete = includeIncomplete;
        }

        return _currentIndex;
    }

    private void OpenHelp()
    {
        try
        {
            _helpService.OpenHelp();
            SetStatus("已打开帮助文档。", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"打开帮助失败：{ex.Message}", isError: true);
        }
    }

    private void OpenFolder(CacheItem? item)
    {
        if (item is null)
        {
            SetStatus("请先在列表中选择要打开的缓存。", isError: true);
            return;
        }

        SelectedItem = item;

        if (!TryGetRoot(out var root))
        {
            return;
        }

        var folderPath = Path.Combine(root, item.Avid.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(folderPath))
        {
            SetStatus($"未找到缓存目录：{folderPath}", isError: true);
            return;
        }

        try
        {
            _explorerService.OpenFolder(folderPath);
            SetStatus($"已打开缓存目录：{folderPath}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"打开文件夹失败：{ex.Message}", isError: true);
        }
    }

    private void OpenSegmentFolder(SegmentDetailItem? item)
    {
        if (item is null)
        {
            SetStatus("请先在分段详情中选择要打开的分段。", isError: true);
            return;
        }

        SelectedSegmentDetail = item;

        if (string.IsNullOrWhiteSpace(item.DirectoryPath) || !Directory.Exists(item.DirectoryPath))
        {
            SetStatus($"未找到分段目录：{item.DirectoryPath}", isError: true);
            return;
        }

        try
        {
            _explorerService.OpenFolder(item.DirectoryPath);
            SetStatus($"已打开分段目录：{item.DirectoryPath}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"打开分段文件夹失败：{ex.Message}", isError: true);
        }
    }

    private Task<PlaybackLaunchResult> PlayCacheAsync(
        BiliVideoCache cache,
        string segmentKey,
        string progressTitle)
    {
        var launchOptions = CreateDefaultLaunchOptions();
        var plan = _playbackService.CreatePagePlan(cache, segmentKey).SelectedPlan;
        if (!plan.RequiresFfmpegPreparation || _playbackProgressDialogService is null)
        {
            return _playbackService.PlayAsync(
                cache,
                segmentKey,
                launchOptions);
        }

        return _playbackProgressDialogService.RunAsync(
            progressTitle,
            (progress, cancellationToken) => _playbackService.PlayAsync(
                cache,
                segmentKey,
                launchOptions,
                progress,
                cancellationToken));
    }

    private async Task PlaySelectedPageAsync()
    {
        if (SelectedSegmentDetail is null)
        {
            SetStatus("请先在分段详情中选择要播放的页面。", isError: true);
            return;
        }

        if (!TryResolveSelectedCache(out var cache))
        {
            SetStatus("当前缓存未加载，请先扫描并选择一条缓存。", isError: true);
            return;
        }

        var selectedPage = SelectedSegmentDetail.PageIndex;
        var selectedPart = SelectedSegmentDetail.PartName;

        if (IsPlaybackBusy)
        {
            SetStatus("已有播放项目正在准备，请稍候。", isError: true);
            return;
        }

        IsPlaybackBusy = true;
        var requestAutomaticCacheMaintenance = false;
        try
        {
            RememberPlaybackTarget(cache, selectedPart);
            SetStatus($"正在准备播放第 {selectedPage} 页：{selectedPart}", isError: false);

            var result = await PlayCacheAsync(
                cache,
                selectedPage.ToString(CultureInfo.InvariantCulture),
                $"\u6b63\u5728\u51c6\u5907\u7b2c {selectedPage} \u9875\uff1a{selectedPart}");
            if (result.Succeeded)
            {
                requestAutomaticCacheMaintenance = ProtectManagedTranscodeArtifact(result);
                SetStatus($"已开始播放第 {selectedPage} 页：{selectedPart}", isError: false);
                return;
            }

            SetStatus($"播放失败：{result.Message}", isError: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("\u5df2\u53d6\u6d88\u8f6c\u7801\uff0c\u672a\u542f\u52a8\u64ad\u653e\u5668\u3002", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"播放失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsPlaybackBusy = false;
            if (requestAutomaticCacheMaintenance)
            {
                _ = RequestAutomaticTranscodeCacheMaintenance();
            }
        }
    }

    private async Task PlayBatchAsync()
    {
        try
        {
            if (!TryBuildBatchTargets(out var targets, out var failureMessage))
            {
                SetStatus(failureMessage, isError: true);
                return;
            }

            _playbackQueue.Clear();
            foreach (var target in targets)
            {
                _playbackQueue.Enqueue(target);
            }

            UpdateQueueState();
            SetStatus($"已创建播放队列，共 {targets.Count} 个页面。", isError: false);
            await PlayNextQueuedAsync();
        }
        catch (Exception ex)
        {
            ResetPlaybackQueue();
            SetStatus($"创建播放队列失败：{ex.Message}", isError: true);
        }
    }

    private async Task PlayNextQueuedAsync()
    {
        if (IsPlaybackBusy)
        {
            SetStatus("已有播放项目正在准备，请稍候。", isError: true);
            return;
        }

        if (_playbackQueue.Count == 0)
        {
            SetStatus("播放队列为空。", isError: true);
            return;
        }

        var target = _playbackQueue.Dequeue();
        UpdateQueueState();
        IsPlaybackBusy = true;
        var requestAutomaticCacheMaintenance = false;

        try
        {
            RememberPlaybackTarget(target.Cache, target.PartName);
            SetStatus(
                $"正在准备：Avid {target.Cache.Avid} 第 {target.PageIndex} 页 {target.PartName}",
                isError: false);
            var result = await PlayCacheAsync(
                target.Cache,
                target.PageIndex.ToString(CultureInfo.InvariantCulture),
                $"\u6b63\u5728\u51c6\u5907 Avid {target.Cache.Avid} \u7b2c {target.PageIndex} \u9875");
            if (result.Succeeded)
            {
                requestAutomaticCacheMaintenance = ProtectManagedTranscodeArtifact(result);
                var suffix = QueueLength > 0
                    ? $"队列剩余 {QueueLength} 项，可点击“播放下一项”。"
                    : "播放队列已完成。";
                SetStatus(
                    $"已启动 Avid {target.Cache.Avid} 第 {target.PageIndex} 页。{suffix}",
                    isError: false);
                return;
            }

            SetStatus(
                $"播放失败：Avid {target.Cache.Avid} 第 {target.PageIndex} 页，{result.Message}。队列剩余 {QueueLength} 项。",
                isError: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus($"\u5df2\u53d6\u6d88\u8f6c\u7801\uff0c\u672a\u542f\u52a8\u64ad\u653e\u5668\u3002\u961f\u5217\u5269\u4f59 {QueueLength} \u9879\u3002", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"播放失败：{ex.Message}。队列剩余 {QueueLength} 项。", isError: true);
        }
        finally
        {
            IsPlaybackBusy = false;
            UpdateQueueState();
            if (requestAutomaticCacheMaintenance)
            {
                _ = RequestAutomaticTranscodeCacheMaintenance();
            }
        }
    }

    private void ClearPlaybackQueue()
    {
        ResetPlaybackQueue();
        SetStatus("已清空播放队列。", isError: false);
    }

    private void ResetPlaybackQueue()
    {
        _playbackQueue.Clear();
        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        QueueLength = _playbackQueue.Count;
        PlayNextCommand.RaiseCanExecuteChanged();
        ClearQueueCommand.RaiseCanExecuteChanged();
    }

    private async Task DeleteAsync()
    {
        if (!TryGetRoot(out var root))
        {
            return;
        }

        var targets = SelectedItems.Count > 0
            ? SelectedItems.DistinctBy(item => item.Avid).OrderBy(item => item.Avid).ToList()
            : SelectedItem is null
                ? new List<CacheItem>()
                : new List<CacheItem> { SelectedItem };

        if (targets.Count == 0)
        {
            SetStatus("请先在列表中选择要删除的缓存。", isError: true);
            return;
        }

        var selectedSize = targets.Sum(item => item.SizeMbValue);
        var actionText = _trashService is null ? "永久删除" : "移至应用回收站";
        var confirm = _dialogService.Confirm(
            $"确认将 {targets.Count} 条缓存（约 {selectedSize:F2} MB）{actionText}？" +
            (_trashService is null
                ? $"{Environment.NewLine}注意：删除后无法恢复。"
                : $"{Environment.NewLine}本次操作可以通过“撤销删除”恢复。"),
            "删除确认");

        if (!confirm)
        {
            return;
        }

        CancelActiveOperation();
        var includeIncomplete = IncludeIncomplete;
        var operationId = BeginOperation();
        IsBusy = true;

        try
        {
            SetStatus($"正在处理 {targets.Count} 条缓存，请稍候...", isError: false);

            var results = await Task.Run(() => targets
                .Select(item => DeleteTarget(root, item))
                .ToList());

            var successfulTrashOperations = results
                .Where(item => item.TrashOperation is not null)
                .Select(item => item.TrashOperation!)
                .ToList();
            if (successfulTrashOperations.Count > 0)
            {
                _lastTrashOperations.Clear();
                _lastTrashOperations.AddRange(successfulTrashOperations);
                _lastTrashRoot = Path.GetFullPath(root);
            }

            UndoDeleteCommand.RaiseCanExecuteChanged();

            var succeeded = results.Count(item => item.Succeeded);
            var failed = results.Where(item => !item.Succeeded).ToList();

            if (succeeded > 0 &&
                IsOperationCurrent(operationId) &&
                IsCurrentContext(root, includeIncomplete))
            {
                await RefreshAfterDeletionAsync(root, includeIncomplete, operationId);
            }

            if (failed.Count == 0)
            {
                SetStatus(
                    _trashService is null
                        ? $"已永久删除 {succeeded} 条缓存。"
                        : $"已将 {succeeded} 条缓存移至应用回收站，可撤销。原目录：{root}",
                    isError: false);
                return;
            }

            SetStatus(
                $"处理完成：成功 {succeeded} 条，失败 {failed.Count} 条。首个失败 Avid {failed[0].Avid}：{failed[0].ErrorMessage ?? "未找到目录"}",
                isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private DeleteOperationResult DeleteTarget(string root, CacheItem item)
    {
        try
        {
            if (_trashService is not null)
            {
                var result = _trashService.MoveToTrash(root, item.Avid);
                return new DeleteOperationResult(
                    item.Avid,
                    result.Found,
                    result.Succeeded,
                    result.ErrorMessage,
                    result.Succeeded ? result : null);
            }

            var deleteResult = _cacheService.DeleteByAvid(root, item.Avid);
            return new DeleteOperationResult(
                item.Avid,
                deleteResult.Found,
                deleteResult.Deleted,
                deleteResult.ErrorMessage,
                null);
        }
        catch (Exception ex)
        {
            return new DeleteOperationResult(item.Avid, false, false, ex.Message, null);
        }
    }

    private async Task UndoDeleteAsync()
    {
        if (_trashService is null || _lastTrashOperations.Count == 0 || string.IsNullOrWhiteSpace(_lastTrashRoot))
        {
            SetStatus("当前没有可以撤销的删除操作。", isError: true);
            return;
        }

        var root = _lastTrashRoot;
        if (!Directory.Exists(root))
        {
            SetStatus($"原缓存根目录不存在，无法撤销：{root}", isError: true);
            return;
        }

        var includeIncomplete = IncludeIncomplete;
        var operationId = BeginOperation();
        var pending = _lastTrashOperations.ToList();
        IsBusy = true;

        try
        {
            var results = await Task.Run(() => pending
                .AsEnumerable()
                .Reverse()
                .Select(item => RestoreTrashOperation(root, item))
                .ToList());

            var restored = results.Count(item => item.Succeeded);
            var failed = results.Where(item => !item.Succeeded).ToList();
            var warnings = results
                .Where(item => item.Succeeded && !string.IsNullOrWhiteSpace(item.ErrorMessage))
                .ToList();
            _lastTrashOperations.Clear();
            _lastTrashOperations.AddRange(pending.Where(item =>
                failed.Any(failure => string.Equals(
                    failure.TrashPath,
                    item.TrashPath,
                    StringComparison.OrdinalIgnoreCase))));
            if (_lastTrashOperations.Count == 0)
            {
                _lastTrashRoot = null;
            }

            UndoDeleteCommand.RaiseCanExecuteChanged();

            if (restored > 0 &&
                IsOperationCurrent(operationId) &&
                IsCurrentContext(root, includeIncomplete))
            {
                await RefreshAfterDeletionAsync(root, includeIncomplete, operationId);
            }

            SetStatus(
                failed.Count > 0
                    ? $"已恢复 {restored} 条，失败 {failed.Count} 条：{failed[0].ErrorMessage}"
                    : warnings.Count > 0
                        ? $"已恢复 {restored} 条，但有 {warnings.Count} 条需要注意：" +
                          warnings[0].ErrorMessage
                        : $"已恢复 {restored} 条缓存到：{root}",
                isError: failed.Count > 0 || warnings.Count > 0);
        }
        catch (Exception ex)
        {
            SetStatus($"撤销删除失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CacheTrashOperationResult RestoreTrashOperation(
        string root,
        CacheTrashOperationResult operation)
    {
        try
        {
            return _trashService!.Restore(root, operation.Avid, operation.TrashPath!);
        }
        catch (Exception ex)
        {
            return new CacheTrashOperationResult(
                operation.Avid,
                Directory.Exists(operation.TrashPath),
                false,
                operation.OriginalPath,
                operation.TrashPath,
                ex.Message);
        }
    }

    private void OpenTrash()
    {
        if (_trashService is null || !TryGetRoot(out var root))
        {
            return;
        }

        try
        {
            var path = _trashService.GetTrashDirectory(root);
            Directory.CreateDirectory(path);
            _explorerService.OpenFolder(path);
            SetStatus($"已打开应用回收站：{path}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"打开应用回收站失败：{ex.Message}", isError: true);
        }
    }

    private void Clear()
    {
        ClearItems();
        ResetPlaybackQueue();
        SetStatus("已清空列表和播放队列。", isError: false);
    }

    private bool CanDelete() => !IsBusy && !IsPlaybackBusy && (SelectedItem is not null || SelectedItems.Count > 0);

    private bool CanUndoDelete() => !IsBusy && _trashService is not null && _lastTrashOperations.Count > 0;

    private bool CanPlayNext() => !IsBusy && !IsPlaybackBusy && _playbackQueue.Count > 0;

    private bool CanClearQueue() => _playbackQueue.Count > 0;

    private bool CanPlaySelectedPage() => !IsBusy && !IsPlaybackBusy && SelectedItem is not null && SelectedSegmentDetail is not null;

    private bool CanPlayBatch() => !IsBusy && !IsPlaybackBusy && (SelectedSegmentDetails.Count > 0 || SelectedItems.Count > 0);

    private async Task RefreshAfterDeletionAsync(string root, bool includeIncomplete, long operationId)
    {
        var index = await Task.Run(() => _cacheService.BuildIndex(root, includeIncomplete));
        if (!IsOperationCurrent(operationId) || !IsCurrentContext(root, includeIncomplete))
        {
            return;
        }

        _currentRoot = root;
        _currentIndex = index;
        _currentIncludeIncomplete = includeIncomplete;

        var keyword = Keyword.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var options = BuildSearchOptions(keyword);
            UpdateItems(index.Search(options));
        }
        else
        {
            UpdateItems(index.VideoCaches);
        }

        SetStatus("缓存列表已刷新。", isError: false);
        _ = RefreshStorageOverviewAsync();
    }

    private bool TryGetRoot(out string root)
    {
        root = RootPath.Trim();

        if (string.IsNullOrWhiteSpace(root))
        {
            SetStatus("请先输入缓存根目录。", isError: true);
            return false;
        }

        if (!Directory.Exists(root))
        {
            SetStatus("目录不存在，请检查路径是否正确。", isError: true);
            return false;
        }

        return true;
    }

    private CacheSearchOptions BuildSearchOptions(string keyword)
    {
        return CacheSearchOptionsFactory.Create(
            keyword,
            MatchMode,
            CaseSensitive,
            SplitKeywords,
            !AnyKeywords,
            BuildScope());
    }

    private CacheSearchScope BuildScope()
    {
        return CacheSearchOptionsFactory.BuildScope(
            IncludePartName,
            IncludeOwnerName,
            IncludeBvid,
            IncludeAvid);
    }

    private void UpdateItems(IEnumerable<BiliVideoCache> caches)
    {
        ClearItems();

        foreach (var cache in caches.OrderBy(c => c.Avid))
        {
            var sizeMb = cache.TotalSize / (1024d * 1024d);
            var lastUpdated = cache.Segments.Max(segment => segment.UpdatedAt);
            var duration = cache.TotalDuration;

            Items.Add(new CacheItem
            {
                Avid = cache.Avid,
                Title = cache.Title,
                OwnerName = cache.OwnerName ?? string.Empty,
                Bvid = cache.Bvid ?? string.Empty,
                DurationValue = duration,
                Duration = FormatDuration(duration),
                SegmentCount = cache.Segments.Count,
                SizeMb = sizeMb.ToString("F2", CultureInfo.InvariantCulture),
                SizeMbValue = sizeMb,
                SizeBytes = cache.TotalSize,
                IsAllCompleted = cache.IsAllCompleted ? "是" : "否",
                IsAllCompletedValue = cache.IsAllCompleted,
                LastUpdatedValue = lastUpdated,
                LastUpdated = lastUpdated == DateTimeOffset.MinValue
                    ? "未知"
                    : lastUpdated.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            });
        }

        UpdateStorageSummary();
    }

    private void ClearItems()
    {
        Items.Clear();
        ReplaceSelection(SelectedItems, Array.Empty<CacheItem>());
        SelectedItem = null;
        ClearSegmentDetails();
        UpdateStorageSummary();
    }

    private void UpdateStorageSummary()
    {
        var totalBytes = SaturatingSumBytes(Items);
        var incompleteCount = Items.Count(item => !item.IsAllCompletedValue);
        var selectedItems = SelectedItems.DistinctBy(item => item.Avid).ToList();
        var selectedBytes = SaturatingSumBytes(selectedItems);

        StorageSummary =
            $"列表 {Items.Count} 条 / {FormatBytes(totalBytes)}，未完成 {incompleteCount} 条；" +
            $"已选 {selectedItems.Count} 条 / {FormatBytes(selectedBytes)}";

        UpdateEmptyState();
    }

    private static long SaturatingSumBytes(IEnumerable<CacheItem> items)
    {
        var total = 0L;
        foreach (var item in items)
        {
            var bytes = item.SizeBytes;
            if (bytes <= 0)
            {
                continue;
            }

            if (total > long.MaxValue - bytes)
            {
                return long.MaxValue;
            }

            total += bytes;
        }

        return total;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):F2} GB";
        }

        return $"{bytes / (1024d * 1024):F2} MB";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes}:{duration.Seconds:D2}";
    }

    private void UpdateSegmentDetails()
    {
        if (!TryResolveSelectedCache(out var cache))
        {
            ClearSegmentDetails();
            return;
        }

        UpdateSegmentDetails(cache);
    }

    private void UpdateSegmentDetails(BiliVideoCache cache)
    {
        var previousSelection = SelectedSegmentDetail?.SegmentKey;
        SegmentDetails.Clear();
        ReplaceSelection(SelectedSegmentDetails, Array.Empty<SegmentDetailItem>());

        foreach (var segment in cache.Segments
                     .OrderBy(item => item.PageIndex)
                     .ThenBy(item => Path.GetFileName(item.SegmentDirectory), StringComparer.OrdinalIgnoreCase))
        {
            var plan = _playbackService.CreatePlan(segment);
            var sizeMb = segment.TotalBytes / (1024d * 1024d);
            var segmentKey = Path.GetFileName(segment.SegmentDirectory);

            SegmentDetails.Add(new SegmentDetailItem
            {
                Avid = segment.Avid,
                SegmentKey = segmentKey,
                PageIndex = segment.PageIndex,
                PartName = segment.PartName,
                StructureKind = plan.StructureKind,
                MaterialKind = plan.MaterialKind.ToString(),
                SizeMb = sizeMb.ToString("F2", CultureInfo.InvariantCulture),
                Duration = segment.TotalDuration.ToString(),
                IsPlayable = plan.IsPlayable ? "是" : "否",
                DirectoryPath = segment.SegmentDirectory
            });
        }

        SelectedSegmentDetail = SegmentDetails.FirstOrDefault(item =>
                                  string.Equals(item.SegmentKey, previousSelection, StringComparison.OrdinalIgnoreCase)) ??
                              SegmentDetails.FirstOrDefault();
        UpdateEmptyState();
    }

    private void ClearSegmentDetails()
    {
        SegmentDetails.Clear();
        ReplaceSelection(SelectedSegmentDetails, Array.Empty<SegmentDetailItem>());
        SelectedSegmentDetail = null;
        UpdateEmptyState();
    }

    private bool TryResolveSelectedCache(out BiliVideoCache cache)
    {
        cache = null!;

        if (SelectedItem is null || _currentIndex is null)
        {
            return false;
        }

        if (!_currentIndex.ByAvid.TryGetValue(SelectedItem.Avid, out var resolvedCache))
        {
            return false;
        }

        cache = resolvedCache;
        return true;
    }

    private bool TryBuildBatchTargets(out IReadOnlyList<BatchPlaybackTarget> targets, out string failureMessage)
    {
        failureMessage = string.Empty;

        if (SelectedSegmentDetails.Count > 0)
        {
            if (!TryResolveSelectedCache(out var currentCache))
            {
                targets = Array.Empty<BatchPlaybackTarget>();
                failureMessage = "当前缓存未加载，请先扫描并选择一条缓存。";
                return false;
            }

            targets = SelectedSegmentDetails
                .GroupBy(item => item.PageIndex)
                .OrderBy(group => group.Key)
                .Select(group => new BatchPlaybackTarget(currentCache, group.Key, group.First().PartName))
                .ToList();

            return true;
        }

        if (SelectedItems.Count == 0)
        {
            targets = Array.Empty<BatchPlaybackTarget>();
            failureMessage = "请先选择要播放的分段或缓存。";
            return false;
        }

        if (_currentIndex is null)
        {
            targets = Array.Empty<BatchPlaybackTarget>();
            failureMessage = "当前缓存未加载，请先执行扫描。";
            return false;
        }

        var resolvedTargets = new List<BatchPlaybackTarget>();
        foreach (var item in SelectedItems.OrderBy(item => item.Avid))
        {
            if (!_currentIndex.ByAvid.TryGetValue(item.Avid, out var cache))
            {
                continue;
            }

            resolvedTargets.AddRange(_playbackService.CreatePagePlans(cache)
                .OrderBy(plan => plan.PageIndex)
                .Select(plan => new BatchPlaybackTarget(cache, plan.PageIndex, plan.PartName)));
        }

        if (resolvedTargets.Count == 0)
        {
            targets = Array.Empty<BatchPlaybackTarget>();
            failureMessage = "没有找到可播放的缓存页面。";
            return false;
        }

        targets = resolvedTargets
            .GroupBy(target => (target.Cache.Avid, target.PageIndex))
            .Select(group => group.First())
            .ToList();

        return true;
    }

    private PlaybackLaunchOptions CreateDefaultLaunchOptions()
    {
        return new PlaybackLaunchOptions
        {
            PreferredPlayer = PreferredPlayer
        };
    }

    private void RaiseSelectionCommandStates()
    {
        PlaySelectedPageCommand.RaiseCanExecuteChanged();
        PlayBatchCommand.RaiseCanExecuteChanged();
        PlayNextCommand.RaiseCanExecuteChanged();
        ClearQueueCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        UndoDeleteCommand.RaiseCanExecuteChanged();
        PurgeTrashCommand.RaiseCanExecuteChanged();
        OpenTranscodeCacheCommand.RaiseCanExecuteChanged();
        CleanupTranscodeCacheCommand.RaiseCanExecuteChanged();
        ClearTranscodeCacheCommand.RaiseCanExecuteChanged();
        RefreshStorageOverviewCommand.RaiseCanExecuteChanged();
        ExportMp4Command.RaiseCanExecuteChanged();
    }

    private static void ReplaceSelection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source.Distinct())
        {
            target.Add(item);
        }
    }

    private void LoadSettings()
    {
        if (_settingsService is null)
        {
            return;
        }

        AppSettingsLoadResult report;
        _loadingSettings = true;
        try
        {
            report = _settingsService.LoadWithReport();
            _settingsLoadKind = report.LoadKind;
            _sourceSettingsSchemaVersion = report.SourceSchemaVersion;
            var settings = report.Settings;
            _settingsCanSave = report.CanSave;
            _settingsSaveBlockedMessage = report.CanSave
                ? null
                : report.UserMessage ?? "当前设置文件不能由本版本安全保存。";
            _canRunAutomaticTranscodeCacheMaintenance =
                report.CanRunAutomaticMaintenance;
            RootPath = settings.RootPath;
            IncludeIncomplete = settings.IncludeIncomplete;
            SplitKeywords = settings.SplitKeywords;
            AnyKeywords = settings.AnyKeywords;
            IncludePartName = settings.IncludePartName;
            IncludeOwnerName = settings.IncludeOwnerName;
            IncludeBvid = settings.IncludeBvid;
            IncludeAvid = settings.IncludeAvid;
            CaseSensitive = settings.CaseSensitive;
            MatchMode = settings.MatchMode;
            PreferredPlayer = settings.PreferredPlayer;
            TranscodeCacheRetentionDays =
                PlaybackArtifactCleanupOptions.IsValidRetentionDays(settings.TranscodeCacheRetentionDays)
                    ? settings.TranscodeCacheRetentionDays
                    : PlaybackArtifactCleanupOptions.DefaultRetentionDays;
            TranscodeCacheMaxSizeGigabytes =
                PlaybackArtifactCleanupOptions.IsValidMaxSizeGigabytes(settings.TranscodeCacheMaxSizeGigabytes)
                    ? settings.TranscodeCacheMaxSizeGigabytes
                    : PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes;
        }
        catch (Exception ex)
        {
            _settingsLoadKind = AppSettingsLoadKind.ReadError;
            _sourceSettingsSchemaVersion = null;
            _settingsCanSave = false;
            _canRunAutomaticTranscodeCacheMaintenance = false;
            _settingsSaveBlockedMessage = $"读取设置失败：{ex.Message}";
            SetStatus(
                $"读取设置失败：{ex.Message}；本次启动已停用自动缓存维护。",
                isError: true);
            return;
        }
        finally
        {
            _loadingSettings = false;
        }

        var message = report.UserMessage;
        var isError = report.IsUnsupported ||
            !report.CanRunAutomaticMaintenance;
        if (report.RequiresSave && report.CanSave)
        {
            try
            {
                _settingsService.Save(report.Settings);
            }
            catch (Exception ex)
            {
                HandleSettingsSaveFailure(ex);
                message = string.IsNullOrWhiteSpace(message)
                    ? $"迁移后的设置保存失败：{ex.Message}"
                    : $"{message} 迁移后的设置保存失败：{ex.Message}";
                isError = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            SetStatus(message, isError);
        }
    }

    private void SaveSettings()
    {
        if (_settingsService is null || _loadingSettings)
        {
            return;
        }

        if (!_settingsCanSave)
        {
            SetStatus(
                $"设置未保存：{_settingsSaveBlockedMessage ?? "当前会话已禁止保存设置。"}",
                isError: true);
            return;
        }

        try
        {
            _settingsService.Save(new AppSettings
            {
                RootPath = RootPath.Trim(),
                IncludeIncomplete = IncludeIncomplete,
                SplitKeywords = SplitKeywords,
                AnyKeywords = AnyKeywords,
                IncludePartName = IncludePartName,
                IncludeOwnerName = IncludeOwnerName,
                IncludeBvid = IncludeBvid,
                IncludeAvid = IncludeAvid,
                CaseSensitive = CaseSensitive,
                MatchMode = MatchMode,
                PreferredPlayer = PreferredPlayer,
                TranscodeCacheRetentionDays = TranscodeCacheRetentionDays,
                TranscodeCacheMaxSizeGigabytes = TranscodeCacheMaxSizeGigabytes
            });
        }
        catch (Exception ex)
        {
            HandleSettingsSaveFailure(ex);
            SetStatus($"保存设置失败：{ex.Message}", isError: true);
        }
    }

    private void HandleSettingsSaveFailure(Exception exception)
    {
        _canRunAutomaticTranscodeCacheMaintenance = false;
        if (exception is InvalidOperationException)
        {
            _settingsCanSave = false;
        }

        _settingsSaveBlockedMessage =
            $"设置保存失败，本次启动已停用自动缓存维护：{exception.Message}";
    }

    private bool SetSettingField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetField(ref field, value, propertyName))
        {
            return false;
        }

        SaveSettings();
        return true;
    }

    private static string BuildScanStatus(CacheIndexBuildResult report)
    {
        var message = $"扫描完成，共 {report.Index.VideoCaches.Count} 条缓存，收录 {report.IncludedEntries} 个分段。";
        if (!report.HasWarnings)
        {
            return message;
        }

        return message +
               $" 跳过未完成 {report.SkippedIncompleteEntries}，" +
               $"无效条目 {report.InvalidEntries}，不可访问目录 {report.InaccessibleDirectories}。";
    }

    private void SetStatus(string message, bool isError)
    {
        RecordStatusForDiagnostics(message, isError);
        StatusMessage = message;
        IsStatusError = isError;
        StatusBrush = ResolveStatusBrush(isError);
    }

    /// <summary>
    /// 优先取应用资源里的主题画刷，深色模式下才不会出现深底深红字；
    /// 单元测试没有 Application 实例，回退到浅色主题的固定色。
    /// </summary>
    private static Media.Brush ResolveStatusBrush(bool isError)
    {
        var resourceKey = isError
            ? AppThemePalette.StatusErrorBrushKey
            : AppThemePalette.StatusNormalBrushKey;

        if (System.Windows.Application.Current?.TryFindResource(resourceKey) is Media.Brush themed)
        {
            return themed;
        }

        var brush = new Media.SolidColorBrush(isError
            ? AppThemePalette.FallbackStatusError
            : AppThemePalette.FallbackStatusNormal);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private sealed record DeleteOperationResult(
        long Avid,
        bool Found,
        bool Succeeded,
        string? ErrorMessage,
        CacheTrashOperationResult? TrashOperation);

    private sealed record BatchPlaybackTarget(BiliVideoCache Cache, int PageIndex, string PartName);
}
