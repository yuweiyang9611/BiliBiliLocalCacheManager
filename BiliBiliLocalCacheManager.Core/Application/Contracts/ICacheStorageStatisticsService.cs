using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Application.Contracts;

public interface ICacheStorageStatisticsService
{
    CacheStorageStatistics GetStatistics(
        string rootDirectory,
        CancellationToken cancellationToken = default);
}
