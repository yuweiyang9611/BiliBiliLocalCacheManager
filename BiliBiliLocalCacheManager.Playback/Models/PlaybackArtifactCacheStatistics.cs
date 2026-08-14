namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed record PlaybackArtifactCacheStatistics(
    string RootDirectory,
    int FileCount,
    long TotalBytes);
