using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactStoreTests
{
    [Fact]
    public void GetOrCreate_ShouldReuseArtifactForUnchangedSources()
    {
        using var fixture = new ArtifactFixture();
        var plan = fixture.CreatePlan("source.blv");
        var producerCalls = 0;

        var first = fixture.Store.GetOrCreate(plan, ".mp4", path =>
        {
            producerCalls++;
            File.WriteAllText(path, "prepared");
        });
        var second = fixture.Store.GetOrCreate(plan, ".mp4", _ => producerCalls++);

        Assert.False(first.WasReused);
        Assert.True(second.WasReused);
        Assert.Equal(first.OutputPath, second.OutputPath);
        Assert.Equal(1, producerCalls);
    }

    [Fact]
    public void GetOrCreate_ShouldUseNewArtifactWhenSourceChanges()
    {
        using var fixture = new ArtifactFixture();
        var plan = fixture.CreatePlan("source.blv");
        var first = fixture.Store.GetOrCreate(plan, ".mp4", path => File.WriteAllText(path, "first"));

        File.AppendAllText(plan.MediaFiles[0], "changed");
        File.SetLastWriteTimeUtc(plan.MediaFiles[0], DateTime.UtcNow.AddSeconds(1));
        var second = fixture.Store.GetOrCreate(plan, ".mp4", path => File.WriteAllText(path, "second"));

        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.False(second.WasReused);
    }

    [Fact]
    public void GetOrCreate_ShouldRemovePartialFileWhenProducerFails()
    {
        using var fixture = new ArtifactFixture();
        var plan = fixture.CreatePlan("source.blv");

        Assert.Throws<InvalidOperationException>(() => fixture.Store.GetOrCreate(plan, ".mp4", path =>
        {
            File.WriteAllText(path, "partial");
            throw new InvalidOperationException("boom");
        }));

        var files = Directory.Exists(fixture.Store.RootDirectory)
            ? Directory.GetFiles(fixture.Store.RootDirectory, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.Empty(files);
    }

    [Fact]
    public void Cleanup_ShouldDeleteExpiredArtifacts()
    {
        using var fixture = new ArtifactFixture();
        var plan = fixture.CreatePlan("source.blv");
        var artifact = fixture.Store.GetOrCreate(plan, ".mp4", path => File.WriteAllBytes(path, new byte[32]));
        File.SetLastWriteTimeUtc(artifact.OutputPath, DateTime.UtcNow.AddDays(-10));

        var result = fixture.Store.Cleanup(new PlaybackArtifactCleanupOptions
        {
            MaxAge = TimeSpan.FromDays(7),
            MaxTotalBytes = long.MaxValue
        });

        Assert.Equal(1, result.DeletedFileCount);
        Assert.Equal(32, result.FreedBytes);
        Assert.False(File.Exists(artifact.OutputPath));
    }

    private sealed class ArtifactFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"bili_artifact_store_test_{Guid.NewGuid():N}");

        public ArtifactFixture()
        {
            Directory.CreateDirectory(_root);
            Store = new PlaybackArtifactStore(Path.Combine(_root, "artifacts"));
        }

        public PlaybackArtifactStore Store { get; }

        public CachePlaybackPlan CreatePlan(string sourceFileName)
        {
            var sourceDirectory = Path.Combine(_root, "source");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, sourceFileName);
            File.WriteAllText(sourcePath, "source");

            return CachePlaybackPlan.Playable(
                100,
                "Title",
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
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Ignore test cleanup failures.
            }
        }
    }
}
