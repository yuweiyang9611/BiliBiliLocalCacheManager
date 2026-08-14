namespace BiliBiliLocalCacheManager.Core.Application.Models;

/// <summary>
/// 应用回收站中的一条可枚举条目。
/// </summary>
/// <param name="Avid">原缓存的 avid。</param>
/// <param name="TrashPath">回收站中的完整路径，还原时需要原样传给 Restore。</param>
/// <param name="OriginalPath">还原后会写回的原始路径。</param>
/// <param name="DeletedAtUtc">移入回收站的时间（UTC）。</param>
/// <param name="FileCount">条目内的文件数量，统计失败时为 0。</param>
/// <param name="TotalBytes">条目占用的字节数，统计失败时为 0。</param>
/// <param name="IsRestorable">是否可以还原。已进入永久清理或身份校验失败的条目不可还原。</param>
/// <param name="UnavailableReason">不可还原的原因，可还原时为 null。</param>
public sealed record CacheTrashEntry(
    long Avid,
    string TrashPath,
    string OriginalPath,
    DateTimeOffset DeletedAtUtc,
    int FileCount,
    long TotalBytes,
    bool IsRestorable,
    string? UnavailableReason);
