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
        IProgress<CacheScanProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = BuildIndex(rootDirectory, options);
        cancellationToken.ThrowIfCancellationRequested();
        return CacheIndexBuildResult.FromIndex(index);
    }

    IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        CacheIndexBuildOptions? buildOptions,
        CacheSearchOptions searchOptions);

    IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        CacheIndexBuildOptions? buildOptions,
        CacheSearchOptions searchOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searchOptions);
        var result = BuildIndexWithReport(rootDirectory, buildOptions, cancellationToken);
        return result.Index.Search(searchOptions, cancellationToken);
    }

    IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        bool includeIncomplete,
        CacheSearchOptions searchOptions);

    IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        bool includeIncomplete,
        CacheSearchOptions searchOptions,
        CancellationToken cancellationToken)
        => Search(rootDirectory, new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = includeIncomplete
        }, searchOptions, cancellationToken);

    BiliVideoCache? FindByAvid(string rootDirectory, CacheIndexBuildOptions? buildOptions, long avid);

    BiliVideoCache? FindByAvid(
        string rootDirectory,
        CacheIndexBuildOptions? buildOptions,
        long avid,
        CancellationToken cancellationToken)
    {
        var result = BuildIndexWithReport(rootDirectory, buildOptions, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Index.ByAvid.GetValueOrDefault(avid);
    }

    BiliVideoCache? FindByAvid(string rootDirectory, bool includeIncomplete, long avid);

    BiliVideoCache? FindByAvid(
        string rootDirectory,
        bool includeIncomplete,
        long avid,
        CancellationToken cancellationToken)
        => FindByAvid(rootDirectory, new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = includeIncomplete
        }, avid, cancellationToken);

    CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false);
}
