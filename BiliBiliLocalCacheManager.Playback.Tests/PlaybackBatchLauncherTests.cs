using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackBatchLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blcm-playlist-\u7f13\u5b58 #@ " + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BatchLaunch_UsesOneOrderedUtf8PlaylistAndDeduplicatesPaths()
    {
        var store = new PlaybackArtifactStore(_root);
        var first = Media(1);
        var second = Media(2);
        var recorder = new Recorder();
        var launcher = new PlaybackBatchLauncher(store, recorder);
        var result = launcher.LaunchBatch([
            new(second, "\u4e2d\u6587\r\n#EXTINF:injected", TimeSpan.FromMinutes(1)),
            new(first, "First", TimeSpan.FromMinutes(1)), new(second, "Duplicate", TimeSpan.Zero)]);
        Assert.True(result.Succeeded);
        Assert.Equal(1, recorder.Calls);
        Assert.EndsWith(".m3u8", recorder.Path);
        var lines = File.ReadAllLines(recorder.Path!);
        Assert.Equal(["#EXTM3U", "#EXTINF:-1,\u4e2d\u6587  #EXTINF:injected", second, "#EXTINF:-1,First", first], lines);
        Assert.False(File.ReadAllBytes(recorder.Path!).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
    }

    [Fact]
    public void LargeQueueProtection_SurvivesAnotherStoreClearAndExpires()
    {
        var store = new PlaybackArtifactStore(_root);
        var items = Enumerable.Range(1, 70).Select(i => new PlaybackQueueItem(Media(i), i.ToString(), TimeSpan.FromMinutes(10))).ToArray();
        var recorder = new Recorder();
        new PlaybackBatchLauncher(store, recorder).LaunchBatch(items);
        var deadline = new DateTimeOffset(long.Parse(File.ReadAllText(items[0].Path + ".protected-until")), TimeSpan.Zero);
        Assert.True(deadline > DateTimeOffset.UtcNow.AddHours(12));
        var other = new PlaybackArtifactStore(_root);
        Assert.Equal(0, other.Clear().DeletedFileCount);
        Assert.All(items, item => Assert.True(File.Exists(item.Path)));
        Assert.True(File.Exists(recorder.Path));
        foreach (var metadata in Directory.EnumerateFiles(_root, "*.protected-until", SearchOption.AllDirectories))
            File.WriteAllText(metadata, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks.ToString());
        Assert.Equal(71, other.Clear().DeletedFileCount);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.protected-until", SearchOption.AllDirectories));
    }

    [Fact]
    public void SingleItemAndCancellation_DoNotCreatePlaylistsOrExtraLaunches()
    {
        var path = Media(1);
        var recorder = new Recorder();
        var launcher = new PlaybackBatchLauncher(new PlaybackArtifactStore(_root), recorder);
        launcher.LaunchBatch([new(path, "Single", TimeSpan.Zero)]);
        Assert.Equal(path, recorder.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => launcher.LaunchBatch([new(path, "", TimeSpan.Zero)], cancellationToken: cancellation.Token));
        Assert.Equal(1, recorder.Calls);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.m3u8", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("https://example.com/movie.mp4")]
    [InlineData("relative.mp4")]
    [InlineData("//server/share/movie.mp4")]
    public void RejectsNonlocalPaths(string path)
    {
        var recorder = new Recorder();
        Assert.Throws<IOException>(() => new PlaybackBatchLauncher(new PlaybackArtifactStore(_root), recorder)
            .LaunchBatch([new(path, "", TimeSpan.Zero)]));
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public void CleanupActivity_CanCancelBeforeAnyArtifactIsDeleted()
    {
        var path = Media(1);
        var store = new PlaybackArtifactStore(_root, () => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => store.Cleanup(new PlaybackArtifactCleanupOptions { MaxAge = TimeSpan.Zero }));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PlaylistRejectsMediaThroughDirectoryLinks()
    {
        var media = Media(1);
        var link = Path.Combine(_root, "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, Path.GetDirectoryName(media)! }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, Path.GetDirectoryName(media)!);
        try
        {
            var recorder = new Recorder();
            Assert.Throws<IOException>(() => new PlaybackBatchLauncher(new PlaybackArtifactStore(_root), recorder)
                .LaunchBatch([new(Path.Combine(link, Path.GetFileName(media)), "", TimeSpan.Zero)]));
            Assert.Equal(0, recorder.Calls);
        }
        finally { Directory.Delete(link); }
    }

    private string Media(int i)
    {
        var directory = Path.Combine(_root, i.ToString(), "Page_1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, i.ToString("x24") + ".mp4");
        File.WriteAllText(path, "media");
        return path;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Recorder : IPlaybackLauncher
    {
        public int Calls;
        public string? Path;
        public PlaybackLaunchResult Launch(PlaybackMaterializationResult result, PlaybackLaunchOptions? options = null)
        {
            Calls++;
            Path = result.OutputPath;
            return PlaybackLaunchResult.Success("Handed to player", "test");
        }
    }
}
