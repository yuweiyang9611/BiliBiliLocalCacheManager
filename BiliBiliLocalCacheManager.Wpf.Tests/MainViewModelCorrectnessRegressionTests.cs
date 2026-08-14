using System.Globalization;
using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelCorrectnessRegressionTests
{
    [Fact]
    public void StorageSummary_ShouldSaturateAcrossMultipleHugeCacheItems()
    {
        var viewModel = CreateViewModel(new CachePlaybackService());
        viewModel.Items.Add(new CacheItem
        {
            Avid = 1,
            Title = "Huge 1",
            SizeBytes = long.MaxValue
        });
        viewModel.Items.Add(new CacheItem
        {
            Avid = 2,
            Title = "Huge 2",
            SizeBytes = long.MaxValue
        });

        viewModel.SetSelectedCaches(viewModel.Items);

        Assert.Contains("列表 2 条", viewModel.StorageSummary, StringComparison.Ordinal);
        Assert.Contains("已选 2 条", viewModel.StorageSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoDelete_ShouldRestoreToOriginalRootAfterRootChanges()
    {
        var originalRoot = CreateTempRoot();
        var otherRoot = CreateTempRoot();
        try
        {
            CreateDashEntry(originalRoot, 100, "c_1", 1, completed: true);
            var viewModel = CreateViewModel(new CachePlaybackService());
            viewModel.RootPath = originalRoot;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = Assert.Single(viewModel.Items);

            await ExecuteCommandAsync(viewModel.DeleteCommand);
            Assert.False(Directory.Exists(Path.Combine(originalRoot, "100")));

            viewModel.RootPath = otherRoot;
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));
            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);

            Assert.True(Directory.Exists(Path.Combine(originalRoot, "100")));
            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
        }
        finally
        {
            SafeDeleteDirectory(originalRoot);
            SafeDeleteDirectory(otherRoot);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldPermanentlyDeleteEntriesAndInvalidateUndo()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, 100, "c_1", 1, completed: true);
            var trashService = new FileSystemCacheTrashService();
            var viewModel = CreateViewModel(new CachePlaybackService(), trashService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = Assert.Single(viewModel.Items);
            await ExecuteCommandAsync(viewModel.DeleteCommand);

            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));
            Assert.Single(Directory.GetDirectories(trashService.GetTrashDirectory(root)));

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.Empty(Directory.GetDirectories(trashService.GetTrashDirectory(root)));
            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
            Assert.Contains("彻底删除 1 条", viewModel.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldRequireSecondConfirmationBeforeDeletingUntrustedLegacyEntry()
    {
        var root = CreateTempRoot();
        try
        {
            var trashService = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                trashService.GetTrashDirectory(root),
                $"100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, "payload.bin"), "payload");
            var dialogService = new SequenceDialogService(true, true);
            var viewModel = CreateViewModel(
                new CachePlaybackService(),
                trashService,
                dialogService: dialogService);
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.False(Directory.Exists(trashPath));
            Assert.Equal(2, dialogService.ConfirmCallCount);
            Assert.Contains("彻底删除 1 条", viewModel.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldPreserveUntrustedLegacyEntry_WhenSecondConfirmationIsDeclined()
    {
        var root = CreateTempRoot();
        try
        {
            var trashService = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                trashService.GetTrashDirectory(root),
                $"100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, "payload.bin"), "payload");
            var dialogService = new SequenceDialogService(true, false);
            var viewModel = CreateViewModel(
                new CachePlaybackService(),
                trashService,
                dialogService: dialogService);
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.True(Directory.Exists(trashPath));
            Assert.Equal(2, dialogService.ConfirmCallCount);
            Assert.Contains("跳过 1 条", viewModel.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldKeepEntriesAndUndo_WhenConfirmationIsDeclined()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, 100, "c_1", 1, completed: true);
            var trashService = new FileSystemCacheTrashService();
            var dialogService = new SequenceDialogService(true, false);
            var viewModel = CreateViewModel(
                new CachePlaybackService(),
                trashService,
                dialogService: dialogService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = Assert.Single(viewModel.Items);
            await ExecuteCommandAsync(viewModel.DeleteCommand);

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.Single(Directory.GetDirectories(trashService.GetTrashDirectory(root)));
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));
            Assert.Equal(2, dialogService.ConfirmCallCount);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldPreserveUndoStateForAnotherRoot()
    {
        var originalRoot = CreateTempRoot();
        var otherRoot = CreateTempRoot();
        try
        {
            CreateDashEntry(originalRoot, 100, "c_1", 1, completed: true);
            var viewModel = CreateViewModel(new CachePlaybackService());
            viewModel.RootPath = originalRoot;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = Assert.Single(viewModel.Items);
            await ExecuteCommandAsync(viewModel.DeleteCommand);

            viewModel.RootPath = otherRoot;
            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));
            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);
            Assert.True(Directory.Exists(Path.Combine(originalRoot, "100")));
        }
        finally
        {
            SafeDeleteDirectory(originalRoot);
            SafeDeleteDirectory(otherRoot);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldInvalidateUndoAndReportPartialFailure()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, 100, "c_1", 1, completed: true);
            var trashService = new FaultInjectingTrashService
            {
                PurgeResultOverride = new CacheTrashPurgeResult(
                    0,
                    12,
                    1,
                    0,
                    "Injected purge failure.",
                    PartiallyDeletedEntryCount: 1,
                    PendingPurgeEntryCount: 1)
            };
            var viewModel = CreateViewModel(new CachePlaybackService(), trashService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = Assert.Single(viewModel.Items);
            await ExecuteCommandAsync(viewModel.DeleteCommand);

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
            Assert.Contains("Injected purge failure.", viewModel.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("已部分删除", viewModel.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("不能撤销或恢复", viewModel.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task IncludeIncompleteChange_ShouldClearStaleResults()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, 100, "c_1", 1, completed: true);
            CreateDashEntry(root, 200, "c_1", 1, completed: false);
            var viewModel = CreateViewModel(new CachePlaybackService());
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            Assert.Single(viewModel.Items);
            viewModel.IncludeIncomplete = true;

            Assert.Empty(viewModel.Items);
            Assert.Null(viewModel.SelectedItem);
            Assert.Empty(viewModel.SegmentDetails);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlayNext_ShouldStayDisabledWhileCurrentQueueItemIsPreparing()
    {
        var root = CreateTempRoot();
        using var release = new ManualResetEventSlim(false);
        var playback = new BlockingPlaybackService(release);
        try
        {
            CreateDashEntry(root, 300, "c_1", 1, completed: true);
            CreateDashEntry(root, 300, "c_2", 2, completed: true);
            var viewModel = CreateViewModel(playback);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SetSelectedCaches(viewModel.Items.ToArray());

            viewModel.PlayBatchCommand.Execute(null);
            Assert.True(playback.Started.Wait(TimeSpan.FromSeconds(5)));

            Assert.False(viewModel.PlayNextCommand.CanExecute(null));
            Assert.Equal(1, playback.PlayCallCount);

            release.Set();
            await (viewModel.PlayBatchCommand.ExecutionTask ?? throw new InvalidOperationException("Playback task was not created."));

            Assert.Equal(1, playback.PlayCallCount);
            Assert.Equal(1, viewModel.QueueLength);
            Assert.True(viewModel.PlayNextCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task BatchDelete_ShouldKeepUndoForSuccessfulItems_WhenAnotherItemThrows()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, 100, "c_1", 1, completed: true);
            CreateDashEntry(root, 200, "c_1", 1, completed: true);
            var trashService = new FaultInjectingTrashService { ThrowOnMoveAvid = 200 };
            var viewModel = CreateViewModel(new CachePlaybackService(), trashService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SetSelectedCaches(viewModel.Items.ToArray());

            await ExecuteCommandAsync(viewModel.DeleteCommand);

            Assert.False(Directory.Exists(Path.Combine(root, "100")));
            Assert.True(Directory.Exists(Path.Combine(root, "200")));
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));

            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);

            Assert.True(Directory.Exists(Path.Combine(root, "100")));
            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UndoDelete_ShouldContinueRestoringOtherItems_WhenOneItemThrows()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, 100, "c_1", 1, completed: true);
            CreateDashEntry(root, 200, "c_1", 1, completed: true);
            var trashService = new FaultInjectingTrashService();
            var viewModel = CreateViewModel(new CachePlaybackService(), trashService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SetSelectedCaches(viewModel.Items.ToArray());
            await ExecuteCommandAsync(viewModel.DeleteCommand);
            trashService.ThrowOnRestoreAvid = 200;

            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);

            Assert.True(Directory.Exists(Path.Combine(root, "100")));
            Assert.False(Directory.Exists(Path.Combine(root, "200")));
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));

            trashService.ThrowOnRestoreAvid = null;
            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);

            Assert.True(Directory.Exists(Path.Combine(root, "200")));
            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlaySelectedPage_ShouldShowProgressAndTreatCancellationAsNormal_WhenFfmpegRequired()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, 300, "c_1", 1, completed: true);
            var playback = new CancelledAsyncPlaybackService();
            var progressDialog = new CancellingPlaybackProgressDialogService();
            var viewModel = CreateViewModel(
                playback,
                playbackProgressDialogService: progressDialog);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = Assert.Single(viewModel.Items);
            viewModel.SelectedSegmentDetail = Assert.Single(viewModel.SegmentDetails);

            await ExecuteCommandAsync(viewModel.PlaySelectedPageCommand);

            Assert.Equal(1, progressDialog.CallCount);
            Assert.Equal(1, playback.AsyncPlayCallCount);
            Assert.Equal(0, playback.SyncPlayCallCount);
            Assert.Contains("\u53d6\u6d88", viewModel.StatusMessage, StringComparison.Ordinal);
            Assert.True(viewModel.PlaySelectedPageCommand.CanExecute(null));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static MainViewModel CreateViewModel(
        ICachePlaybackService playbackService,
        ICacheTrashService? trashService = null,
        IPlaybackProgressDialogService? playbackProgressDialogService = null,
        IDialogService? dialogService = null)
    {
        return new MainViewModel(
            new CacheManager(),
            playbackService,
            dialogService ?? new ConfirmDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService(),
            trashService ?? new FileSystemCacheTrashService(),
            settingsService: null,
            playbackProgressDialogService: playbackProgressDialogService);
    }

    private static async Task ExecuteCommandAsync(AsyncRelayCommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await (command.ExecutionTask ?? throw new InvalidOperationException("Command task was not created."));
    }

    private static void CreateDashEntry(string root, long avid, string segmentName, int page, bool completed)
    {
        var segment = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture), segmentName);
        var quality = Path.Combine(segment, "80");
        Directory.CreateDirectory(quality);
        File.WriteAllText(
            Path.Combine(segment, "entry.json"),
            $$"""
              {
                "is_completed": {{completed.ToString().ToLowerInvariant()}},
                "total_bytes": 10,
                "downloaded_bytes": 10,
                "title": "Title {{avid}}",
                "type_tag": "80",
                "cover": "cover",
                "prefered_video_quality": 80,
                "guessed_total_bytes": 10,
                "total_time_milli": 1000,
                "danmaku_count": 0,
                "time_update_stamp": 0,
                "time_create_stamp": 0,
                "avid": {{avid}},
                "spid": 0,
                "seasion_id": 0,
                "page_data": {
                  "cid": {{page}},
                  "page": {{page}},
                  "from": "local",
                  "part": "P{{page}}",
                  "vid": "",
                  "has_alias": false,
                  "tid": 0
                }
              }
              """);
        File.WriteAllText(Path.Combine(quality, "video.m4s"), "video");
        File.WriteAllText(Path.Combine(quality, "audio.m4s"), "audio");
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bili_wpf_correctness_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore test cleanup failures.
        }
    }

    private sealed class ConfirmDialogService : IDialogService
    {
        public bool Confirm(string message, string title) => true;
        public string? PickFolder(string title, string? initialPath) => null;
    }

    private sealed class SequenceDialogService(params bool[] confirmations) : IDialogService
    {
        private readonly Queue<bool> _confirmations = new(confirmations);

        public int ConfirmCallCount { get; private set; }

        public bool Confirm(string message, string title)
        {
            ConfirmCallCount++;
            if (_confirmations.Count == 0)
            {
                throw new InvalidOperationException("No confirmation result was configured for this call.");
            }

            return _confirmations.Dequeue();
        }

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

    private sealed class BlockingPlaybackService : ICachePlaybackService
    {
        private readonly CachePlaybackService _inner = new();
        private readonly ManualResetEventSlim _release;
        private int _playCallCount;

        public BlockingPlaybackService(ManualResetEventSlim release)
        {
            _release = release;
        }

        public ManualResetEventSlim Started { get; } = new(false);
        public int PlayCallCount => Volatile.Read(ref _playCallCount);

        public CachePlaybackPlan CreatePlan(BiliSegment segment) => _inner.CreatePlan(segment);
        public CachePlaybackPlan CreatePlan(BiliVideoCache cache, string? segmentKey = null) => _inner.CreatePlan(cache, segmentKey);
        public CachePlaybackPagePlan CreatePagePlan(BiliVideoCache cache, string? segmentKey = null) => _inner.CreatePagePlan(cache, segmentKey);
        public IReadOnlyList<CachePlaybackPagePlan> CreatePagePlans(BiliVideoCache cache) => _inner.CreatePagePlans(cache);

        public PlaybackLaunchResult Play(BiliSegment segment, PlaybackLaunchOptions? launchOptions = null) => Block();

        public PlaybackLaunchResult Play(
            BiliVideoCache cache,
            string? segmentKey = null,
            PlaybackLaunchOptions? launchOptions = null) => Block();

        private PlaybackLaunchResult Block()
        {
            Interlocked.Increment(ref _playCallCount);
            Started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
            {
                return PlaybackLaunchResult.Failure("Timed out waiting for the test gate.");
            }

            return PlaybackLaunchResult.Success("Started", "TestPlayer");
        }
    }

    private sealed class CancelledAsyncPlaybackService : ICachePlaybackService
    {
        private readonly CachePlaybackService _inner = new();

        public int AsyncPlayCallCount { get; private set; }
        public int SyncPlayCallCount { get; private set; }

        public CachePlaybackPlan CreatePlan(BiliSegment segment) => _inner.CreatePlan(segment);
        public CachePlaybackPlan CreatePlan(BiliVideoCache cache, string? segmentKey = null) => _inner.CreatePlan(cache, segmentKey);
        public CachePlaybackPagePlan CreatePagePlan(BiliVideoCache cache, string? segmentKey = null) => _inner.CreatePagePlan(cache, segmentKey);
        public IReadOnlyList<CachePlaybackPagePlan> CreatePagePlans(BiliVideoCache cache) => _inner.CreatePagePlans(cache);

        public PlaybackLaunchResult Play(BiliSegment segment, PlaybackLaunchOptions? launchOptions = null)
        {
            SyncPlayCallCount++;
            return PlaybackLaunchResult.Success("Started", "Test");
        }

        public PlaybackLaunchResult Play(
            BiliVideoCache cache,
            string? segmentKey = null,
            PlaybackLaunchOptions? launchOptions = null)
        {
            SyncPlayCallCount++;
            return PlaybackLaunchResult.Success("Started", "Test");
        }

        public Task<PlaybackLaunchResult> PlayAsync(
            BiliVideoCache cache,
            string? segmentKey = null,
            PlaybackLaunchOptions? launchOptions = null,
            IProgress<PlaybackPreparationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            AsyncPlayCallCount++;
            return Task.FromCanceled<PlaybackLaunchResult>(cancellationToken);
        }
    }

    private sealed class CancellingPlaybackProgressDialogService : IPlaybackProgressDialogService
    {
        public int CallCount { get; private set; }

        public async Task<PlaybackLaunchResult> RunAsync(
            string title,
            Func<IProgress<PlaybackPreparationProgress>, CancellationToken, Task<PlaybackLaunchResult>> operation)
        {
            CallCount++;
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            return await operation(
                new InlineProgress<PlaybackPreparationProgress>(_ => { }),
                cancellationSource.Token);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FaultInjectingTrashService : ICacheTrashService
    {
        private readonly FileSystemCacheTrashService _inner = new();

        public long? ThrowOnMoveAvid { get; set; }
        public long? ThrowOnRestoreAvid { get; set; }
        public CacheTrashPurgeResult? PurgeResultOverride { get; set; }

        public string GetTrashDirectory(string rootDirectory) => _inner.GetTrashDirectory(rootDirectory);

        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid)
        {
            if (ThrowOnMoveAvid == avid)
            {
                throw new IOException("Injected move failure.");
            }

            return _inner.MoveToTrash(rootDirectory, avid);
        }

        public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath)
        {
            if (ThrowOnRestoreAvid == avid)
            {
                throw new IOException("Injected restore failure.");
            }

            return _inner.Restore(rootDirectory, avid, trashPath);
        }

        public CacheTrashStatistics GetStatistics(
            string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetStatistics(rootDirectory, cancellationToken);
        }

        public IReadOnlyList<CacheTrashEntry> ListEntries(
            string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            return _inner.ListEntries(rootDirectory, cancellationToken);
        }

        public CacheTrashPurgeResult Purge(
            string rootDirectory,
            bool includeUntrustedLegacyEntries = false)
        {
            return PurgeResultOverride ?? _inner.Purge(rootDirectory, includeUntrustedLegacyEntries);
        }
    }
}
