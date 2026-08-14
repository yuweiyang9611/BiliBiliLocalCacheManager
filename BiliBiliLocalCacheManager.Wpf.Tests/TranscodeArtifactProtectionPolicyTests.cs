using System.IO;
using System.Reflection;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;
using System.Windows.Media;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class TranscodeArtifactProtectionPolicyTests
{
    [Fact]
    public void Selection_CapsProtectionAtEightMostRecentExistingArtifacts()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = Enumerable.Range(0, 10)
            .Select(index => new MainViewModel.TranscodeArtifactProtectionCandidate(
                $"artifact-{index}",
                now - TimeSpan.FromMinutes(index),
                FileLength: 1))
            .ToArray();

        var selected = MainViewModel.SelectProtectedTranscodePathsForCleanup(
            candidates,
            now,
            maxProtectedBytes: long.MaxValue);

        Assert.Equal(MainViewModel.MaximumProtectedTranscodeArtifactCount, selected.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(index => $"artifact-{index}"), selected);
    }

    [Fact]
    public void Selection_DropsExpiredProtection()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new[]
        {
            new MainViewModel.TranscodeArtifactProtectionCandidate(
                "expired",
                now - MainViewModel.ProtectedTranscodeArtifactLifetime - TimeSpan.FromSeconds(1),
                1),
            new MainViewModel.TranscodeArtifactProtectionCandidate(
                "valid",
                now - MainViewModel.ProtectedTranscodeArtifactLifetime,
                1)
        };

        var selected = MainViewModel.SelectProtectedTranscodePathsForCleanup(
            candidates,
            now,
            maxProtectedBytes: long.MaxValue);

        Assert.Equal(["valid"], selected);
    }

    [Fact]
    public void Selection_UsesCapacityBudgetWithOnlyNewestArtifactAsException()
    {
        var now = DateTimeOffset.UtcNow;
        const long oneGigabyte = 1024L * 1024 * 1024;
        var newestOversized = new[]
        {
            new MainViewModel.TranscodeArtifactProtectionCandidate("newest", now, oneGigabyte * 2),
            new MainViewModel.TranscodeArtifactProtectionCandidate("older", now, 1)
        };
        var cumulative = new[]
        {
            new MainViewModel.TranscodeArtifactProtectionCandidate("newest", now, 600L * 1024 * 1024),
            new MainViewModel.TranscodeArtifactProtectionCandidate("next", now, 300L * 1024 * 1024),
            new MainViewModel.TranscodeArtifactProtectionCandidate("would-overflow", now, 200L * 1024 * 1024),
            new MainViewModel.TranscodeArtifactProtectionCandidate("older-small", now, 1)
        };

        Assert.Equal(
            ["newest"],
            MainViewModel.SelectProtectedTranscodePathsForCleanup(
                newestOversized,
                now,
                oneGigabyte));
        Assert.Equal(
            ["newest", "next"],
            MainViewModel.SelectProtectedTranscodePathsForCleanup(
                cumulative,
                now,
                oneGigabyte));
    }

    [Fact]
    public void ReplayingEvictedArtifact_RefreshesItToMostRecentProtection()
    {
        using var directory = new TemporaryDirectory();
        var store = new PlaybackArtifactStore(directory.Path);
        var viewModel = CreateViewModel(store);
        var paths = Enumerable.Range(0, 9)
            .Select(index => CreateArtifact(directory.Path, index))
            .ToArray();
        foreach (var path in paths)
        {
            Assert.True(InvokeProtect(viewModel, path));
        }

        Assert.True(InvokeProtect(viewModel, paths[0]));
        var options = InvokeCreateCleanupOptions(viewModel);

        Assert.Equal(MainViewModel.MaximumProtectedTranscodeArtifactCount, options.ProtectedPaths.Count);
        Assert.Equal(
            new[] { paths[0], paths[8], paths[7], paths[6], paths[5], paths[4], paths[3], paths[2] },
            options.ProtectedPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(paths[1], options.ProtectedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clear_WhenBytesRemain_ReportsIncompleteClearAsError()
    {
        var store = new IncompleteClearArtifactStore();
        var viewModel = CreateViewModel(store);

        viewModel.ClearTranscodeCacheCommand.Execute(null);
        await (viewModel.ClearTranscodeCacheCommand.ExecutionTask ??
            throw new InvalidOperationException("Clear command task was not created."));

        Assert.Contains("未完全清空", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("剩余容量", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("已清空：", viewModel.StatusMessage, StringComparison.Ordinal);
        var brush = Assert.IsType<SolidColorBrush>(viewModel.StatusBrush);
        Assert.Equal(Color.FromRgb(192, 0, 0), brush.Color);
    }

    private static MainViewModel CreateViewModel(IPlaybackArtifactStore store)
    {
        return new MainViewModel(
            new CacheManager(),
            new CachePlaybackService(store),
            new ConfirmingDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService(),
            playbackArtifactStore: store);
    }

    private static string CreateArtifact(string root, int index)
    {
        var path = Path.Combine(root, "100", $"artifact-{index}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"artifact-{index}");
        return Path.GetFullPath(path);
    }

    private static bool InvokeProtect(MainViewModel viewModel, string path)
    {
        var method = typeof(MainViewModel).GetMethod(
            "ProtectManagedTranscodeArtifact",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(
            viewModel,
            [PlaybackLaunchResult.Success("started", "test", path)]));
    }

    private static PlaybackArtifactCleanupOptions InvokeCreateCleanupOptions(
        MainViewModel viewModel)
    {
        var method = typeof(MainViewModel).GetMethod(
            "CreateTranscodeCacheCleanupOptions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<PlaybackArtifactCleanupOptions>(method.Invoke(viewModel, null));
    }

    private sealed class IncompleteClearArtifactStore : IPlaybackArtifactStore
    {
        public string RootDirectory { get; } = Path.GetTempPath();

        public PlaybackArtifactMaterialization GetOrCreate(
            CachePlaybackPlan plan,
            string extension,
            Action<string> producer,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PlaybackArtifactCacheStatistics GetStatistics() =>
            new(RootDirectory, FileCount: 2, TotalBytes: 2 * 1024 * 1024);

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null) =>
            new(0, 0, 2 * 1024 * 1024);

        public PlaybackArtifactCleanupResult Cleanup(
            PlaybackArtifactCleanupOptions? options = null) =>
            new(0, 0, 0, 2 * 1024 * 1024);

        public PlaybackArtifactCleanupResult Clear() =>
            new(
                DeletedFileCount: 1,
                FreedBytes: 1024 * 1024,
                FailedFileCount: 0,
                RemainingBytes: 1024 * 1024,
                Statistics: new PlaybackArtifactCacheStatistics(
                    RootDirectory,
                    FileCount: 1,
                    TotalBytes: 1024 * 1024));
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"BiliBiliLocalCacheManager.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }
}
