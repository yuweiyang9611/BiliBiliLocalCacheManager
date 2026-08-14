using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;
using Xunit;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

/// <summary>
/// 覆盖体验改进：空关键字显示全部、启动自动扫描、空状态引导、列表新增列、窗口标题。
/// </summary>
public sealed class MainViewModelQuickWinTests
{
    [Fact]
    public async Task SearchCommand_ShouldShowEverything_WhenKeywordIsEmpty()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, title: "Alpha");
            CreateEntry(root, avid: 200, title: "Beta");

            var viewModel = CreateViewModel();
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.Keyword = "Alpha";
            await ExecuteCommandAsync(viewModel.SearchCommand);
            Assert.Single(viewModel.Items);

            // 清空关键字后再搜索应当回到全部结果，而不是报错。
            viewModel.Keyword = string.Empty;
            await ExecuteCommandAsync(viewModel.SearchCommand);

            Assert.Equal(2, viewModel.Items.Count);
            Assert.Contains("已显示全部 2 条", viewModel.StatusMessage);
            Assert.False(viewModel.IsStatusError);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task TryAutoScanOnStartup_ShouldScanRememberedDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, title: "记住的目录");

            var viewModel = CreateViewModel();
            viewModel.RootPath = root;

            await viewModel.TryAutoScanOnStartupAsync();

            Assert.Single(viewModel.Items);
            Assert.False(viewModel.ShowEmptyState);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task TryAutoScanOnStartup_ShouldRunOnlyOnce()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, title: "只扫一次");

            var viewModel = CreateViewModel();
            viewModel.RootPath = root;

            await viewModel.TryAutoScanOnStartupAsync();
            viewModel.Keyword = string.Empty;

            // 第二次调用应当直接返回，不再触发扫描。
            var second = viewModel.TryAutoScanOnStartupAsync();
            Assert.True(second.IsCompleted);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryAutoScanOnStartup_ShouldReportMissingDirectory()
    {
        var viewModel = CreateViewModel();
        viewModel.RootPath = Path.Combine(Path.GetTempPath(), "blcm-missing-" + Guid.NewGuid().ToString("N"));

        var task = viewModel.TryAutoScanOnStartupAsync();

        Assert.True(task.IsCompleted);
        Assert.Contains("不存在", viewModel.StatusMessage);
        Assert.True(viewModel.IsStatusError);
    }

    [Fact]
    public void EmptyState_ShouldGuideFirstRun_BeforeAnyScan()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.ShowEmptyState);
        Assert.Equal("尚未加载缓存", viewModel.EmptyStateTitle);
        Assert.Contains("浏览", viewModel.EmptyStateHint);
        Assert.Contains("扫描", viewModel.EmptyStateHint);
        Assert.True(viewModel.ShowSegmentEmptyState);
    }

    [Fact]
    public async Task EmptyState_ShouldExplainEmptyDirectory_AfterScanningNothing()
    {
        var root = CreateTempRoot();
        try
        {
            var viewModel = CreateViewModel();
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.ScanCommand);

            Assert.True(viewModel.ShowEmptyState);
            Assert.Equal("这个目录里没有找到缓存", viewModel.EmptyStateTitle);
            Assert.Contains("未完成", viewModel.EmptyStateHint);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task EmptyState_ShouldExplainNoMatches_WhenSearchMisses()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, title: "Alpha");

            var viewModel = CreateViewModel();
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            viewModel.Keyword = "不存在的关键字";
            await ExecuteCommandAsync(viewModel.SearchCommand);

            Assert.True(viewModel.ShowEmptyState);
            Assert.Equal("没有匹配的缓存", viewModel.EmptyStateTitle);
            Assert.Contains("清空关键字", viewModel.EmptyStateHint);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ScanCommand_ShouldPopulateOwnerBvidAndDuration()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(
                root,
                avid: 100,
                title: "有元数据",
                ownerName: "某位 UP 主",
                bvid: "BV1xx411c7mD",
                totalTimeMilli: 3 * 60 * 1000 + 25 * 1000);

            var viewModel = CreateViewModel();
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            var item = Assert.Single(viewModel.Items);
            Assert.Equal("某位 UP 主", item.OwnerName);
            Assert.Equal("BV1xx411c7mD", item.Bvid);
            Assert.Equal("3:25", item.Duration);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ScanCommand_ShouldLeaveMetadataBlank_WhenLegacyCacheLacksIt()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, title: "旧版缓存", totalTimeMilli: 0);

            var viewModel = CreateViewModel();
            viewModel.RootPath = root;
            await ExecuteCommandAsync(viewModel.ScanCommand);

            var item = Assert.Single(viewModel.Items);
            Assert.Equal(string.Empty, item.OwnerName);
            Assert.Equal(string.Empty, item.Bvid);
            Assert.Equal(string.Empty, item.Duration);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void WindowTitle_ShouldIncludeVersionWithoutCommitSuffix()
    {
        var viewModel = CreateViewModel();

        Assert.StartsWith("BiliBili 本地缓存管理器", viewModel.WindowTitle);
        Assert.Contains("v", viewModel.WindowTitle);
        Assert.DoesNotContain("+", viewModel.WindowTitle);
    }

    private static MainViewModel CreateViewModel()
    {
        return new MainViewModel(
            new CacheManager(),
            new CachePlaybackService(),
            new StubDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService());
    }

    private sealed class StubDialogService : IDialogService
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

    private static async Task ExecuteCommandAsync(AsyncRelayCommand command)
    {
        command.Execute(null);
        if (command.ExecutionTask is not null)
        {
            await command.ExecutionTask;
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "blcm-quickwin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateEntry(
        string root,
        long avid,
        string title,
        string? ownerName = null,
        string? bvid = null,
        long totalTimeMilli = 1000)
    {
        var segmentDirectory = Path.Combine(
            root,
            avid.ToString(CultureInfo.InvariantCulture),
            "c_1");
        Directory.CreateDirectory(segmentDirectory);
        File.WriteAllText(Path.Combine(segmentDirectory, "1.mp4"), "dummy");

        var ownerJson = ownerName is null
            ? string.Empty
            : $"\"owner_name\": \"{ownerName}\",";
        var bvidJson = bvid is null
            ? string.Empty
            : $"\"bvid\": \"{bvid}\",";

        File.WriteAllText(
            Path.Combine(segmentDirectory, "entry.json"),
            $$"""
              {
                "is_completed": true,
                "total_bytes": 5,
                "downloaded_bytes": 5,
                {{ownerJson}}
                {{bvidJson}}
                "title": "{{title}}",
                "type_tag": "80",
                "cover": "cover",
                "prefered_video_quality": 80,
                "total_time_milli": {{totalTimeMilli}},
                "danmaku_count": 0,
                "avid": {{avid}},
                "page_data": {
                  "cid": 1,
                  "page": 1,
                  "part": "P1"
                }
              }
              """);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
