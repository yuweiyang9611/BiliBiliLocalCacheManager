using System.IO;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Services;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class StorageOverviewServiceTests
{
    [Fact]
    public void GetSnapshot_ShouldCombineActualManagedAndReclaimableBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-overview-root");
        var original = new RecordingStorageStatisticsService(new CacheStorageStatistics(
            Path.GetFullPath(root), 2, 4, 100, 0, 1, null));
        var trash = new RecordingTrashService(new CacheTrashStatistics(
            Path.Combine(root, ".trash"), 1, 2, 20, 0, 1, null));
        var artifacts = new RecordingArtifactStore(
            new PlaybackArtifactCacheStatistics("artifacts", 3, 30),
            new PlaybackArtifactCleanupPreview(1, 10, 20));
        var service = new StorageOverviewService(original, trash, artifacts);

        var result = service.GetSnapshot(root, new PlaybackArtifactCleanupOptions());

        Assert.Equal(150, result.ManagedTotalBytes);
        Assert.Equal(30, result.ReclaimableBytes);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Errors);
        Assert.Same(original.Result, result.OriginalCache);
        Assert.Same(trash.Result, result.Trash);
        Assert.Equal(1, original.CallCount);
        Assert.Equal(1, trash.StatisticsCallCount);
    }

    [Fact]
    public void GetSnapshot_ShouldPreserveAvailableSourcesAndReportPartialFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-overview-partial-root");
        var original = new RecordingStorageStatisticsService(new CacheStorageStatistics(
            Path.GetFullPath(root), 2, 2, 40, 1, 0, "blocked"));
        var trash = new RecordingTrashService(null) { ThrowOnStatistics = true };
        var artifacts = new RecordingArtifactStore(
            new PlaybackArtifactCacheStatistics("artifacts", 1, 12),
            new PlaybackArtifactCleanupPreview(1, 5, 7));
        var service = new StorageOverviewService(original, trash, artifacts);

        var result = service.GetSnapshot(root, new PlaybackArtifactCleanupOptions());

        Assert.False(result.IsComplete);
        Assert.Equal(52, result.ManagedTotalBytes);
        Assert.Equal(5, result.ReclaimableBytes);
        Assert.NotNull(result.OriginalCache);
        Assert.Null(result.Trash);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, item => item.Contains("原始缓存", StringComparison.Ordinal));
        Assert.Contains(result.Errors, item => item.Contains("应用回收站", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshTranscode_ShouldReuseOriginalAndTrashWithoutCallingCoreAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-overview-light-root");
        var original = new RecordingStorageStatisticsService(new CacheStorageStatistics(
            Path.GetFullPath(root), 1, 1, 100, 0, 0, null));
        var trash = new RecordingTrashService(new CacheTrashStatistics(
            Path.Combine(root, ".trash"), 1, 1, 20, 0, 0, null));
        var artifacts = new RecordingArtifactStore(
            new PlaybackArtifactCacheStatistics("artifacts", 2, 30),
            new PlaybackArtifactCleanupPreview(1, 10, 20));
        var service = new StorageOverviewService(original, trash, artifacts);
        var initial = service.GetSnapshot(root, new PlaybackArtifactCleanupOptions());

        artifacts.Statistics = new PlaybackArtifactCacheStatistics("artifacts", 1, 8);
        artifacts.Preview = new PlaybackArtifactCleanupPreview(0, 0, 8);
        var refreshed = service.RefreshTranscode(
            initial,
            new PlaybackArtifactCleanupOptions());

        Assert.Equal(1, original.CallCount);
        Assert.Equal(1, trash.StatisticsCallCount);
        Assert.Same(initial.OriginalCache, refreshed.OriginalCache);
        Assert.Same(initial.Trash, refreshed.Trash);
        Assert.Equal(128, refreshed.ManagedTotalBytes);
        Assert.Equal(20, refreshed.ReclaimableBytes);
        Assert.Equal(8, refreshed.TranscodeCache?.TotalBytes);
    }

    [Fact]
    public void GetSnapshot_ShouldReportTranscodeStatisticsFailureInsteadOfPublishingCompleteZero()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-overview-transcode-failure-root");
        var original = new RecordingStorageStatisticsService(new CacheStorageStatistics(
            Path.GetFullPath(root), 1, 1, 10, 0, 0, null));
        var trash = new RecordingTrashService(new CacheTrashStatistics(
            Path.Combine(root, ".trash"), 0, 0, 0, 0, 0, null));
        var artifacts = new RecordingArtifactStore(
            new PlaybackArtifactCacheStatistics("artifacts", 0, 0),
            new PlaybackArtifactCleanupPreview(0, 0, 0))
        {
            ThrowOnStatistics = true
        };
        var service = new StorageOverviewService(original, trash, artifacts);

        var result = service.GetSnapshot(root, new PlaybackArtifactCleanupOptions());

        Assert.False(result.IsComplete);
        Assert.Null(result.TranscodeCache);
        Assert.Contains(
            result.Errors,
            error => error.Contains("转码缓存统计失败", StringComparison.Ordinal));
    }

    [Fact]
    public void GetSnapshot_ShouldIncludeManagedBuildFilesInTotalAndCleanupPreview()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storage-overview-build-root-{Guid.NewGuid():N}");
        var artifactRoot = Path.Combine(root, "artifacts");
        var page = Path.Combine(artifactRoot, "100", "Page_1");
        Directory.CreateDirectory(page);
        var fingerprint = "0123456789abcdef01234567";
        var staleBuild = Path.Combine(
            page,
            $"{fingerprint}.building-{Guid.NewGuid():N}.mp4");
        var recentBuild = Path.Combine(
            page,
            $"{fingerprint}.building-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(staleBuild, new byte[13]);
        File.WriteAllBytes(recentBuild, new byte[17]);
        File.SetLastWriteTimeUtc(staleBuild, DateTime.UtcNow.AddHours(-2));
        try
        {
            var original = new RecordingStorageStatisticsService(new CacheStorageStatistics(
                Path.GetFullPath(root), 0, 0, 0, 0, 0, null));
            var trash = new RecordingTrashService(new CacheTrashStatistics(
                Path.Combine(root, ".trash"), 0, 0, 0, 0, 0, null));
            var service = new StorageOverviewService(
                original,
                trash,
                new PlaybackArtifactStore(artifactRoot));

            var result = service.GetSnapshot(root, new PlaybackArtifactCleanupOptions());

            Assert.Equal(2, result.TranscodeCache?.FileCount);
            Assert.Equal(30, result.TranscodeCache?.TotalBytes);
            Assert.Equal(13, result.TranscodeCleanupPreview?.ReclaimableBytes);
            Assert.Equal(17, result.TranscodeCleanupPreview?.RemainingBytes);
            Assert.Equal(30, result.ManagedTotalBytes);
            Assert.Equal(13, result.ReclaimableBytes);
            Assert.True(result.IsComplete);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingStorageStatisticsService(CacheStorageStatistics result) :
        ICacheStorageStatisticsService
    {
        public CacheStorageStatistics Result { get; } = result;

        public int CallCount { get; private set; }

        public CacheStorageStatistics GetStatistics(
            string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Result;
        }
    }

    private sealed class RecordingTrashService(CacheTrashStatistics? result) : ICacheTrashService
    {
        public CacheTrashStatistics? Result { get; } = result;

        public bool ThrowOnStatistics { get; init; }

        public int StatisticsCallCount { get; private set; }

        public string GetTrashDirectory(string rootDirectory) => Path.Combine(rootDirectory, ".trash");

        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid) =>
            throw new NotSupportedException();

        public CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath) =>
            throw new NotSupportedException();

        public IReadOnlyList<CacheTrashEntry> ListEntries(
            string rootDirectory,
            CancellationToken cancellationToken = default) =>
            Array.Empty<CacheTrashEntry>();

        public CacheTrashStatistics GetStatistics(
            string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatisticsCallCount++;
            if (ThrowOnStatistics)
            {
                throw new IOException("Injected trash statistics failure.");
            }

            return Result ?? throw new InvalidOperationException("A result was not configured.");
        }

        public CacheTrashPurgeResult Purge(
            string rootDirectory,
            bool includeUntrustedLegacyEntries = false) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingArtifactStore(
        PlaybackArtifactCacheStatistics statistics,
        PlaybackArtifactCleanupPreview preview) : IPlaybackArtifactStore
    {
        public string RootDirectory => Statistics.RootDirectory;

        public PlaybackArtifactCacheStatistics Statistics { get; set; } = statistics;

        public PlaybackArtifactCleanupPreview Preview { get; set; } = preview;

        public bool ThrowOnStatistics { get; init; }

        public PlaybackArtifactMaterialization GetOrCreate(
            CachePlaybackPlan plan,
            string extension,
            Action<string> producer,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public PlaybackArtifactCacheStatistics GetStatistics()
        {
            if (ThrowOnStatistics)
            {
                throw new UnauthorizedAccessException("Injected artifact statistics failure.");
            }

            return Statistics;
        }

        public PlaybackArtifactCleanupPreview PreviewCleanup(
            PlaybackArtifactCleanupOptions? options = null) => Preview;

        public PlaybackArtifactCleanupResult Cleanup(
            PlaybackArtifactCleanupOptions? options = null) => throw new NotSupportedException();

        public PlaybackArtifactCleanupResult Clear() => throw new NotSupportedException();
    }
}
