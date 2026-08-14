namespace BiliBiliLocalCacheManager.Core.Application.Models;

public sealed record CacheTrashPurgeResult(
    int DeletedEntryCount,
    long FreedBytes,
    int FailedEntryCount,
    int SkippedEntryCount,
    string? FirstErrorMessage,
    int PartiallyDeletedEntryCount = 0,
    int PendingPurgeEntryCount = 0,
    IReadOnlyList<string>? NonRestorableTrashPaths = null);
