namespace BiliBiliLocalCacheManager.Core.Application.Models;

public sealed record CacheStorageStatistics(
    string RootDirectory,
    int ManagedEntryCount,
    int FileCount,
    long TotalBytes,
    int FailedEntryCount,
    int SkippedEntryCount,
    string? FirstErrorMessage);
