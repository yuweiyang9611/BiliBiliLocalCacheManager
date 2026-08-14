using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelStorageOverviewTests
{
    [Fact]
    public async Task RefreshCommand_ShouldPublishThreeStorageCategoriesAndRootChangeShouldInvalidateThem()
    {
        var artifactStore = new RecordingArtifactStore();
        var snapshot = CreateSnapshot(originalBytes: 100, transcodeBytes: 30, trashBytes: 20, reclaimableBytes: 25);
        var overviewService = new RecordingStorageOverviewService(snapshot);
        var viewModel = CreateViewModel(
            artifactStore,
            overviewService,
            new RecordingTrashService());
        viewModel.RootPath = Path.Combine(Path.GetTempPath(), "overview-root-one");

        await ExecuteCommandAsync(viewModel.RefreshStorageOverviewCommand);

        Assert.Same(snapshot, viewModel.StorageOverview);
        Assert.Equal(1, overviewService.FullRefreshCount);
        Assert.Contains("150.00 MB", viewModel.StorageOverviewSummary, StringComparison.Ordinal);
        Assert.Contains("预计可释放", viewModel.StorageOverviewSummary, StringComparison.Ordinal);
        Assert.Contains("2 条", viewModel.OriginalCacheStorageSummary, StringComparison.Ordinal);
        Assert.Contains("按当前策略", viewModel.TranscodeStorageSummary, StringComparison.Ordinal);
        Assert.Contains("可释放", viewModel.TrashStorageSummary, StringComparison.Ordinal);

        viewModel.RootPath = Path.Combine(Path.GetTempPath(), "overview-root-two");

        Assert.Null(viewModel.StorageOverview);
        Assert.Contains("待刷新", viewModel.StorageOverviewSummary, StringComparison.Ordinal);
        Assert.Equal(1, overviewService.FullRefreshCount);
    }

    [Fact]
    public async Task RefreshCommand_ShouldReportUntrustedLegacyTrashSeparately()
    {
        const long megabyte = 1024L * 1024;
        var baseSnapshot = CreateSnapshot(
            originalBytes: 100,
            transcodeBytes: 30,
            trashBytes: 20,
            reclaimableBytes: 25);
        var snapshot = baseSnapshot with
        {
            Trash = new CacheTrashStatistics(
                "trash",
                1,
                2,
                20 * megabyte,
                0,
                3,
                null,
                UntrustedLegacyEntryCount: 2,
                UntrustedLegacyFileCount: 5,
                UntrustedLegacyBytes: 7 * megabyte,
                PendingPurgeEntryCount: 1)
        };
        var viewModel = CreateViewModel(
            new RecordingArtifactStore(),
            new RecordingStorageOverviewService(snapshot),
            new RecordingTrashService());
        viewModel.RootPath = Path.Combine(Path.GetTempPath(), "overview-untrusted-trash-root");

        await ExecuteCommandAsync(viewModel.RefreshStorageOverviewCommand);

        Assert.Contains("旧版未验证 2 条 / 5 个文件 / 7.00 MB", viewModel.TrashStorageSummary, StringComparison.Ordinal);
        Assert.Contains("清空时需二次确认", viewModel.TrashStorageSummary, StringComparison.Ordinal);
        Assert.Contains("其他未知 1 项不会清理", viewModel.TrashStorageSummary, StringComparison.Ordinal);
        Assert.Contains("待重试永久清理 1 条（不可恢复）", viewModel.TrashStorageSummary, StringComparison.Ordinal);
        Assert.Contains("150.00 MB", viewModel.StorageOverviewSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootChange_ShouldCancelBlockedRefreshAndAllowNewRootToRefresh()
    {
        var oldRoot = Path.Combine(Path.GetTempPath(), $"overview-old-root-{Guid.NewGuid():N}");
        var newRoot = Path.Combine(Path.GetTempPath(), $"overview-new-root-{Guid.NewGuid():N}");
        var replacementSnapshot = CreateSnapshot(200, 40, 10, 15);
        using var overviewService = new BlockingStorageOverviewService(
            oldRoot,
            newRoot,
            replacementSnapshot);
        var viewModel = CreateViewModel(
            new RecordingArtifactStore(),
            overviewService,
            new RecordingTrashService());
        viewModel.RootPath = oldRoot;

        viewModel.RefreshStorageOverviewCommand.Execute(null);
        var oldRefresh = viewModel.RefreshStorageOverviewCommand.ExecutionTask ??
            throw new InvalidOperationException("Refresh task was not created.");
        await overviewService.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.RootPath = newRoot;
        var newRefresh = viewModel.StartBackgroundTranscodeCacheMaintenance();

        try
        {
            await Task.WhenAll(oldRefresh, newRefresh).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            overviewService.ReleaseFirstCall();
        }

        Assert.True(overviewService.FirstCallCanceled.Task.IsCompletedSuccessfully);
        Assert.Equal(2, overviewService.FullRefreshCount);
        Assert.Equal(newRoot, overviewService.LastRequestedRoot);
        Assert.Same(replacementSnapshot, viewModel.StorageOverview);
        Assert.DoesNotContain("失败", viewModel.StorageOverviewSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualTranscodeCleanup_ShouldUseLightweightRefreshAndRecordMaintenance()
    {
        var artifactStore = new RecordingArtifactStore
        {
            CleanupResult = new PlaybackArtifactCleanupResult(
                1,
                12L * 1024 * 1024,
                0,
                18)
        };
        var initial = CreateSnapshot(100, 30, 20, 25);
        var afterCleanup = CreateSnapshot(100, 18, 20, 20);
        var overviewService = new RecordingStorageOverviewService(initial)
        {
            TranscodeSnapshot = afterCleanup
        };
        var viewModel = CreateViewModel(
            artifactStore,
            overviewService,
            new RecordingTrashService());
        viewModel.RootPath = Path.Combine(Path.GetTempPath(), "overview-cleanup-root");
        await ExecuteCommandAsync(viewModel.RefreshStorageOverviewCommand);
        viewModel.TranscodeCacheRetentionDays = 14;

        Assert.NotNull(viewModel.StorageOverview);
        Assert.Null(viewModel.StorageOverview.TranscodeCleanupPreview);

        await ExecuteCommandAsync(viewModel.CleanupTranscodeCacheCommand);

        Assert.Equal(1, overviewService.FullRefreshCount);
        Assert.Equal(1, overviewService.TranscodeRefreshCount);
        Assert.Same(afterCleanup, viewModel.StorageOverview);
        Assert.Contains("按策略清理", viewModel.LastStorageMaintenanceSummary, StringComparison.Ordinal);
        Assert.Contains("12.00 MB", viewModel.LastStorageMaintenanceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupMaintenance_ShouldPerformOneFullRefreshWhenNoSnapshotExists()
    {
        var artifactStore = new RecordingArtifactStore();
        var overviewService = new RecordingStorageOverviewService(CreateSnapshot(100, 30, 20, 25));
        var viewModel = CreateViewModel(
            artifactStore,
            overviewService,
            new RecordingTrashService());
        viewModel.RootPath = Path.Combine(Path.GetTempPath(), "overview-startup-root");

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(1, artifactStore.CleanupCallCount);
        Assert.Equal(1, overviewService.FullRefreshCount);
        Assert.Equal(0, overviewService.TranscodeRefreshCount);
        Assert.NotNull(viewModel.StorageOverview);
    }

    [Fact]
    public async Task Scan_ShouldCompleteBeforeBlockedStorageStatistics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"overview-scan-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var overviewService = new ForegroundBlockingStorageOverviewService(
            CreateSnapshot(0, 0, 0, 0));
        try
        {
            var viewModel = CreateViewModel(
                new RecordingArtifactStore(),
                overviewService,
                new RecordingTrashService());
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.ScanCommand);
            await overviewService.WaitForNextCallAsync();

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.ScanCommand.ExecutionTask?.IsFaulted ?? true);
            Assert.True(viewModel.IsStorageOverviewBusy);
        }
        finally
        {
            overviewService.ReleaseAll();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAndUndo_ShouldCompleteBeforeBlockedStorageStatistics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"overview-delete-root-{Guid.NewGuid():N}");
        var avidDirectory = Path.Combine(root, "123");
        Directory.CreateDirectory(avidDirectory);
        File.WriteAllText(Path.Combine(avidDirectory, "cache.bin"), "cache");
        using var overviewService = new ForegroundBlockingStorageOverviewService(
            CreateSnapshot(0, 0, 0, 0));
        try
        {
            var trashService = new BiliBiliLocalCacheManager.Core.Infrastructure.Management.FileSystemCacheTrashService();
            var viewModel = CreateViewModel(
                new RecordingArtifactStore(),
                overviewService,
                trashService);
            viewModel.RootPath = root;
            var item = new CacheItem { Avid = 123, Title = "Delete me" };
            viewModel.Items.Add(item);
            viewModel.SelectedItem = item;

            await ExecuteCommandAsync(viewModel.DeleteCommand);
            await overviewService.WaitForNextCallAsync();

            Assert.False(viewModel.IsBusy);
            Assert.False(Directory.Exists(avidDirectory));
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));

            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);
            await overviewService.WaitForNextCallAsync();

            Assert.False(viewModel.IsBusy);
            Assert.True(Directory.Exists(avidDirectory));
        }
        finally
        {
            overviewService.ReleaseAll();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldPerformFullRefreshAndRecordMaintenance()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"overview-purge-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var artifactStore = new RecordingArtifactStore();
        using var overviewService = new ForegroundBlockingStorageOverviewService(
            CreateSnapshot(100, 30, 0, 5));
        var trashService = new RecordingTrashService
        {
            PurgeResult = new CacheTrashPurgeResult(2, 24, 0, 0, null)
        };
        try
        {
            var viewModel = CreateViewModel(artifactStore, overviewService, trashService);
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);
            await overviewService.WaitForNextCallAsync();

            Assert.Equal(1, overviewService.FullRefreshCount);
            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.IsStorageOverviewBusy);
            Assert.Contains("回收站彻底清理", viewModel.LastStorageMaintenanceSummary, StringComparison.Ordinal);
            Assert.Equal(1, trashService.PurgeCallCount);
            Assert.False(trashService.LastIncludeUntrustedLegacyEntries ?? true);
        }
        finally
        {
            overviewService.ReleaseAll();
            Directory.Delete(root, recursive: true);
        }
    }

    private static MainViewModel CreateViewModel(
        IPlaybackArtifactStore artifactStore,
        IStorageOverviewService overviewService,
        ICacheTrashService trashService)
    {
        return new MainViewModel(
            new CacheManager(),
            new CachePlaybackService(artifactStore),
            new ConfirmingDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService(),
            trashService: trashService,
            playbackArtifactStore: artifactStore,
            storageOverviewService: overviewService);
    }

    private static StorageOverviewSnapshot CreateSnapshot(
        long originalBytes,
        long transcodeBytes,
        long trashBytes,
        long reclaimableBytes)
    {
        const long megabyte = 1024L * 1024;
        originalBytes *= megabyte;
        transcodeBytes *= megabyte;
        trashBytes *= megabyte;
        reclaimableBytes *= megabyte;
        var root = Path.Combine(Path.GetTempPath(), "overview-snapshot-root");
        var original = new CacheStorageStatistics(root, 2, 4, originalBytes, 0, 0, null);
        var transcode = new PlaybackArtifactCacheStatistics("artifacts", 2, transcodeBytes);
        var previewBytes = Math.Max(0, reclaimableBytes - trashBytes);
        var preview = new PlaybackArtifactCleanupPreview(
            previewBytes > 0 ? 1 : 0,
            previewBytes,
            Math.Max(0, transcodeBytes - previewBytes));
        var trash = new CacheTrashStatistics("trash", 1, 2, trashBytes, 0, 0, null);
        return new StorageOverviewSnapshot(
            root,
            original,
            transcode,
            preview,
            trash,
            originalBytes + transcodeBytes + trashBytes,
            reclaimableBytes,
            DateTimeOffset.Now,
            Array.Empty<string>());
    }

    private static async Task ExecuteCommandAsync(AsyncRelayCommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await (command.ExecutionTask ?? throw new InvalidOperationException("Command task was not created."));
    }

    private sealed class RecordingStorageOverviewService : IStorageOverviewService
    {
        private readonly StorageOverviewSnapshot _fullSnapshot;

        public RecordingStorageOverviewService(StorageOverviewSnapshot fullSnapshot)
        {
            _fullSnapshot = fullSnapshot;
            TranscodeSnapshot = fullSnapshot;
        }

        public StorageOverviewSnapshot TranscodeSnapshot { get; init; }

        public int FullRefreshCount { get; private set; }

        public int TranscodeRefreshCount { get; private set; }

        public StorageOverviewSnapshot GetSnapshot(
            string? cacheRoot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FullRefreshCount++;
            return _fullSnapshot;
        }

        public StorageOverviewSnapshot RefreshTranscode(
            StorageOverviewSnapshot snapshot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranscodeRefreshCount++;
            return TranscodeSnapshot;
        }
    }

    private sealed class BlockingStorageOverviewService(
        string blockedRoot,
        string replacementRoot,
        StorageOverviewSnapshot replacementSnapshot) : IStorageOverviewService, IDisposable
    {
        private readonly ManualResetEventSlim _releaseFirstCall = new(initialState: false);
        private int _fullRefreshCount;

        public TaskCompletionSource<bool> FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstCallCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FullRefreshCount => Volatile.Read(ref _fullRefreshCount);

        public string? LastRequestedRoot { get; private set; }

        public StorageOverviewSnapshot GetSnapshot(
            string? cacheRoot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cleanupOptions);
            Interlocked.Increment(ref _fullRefreshCount);
            LastRequestedRoot = cacheRoot;
            if (!string.Equals(cacheRoot, blockedRoot, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(replacementRoot, cacheRoot);
                cancellationToken.ThrowIfCancellationRequested();
                return replacementSnapshot;
            }

            FirstCallStarted.TrySetResult(true);
            var signaled = WaitHandle.WaitAny(
                new[] { cancellationToken.WaitHandle, _releaseFirstCall.WaitHandle });
            if (signaled == 0)
            {
                FirstCallCanceled.TrySetResult(true);
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new InvalidOperationException("The blocked refresh was released without cancellation.");
        }

        public StorageOverviewSnapshot RefreshTranscode(
            StorageOverviewSnapshot snapshot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A root change must force a full refresh.");

        public void ReleaseFirstCall()
        {
            _releaseFirstCall.Set();
        }

        public void Dispose()
        {
            _releaseFirstCall.Dispose();
        }
    }

    private sealed class ForegroundBlockingStorageOverviewService(
        StorageOverviewSnapshot snapshot) : IStorageOverviewService, IDisposable
    {
        private readonly ManualResetEventSlim _releaseAll = new(initialState: false);
        private readonly SemaphoreSlim _startedCalls = new(0);
        private int _fullRefreshCount;

        public int FullRefreshCount => Volatile.Read(ref _fullRefreshCount);

        public StorageOverviewSnapshot GetSnapshot(
            string? cacheRoot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cleanupOptions);
            Interlocked.Increment(ref _fullRefreshCount);
            _startedCalls.Release();
            var signaled = WaitHandle.WaitAny(
                new[] { cancellationToken.WaitHandle, _releaseAll.WaitHandle });
            if (signaled == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return snapshot;
        }

        public StorageOverviewSnapshot RefreshTranscode(
            StorageOverviewSnapshot currentSnapshot,
            PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default) =>
            GetSnapshot(currentSnapshot.CacheRoot, cleanupOptions, cancellationToken);

        public async Task WaitForNextCallAsync()
        {
            Assert.True(await _startedCalls.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        public void ReleaseAll()
        {
            _releaseAll.Set();
        }

        public void Dispose()
        {
            _releaseAll.Set();
            _releaseAll.Dispose();
            _startedCalls.Dispose();
        }
    }

    private sealed class RecordingArtifactStore : IPlaybackArtifactStore
    {
        public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "overview-artifacts");

        public int CleanupCallCount { get; private set; }

        public PlaybackArtifactCleanupResult CleanupResult { get; init; } =
            new(0, 0, 0, 30);

        public PlaybackArtifactMaterialization GetOrCreate(
            CachePlaybackPlan plan,
            string extension,
            Action<string> producer,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public PlaybackArtifactCacheStatistics GetStatistics() =>
            new(RootDirectory, 2, CleanupResult.RemainingBytes);

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null) =>
            new(0, 0, CleanupResult.RemainingBytes);

        public PlaybackArtifactCleanupResult Cleanup(PlaybackArtifactCleanupOptions? options = null)
        {
            CleanupCallCount++;
            return CleanupResult;
        }

        public PlaybackArtifactCleanupResult Clear() => new(2, 30, 0, 0);
    }

    private sealed class RecordingTrashService : ICacheTrashService
    {
        public CacheTrashPurgeResult PurgeResult { get; init; } = new(0, 0, 0, 0, null);

        public int PurgeCallCount { get; private set; }
        public bool? LastIncludeUntrustedLegacyEntries { get; private set; }

        public string GetTrashDirectory(string rootDirectory) => Path.Combine(rootDirectory, ".trash");

        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid) =>
            throw new NotSupportedException();

        public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath) =>
            throw new NotSupportedException();

        public CacheTrashStatistics GetStatistics(
            string rootDirectory,
            CancellationToken cancellationToken = default) =>
            new(Path.Combine(rootDirectory, ".trash"), 0, 0, 0, 0, 0, null);

        public IReadOnlyList<CacheTrashEntry> ListEntries(
            string rootDirectory,
            CancellationToken cancellationToken = default) =>
            Array.Empty<CacheTrashEntry>();

        public CacheTrashPurgeResult Purge(
            string rootDirectory,
            bool includeUntrustedLegacyEntries = false)
        {
            PurgeCallCount++;
            LastIncludeUntrustedLegacyEntries = includeUntrustedLegacyEntries;
            return PurgeResult;
        }
    }

    private sealed class ConfirmingDialogService : IDialogService
    {
        public bool Confirm(string message, string title) => true;

        public string? PickFolder(string title, string? initialPath) => null;
    }

    private sealed class NoOpHelpService : IHelpService
    {
        public void OpenHelp()
        {
        }
    }

    private sealed class NoOpExplorerService : IExplorerService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }
}
