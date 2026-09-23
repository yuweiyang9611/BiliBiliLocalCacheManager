using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Contracts;

public interface ICacheTrashService
{
    string GetTrashDirectory(string rootDirectory);

    CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid);

    CacheTrashPartTarget CapturePartTarget(string rootDirectory, BiliSegment segment)
        => throw new NotSupportedException("Part trash is not supported by this service.");

    CacheTrashOperationResult MovePartToTrash(string rootDirectory, CacheTrashPartTarget target)
        => throw new NotSupportedException("Part trash is not supported by this service.");

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
        bool includeUntrustedLegacyEntries = false,
        IReadOnlyCollection<string>? expectedEntryIds = null);
}
