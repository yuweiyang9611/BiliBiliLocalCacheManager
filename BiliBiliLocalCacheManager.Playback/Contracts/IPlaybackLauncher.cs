using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface IPlaybackLauncher
{
    PlaybackLaunchResult Launch(PlaybackMaterializationResult materializationResult, PlaybackLaunchOptions? launchOptions = null);
}
