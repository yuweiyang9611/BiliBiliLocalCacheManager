using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Cli.Commands;

public sealed partial class PlayCommand
{
    private static ICachePlaybackService CreatePlaybackService()
    {
        var artifactStore = PlaybackArtifactStore.Shared;
        artifactStore.Cleanup();
        return new CachePlaybackService(artifactStore);
    }
}
