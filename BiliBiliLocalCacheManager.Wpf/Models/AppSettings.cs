using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Wpf.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string RootPath { get; set; } = string.Empty;
    public bool IncludeIncomplete { get; set; }
    public bool SplitKeywords { get; set; } = true;
    public bool AnyKeywords { get; set; }
    public bool IncludePartName { get; set; } = true;
    public bool IncludeOwnerName { get; set; }
    public bool IncludeBvid { get; set; }
    public bool IncludeAvid { get; set; }
    public bool CaseSensitive { get; set; }
    public CacheSearchMatchMode MatchMode { get; set; } = CacheSearchMatchMode.Contains;
    public PlaybackPlayerPreference PreferredPlayer { get; set; } = PlaybackPlayerPreference.SystemDefaultFirst;
    public int TranscodeCacheRetentionDays { get; set; } =
        PlaybackArtifactCleanupOptions.DefaultRetentionDays;
    public int TranscodeCacheMaxSizeGigabytes { get; set; } =
        PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes;

    public PlaybackArtifactCleanupOptions CreateTranscodeCacheCleanupOptions()
    {
        return PlaybackArtifactCleanupOptions.FromUserLimits(
            TranscodeCacheRetentionDays,
            TranscodeCacheMaxSizeGigabytes);
    }
}
