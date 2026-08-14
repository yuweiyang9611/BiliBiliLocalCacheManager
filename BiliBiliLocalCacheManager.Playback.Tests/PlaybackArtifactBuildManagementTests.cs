using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactBuildManagementTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void StatisticsAndClear_ShouldIncludeManagedBuildFilesAndPreserveUnknownFiles()
    {
        using var fixture = new BuildFixture();
        var output = fixture.CreateArtifact(10);
        var staleBuild = fixture.CreateBuildFile(output, 13);
        var recentBuild = fixture.CreateBuildFile(output, 17);
        var unknown = Path.Combine(Path.GetDirectoryName(output)!, "manual.mp4");
        File.WriteAllBytes(unknown, new byte[19]);
        File.SetLastWriteTimeUtc(staleBuild, DateTime.UtcNow.AddHours(-2));

        var statistics = fixture.Store.GetStatistics();
        var result = fixture.Store.Clear();

        Assert.Equal(3, statistics.FileCount);
        Assert.Equal(40, statistics.TotalBytes);
        Assert.Equal(3, result.DeletedFileCount);
        Assert.Equal(40, result.FreedBytes);
        Assert.Equal(0, result.RemainingBytes);
        Assert.Equal(0, result.Statistics?.FileCount);
        Assert.False(File.Exists(staleBuild));
        Assert.False(File.Exists(recentBuild));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Clear_ShouldLeaveActiveBuildAndDeleteItAfterOutputLockIsReleased()
    {
        using var fixture = new BuildFixture();
        var output = fixture.CreateArtifact(10);
        File.Delete(output);
        var activeBuild = fixture.CreateBuildFile(output, 17);

        PlaybackArtifactCleanupResult lockedResult;
        using (new FileStream(
                   output + ".lock",
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            lockedResult = fixture.Store.Clear();
        }

        Assert.Equal(0, lockedResult.DeletedFileCount);
        Assert.Equal(1, lockedResult.FailedFileCount);
        Assert.True(lockedResult.RemainingBytes > 0);
        Assert.Equal(1, lockedResult.Statistics?.FileCount);
        Assert.True(File.Exists(activeBuild));

        var unlockedResult = fixture.Store.Clear();

        Assert.Equal(1, unlockedResult.DeletedFileCount);
        Assert.Equal(17, unlockedResult.FreedBytes);
        Assert.Equal(0, unlockedResult.RemainingBytes);
        Assert.False(File.Exists(activeBuild));
    }

    [Fact]
    public async Task GetStatistics_ShouldTreatBuildPromotionAsTransientAndRemainUsable()
    {
        using var fixture = new BuildFixture();
        using var buildReady = new ManualResetEventSlim();
        using var allowPromotion = new ManualResetEventSlim();
        using var enumerationReady = new ManualResetEventSlim();
        using var continueStatistics = new ManualResetEventSlim();
        var plan = fixture.CreatePlan();
        var materializationTask = Task.Factory.StartNew(
            () => fixture.Store.GetOrCreate(
                plan,
                ".mp4",
                path =>
                {
                    File.WriteAllBytes(path, new byte[32]);
                    buildReady.Set();
                    if (!allowPromotion.Wait(CoordinationTimeout))
                    {
                        throw new TimeoutException("Timed out waiting to promote the build artifact.");
                    }
                }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(buildReady.Wait(CoordinationTimeout));
        fixture.Store.AfterStrictFileEnumerationForTesting = () =>
        {
            enumerationReady.Set();
            if (!continueStatistics.Wait(CoordinationTimeout))
            {
                throw new TimeoutException("Timed out waiting to continue the statistics snapshot.");
            }
        };
        var concurrentStatisticsTask = Task.Factory.StartNew(
            fixture.Store.GetStatistics,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(enumerationReady.Wait(CoordinationTimeout));
        allowPromotion.Set();
        var materialization = await materializationTask.WaitAsync(CoordinationTimeout);
        continueStatistics.Set();
        var concurrentStatistics = await concurrentStatisticsTask.WaitAsync(CoordinationTimeout);
        fixture.Store.AfterStrictFileEnumerationForTesting = null;
        var finalStatistics = fixture.Store.GetStatistics();

        Assert.InRange(concurrentStatistics.FileCount, 0, 1);
        Assert.True(File.Exists(materialization.OutputPath));
        Assert.Equal(1, finalStatistics.FileCount);
        Assert.Equal(32, finalStatistics.TotalBytes);
    }

    private sealed class BuildFixture : IDisposable
    {
        public BuildFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"bili_artifact_build_management_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Store = new PlaybackArtifactStore(Path.Combine(Root, "cache"));
        }

        public string Root { get; }

        public PlaybackArtifactStore Store { get; }

        public string CreateArtifact(int length)
        {
            return Store.GetOrCreate(
                CreatePlan(),
                ".mp4",
                path => File.WriteAllBytes(path, new byte[length])).OutputPath;
        }

        public CachePlaybackPlan CreatePlan()
        {
            var source = Path.Combine(Root, "source.blv");
            File.WriteAllText(source, "source");
            return CachePlaybackPlan.Playable(
                100,
                "Title",
                1,
                "P1",
                "c_1",
                Root,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { source });
        }

        public string CreateBuildFile(string outputPath, int length)
        {
            var directory = Path.GetDirectoryName(outputPath)!;
            var fingerprint = Path.GetFileNameWithoutExtension(outputPath);
            var build = Path.Combine(
                directory,
                $"{fingerprint}.building-{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(build, new byte[length]);
            return build;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Ignore best-effort test cleanup failures.
            }
        }
    }
}
