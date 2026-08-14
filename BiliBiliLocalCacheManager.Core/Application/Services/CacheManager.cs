using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Application.Services;

/// <summary>
/// Default application service that orchestrates index/search/delete operations.
/// </summary>
public sealed class CacheManager : ICacheManager
{
    private readonly ICacheIndexBuilder _indexBuilder;
    private readonly ICacheDeletionService _deletionService;

    public CacheManager()
        : this(new FileSystemCacheIndexBuilder(), new FileSystemCacheDeletionService())
    {
    }

    // ReSharper disable once ConvertToPrimaryConstructor
    public CacheManager(ICacheIndexBuilder indexBuilder, ICacheDeletionService deletionService)
    {
        _indexBuilder = indexBuilder ?? throw new ArgumentNullException(nameof(indexBuilder));
        _deletionService = deletionService ?? throw new ArgumentNullException(nameof(deletionService));
    }

    public CacheIndex BuildIndex(string rootDirectory, CacheIndexBuildOptions? options = null)
    {
        return _indexBuilder.BuildIndex(rootDirectory, options);
    }

    public CacheIndexBuildResult BuildIndexWithReport(
        string rootDirectory,
        CacheIndexBuildOptions? options = null)
    {
        return _indexBuilder.BuildIndexWithReport(rootDirectory, options);
    }

    public CacheIndexBuildResult BuildIndexWithReport(
        string rootDirectory,
        CacheIndexBuildOptions? options,
        CancellationToken cancellationToken,
        IProgress<CacheScanProgress>? progress = null)
    {
        return _indexBuilder.BuildIndexWithReport(rootDirectory, options, cancellationToken, progress);
    }

    public CacheIndex BuildIndex(string rootDirectory, bool includeIncomplete)
    {
        return BuildIndex(rootDirectory, new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = includeIncomplete
        });
    }

    public IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        CacheIndexBuildOptions? buildOptions,
        CacheSearchOptions searchOptions)
    {
        ArgumentNullException.ThrowIfNull(searchOptions);

        var index = BuildIndex(rootDirectory, buildOptions);
        return index.Search(searchOptions);
    }

    public IReadOnlyCollection<BiliVideoCache> Search(
        string rootDirectory,
        bool includeIncomplete,
        CacheSearchOptions searchOptions)
    {
        return Search(rootDirectory, new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = includeIncomplete
        }, searchOptions);
    }

    public BiliVideoCache? FindByAvid(string rootDirectory, CacheIndexBuildOptions? buildOptions, long avid)
    {
        var index = BuildIndex(rootDirectory, buildOptions);
        return index.ByAvid.TryGetValue(avid, out var cache) ? cache : null;
    }

    public BiliVideoCache? FindByAvid(string rootDirectory, bool includeIncomplete, long avid)
    {
        return FindByAvid(rootDirectory, new CacheIndexBuildOptions
        {
            IncludeIncompleteEntries = includeIncomplete
        }, avid);
    }

    public CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false)
    {
        return _deletionService.DeleteByAvid(rootDirectory, avid, dryRun);
    }
}
