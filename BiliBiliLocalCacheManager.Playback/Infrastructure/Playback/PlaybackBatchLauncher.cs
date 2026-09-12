using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class PlaybackBatchLauncher(PlaybackArtifactStore store, IPlaybackLauncher launcher) : IPlaybackBatchLauncher
{
    public PlaybackLaunchResult LaunchBatch(IReadOnlyList<PlaybackQueueItem> items,
        PlaybackLaunchOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0) return PlaybackLaunchResult.Failure("No playable items.");
        foreach (var item in items) ValidateLocalFile(item.Path);
        var unique = items.DistinctBy(item => Path.GetFullPath(item.Path), PlaybackFileSystem.PathComparer).ToArray();
        var seconds = unique.Sum(item => Math.Max(0, item.Duration.TotalSeconds));
        var until = DateTimeOffset.UtcNow.AddSeconds(Math.Max(6 * 3600, seconds + 3600));
        foreach (var item in unique)
        {
            ValidateLocalFile(item.Path);
            store.ProtectUntilIfManaged(item.Path, until, cancellationToken);
        }
        var path = unique.Length == 1 ? unique[0].Path : store.CreatePlaylist(unique, until, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return launcher.Launch(PlaybackMaterializationResult.Success(path, true, "Prepared playback queue", "Playlist"), options);
    }

    public static void ValidateLocalFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.IndexOfAny(['\r', '\n', '\0']) >= 0 || !File.Exists(path))
            throw new IOException("A playlist entry must be an existing local media file.");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Playlist media must not traverse symbolic links or junctions.");
    }
}
