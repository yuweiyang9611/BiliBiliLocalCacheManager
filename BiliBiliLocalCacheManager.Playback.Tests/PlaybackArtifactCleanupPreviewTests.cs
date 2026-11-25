using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactCleanupPreviewTests
{
    [Fact]
    public void MeasureStorage_CancellationAtFirstFileStopsSnapshot()
    {
        using var fixture = new PreviewFixture();
        fixture.CreateArtifact("first.blv", 1, 8);
        fixture.CreateArtifact("second.blv", 2, 16);
        using var cancellation = new CancellationTokenSource();
        var visits = 0;
        var store = new PlaybackArtifactStore(fixture.Store.RootDirectory, () =>
        {
            visits++;
            cancellation.Cancel();
        });

        var error = Assert.Throws<OperationCanceledException>(() => store.MeasureStorage(new(), out _, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, visits);
    }

    [Fact]
    public void MeasureStorage_CancellationDuringProtectionCheckSkipsRemainingFiles()
    {
        using var fixture = new PreviewFixture();
        fixture.CreateArtifact("first.blv", 1, 8);
        fixture.CreateArtifact("second.blv", 2, 16);
        using var cancellation = new CancellationTokenSource();
        var clock = new CancelOnProtectionCheck(cancellation);
        var store = new PlaybackArtifactStore(fixture.Store.RootDirectory, timeProvider: clock);

        var error = Assert.Throws<OperationCanceledException>(() => store.MeasureStorage(new(), out _, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, clock.ChecksAfterCancellation);
    }

    private sealed class CancelOnProtectionCheck(CancellationTokenSource cancellation) : TimeProvider
    {
        private int _reads;
        public int ChecksAfterCancellation { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (cancellation.IsCancellationRequested) ChecksAfterCancellation++;
            // The first read establishes the policy time; the next reads check per-file protection.
            if (++_reads == 2) cancellation.Cancel();
            return DateTimeOffset.UnixEpoch;
        }
    }

    [Fact]
    public void MeasureStorage_VisitsEachFileOnceAndExcludesProtectedBytesFromReclaimableSpace()
    {
        using var fixture = new PreviewFixture();
        var expired = fixture.CreateArtifact("expired.blv", 1, 32);
        var leased = fixture.CreateArtifact("leased.blv", 2, 16);
        var protectedBySession = fixture.CreateArtifact("selected.blv", 3, 8);
        var now = DateTimeOffset.UtcNow;
        foreach (var path in new[] { expired, leased, protectedBySession })
            File.SetLastWriteTimeUtc(path, now.AddDays(-45).UtcDateTime);
        fixture.Store.ProtectUntilIfManaged(leased, now.AddHours(6));
        var visits = 0;
        var store = new PlaybackArtifactStore(fixture.Store.RootDirectory, () => visits++);

        var preview = store.MeasureStorage(new PlaybackArtifactCleanupOptions { ProtectedPaths = [protectedBySession] },
            out var statistics);

        Assert.Equal(4, visits); // Three artifacts and the lease marker.
        Assert.Equal(3, statistics.FileCount);
        Assert.Equal(56, statistics.TotalBytes);
        Assert.Equal(1, preview.CandidateFileCount);
        Assert.Equal(32, preview.ReclaimableBytes);
        Assert.Equal(24, preview.RemainingBytes);
    }

    [Fact]
    public void PreviewCleanup_ShouldMatchCleanupForExpiredArtifacts_WithoutChangingFiles()
    {
        using var fixture = new PreviewFixture();
        var expired = fixture.CreateArtifact("expired.blv", 1, 32);
        var retained = fixture.CreateArtifact("retained.blv", 2, 16);
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(retained, DateTime.UtcNow.AddDays(-1));
        var before = SnapshotFiles(fixture.Store.RootDirectory);
        var options = new PlaybackArtifactCleanupOptions
        {
            MaxAge = TimeSpan.FromDays(7),
            MaxTotalBytes = long.MaxValue
        };

        var preview = fixture.Store.PreviewCleanup(options);

        Assert.Equal(1, preview.CandidateFileCount);
        Assert.Equal(32, preview.ReclaimableBytes);
        Assert.Equal(16, preview.RemainingBytes);
        Assert.Equal(before, SnapshotFiles(fixture.Store.RootDirectory));

        var result = fixture.Store.Cleanup(options);

        Assert.Equal(preview.CandidateFileCount, result.DeletedFileCount);
        Assert.Equal(preview.ReclaimableBytes, result.FreedBytes);
        Assert.Equal(preview.RemainingBytes, result.RemainingBytes);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(retained));
    }

    [Fact]
    public void PreviewCleanup_ShouldApplyCapacityLruWhileRespectingGracePeriod()
    {
        using var fixture = new PreviewFixture();
        var oldest = fixture.CreateArtifact("oldest.blv", 1, 32);
        var older = fixture.CreateArtifact("older.blv", 2, 32);
        var recent = fixture.CreateArtifact("recent.blv", 3, 32);
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        var options = new PlaybackArtifactCleanupOptions
        {
            MaxAge = TimeSpan.FromDays(365),
            MaxTotalBytes = 32,
            CapacityEvictionGracePeriod = TimeSpan.FromMinutes(5)
        };

        var preview = fixture.Store.PreviewCleanup(options);

        Assert.Equal(2, preview.CandidateFileCount);
        Assert.Equal(64, preview.ReclaimableBytes);
        Assert.Equal(32, preview.RemainingBytes);

        var result = fixture.Store.Cleanup(options);

        Assert.Equal(preview.CandidateFileCount, result.DeletedFileCount);
        Assert.Equal(preview.ReclaimableBytes, result.FreedBytes);
        Assert.Equal(preview.RemainingBytes, result.RemainingBytes);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void PreviewCleanup_ShouldUseProtectedPathsFromEachCall()
    {
        using var fixture = new PreviewFixture();
        var artifact = fixture.CreateArtifact("protected.blv", 1, 32);
        File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddDays(-10));
        var options = new PlaybackArtifactCleanupOptions
        {
            MaxAge = TimeSpan.FromDays(7),
            MaxTotalBytes = 0,
            CapacityEvictionGracePeriod = TimeSpan.Zero
        };

        var unprotected = fixture.Store.PreviewCleanup(options);
        options.ProtectedPaths = new[] { artifact };
        var protectedPreview = fixture.Store.PreviewCleanup(options);

        Assert.Equal(1, unprotected.CandidateFileCount);
        Assert.Equal(32, unprotected.ReclaimableBytes);
        Assert.Equal(0, protectedPreview.CandidateFileCount);
        Assert.Equal(0, protectedPreview.ReclaimableBytes);
        Assert.Equal(32, protectedPreview.RemainingBytes);

        var result = fixture.Store.Cleanup(options);
        Assert.Equal(0, result.DeletedFileCount);
        Assert.True(File.Exists(artifact));
    }

    [Fact]
    public void Cleanup_ShouldContinueCapacityEviction_WhenPlannedRetentionCandidateIsLocked()
    {
        using var fixture = new PreviewFixture();
        var lockedExpired = fixture.CreateArtifact("locked-expired.blv", 1, 32);
        var capacityFallback = fixture.CreateArtifact("capacity-fallback.blv", 2, 32);
        File.SetLastWriteTimeUtc(lockedExpired, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(capacityFallback, DateTime.UtcNow.AddHours(-2));
        var options = new PlaybackArtifactCleanupOptions
        {
            MaxAge = TimeSpan.FromDays(7),
            MaxTotalBytes = 32,
            CapacityEvictionGracePeriod = TimeSpan.FromMinutes(5)
        };
        var preview = fixture.Store.PreviewCleanup(options);

        Assert.Equal(1, preview.CandidateFileCount);
        Assert.Equal(32, preview.ReclaimableBytes);
        Assert.Equal(32, preview.RemainingBytes);

        using (new FileStream(
                   lockedExpired + ".lock",
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var result = fixture.Store.Cleanup(options);

            Assert.Equal(1, result.DeletedFileCount);
            Assert.Equal(32, result.FreedBytes);
            Assert.Equal(32, result.RemainingBytes);
            Assert.True(File.Exists(lockedExpired));
            Assert.False(File.Exists(capacityFallback));
        }
    }

    [Fact]
    public void PreviewCleanup_ShouldIncludeOnlyStaleManagedBuildFiles()
    {
        using var fixture = new PreviewFixture();
        var outputPath = fixture.CreateArtifact("source.blv", 1, 32);
        File.Delete(outputPath);
        var directory = Path.GetDirectoryName(outputPath)!;
        var fingerprint = Path.GetFileNameWithoutExtension(outputPath);
        var staleBuild = Path.Combine(
            directory,
            $"{fingerprint}.building-{Guid.NewGuid():N}.mp4");
        var recentBuild = Path.Combine(
            directory,
            $"{fingerprint}.building-{Guid.NewGuid():N}.mp4");
        var unmanaged = Path.Combine(directory, "not-a-managed-artifact.mp4");
        File.WriteAllBytes(staleBuild, new byte[13]);
        File.WriteAllBytes(recentBuild, new byte[17]);
        File.WriteAllBytes(unmanaged, new byte[19]);
        File.SetLastWriteTimeUtc(staleBuild, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(unmanaged, DateTime.UtcNow.AddHours(-2));
        var before = SnapshotFiles(fixture.Store.RootDirectory);
        var options = new PlaybackArtifactCleanupOptions
        {
            MaxAge = TimeSpan.FromDays(365),
            MaxTotalBytes = long.MaxValue
        };

        var preview = fixture.Store.PreviewCleanup(options);

        Assert.Equal(1, preview.CandidateFileCount);
        Assert.Equal(13, preview.ReclaimableBytes);
        Assert.Equal(17, preview.RemainingBytes);
        Assert.Equal(before, SnapshotFiles(fixture.Store.RootDirectory));

        var result = fixture.Store.Cleanup(options);

        Assert.Equal(preview.CandidateFileCount, result.DeletedFileCount);
        Assert.Equal(preview.ReclaimableBytes, result.FreedBytes);
        Assert.Equal(preview.RemainingBytes, result.RemainingBytes);
        Assert.Equal(1, result.Statistics?.FileCount);
        Assert.False(File.Exists(staleBuild));
        Assert.True(File.Exists(recentBuild));
        Assert.True(File.Exists(unmanaged));
    }

    [Fact]
    public void PreviewCleanup_ShouldNotTraverseManagedLookingDirectoryLink()
    {
        using var fixture = new PreviewFixture(createStoreDirectory: false);
        var outsidePage = Path.Combine(fixture.RootDirectory, "outside-page");
        var avidDirectory = Path.Combine(fixture.Store.RootDirectory, "999");
        var pageLink = Path.Combine(avidDirectory, "Page_1");
        Directory.CreateDirectory(avidDirectory);
        Directory.CreateDirectory(outsidePage);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(pageLink, outsidePage);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or
                    IOException or
                    PlatformNotSupportedException)
            {
                return;
            }

            var outsideArtifact = Path.Combine(
                outsidePage,
                "0123456789abcdef01234567.mp4");
            File.WriteAllBytes(outsideArtifact, new byte[32]);
            File.SetLastWriteTimeUtc(outsideArtifact, DateTime.UtcNow.AddDays(-10));

            var preview = fixture.Store.PreviewCleanup(new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.Zero,
                MaxTotalBytes = 0,
                CapacityEvictionGracePeriod = TimeSpan.Zero
            });

            Assert.Equal(0, preview.CandidateFileCount);
            Assert.Equal(0, preview.ReclaimableBytes);
            Assert.Equal(0, preview.RemainingBytes);
            Assert.True(File.Exists(outsideArtifact));
        }
        finally
        {
            if (Directory.Exists(pageLink))
            {
                Directory.Delete(pageLink);
            }
        }
    }

    private static IReadOnlyList<(string Path, long Length, DateTime LastWriteTimeUtc)> SnapshotFiles(
        string rootDirectory)
    {
        return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Select(file => (file.FullName, file.Length, file.LastWriteTimeUtc))
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class PreviewFixture : IDisposable
    {
        public PreviewFixture(bool createStoreDirectory = true)
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"bili_artifact_cleanup_preview_{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootDirectory);
            Store = new PlaybackArtifactStore(Path.Combine(RootDirectory, "cache"));
            if (createStoreDirectory)
            {
                Directory.CreateDirectory(Store.RootDirectory);
            }
        }

        public string RootDirectory { get; }

        public PlaybackArtifactStore Store { get; }

        public string CreateArtifact(string sourceName, int pageIndex, int length)
        {
            var sourceDirectory = Path.Combine(RootDirectory, "source");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, sourceName);
            File.WriteAllText(sourcePath, sourceName);
            var plan = CachePlaybackPlan.Playable(
                100,
                "Title",
                pageIndex,
                $"P{pageIndex}",
                $"c_{pageIndex}",
                sourceDirectory,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { sourcePath });

            return Store.GetOrCreate(
                plan,
                ".mp4",
                path => File.WriteAllBytes(path, new byte[length])).OutputPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
            catch
            {
                // Ignore best-effort test cleanup failures.
            }
        }
    }
}
