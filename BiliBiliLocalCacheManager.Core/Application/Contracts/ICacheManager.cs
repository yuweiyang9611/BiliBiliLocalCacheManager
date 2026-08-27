using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Contracts;

/// <summary>
/// High-level cache operations for CLI and desktop clients.
/// </summary>
public interface ICacheManager
{
    CacheIndex BuildIndex(string rootDirectory, CacheIndexBuildOptions? options = null);

    CacheIndex BuildIndex(string rootDirectory, bool includeIncomplete);

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

    IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        CacheIndexBuildOptions? buildOptions,
        CacheSearchOptions searchOptions);

    IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        bool includeIncomplete,
        CacheSearchOptions searchOptions);

    BiliVideoCache? FindByAvid(string rootDirectory, CacheIndexBuildOptions? buildOptions, long avid);

    BiliVideoCache? FindByAvid(string rootDirectory, bool includeIncomplete, long avid);

    CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false);
}
