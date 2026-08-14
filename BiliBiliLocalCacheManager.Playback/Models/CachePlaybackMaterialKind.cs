namespace BiliBiliLocalCacheManager.Playback.Models;

public enum CachePlaybackMaterialKind
{
    Unavailable = 0,
    SingleFile = 1,
    OrderedPair = 2,
    OrderedFiles = OrderedPair,
    DashPair = 3
}
