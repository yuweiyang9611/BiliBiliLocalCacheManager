using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;
using System.Windows.Media;
using Xunit;
using PlaybackContracts = BiliBiliLocalCacheManager.Playback.Contracts;
using PlaybackModels = BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task ScanCommand_ShouldPopulateItems()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, isCompleted: true, title: "Test Title");

            var viewModel = CreateViewModel(alwaysConfirm: true);
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.ScanCommand);

            Assert.Single(viewModel.Items);
            Assert.Equal(100, viewModel.Items[0].Avid);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SearchCommand_ShouldFilterByTitle()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, isCompleted: true, title: "Alpha");
            CreateEntry(root, avid: 200, isCompleted: true, title: "Beta");

            var viewModel = CreateViewModel(alwaysConfirm: true);
            viewModel.RootPath = root;
            viewModel.Keyword = "Alpha";

            await ExecuteCommandAsync(viewModel.SearchCommand);

            Assert.Single(viewModel.Items);
            Assert.Equal(100, viewModel.Items[0].Avid);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RootPath_ShouldClearItemsAndSelection_WhenChanged()
    {
        var root = CreateTempRoot();
        var newRoot = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 110, isCompleted: true, title: "Alpha");

            var viewModel = CreateViewModel(alwaysConfirm: true);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SelectedItem = viewModel.Items[0];
            viewModel.RootPath = newRoot;

            Assert.Empty(viewModel.Items);
            Assert.Null(viewModel.SelectedItem);
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(newRoot);
        }
    }

    [Fact]
    public async Task ScanCommand_ShouldIgnoreResults_WhenRootChangesDuringScan()
    {
        var root = CreateTempRoot();
        var newRoot = CreateTempRoot();
        using var gate = new ManualResetEventSlim(false);

        try
        {
            CreateEntry(root, avid: 120, isCompleted: true, title: "Beta");

            var cacheService = new BlockingCacheManager(gate);
            var viewModel = CreateViewModel(alwaysConfirm: true, cacheService);
            viewModel.RootPath = root;

            viewModel.ScanCommand.Execute(null);

            viewModel.RootPath = newRoot;

            gate.Set();

            if (viewModel.ScanCommand.ExecutionTask is not null)
            {
                await viewModel.ScanCommand.ExecutionTask;
            }

            Assert.Empty(viewModel.Items);
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(newRoot);
        }
    }

    [Fact]
    public async Task DeleteCommand_ShouldRemoveDirectory_WhenConfirmed()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 300, isCompleted: true, title: "Gamma");

            var viewModel = CreateViewModel(alwaysConfirm: true);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = viewModel.Items[0];

            await ExecuteCommandAsync(viewModel.DeleteCommand);

            var avidDir = Path.Combine(root, "300");
            Assert.False(Directory.Exists(avidDir));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DeleteCommand_ShouldKeepDirectory_WhenCancelled()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 400, isCompleted: true, title: "Delta");

            var viewModel = CreateViewModel(alwaysConfirm: false);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = viewModel.Items[0];

            await ExecuteCommandAsync(viewModel.DeleteCommand);

            var avidDir = Path.Combine(root, "400");
            Assert.True(Directory.Exists(avidDir));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DeleteCommand_ShouldMoveToTrashAndUndo_WhenTrashServiceAvailable()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 450, isCompleted: true, title: "Recoverable");
            var trashService = new BiliBiliLocalCacheManager.Core.Infrastructure.Management.FileSystemCacheTrashService();
            var storageOverviewService = new CountingStorageOverviewService();
            var viewModel = CreateViewModel(
                alwaysConfirm: true,
                trashService: trashService,
                storageOverviewService: storageOverviewService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            await WaitUntilAsync(() => storageOverviewService.FullRefreshCount >= 1);
            viewModel.SelectedItem = viewModel.Items[0];
            var refreshCountAfterScan = storageOverviewService.FullRefreshCount;

            await ExecuteCommandAsync(viewModel.DeleteCommand);
            await WaitUntilAsync(() =>
                storageOverviewService.FullRefreshCount >= refreshCountAfterScan + 1);

            Assert.False(Directory.Exists(Path.Combine(root, "450")));
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));
            Assert.Equal(refreshCountAfterScan + 1, storageOverviewService.FullRefreshCount);

            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);
            await WaitUntilAsync(() =>
                storageOverviewService.FullRefreshCount >= refreshCountAfterScan + 2);

            Assert.True(Directory.Exists(Path.Combine(root, "450")));
            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
            Assert.Equal(refreshCountAfterScan + 2, storageOverviewService.FullRefreshCount);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task OpenFolderCommand_ShouldOpenFolder_WhenSelectionValid()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 500, isCompleted: true, title: "Omega");

            var viewModel = CreateViewModelWithExplorer(alwaysConfirm: true, out var explorerService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            var item = viewModel.Items[0];
            viewModel.OpenFolderCommand.Execute(item);

            Assert.Equal(Path.Combine(root, "500"), explorerService.LastOpenedPath);
            Assert.Equal(1, explorerService.CallCount);
            Assert.Same(item, viewModel.SelectedItem);
            AssertStatusBrush(viewModel.StatusBrush, NormalStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void OpenFolderCommand_ShouldSetError_WhenItemMissing()
    {
        var viewModel = CreateViewModelWithExplorer(alwaysConfirm: true, out var explorerService);

        viewModel.OpenFolderCommand.Execute(null);

        Assert.Equal(0, explorerService.CallCount);
        AssertStatusBrush(viewModel.StatusBrush, ErrorStatusColor);
    }

    [Fact]
    public void OpenFolderCommand_ShouldSetError_WhenRootMissing()
    {
        var viewModel = CreateViewModelWithExplorer(alwaysConfirm: true, out var explorerService);
        var item = new CacheItem { Avid = 600, Title = "Theta" };

        viewModel.OpenFolderCommand.Execute(item);

        Assert.Equal(0, explorerService.CallCount);
        Assert.Same(item, viewModel.SelectedItem);
        AssertStatusBrush(viewModel.StatusBrush, ErrorStatusColor);
    }

    [Fact]
    public void OpenFolderCommand_ShouldSetError_WhenFolderMissing()
    {
        var root = CreateTempRoot();
        try
        {
            var viewModel = CreateViewModelWithExplorer(alwaysConfirm: true, out var explorerService);
            var item = new CacheItem { Avid = 700, Title = "Sigma" };

            viewModel.RootPath = root;
            viewModel.OpenFolderCommand.Execute(item);

            Assert.Equal(0, explorerService.CallCount);
            AssertStatusBrush(viewModel.StatusBrush, ErrorStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task OpenFolderCommand_ShouldSetError_WhenExplorerFails()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 800, isCompleted: true, title: "Lambda");

            var viewModel = CreateViewModelWithExplorer(alwaysConfirm: true, out var explorerService);
            explorerService.ThrowOnOpen = true;
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            var item = viewModel.Items[0];
            viewModel.OpenFolderCommand.Execute(item);

            Assert.Equal(1, explorerService.CallCount);
            AssertStatusBrush(viewModel.StatusBrush, ErrorStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task OpenSegmentFolderCommand_ShouldOpenSegmentFolder_WhenSelectionValid()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 810, segmentName: "c_1", part: "P1");

            var viewModel = CreateViewModelWithExplorer(alwaysConfirm: true, out var explorerService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SelectedItem = viewModel.Items[0];
            var segment = viewModel.SegmentDetails[0];

            viewModel.OpenSegmentFolderCommand.Execute(segment);

            Assert.Equal(Path.Combine(root, "810", "c_1"), explorerService.LastOpenedPath);
            Assert.Equal(1, explorerService.CallCount);
            Assert.Same(segment, viewModel.SelectedSegmentDetail);
            AssertStatusBrush(viewModel.StatusBrush, NormalStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task OpenSegmentFolderCommand_ShouldSetError_WhenFolderMissing()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 811, segmentName: "c_1", part: "P1");

            var viewModel = CreateViewModelWithExplorer(alwaysConfirm: true, out var explorerService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SelectedItem = viewModel.Items[0];
            var segment = viewModel.SegmentDetails[0];
            Directory.Delete(segment.DirectoryPath, recursive: true);

            viewModel.OpenSegmentFolderCommand.Execute(segment);

            Assert.Equal(0, explorerService.CallCount);
            Assert.Same(segment, viewModel.SelectedSegmentDetail);
            AssertStatusBrush(viewModel.StatusBrush, ErrorStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlaySelectedPageCommand_ShouldPlaySelectedPage_WhenSelectionValid()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 812, segmentName: "c_1", part: "P1");

            var playbackService = new FakePlaybackService();
            var viewModel = CreateViewModel(alwaysConfirm: true, playbackService: playbackService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SelectedItem = viewModel.Items[0];
            viewModel.SelectedSegmentDetail = viewModel.SegmentDetails[0];

            await ExecuteCommandAsync(viewModel.PlaySelectedPageCommand);

            Assert.Equal(1, playbackService.PlayCallCount);
            Assert.Equal("1", playbackService.LastSegmentKey);
            AssertStatusBrush(viewModel.StatusBrush, NormalStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlaySelectedPageCommand_ShouldSetError_WhenSegmentMissing()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 813, segmentName: "c_1", part: "P1");

            var playbackService = new FakePlaybackService();
            var viewModel = CreateViewModel(alwaysConfirm: true, playbackService: playbackService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SelectedItem = viewModel.Items[0];
            viewModel.SelectedSegmentDetail = null;

            await ExecuteCommandAsync(viewModel.PlaySelectedPageCommand);

            Assert.Equal(0, playbackService.PlayCallCount);
            AssertStatusBrush(viewModel.StatusBrush, ErrorStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlayBatchCommand_ShouldPlayDistinctSelectedPages_WhenSegmentsSelected()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 814, segmentName: "c_1", part: "P1", pageIndex: 1);
            CreateDashEntry(root, avid: 814, segmentName: "legacy_1", part: "P1", pageIndex: 1);
            CreateDashEntry(root, avid: 814, segmentName: "c_2", part: "P2", pageIndex: 2);

            var playbackService = new FakePlaybackService();
            var viewModel = CreateViewModel(alwaysConfirm: true, playbackService: playbackService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SelectedItem = viewModel.Items[0];
            viewModel.SetSelectedSegments(viewModel.SegmentDetails.ToArray());

            await ExecuteCommandAsync(viewModel.PlayBatchCommand);

            Assert.Equal(1, playbackService.PlayCallCount);
            Assert.Equal(1, viewModel.QueueLength);
            Assert.Equal((814, "1"), playbackService.PlayedCacheCalls[0]);

            await ExecuteCommandAsync(viewModel.PlayNextCommand);

            Assert.Equal(2, playbackService.PlayCallCount);
            Assert.Equal(0, viewModel.QueueLength);
            Assert.Equal(
                new List<(long Avid, string? SegmentKey)> { (814, "1"), (814, "2") },
                playbackService.PlayedCacheCalls);
            AssertStatusBrush(viewModel.StatusBrush, NormalStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlayBatchCommand_ShouldPlayAllPages_WhenCachesSelected()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 821, segmentName: "c_1", part: "A1", pageIndex: 1);
            CreateDashEntry(root, avid: 821, segmentName: "c_2", part: "A2", pageIndex: 2);
            CreateDashEntry(root, avid: 822, segmentName: "c_1", part: "B1", pageIndex: 1);

            var playbackService = new FakePlaybackService();
            var viewModel = CreateViewModel(alwaysConfirm: true, playbackService: playbackService);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SetSelectedCaches(viewModel.Items.ToArray());

            await ExecuteCommandAsync(viewModel.PlayBatchCommand);

            Assert.Equal(1, playbackService.PlayCallCount);
            Assert.Equal(2, viewModel.QueueLength);

            await ExecuteCommandAsync(viewModel.PlayNextCommand);
            await ExecuteCommandAsync(viewModel.PlayNextCommand);

            Assert.Equal(3, playbackService.PlayCallCount);
            Assert.Equal(0, viewModel.QueueLength);
            Assert.Equal(
                new List<(long Avid, string? SegmentKey)> { (821, "1"), (821, "2"), (822, "1") },
                playbackService.PlayedCacheCalls);
            AssertStatusBrush(viewModel.StatusBrush, NormalStatusColor);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SelectedItem_ShouldPopulateSegmentDetails()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 820, segmentName: "c_1", part: "P1");
            CreateDashEntry(root, avid: 820, segmentName: "c_2", part: "P2");

            var viewModel = CreateViewModel(alwaysConfirm: true);
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.SelectedItem = viewModel.Items[0];

            Assert.Equal(2, viewModel.SegmentDetails.Count);
            Assert.Equal("c_1", viewModel.SelectedSegmentDetail?.SegmentKey);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static readonly Color ErrorStatusColor = Color.FromRgb(192, 0, 0);
    private static readonly Color NormalStatusColor = Color.FromRgb(102, 102, 102);

    private static MainViewModel CreateViewModel(
        bool alwaysConfirm,
        ICacheManager? cacheService = null,
        PlaybackContracts.ICachePlaybackService? playbackService = null,
        ICacheTrashService? trashService = null,
        IAppSettingsService? settingsService = null,
        IStorageOverviewService? storageOverviewService = null)
    {
        return CreateViewModelWithExplorer(
            alwaysConfirm,
            out _,
            cacheService,
            playbackService,
            trashService,
            settingsService,
            storageOverviewService);
    }

    private static MainViewModel CreateViewModelWithExplorer(
        bool alwaysConfirm,
        out FakeExplorerService explorerService,
        ICacheManager? cacheService = null,
        PlaybackContracts.ICachePlaybackService? playbackService = null,
        ICacheTrashService? trashService = null,
        IAppSettingsService? settingsService = null,
        IStorageOverviewService? storageOverviewService = null)
    {
        var dialogService = new FakeDialogService(alwaysConfirm);
        var helpService = new FakeHelpService();
        explorerService = new FakeExplorerService();
        return new MainViewModel(
            cacheService ?? new CacheManager(),
            playbackService ?? new FakePlaybackService(),
            dialogService,
            helpService,
            explorerService,
            trashService,
            settingsService,
            storageOverviewService: storageOverviewService);
    }

    private static async Task ExecuteCommandAsync(AsyncRelayCommand command)
    {
        command.Execute(null);
        if (command.ExecutionTask is not null)
        {
            await command.ExecutionTask;
        }
    }

    private static void AssertStatusBrush(Brush brush, Color expected)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(expected, solid.Color);
    }

    private static void CreateEntry(string root, long avid, bool isCompleted, string title)
    {
        var avidDir = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
        var segmentDir = Path.Combine(avidDir, "c_1");
        Directory.CreateDirectory(segmentDir);

        var entryPath = Path.Combine(segmentDir, "entry.json");
        File.WriteAllText(entryPath, BuildEntryJson(avid, title, "P1", isCompleted));

        var videoPath = Path.Combine(segmentDir, "1.mp4");
        File.WriteAllText(videoPath, "dummy");
    }

    private static void CreateDashEntry(string root, long avid, string segmentName, string part, int pageIndex = 1)
    {
        var avidDir = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
        var segmentDir = Path.Combine(avidDir, segmentName);
        var qualityDir = Path.Combine(segmentDir, "80");
        Directory.CreateDirectory(qualityDir);

        var entryPath = Path.Combine(segmentDir, "entry.json");
        File.WriteAllText(entryPath, BuildEntryJson(avid, "Dash Title", part, isCompleted: true, pageIndex));

        File.WriteAllText(Path.Combine(qualityDir, "video.m4s"), "video");
        File.WriteAllText(Path.Combine(qualityDir, "audio.m4s"), "audio");
        File.WriteAllText(Path.Combine(qualityDir, "index.json"), "{}");
    }

    private static string BuildEntryJson(long avid, string title, string part, bool isCompleted, int pageIndex = 1)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return $@"{{
  ""is_completed"": {isCompleted.ToString().ToLowerInvariant()},
  ""total_bytes"": 1000,
  ""downloaded_bytes"": {(isCompleted ? 1000 : 100)},
  ""title"": ""{EscapeJson(title)}"",
  ""type_tag"": ""type"",
  ""cover"": ""cover"",
  ""prefered_video_quality"": 80,
  ""guessed_total_bytes"": 1000,
  ""total_time_milli"": 60000,
  ""danmaku_count"": 0,
  ""time_update_stamp"": {timestamp},
  ""time_create_stamp"": {timestamp},
  ""avid"": {avid.ToString(CultureInfo.InvariantCulture)},
  ""spid"": 0,
  ""seasion_id"": 0,
  ""page_data"": {{
    ""cid"": {pageIndex},
    ""page"": {pageIndex},
    ""from"": ""local"",
    ""part"": ""{EscapeJson(part)}"",
    ""vid"": ""vid"",
    ""has_alias"": false,
    ""tid"": 0
  }}
}}";
    }

    private static string EscapeJson(string value) => value.Replace("\"", "\\\"");

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_wpf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in tests.
        }
    }

    private sealed class BlockingCacheManager : ICacheManager
    {
        private readonly ManualResetEventSlim _gate;
        private readonly ICacheManager _inner = new CacheManager();

        public BlockingCacheManager(ManualResetEventSlim gate)
        {
            _gate = gate;
        }

        public CacheIndex BuildIndex(string rootDirectory, CacheIndexBuildOptions? options = null)
        {
            _gate.Wait();
            return _inner.BuildIndex(rootDirectory, options);
        }

        public CacheIndex BuildIndex(string rootDirectory, bool includeIncomplete)
        {
            _gate.Wait();
            return _inner.BuildIndex(rootDirectory, includeIncomplete);
        }

        public IReadOnlyCollection<BiliVideoCache> Search(
            string rootDirectory,
            CacheIndexBuildOptions? buildOptions,
            CacheSearchOptions searchOptions)
        {
            return _inner.Search(rootDirectory, buildOptions, searchOptions);
        }

        public IReadOnlyCollection<BiliVideoCache> Search(
            string rootDirectory,
            bool includeIncomplete,
            CacheSearchOptions searchOptions)
        {
            return _inner.Search(rootDirectory, includeIncomplete, searchOptions);
        }

        public BiliVideoCache? FindByAvid(string rootDirectory, CacheIndexBuildOptions? buildOptions, long avid)
        {
            return _inner.FindByAvid(rootDirectory, buildOptions, avid);
        }

        public BiliVideoCache? FindByAvid(string rootDirectory, bool includeIncomplete, long avid)
        {
            return _inner.FindByAvid(rootDirectory, includeIncomplete, avid);
        }

        public CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false)
        {
            return _inner.DeleteByAvid(rootDirectory, avid, dryRun);
        }
    }

    private sealed class FakePlaybackService : PlaybackContracts.ICachePlaybackService
    {
        public int PlayCallCount { get; private set; }
        public string? LastSegmentKey { get; private set; }
        public PlaybackModels.PlaybackLaunchOptions? LastLaunchOptions { get; private set; }
        public List<(long Avid, string? SegmentKey)> PlayedCacheCalls { get; } = new();
        public PlaybackModels.PlaybackLaunchResult PlayResult { get; set; } =
            PlaybackModels.PlaybackLaunchResult.Success("ok", "test");

        public PlaybackModels.CachePlaybackPlan CreatePlan(BiliSegment segment)
        {
            return PlaybackModels.CachePlaybackPlan.Playable(
                segment.Avid,
                segment.Title,
                segment.PageIndex,
                segment.PartName,
                Path.GetFileName(segment.SegmentDirectory),
                segment.SegmentDirectory,
                "Test",
                PlaybackModels.CachePlaybackMaterialKind.SingleFile,
                new[] { "file.mp4" });
        }

        public PlaybackModels.CachePlaybackPlan CreatePlan(BiliVideoCache cache, string? segmentKey = null)
        {
            LastSegmentKey = segmentKey;
            return CreatePagePlan(cache, segmentKey).SelectedPlan;
        }

        public PlaybackModels.CachePlaybackPagePlan CreatePagePlan(BiliVideoCache cache, string? segmentKey = null)
        {
            LastSegmentKey = segmentKey;
            var targetGroup = cache.Segments
                .GroupBy(segment => segment.PageIndex)
                .OrderBy(group => group.Key)
                .First(group =>
                {
                    if (string.IsNullOrWhiteSpace(segmentKey))
                    {
                        return true;
                    }

                    return string.Equals(group.Key.ToString(CultureInfo.InvariantCulture), segmentKey, StringComparison.OrdinalIgnoreCase) ||
                           group.Any(segment =>
                               string.Equals(Path.GetFileName(segment.SegmentDirectory), segmentKey, StringComparison.OrdinalIgnoreCase));
                });

            var plans = targetGroup.Select(CreatePlan).ToList();
            return new PlaybackModels.CachePlaybackPagePlan(
                cache.Avid,
                cache.Title,
                targetGroup.Key,
                targetGroup.First().PartName,
                plans,
                plans[0]);
        }

        public IReadOnlyList<PlaybackModels.CachePlaybackPagePlan> CreatePagePlans(BiliVideoCache cache)
        {
            return cache.Segments
                .GroupBy(segment => segment.PageIndex)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var plans = group.Select(CreatePlan).ToList();
                    return new PlaybackModels.CachePlaybackPagePlan(
                        cache.Avid,
                        cache.Title,
                        group.Key,
                        group.First().PartName,
                        plans,
                        plans[0]);
                })
                .ToList();
        }

        public PlaybackModels.PlaybackLaunchResult Play(BiliSegment segment, PlaybackModels.PlaybackLaunchOptions? launchOptions = null)
        {
            PlayCallCount++;
            LastLaunchOptions = launchOptions;
            return PlayResult;
        }

        public PlaybackModels.PlaybackLaunchResult Play(
            BiliVideoCache cache,
            string? segmentKey = null,
            PlaybackModels.PlaybackLaunchOptions? launchOptions = null)
        {
            PlayCallCount++;
            LastSegmentKey = segmentKey;
            LastLaunchOptions = launchOptions;
            PlayedCacheCalls.Add((cache.Avid, segmentKey));
            return PlayResult;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
            {
                throw new TimeoutException("Timed out waiting for the asynchronous view-model update.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class CountingStorageOverviewService : IStorageOverviewService
    {
        public int FullRefreshCount { get; private set; }

        public StorageOverviewSnapshot GetSnapshot(
            string? cacheRoot,
            PlaybackModels.PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FullRefreshCount++;
            return new StorageOverviewSnapshot(
                cacheRoot,
                null,
                null,
                null,
                null,
                0,
                0,
                DateTimeOffset.Now,
                Array.Empty<string>());
        }

        public StorageOverviewSnapshot RefreshTranscode(
            StorageOverviewSnapshot snapshot,
            PlaybackModels.PlaybackArtifactCleanupOptions cleanupOptions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }
    }

    private sealed class FakeDialogService(bool confirm) : IDialogService
    {
        public bool Confirm(string message, string title) => confirm;

        public string? PickFolder(string title, string? initialPath) => null;
    }

    private sealed class FakeHelpService : IHelpService
    {
        public void OpenHelp()
        {
            // 测试中无需实际打开浏览器
        }
    }

    private sealed class FakeExplorerService : IExplorerService
    {
        public string? LastOpenedPath { get; private set; }
        public int CallCount { get; private set; }
        public bool ThrowOnOpen { get; set; }

        public void OpenFolder(string folderPath)
        {
            CallCount++;
            LastOpenedPath = folderPath;

            if (ThrowOnOpen)
            {
                throw new InvalidOperationException("Explorer failure.");
            }
        }
    }
}
