using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Contracts;

/// <summary>
/// 缓存删除相关的应用服务。
/// </summary>
public interface ICacheDeletionService
{
    /// <summary>
    /// 删除指定根目录下某个 avid 对应的缓存目录（即 root/{avid}）。
    /// </summary>
    /// <param name="rootDirectory">B 站缓存根目录。</param>
    /// <param name="avid">要删除的 avid。</param>
    /// <param name="dryRun">如果为 true，则不实际删除，仅计算目标路径并返回 Found 状态。</param>
    /// <returns>删除结果。</returns>
    CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false);
}