using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelTranscodeCacheTests
{
    [Fact]
    public async Task CacheCommands_ShouldExposeStatisticsOpenDirectoryAndClearGeneratedFiles()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_vm_transcode_cache_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            store.GetOrCreate(
                CreatePlan(root, sourcePath),
                ".mp4",
                path => File.WriteAllBytes(path, new byte[32]));
            var explorer = new RecordingExplorerService();
            var dialog = new ConfirmingDialogService();
            var viewModel = new MainViewModel(
                new CacheManager(),
                new CachePlaybackService(store),
                dialog,
                new NoOpHelpService(),
                explorer,
                playbackArtifactStore: store);

            await viewModel.StartBackgroundTranscodeCacheMaintenance();

            Assert.Contains("1", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
            Assert.Contains("20 GB", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);

            viewModel.OpenTranscodeCacheCommand.Execute(null);
            Assert.Equal(store.RootDirectory, explorer.LastOpenedPath);

            viewModel.ClearTranscodeCacheCommand.Execute(null);
            await (viewModel.ClearTranscodeCacheCommand.ExecutionTask ?? Task.CompletedTask);

            Assert.Equal(1, dialog.ConfirmCallCount);
            Assert.Equal(0, store.GetStatistics().FileCount);
            Assert.Contains("0", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ClearCache_ShouldDoNothing_WhenUserDeclinesConfirmation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_vm_transcode_cache_cancel_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            store.GetOrCreate(
                CreatePlan(root, sourcePath),
                ".mp4",
                path => File.WriteAllBytes(path, new byte[32]));
            var viewModel = new MainViewModel(
                new CacheManager(),
                new CachePlaybackService(store),
                new ConfirmingDialogService { ConfirmationResult = false },
                new NoOpHelpService(),
                new RecordingExplorerService(),
                playbackArtifactStore: store);

            viewModel.ClearTranscodeCacheCommand.Execute(null);
            await (viewModel.ClearTranscodeCacheCommand.ExecutionTask ?? Task.CompletedTask);

            Assert.Equal(1, store.GetStatistics().FileCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CachePlaybackPlan CreatePlan(string root, string sourcePath)
    {
        return CachePlaybackPlan.Playable(
            100,
            "Title",
            1,
            "P1",
            "c_1",
            root,
            "LegacyBlv",
            CachePlaybackMaterialKind.SingleFile,
            new[] { sourcePath });
    }

    private sealed class ConfirmingDialogService : IDialogService
    {
        public bool ConfirmationResult { get; init; } = true;

        public int ConfirmCallCount { get; private set; }

        public bool Confirm(string message, string title)
        {
            ConfirmCallCount++;
            return ConfirmationResult;
        }

        public string? PickFolder(string title, string? initialPath) => null;
    }

    private sealed class RecordingExplorerService : IExplorerService
    {
        public string? LastOpenedPath { get; private set; }

        public void OpenFolder(string folderPath)
        {
            LastOpenedPath = folderPath;
        }
    }

    private sealed class NoOpHelpService : IHelpService
    {
        public void OpenHelp()
        {
        }
    }
}
