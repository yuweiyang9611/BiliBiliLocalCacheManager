namespace BiliBiliLocalCacheManager.Core.Domain.Models;

/// <summary>
/// 简单区分旧缓存和新缓存。
/// </summary>
public enum CacheVersion
{
    Legacy = 0,
    Modern = 1
}