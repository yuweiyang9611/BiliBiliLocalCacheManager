namespace BiliBiliLocalCacheManager.Core.Application.Models;

public sealed record CacheScanProgress(
    int ProcessedAvidDirectories,
    int ProcessedSegmentDirectories,
    int IncludedEntries,
    string CurrentPath);
