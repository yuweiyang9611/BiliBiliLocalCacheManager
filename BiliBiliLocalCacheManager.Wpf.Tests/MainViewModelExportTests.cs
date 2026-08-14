using System;
using System.Collections.Generic;
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
/// 导出 MP4 的端到端测试。使用纯 .mp4 单文件布局，
/// 物化时直接复用源文件、不触发 FFmpeg，因此无需真实转码环境。
/// </summary>
public sealed class MainViewModelExportTests
{
    [Fact]
    public async Task ExportMp4_ShouldWriteChosenFile_ForSingleSelection()
    {
        var root = CreateTempRoot();
        var outputDirectory = CreateTempRoot();
        try
        {
            CreateSingleFileCache(root, avid: 100, title: "示例视频", pageIndex: 1, part: "P1");

            var targetPath = Path.Combine(outputDirectory, "导出结果.mp4");
            var saveDialog = new StubSaveDialogService(targetPath);
            var viewModel = CreateViewModel(root, saveDialog, new StubDialogService(null));

            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SetSelectedCaches(viewModel.Items.ToList());

            Assert.True(viewModel.ExportMp4Command.CanExecute(null));
            await ExecuteCommandAsync(viewModel.ExportMp4Command);

            Assert.True(File.Exists(targetPath), $"导出文件不存在：{targetPath}。状态：{viewModel.StatusMessage}");
            Assert.Equal("video-payload", File.ReadAllText(targetPath));
            Assert.Contains("导出完成", viewModel.StatusMessage);
            // 建议的默认文件名应当来自标题而不是 avid。
            Assert.Equal("示例视频.mp4", saveDialog.LastSuggestedFileName);
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task ExportMp4_ShouldWriteOneFilePerPage_ForBatchSelection()
    {
        var root = CreateTempRoot();
        var outputDirectory = CreateTempRoot();
        try
        {
            CreateSingleFileCache(root, avid: 100, title: "第一个", pageIndex: 1, part: "P1");
            CreateSingleFileCache(root, avid: 200, title: "第二个", pageIndex: 1, part: "P1");

            var viewModel = CreateViewModel(
                root,
                new StubSaveDialogService(null),
                new StubDialogService(outputDirectory));

            await ExecuteCommandAsync(viewModel.ScanCommand);
            Assert.Equal(2, viewModel.Items.Count);
            viewModel.SetSelectedCaches(viewModel.Items.ToList());

            await ExecuteCommandAsync(viewModel.ExportMp4Command);

            var exported = Directory.GetFiles(outputDirectory, "*.mp4")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "第一个.mp4", "第二个.mp4" }, exported);
            Assert.Contains("成功 2 个", viewModel.StatusMessage);
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task ExportMp4_ShouldAppendPageSuffix_OnlyForMultiPageCache()
    {
        var root = CreateTempRoot();
        var outputDirectory = CreateTempRoot();
        try
        {
            // 一个分 P 的视频（两页）加一个单页视频，同时批量导出。
            CreateSingleFileCache(root, avid: 100, title: "分P视频", pageIndex: 1, part: "上集");
            CreateSingleFileCache(root, avid: 100, title: "分P视频", pageIndex: 2, part: "下集");
            CreateSingleFileCache(root, avid: 200, title: "单页视频", pageIndex: 1, part: "P1");

            var viewModel = CreateViewModel(
                root,
                new StubSaveDialogService(null),
                new StubDialogService(outputDirectory));

            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SetSelectedCaches(viewModel.Items.ToList());
            await ExecuteCommandAsync(viewModel.ExportMp4Command);

            var exported = Directory.GetFiles(outputDirectory, "*.mp4")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(
                new[] { "分P视频 - P1 上集.mp4", "分P视频 - P2 下集.mp4", "单页视频.mp4" }
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
                exported);
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task ExportMp4_ShouldSanitizeTitleIntoFileName()
    {
        var root = CreateTempRoot();
        var outputDirectory = CreateTempRoot();
        try
        {
            CreateSingleFileCache(root, avid: 100, title: "带/非法:字符?的标题", pageIndex: 1, part: "P1");
            CreateSingleFileCache(root, avid: 200, title: "另一个", pageIndex: 1, part: "P1");

            var viewModel = CreateViewModel(
                root,
                new StubSaveDialogService(null),
                new StubDialogService(outputDirectory));

            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SetSelectedCaches(viewModel.Items.ToList());
            await ExecuteCommandAsync(viewModel.ExportMp4Command);

            var exported = Directory.GetFiles(outputDirectory, "*.mp4")
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("带 非法 字符 的标题.mp4", exported);
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task ExportMp4_ShouldReportCancellation_WhenSaveDialogDismissed()
    {
        var root = CreateTempRoot();
        try
        {
            CreateSingleFileCache(root, avid: 100, title: "示例视频", pageIndex: 1, part: "P1");

            var viewModel = CreateViewModel(
                root,
                new StubSaveDialogService(null),
                new StubDialogService(null));

            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SetSelectedCaches(viewModel.Items.ToList());
            await ExecuteCommandAsync(viewModel.ExportMp4Command);

            Assert.Equal("已取消导出。", viewModel.StatusMessage);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExportMp4_ShouldBeDisabled_WhenNothingSelected()
    {
        var root = CreateTempRoot();
        try
        {
            CreateSingleFileCache(root, avid: 100, title: "示例视频", pageIndex: 1, part: "P1");

            var viewModel = CreateViewModel(
                root,
                new StubSaveDialogService(null),
                new StubDialogService(null));

            await ExecuteCommandAsync(viewModel.ScanCommand);

            Assert.False(viewModel.ExportMp4Command.CanExecute(null));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static MainViewModel CreateViewModel(
        string root,
        IFileSaveDialogService saveDialogService,
        IDialogService dialogService)
    {
        var playbackService = new CachePlaybackService();
        var viewModel = new MainViewModel(
            new CacheManager(),
            playbackService,
            dialogService,
            new NoOpHelpService(),
            new NoOpExplorerService(),
            fileSaveDialogService: saveDialogService,
            materializationService: playbackService);
        viewModel.RootPath = root;
        return viewModel;
    }

    private sealed class StubSaveDialogService(string? pickedPath) : IFileSaveDialogService
    {
        public string? LastSuggestedFileName { get; private set; }

        public string? PickSavePath(
            string title,
            string defaultFileName,
            string defaultExtension,
            string filter)
        {
            LastSuggestedFileName = defaultFileName;
            return pickedPath;
        }
    }

    private sealed class StubDialogService(string? pickedFolder) : IDialogService
    {
        public bool Confirm(string message, string title) => true;

        public string? PickFolder(string title, string? initialPath) => pickedFolder;
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
        var path = Path.Combine(Path.GetTempPath(), "blcm-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 构造旧版 lua 布局下的单个 .mp4 分段，物化时会直接返回源文件，不需要 FFmpeg。
    /// </summary>
    private static void CreateSingleFileCache(
        string root,
        long avid,
        string title,
        int pageIndex,
        string part)
    {
        var segmentDirectory = Path.Combine(
            root,
            avid.ToString(CultureInfo.InvariantCulture),
            pageIndex.ToString(CultureInfo.InvariantCulture));
        var mediaDirectory = Path.Combine(segmentDirectory, "lua.mp4.bapi.9");
        Directory.CreateDirectory(mediaDirectory);

        File.WriteAllText(Path.Combine(mediaDirectory, "0.mp4"), "video-payload");
        File.WriteAllText(
            Path.Combine(segmentDirectory, "entry.json"),
            $$"""
              {
                "is_completed": true,
                "total_bytes": 13,
                "downloaded_bytes": 13,
                "title": "{{title}}",
                "type_tag": "80",
                "cover": "cover",
                "prefered_video_quality": 80,
                "guessed_total_bytes": 13,
                "total_time_milli": 1000,
                "danmaku_count": 0,
                "time_update_stamp": 0,
                "time_create_stamp": 0,
                "avid": {{avid}},
                "spid": 0,
                "seasion_id": 0,
                "page_data": {
                  "cid": {{avid}},
                  "page": {{pageIndex}},
                  "from": "vupload",
                  "part": "{{part}}"
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
