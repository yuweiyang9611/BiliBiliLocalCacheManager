using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;
using Xunit;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

/// <summary>
/// 彻底清空回收站后，撤销状态应当只失效真正被清理掉的条目。
/// </summary>
public sealed class MainViewModelTrashPurgeUndoTests
{
    [Fact]
    public async Task PurgeTrash_ShouldKeepUndoAvailable_ForEntriesThatSurvivedPurge()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, title: "First");
            CreateEntry(root, avid: 200, title: "Second");

            // 只清理第一条，第二条保留在回收站里。
            var trashService = new SelectivePurgeTrashService(
                purgeSelector: moved => moved.Take(1).ToArray());
            var viewModel = CreateViewModel(trashService);
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.ScanCommand);
            Assert.Equal(2, viewModel.Items.Count);

            viewModel.SetSelectedCaches(viewModel.Items.ToList());
            await ExecuteCommandAsync(viewModel.DeleteCommand);

            Assert.Equal(2, trashService.MovedTrashPaths.Count);
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            // 被清理的那条不能再撤销，另一条仍然可以。
            Assert.True(
                viewModel.UndoDeleteCommand.CanExecute(null),
                "未被清理的回收站条目应当仍然可以撤销。");

            await ExecuteCommandAsync(viewModel.UndoDeleteCommand);

            var survivingAvid = trashService.SurvivingAvid;
            Assert.True(
                Directory.Exists(Path.Combine(root, survivingAvid.ToString(CultureInfo.InvariantCulture))),
                $"未被清理的 avid={survivingAvid} 应当被成功还原。");
            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldDisableUndo_WhenEveryEntryWasPurged()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 300, title: "Only");

            var trashService = new SelectivePurgeTrashService(purgeSelector: moved => moved);
            var viewModel = CreateViewModel(trashService);
            viewModel.RootPath = root;

            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = viewModel.Items[0];
            await ExecuteCommandAsync(viewModel.DeleteCommand);
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));

            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.False(viewModel.UndoDeleteCommand.CanExecute(null));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PurgeTrash_ShouldLeaveUndoIntact_WhenPurgingDifferentRoot()
    {
        var deletedRoot = CreateTempRoot();
        var purgedRoot = CreateTempRoot();
        try
        {
            CreateEntry(deletedRoot, avid: 400, title: "Kept");

            var trashService = new SelectivePurgeTrashService(purgeSelector: moved => moved);
            var viewModel = CreateViewModel(trashService);
            viewModel.RootPath = deletedRoot;

            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = viewModel.Items[0];
            await ExecuteCommandAsync(viewModel.DeleteCommand);
            Assert.True(viewModel.UndoDeleteCommand.CanExecute(null));

            // 切换到另一个根目录再清空回收站，不应影响原根目录的撤销状态。
            viewModel.RootPath = purgedRoot;
            await ExecuteCommandAsync(viewModel.PurgeTrashCommand);

            Assert.True(
                viewModel.UndoDeleteCommand.CanExecute(null),
                "清空的是另一个根目录的回收站，原有撤销状态不应被清除。");
        }
        finally
        {
            SafeDeleteDirectory(deletedRoot);
            SafeDeleteDirectory(purgedRoot);
        }
    }

    /// <summary>
    /// 包装真实回收站服务，但由测试决定 Purge 实际清理哪些条目，
    /// 以便构造“部分条目被清理、部分保留”的场景。
    /// </summary>
    private sealed class SelectivePurgeTrashService : ICacheTrashService
    {
        private readonly FileSystemCacheTrashService _inner = new();
        private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>> _purgeSelector;
        private readonly List<string> _moved = new();
        private readonly Dictionary<string, long> _avidByTrashPath =
            new(StringComparer.OrdinalIgnoreCase);

        public SelectivePurgeTrashService(
            Func<IReadOnlyList<string>, IReadOnlyList<string>> purgeSelector)
        {
            _purgeSelector = purgeSelector;
        }

        public IReadOnlyList<string> MovedTrashPaths => _moved;

        public long SurvivingAvid { get; private set; }

        public string GetTrashDirectory(string rootDirectory) =>
            _inner.GetTrashDirectory(rootDirectory);

        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid)
        {
            var result = _inner.MoveToTrash(rootDirectory, avid);
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.TrashPath))
            {
                _moved.Add(result.TrashPath);
                _avidByTrashPath[result.TrashPath] = avid;
            }

            return result;
        }

        public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath) =>
            _inner.Restore(rootDirectory, avid, trashPath);

        public CacheTrashStatistics GetStatistics(
            string rootDirectory,
            CancellationToken cancellationToken = default) =>
            _inner.GetStatistics(rootDirectory, cancellationToken);

        public IReadOnlyList<CacheTrashEntry> ListEntries(
            string rootDirectory,
            CancellationToken cancellationToken = default) =>
            _inner.ListEntries(rootDirectory, cancellationToken);

        public CacheTrashPurgeResult Purge(
            string rootDirectory,
            bool includeUntrustedLegacyEntries = false)
        {
            var trashRoot = Path.GetFullPath(_inner.GetTrashDirectory(rootDirectory));
            // 只清理属于当前根目录的条目，模拟真实 Purge 的作用范围。
            var scoped = _moved
                .Where(path => Path.GetFullPath(path)
                    .StartsWith(trashRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var purged = _purgeSelector(scoped);

            foreach (var path in purged)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }

            var survivors = scoped.Except(purged, StringComparer.OrdinalIgnoreCase).ToList();
            if (survivors.Count > 0 && _avidByTrashPath.TryGetValue(survivors[0], out var avid))
            {
                SurvivingAvid = avid;
            }

            return new CacheTrashPurgeResult(
                DeletedEntryCount: purged.Count,
                FreedBytes: 0,
                FailedEntryCount: 0,
                SkippedEntryCount: survivors.Count,
                FirstErrorMessage: null,
                PartiallyDeletedEntryCount: 0,
                PendingPurgeEntryCount: 0,
                NonRestorableTrashPaths: purged.Select(Path.GetFullPath).ToArray());
        }
    }

    private static MainViewModel CreateViewModel(ICacheTrashService trashService)
    {
        return new MainViewModel(
            new CacheManager(),
            new CachePlaybackService(),
            new ConfirmingDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService(),
            trashService);
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
        var path = Path.Combine(Path.GetTempPath(), "blcm-purge-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateEntry(string root, long avid, string title)
    {
        var segmentDir = Path.Combine(
            root,
            avid.ToString(CultureInfo.InvariantCulture),
            "c_1");
        Directory.CreateDirectory(segmentDir);
        File.WriteAllText(
            Path.Combine(segmentDir, "entry.json"),
            $$"""
            {
              "media_type": 2,
              "has_dash_audio": false,
              "is_completed": true,
              "total_bytes": 5,
              "downloaded_bytes": 5,
              "title": "{{title}}",
              "type_tag": "80",
              "avid": {{avid}},
              "page_data": { "cid": 1, "page": 1, "part": "P1" }
            }
            """);
        File.WriteAllText(Path.Combine(segmentDir, "1.mp4"), "dummy");
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
