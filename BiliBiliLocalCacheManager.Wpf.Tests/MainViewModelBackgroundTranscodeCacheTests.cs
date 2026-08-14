using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainViewModelBackgroundTranscodeCacheTests
{
    [Fact]
    public void Constructor_ShouldNotInspectOrCleanupTranscodeCache()
    {
        using var store = new BlockingArtifactStore();

        var viewModel = CreateViewModel(store);

        Assert.Equal(0, store.CleanupCallCount);
        Assert.Equal(0, store.StatisticsCallCount);
        Assert.False(viewModel.IsTranscodeCacheMaintenanceBusy);
        Assert.Contains("等待后台检查", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupMaintenance_ShouldRunInBackground_WithoutUsingGlobalBusyOrStatus()
    {
        using var store = new BlockingArtifactStore();
        var viewModel = CreateViewModel(store);
        var originalStatus = viewModel.StatusMessage;

        var maintenanceTask = viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.True(store.CleanupStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(maintenanceTask.IsCompleted);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.ScanCommand.CanExecute(null));
        Assert.Equal(originalStatus, viewModel.StatusMessage);
        Assert.True(viewModel.IsTranscodeCacheMaintenanceBusy);

        store.ReleaseCleanup.Set();
        await maintenanceTask;

        Assert.False(viewModel.IsTranscodeCacheMaintenanceBusy);
        Assert.Equal(1, store.CleanupCallCount);
        Assert.Contains("2", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
    }

    [Fact]
    public async Task AutomaticMaintenance_ShouldCoalescePendingRequests_AndNeverRunConcurrently()
    {
        using var store = new BlockingArtifactStore();
        var viewModel = CreateViewModel(store);

        var firstTask = viewModel.StartBackgroundTranscodeCacheMaintenance();
        Assert.True(store.CleanupStarted.Wait(TimeSpan.FromSeconds(5)));

        var secondTask = viewModel.StartBackgroundTranscodeCacheMaintenance();
        var thirdTask = viewModel.StartBackgroundTranscodeCacheMaintenance();
        Assert.Same(firstTask, secondTask);
        Assert.Same(firstTask, thirdTask);

        store.ReleaseCleanup.Set();
        await firstTask;

        Assert.Equal(2, store.CleanupCallCount);
        Assert.Equal(1, store.MaximumConcurrentCleanupCount);
    }

    [Fact]
    public async Task AutomaticMaintenance_ShouldReportFailureInSummary_AndAllowRetry()
    {
        using var store = new BlockingArtifactStore
        {
            BlockCleanup = false,
            ThrowOnCleanupCall = 1
        };
        var viewModel = CreateViewModel(store);
        var originalStatus = viewModel.StatusMessage;

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Contains("失败", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
        Assert.False(viewModel.IsTranscodeCacheMaintenanceBusy);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(2, store.CleanupCallCount);
        Assert.DoesNotContain("失败", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
        Assert.Contains("2", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticMaintenance_ShouldReuseStatisticsReturnedByCleanup()
    {
        using var store = new BlockingArtifactStore
        {
            BlockCleanup = false,
            ReturnCleanupStatistics = true
        };
        var viewModel = CreateViewModel(store);

        await viewModel.StartBackgroundTranscodeCacheMaintenance();

        Assert.Equal(1, store.CleanupCallCount);
        Assert.Equal(0, store.StatisticsCallCount);
        Assert.Contains("2", viewModel.TranscodeCacheSummary, StringComparison.Ordinal);
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

    private sealed class BlockingArtifactStore : IPlaybackArtifactStore, IDisposable
    {
        private int _cleanupCallCount;
        private int _concurrentCleanupCount;
        private int _maximumConcurrentCleanupCount;
        private int _statisticsCallCount;

        public BlockingArtifactStore()
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"bili_vm_background_cleanup_{Guid.NewGuid():N}");
        }

        public string RootDirectory { get; }

        public ManualResetEventSlim CleanupStarted { get; } = new(false);

        public ManualResetEventSlim ReleaseCleanup { get; } = new(false);

        public bool BlockCleanup { get; init; } = true;

        public int ThrowOnCleanupCall { get; init; }

        public bool ReturnCleanupStatistics { get; init; }

        public int CleanupCallCount => Volatile.Read(ref _cleanupCallCount);

        public int MaximumConcurrentCleanupCount => Volatile.Read(ref _maximumConcurrentCleanupCount);

        public int StatisticsCallCount => Volatile.Read(ref _statisticsCallCount);

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
            Interlocked.Increment(ref _statisticsCallCount);
            return new PlaybackArtifactCacheStatistics(RootDirectory, 2, 128);
        }

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null)
        {
            return new PlaybackArtifactCleanupPreview(0, 0, 128);
        }

        public PlaybackArtifactCleanupResult Cleanup(PlaybackArtifactCleanupOptions? options = null)
        {
            var call = Interlocked.Increment(ref _cleanupCallCount);
            var concurrent = Interlocked.Increment(ref _concurrentCleanupCount);
            UpdateMaximumConcurrentCount(concurrent);
            CleanupStarted.Set();
            try
            {
                if (BlockCleanup && !ReleaseCleanup.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Timed out waiting for the cleanup test gate.");
                }

                if (call == ThrowOnCleanupCall)
                {
                    throw new IOException("Injected cleanup failure.");
                }

                return new PlaybackArtifactCleanupResult(
                    0,
                    0,
                    0,
                    128,
                    ReturnCleanupStatistics
                        ? new PlaybackArtifactCacheStatistics(RootDirectory, 2, 128)
                        : null);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCleanupCount);
            }
        }

        public PlaybackArtifactCleanupResult Clear()
        {
            return new PlaybackArtifactCleanupResult(2, 128, 0, 0);
        }

        public void Dispose()
        {
            CleanupStarted.Dispose();
            ReleaseCleanup.Dispose();
        }

        private void UpdateMaximumConcurrentCount(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentCleanupCount);
                if (current >= value ||
                    Interlocked.CompareExchange(ref _maximumConcurrentCleanupCount, value, current) == current)
                {
                    return;
                }
            }
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
