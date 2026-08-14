using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackArtifactCleanupRevalidationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cleanup_ShouldNotDeleteArtifactReusedAfterPlanning(
        bool retentionCandidate)
    {
        using var fixture = new RevalidationFixture();
        var store = new PlaybackArtifactStore(fixture.CacheRoot);
        var plan = fixture.CreatePlan();
        var output = store.GetOrCreate(
            plan,
            ".mp4",
            path => File.WriteAllBytes(path, new byte[32])).OutputPath;
        File.SetLastWriteTimeUtc(
            output,
            retentionCandidate
                ? DateTime.UtcNow.AddDays(-10)
                : DateTime.UtcNow.AddMinutes(-10));
        var candidateReady = new ManualResetEventSlim();
        var allowCleanupToLock = new ManualResetEventSlim();
        store.BeforeCleanupCandidateLockForTesting = path =>
        {
            if (!string.Equals(path, output, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            candidateReady.Set();
            allowCleanupToLock.Wait(TimeSpan.FromSeconds(5));
        };
        var options = retentionCandidate
            ? new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.FromDays(7),
                MaxTotalBytes = long.MaxValue
            }
            : new PlaybackArtifactCleanupOptions
            {
                MaxAge = TimeSpan.FromDays(365),
                MaxTotalBytes = 0,
                CapacityEvictionGracePeriod = TimeSpan.FromMinutes(5)
            };

        var cleanupTask = Task.Run(() => store.Cleanup(options));
        try
        {
            Assert.True(candidateReady.Wait(TimeSpan.FromSeconds(5)));
            var reused = new PlaybackArtifactStore(fixture.CacheRoot).GetOrCreate(
                plan,
                ".mp4",
                _ => throw new InvalidOperationException("The artifact should have been reused."));
            Assert.True(reused.WasReused);
        }
        finally
        {
            allowCleanupToLock.Set();
        }

        var result = await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.DeletedFileCount);
        Assert.Equal(32, result.RemainingBytes);
        Assert.True(File.Exists(output));
    }

    private sealed class RevalidationFixture : IDisposable
    {
        public RevalidationFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"bili_artifact_revalidation_{Guid.NewGuid():N}");
            CacheRoot = Path.Combine(Root, "cache");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CacheRoot { get; }

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
