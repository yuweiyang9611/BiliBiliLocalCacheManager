using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactLockCancellationTests
{
    [Fact]
    public async Task GetOrCreate_ShouldReportCrossProcessWaitAndCancelBeforeProducerRuns()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_cross_process_artifact_lock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            var plan = CreatePlan(root, sourcePath);
            var artifact = store.GetOrCreate(
                plan,
                ".mp4",
                outputPath => File.WriteAllBytes(outputPath, new byte[32]));
            File.Delete(artifact.OutputPath);

            using var externalLock = new FileStream(
                artifact.OutputPath + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var cancellationSource = new CancellationTokenSource();
            var producerCalled = false;
            string? reportedStage = null;
            var stopwatch = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
                store.GetOrCreate(
                    plan,
                    ".mp4",
                    outputPath =>
                    {
                        producerCalled = true;
                        File.WriteAllBytes(outputPath, new byte[32]);
                    },
                    cancellationSource.Token,
                    (stage, percentage) =>
                    {
                        reportedStage = stage;
                        Assert.Null(percentage);
                        cancellationSource.Cancel();
                    })).WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("正在等待其他实例生成播放缓存", reportedStage);
            Assert.False(producerCalled);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
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
    public void GetOrCreate_CacheHitWithoutLockConflict_ShouldNotReportWaitProgress()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_cache_hit_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            var plan = CreatePlan(root, sourcePath);
            var first = store.GetOrCreate(
                plan,
                ".mp4",
                outputPath => File.WriteAllBytes(outputPath, new byte[32]));
            var producerCalled = false;
            var progressCalled = false;

            var reused = store.GetOrCreate(
                plan,
                ".mp4",
                _ => producerCalled = true,
                CancellationToken.None,
                (_, _) => progressCalled = true);

            Assert.True(reused.WasReused);
            Assert.Equal(first.OutputPath, reused.OutputPath);
            Assert.False(producerCalled);
            Assert.False(progressCalled);
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
    public async Task GetOrCreate_ShouldObserveCancellation_WhileWaitingForSameProcessProducer()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bili_in_process_artifact_lock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var producerEntered = new ManualResetEventSlim(false);
        using var releaseProducer = new ManualResetEventSlim(false);
        try
        {
            var sourcePath = Path.Combine(root, "source.blv");
            File.WriteAllText(sourcePath, "source");
            var store = new PlaybackArtifactStore(Path.Combine(root, "cache"));
            var plan = CachePlaybackPlan.Playable(
                100,
                "Title",
                1,
                "P1",
                "c_1",
                root,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { sourcePath });

            var firstTask = Task.Run(() => store.GetOrCreate(plan, ".mp4", outputPath =>
            {
                producerEntered.Set();
                Assert.True(releaseProducer.Wait(TimeSpan.FromSeconds(5)));
                File.WriteAllBytes(outputPath, new byte[32]);
            }));
            Assert.True(producerEntered.Wait(TimeSpan.FromSeconds(5)));

            using var cancellationSource =
                new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var waitingProducerCalled = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
                store.GetOrCreate(
                    plan,
                    ".mp4",
                    outputPath =>
                    {
                        waitingProducerCalled = true;
                        File.WriteAllBytes(outputPath, new byte[32]);
                    },
                    cancellationSource.Token)));

            Assert.False(waitingProducerCalled);
            releaseProducer.Set();
            await firstTask;
        }
        finally
        {
            releaseProducer.Set();
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
}
