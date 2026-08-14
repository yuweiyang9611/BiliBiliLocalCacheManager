using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Commands;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelPostPlaybackMaintenanceTests
{
    [Fact]
    public async Task SuccessfulManagedPlayback_ShouldProtectArtifact_ThenRequestIdleCleanup()
    {
        var cacheRoot = CreateTempRoot("cache");
        using var artifactStore = new RecordingArtifactStore();
        try
        {
            CreateDashEntry(cacheRoot);
            var managedArtifactPath = Path.Combine(
                artifactStore.RootDirectory,
                "100",
                "Page_1",
                "0123456789abcdef01234567.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(managedArtifactPath)!);
            File.WriteAllText(managedArtifactPath, "managed playback artifact");
            var playbackService = new ControlledPlaybackService(managedArtifactPath);
            var viewModel = CreateViewModel(playbackService, artifactStore);
            viewModel.RootPath = cacheRoot;
            await ExecuteCommandAsync(viewModel.ScanCommand);
            viewModel.SelectedItem = Assert.Single(viewModel.Items);
            viewModel.SelectedSegmentDetail = Assert.Single(viewModel.SegmentDetails);

            viewModel.PlaySelectedPageCommand.Execute(null);
            Assert.True(playbackService.PlayStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, artifactStore.CleanupCallCount);

            playbackService.CompletePlayback();
            await (viewModel.PlaySelectedPageCommand.ExecutionTask ??
                throw new InvalidOperationException("Playback command task was not created."));
            Assert.True(artifactStore.CleanupCompleted.Wait(TimeSpan.FromSeconds(5)));

            var cleanupOptions = Assert.IsType<PlaybackArtifactCleanupOptions>(
                artifactStore.LastCleanupOptions);
            Assert.Contains(
                Path.GetFullPath(managedArtifactPath),
                cleanupOptions.ProtectedPaths,
                StringComparer.OrdinalIgnoreCase);
            Assert.Contains("已开始播放", viewModel.StatusMessage, StringComparison.Ordinal);
            Assert.True(viewModel.PlaySelectedPageCommand.CanExecute(null));

            Assert.True(SpinWait.SpinUntil(
                () => !viewModel.IsTranscodeCacheMaintenanceBusy,
                TimeSpan.FromSeconds(5)));
            await ExecuteCommandAsync(viewModel.ClearTranscodeCacheCommand);

            Assert.Equal(1, artifactStore.ClearCallCount);
        }
        finally
        {
            SafeDeleteDirectory(cacheRoot);
        }
    }

    private static MainViewModel CreateViewModel(
        ICachePlaybackService playbackService,
        IPlaybackArtifactStore artifactStore)
    {
        return new MainViewModel(
            new CacheManager(),
            playbackService,
            new ConfirmingDialogService(),
            new NoOpHelpService(),
            new NoOpExplorerService(),
            playbackArtifactStore: artifactStore);
    }

    private static async Task ExecuteCommandAsync(AsyncRelayCommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await (command.ExecutionTask ?? throw new InvalidOperationException("Command task was not created."));
    }

    private static void CreateDashEntry(string root)
    {
        var segment = Path.Combine(root, "100", "c_1");
        var quality = Path.Combine(segment, "80");
        Directory.CreateDirectory(quality);
        File.WriteAllText(
            Path.Combine(segment, "entry.json"),
            $$"""
              {
                "is_completed": true,
                "total_bytes": 10,
                "downloaded_bytes": 10,
                "title": "Title 100",
                "type_tag": "80",
                "cover": "cover",
                "prefered_video_quality": 80,
                "guessed_total_bytes": 10,
                "total_time_milli": 1000,
                "danmaku_count": 0,
                "time_update_stamp": 0,
                "time_create_stamp": 0,
                "avid": 100,
                "spid": 0,
                "seasion_id": 0,
                "page_data": {
                  "cid": 1,
                  "page": 1,
                  "from": "local",
                  "part": "P1",
                  "vid": "",
                  "has_alias": false,
                  "tid": 0
                }
              }
              """);
        File.WriteAllText(Path.Combine(quality, "video.m4s"), "video");
        File.WriteAllText(Path.Combine(quality, "audio.m4s"), "audio");
    }

    private static string CreateTempRoot(string suffix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"bili_vm_post_playback_{suffix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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
            // Ignore test cleanup failures.
        }
    }

    private sealed class ControlledPlaybackService(string managedArtifactPath) : ICachePlaybackService
    {
        private readonly CachePlaybackService _inner = new();
        private readonly TaskCompletionSource<PlaybackLaunchResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim PlayStarted { get; } = new(false);

        public CachePlaybackPlan CreatePlan(BiliSegment segment) => _inner.CreatePlan(segment);

        public CachePlaybackPlan CreatePlan(BiliVideoCache cache, string? segmentKey = null) =>
            _inner.CreatePlan(cache, segmentKey);

        public CachePlaybackPagePlan CreatePagePlan(BiliVideoCache cache, string? segmentKey = null) =>
            _inner.CreatePagePlan(cache, segmentKey);

        public IReadOnlyList<CachePlaybackPagePlan> CreatePagePlans(BiliVideoCache cache) =>
            _inner.CreatePagePlans(cache);

        public PlaybackLaunchResult Play(BiliSegment segment, PlaybackLaunchOptions? launchOptions = null) =>
            throw new NotSupportedException();

        public PlaybackLaunchResult Play(
            BiliVideoCache cache,
            string? segmentKey = null,
            PlaybackLaunchOptions? launchOptions = null) =>
            throw new NotSupportedException();

        public Task<PlaybackLaunchResult> PlayAsync(
            BiliVideoCache cache,
            string? segmentKey = null,
            PlaybackLaunchOptions? launchOptions = null,
            IProgress<PlaybackPreparationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            PlayStarted.Set();
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void CompletePlayback()
        {
            _completion.TrySetResult(PlaybackLaunchResult.Success(
                "Started",
                "TestPlayer",
                managedArtifactPath));
        }
    }

    private sealed class RecordingArtifactStore : IPlaybackArtifactStore, IDisposable
    {
        private int _cleanupCallCount;
        private int _clearCallCount;

        public RecordingArtifactStore()
        {
            RootDirectory = CreateTempRoot("artifacts");
        }

        public string RootDirectory { get; }

        public ManualResetEventSlim CleanupCompleted { get; } = new(false);

        public int CleanupCallCount => Volatile.Read(ref _cleanupCallCount);

        public int ClearCallCount => Volatile.Read(ref _clearCallCount);

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
            return new PlaybackArtifactCacheStatistics(RootDirectory, 1, 64);
        }

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null)
        {
            return new PlaybackArtifactCleanupPreview(0, 0, 64);
        }

        public PlaybackArtifactCleanupResult Cleanup(PlaybackArtifactCleanupOptions? options = null)
        {
            LastCleanupOptions = options;
            Interlocked.Increment(ref _cleanupCallCount);
            CleanupCompleted.Set();
            return new PlaybackArtifactCleanupResult(0, 0, 0, 64);
        }

        public PlaybackArtifactCleanupResult Clear()
        {
            Interlocked.Increment(ref _clearCallCount);
            return new PlaybackArtifactCleanupResult(1, 64, 0, 0);
        }

        public void Dispose()
        {
            CleanupCompleted.Dispose();
            SafeDeleteDirectory(RootDirectory);
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
