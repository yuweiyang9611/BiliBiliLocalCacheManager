using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackPersistentCacheTests
{
    [Fact]
    public void GetOrCreate_ShouldReuseAcrossStoreInstances_WhenDisplayMetadataChanges()
    {
        using var fixture = new CacheFixture();
        var firstStore = new PlaybackArtifactStore(fixture.CacheRoot);
        var secondStore = new PlaybackArtifactStore(fixture.CacheRoot);
        var firstPlan = fixture.CreatePlan("source.blv", "Old title");
        var renamedPlan = fixture.CreatePlan("source.blv", "Renamed title");
        var producerCalls = 0;

        var first = firstStore.GetOrCreate(firstPlan, ".mp4", path =>
        {
            producerCalls++;
            File.WriteAllBytes(path, new byte[32]);
        });
        var second = secondStore.GetOrCreate(renamedPlan, ".mp4", _ => producerCalls++);

        Assert.False(first.WasReused);
        Assert.True(second.WasReused);
        Assert.Equal(first.OutputPath, second.OutputPath);
        Assert.Equal(1, producerCalls);
    }

    [Fact]
    public void StatisticsAndClear_ShouldOnlyManageGeneratedArtifacts()
    {
        using var fixture = new CacheFixture();
        var store = new PlaybackArtifactStore(fixture.CacheRoot);
        store.GetOrCreate(
            fixture.CreatePlan("first.blv", "First"),
            ".mp4",
            path => File.WriteAllBytes(path, new byte[10]));
        store.GetOrCreate(
            fixture.CreatePlan("second.blv", "Second"),
            ".mp4",
            path => File.WriteAllBytes(path, new byte[20]));
        var outsideSentinel = Path.Combine(fixture.Root, "outside.txt");
        File.WriteAllText(outsideSentinel, "keep");

        var insideSentinel = Path.Combine(
            fixture.CacheRoot,
            "notes.txt");
        Directory.CreateDirectory(fixture.CacheRoot);
        File.WriteAllText(insideSentinel, "keep");

        var statistics = store.GetStatistics();
        var result = store.Clear();
        Assert.Equal(2, statistics.FileCount);
        Assert.Equal(30, statistics.TotalBytes);
        Assert.Equal(2, result.DeletedFileCount);
        Assert.Equal(30, result.FreedBytes);
        Assert.Equal(0, result.FailedFileCount);
        Assert.Equal(0, result.RemainingBytes);
        Assert.True(File.Exists(insideSentinel));
        Assert.True(File.Exists(outsideSentinel));
        Assert.Equal(0, store.GetStatistics().FileCount);
    }

    [Fact]
    public void GetOrCreate_ShouldDiscardOutput_WhenSourceChangesDuringGeneration()
    {
        using var fixture = new CacheFixture();
        var store = new PlaybackArtifactStore(fixture.CacheRoot);
        var plan = fixture.CreatePlan("changing.blv", "Changing");

        Assert.Throws<IOException>(() => store.GetOrCreate(plan, ".mp4", outputPath =>
        {
            File.WriteAllBytes(outputPath, new byte[32]);
            File.AppendAllText(plan.MediaFiles[0], "changed");
            File.SetLastWriteTimeUtc(plan.MediaFiles[0], DateTime.UtcNow.AddSeconds(1));
        }));

        Assert.Equal(0, store.GetStatistics().FileCount);
    }

    [Fact]
    public async Task GetOrCreate_ShouldObserveCancellation_WhileWaitingForAnotherProcessLock()
    {
        using var fixture = new CacheFixture();
        var store = new PlaybackArtifactStore(fixture.CacheRoot);
        var plan = fixture.CreatePlan("locked.blv", "Locked");
        var first = store.GetOrCreate(
            plan,
            ".mp4",
            path => File.WriteAllBytes(path, new byte[32]));
        File.Delete(first.OutputPath);

        await using var heldLock = new FileStream(
            first.OutputPath + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var producerCalled = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            store.GetOrCreate(
                plan,
                ".mp4",
                path =>
                {
                    producerCalled = true;
                    File.WriteAllBytes(path, new byte[32]);
                },
                cancellationSource.Token)));

        Assert.False(producerCalled);
    }

    private sealed class CacheFixture : IDisposable
    {
        public CacheFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"bili_persistent_cache_test_{Guid.NewGuid():N}");
            CacheRoot = Path.Combine(Root, "cache");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CacheRoot { get; }

        public CachePlaybackPlan CreatePlan(string sourceFileName, string title)
        {
            var sourceDirectory = Path.Combine(Root, "source");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, sourceFileName);
            if (!File.Exists(sourcePath))
            {
                File.WriteAllText(sourcePath, "source");
            }

            return CachePlaybackPlan.Playable(
                100,
                title,
                1,
                "P1",
                "c_1",
                sourceDirectory,
                "LegacyBlv",
                CachePlaybackMaterialKind.SingleFile,
                new[] { sourcePath });
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
