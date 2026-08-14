using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Contracts;

/// <summary>
/// 应用服务接口：从缓存根目录扫描并构建 B 站缓存索引。
/// </summary>
public interface ICacheIndexBuilder
{
    /// <summary>
    /// 从指定缓存根目录构建完整的缓存索引。
    /// </summary>
    /// <param name="rootDirectory">B 站缓存根目录路径。</param>
    /// <param name="options">可选的构建参数，null 则使用默认配置。</param>
    /// <returns>构建出的缓存索引。</returns>
    CacheIndex BuildIndex(string rootDirectory, CacheIndexBuildOptions? options = null);

    CacheIndexBuildResult BuildIndexWithReport(
        string rootDirectory,
        CacheIndexBuildOptions? options = null) =>
        BuildIndexWithReport(rootDirectory, options, CancellationToken.None);

    CacheIndexBuildResult BuildIndexWithReport(
        string rootDirectory,
        CacheIndexBuildOptions? options,
        CancellationToken cancellationToken,
        IProgress<CacheScanProgress>? progress = null) =>
        CacheIndexBuildResult.FromIndex(BuildIndex(rootDirectory, options));
}