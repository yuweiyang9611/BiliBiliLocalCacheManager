using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

/// <summary>
/// Permanently deletes one physical root/{avid}/... cache tree.
/// </summary>
public sealed class FileSystemCacheDeletionService : ICacheDeletionService
{
    private readonly FileSystemCacheTrashService _fileSystem = new();

    internal Action<string>? BeforeDirectoryEnumerationForTesting
    {
        get => _fileSystem.BeforeTrashDirectoryEnumerationForTesting;
        set => _fileSystem.BeforeTrashDirectoryEnumerationForTesting = value;
    }

    public CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false)
        => _fileSystem.DeleteCacheByAvid(rootDirectory, avid, dryRun);
}
