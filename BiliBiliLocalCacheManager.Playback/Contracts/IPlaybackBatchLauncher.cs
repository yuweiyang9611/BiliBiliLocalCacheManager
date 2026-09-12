using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface IPlaybackBatchLauncher
{
    PlaybackLaunchResult LaunchBatch(IReadOnlyList<PlaybackQueueItem> items,
        PlaybackLaunchOptions? options = null, CancellationToken cancellationToken = default);
}
