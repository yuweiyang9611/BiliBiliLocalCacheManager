namespace BiliBiliLocalCacheManager.Core.Application.Models;

public sealed record CacheTrashOperationResult(
    long Avid,
    bool Found,
    bool Succeeded,
    string OriginalPath,
    string? TrashPath,
    string? ErrorMessage);
