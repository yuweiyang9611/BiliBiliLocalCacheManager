using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Contracts;

public interface ICacheTrashService
{
    string GetTrashDirectory(string rootDirectory);

    CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid);

    CacheTrashOperationResult Restore(string rootDirectory, long avid, string trashPath);

    CacheTrashStatistics GetStatistics(
        string rootDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 逐条列出回收站内由本应用管理的条目，用于展示与选择性还原。
    /// </summary>
    IReadOnlyList<CacheTrashEntry> ListEntries(
        string rootDirectory,
        CancellationToken cancellationToken = default);

    CacheTrashPurgeResult Purge(
        string rootDirectory,
        bool includeUntrustedLegacyEntries = false);
}
