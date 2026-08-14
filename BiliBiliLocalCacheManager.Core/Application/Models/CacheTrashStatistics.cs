namespace BiliBiliLocalCacheManager.Core.Application.Models;

public sealed record CacheTrashStatistics(
    string TrashDirectory,
    int ManagedEntryCount,
    int FileCount,
    long TotalBytes,
    int FailedEntryCount,
    int SkippedEntryCount,
    string? FirstErrorMessage,
    int UntrustedLegacyEntryCount = 0,
    int UntrustedLegacyFileCount = 0,
    long UntrustedLegacyBytes = 0,
    int PendingPurgeEntryCount = 0);
