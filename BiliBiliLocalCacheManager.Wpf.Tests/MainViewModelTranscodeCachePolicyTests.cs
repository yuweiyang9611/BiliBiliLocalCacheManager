using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelTranscodeCachePolicyTests
{
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    [Fact]
    public async Task CleanupCommand_ShouldUseLoadedPolicyAndSettingsChangesShouldPersist()
    {
        var store = new RecordingArtifactStore();
        var settingsService = new RecordingSettingsService(new AppSettings
        {
            TranscodeCacheRetentionDays = 7,
            TranscodeCacheMaxSizeGigabytes = 8
        });
        var viewModel = CreateViewModel(store, settingsService);

        Assert.Equal(7, viewModel.TranscodeCacheRetentionDays);
        Assert.Equal(8, viewModel.TranscodeCacheMaxSizeGigabytes);
        Assert.Contains("\u4fdd\u7559 7 \u5929", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Contains("8 GB", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);

        viewModel.CleanupTranscodeCacheCommand.Execute(null);
        await (viewModel.CleanupTranscodeCacheCommand.ExecutionTask ?? Task.CompletedTask);

        var cleanupOptions = Assert.IsType<PlaybackArtifactCleanupOptions>(
            store.LastCleanupOptions);
        Assert.Equal(TimeSpan.FromDays(7), cleanupOptions.MaxAge);
        Assert.Equal(8L * BytesPerGigabyte, cleanupOptions.MaxTotalBytes);

        viewModel.TranscodeCacheRetentionDays = 14;
        viewModel.TranscodeCacheMaxSizeGigabytes = 64;

        Assert.NotNull(settingsService.LastSaved);
        Assert.Equal(14, settingsService.LastSaved.TranscodeCacheRetentionDays);
        Assert.Equal(64, settingsService.LastSaved.TranscodeCacheMaxSizeGigabytes);
        Assert.Contains("\u4fdd\u7559 14 \u5929", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Contains("64 GB", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyProperties_ShouldRejectOutOfRangeValuesWithChineseMessages()
    {
        var viewModel = CreateViewModel(
            new RecordingArtifactStore(),
            new RecordingSettingsService(new AppSettings()));

        var retentionException = Assert.Throws<ArgumentOutOfRangeException>(
            () => viewModel.TranscodeCacheRetentionDays = 0);
        var sizeException = Assert.Throws<ArgumentOutOfRangeException>(
            () => viewModel.TranscodeCacheMaxSizeGigabytes = 0);

        Assert.Contains("\u4fdd\u7559\u5929\u6570", retentionException.Message, StringComparison.Ordinal);
        Assert.Contains("\u5bb9\u91cf\u4e0a\u9650", sizeException.Message, StringComparison.Ordinal);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultRetentionDays,
            viewModel.TranscodeCacheRetentionDays);
        Assert.Equal(
            PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes,
            viewModel.TranscodeCacheMaxSizeGigabytes);
    }

    private static MainViewModel CreateViewModel(
        IPlaybackArtifactStore store,
        IAppSettingsService settingsService)
    {
        return new MainViewModel(
            new CacheManager(),
            new CachePlaybackService(store),
            new ConfirmingDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService(),
            settingsService: settingsService,
            playbackArtifactStore: store);
    }

    private sealed class RecordingSettingsService(AppSettings settings) : IAppSettingsService
    {
        public AppSettings? LastSaved { get; private set; }

        public AppSettings Load() => settings;

        public void Save(AppSettings value)
        {
            LastSaved = value;
        }
    }

    private sealed class RecordingArtifactStore : IPlaybackArtifactStore
    {
        public string RootDirectory { get; } = Path.GetTempPath();

        public PlaybackArtifactCleanupOptions? LastCleanupOptions { get; private set; }

        public PlaybackArtifactMaterialization GetOrCreate(
            CachePlaybackPlan plan,
            string extension,
            Action<string> producer,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public PlaybackArtifactCacheStatistics GetStatistics()
        {
            return new PlaybackArtifactCacheStatistics(RootDirectory, 2, 123);
        }

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null)
        {
            return new PlaybackArtifactCleanupPreview(0, 0, 123);
        }

        public PlaybackArtifactCleanupResult Cleanup(
            PlaybackArtifactCleanupOptions? options = null)
        {
            LastCleanupOptions = options;
            return new PlaybackArtifactCleanupResult(0, 0, 0, 123);
        }

        public PlaybackArtifactCleanupResult Clear()
        {
            return new PlaybackArtifactCleanupResult(2, 123, 0, 0);
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
