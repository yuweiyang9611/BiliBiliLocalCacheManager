namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed class PlaybackLaunchOptions
{
    public PlaybackPlayerPreference PreferredPlayer { get; init; } = PlaybackPlayerPreference.SystemDefaultFirst;
}
