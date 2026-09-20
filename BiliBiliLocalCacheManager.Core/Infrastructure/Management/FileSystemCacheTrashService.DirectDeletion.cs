using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed partial class FileSystemCacheTrashService
{
    internal CacheDeletionResult DeleteCacheByAvid(string rootDirectory, long avid, bool dryRun)
    {
        var root = CacheRootSafety.ValidatePhysicalRoot(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(avid);
        var targetPath = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));

        using var transaction = EnterMutationTransaction(root, CacheTrashMutationOperation.Delete);
        if (!Directory.Exists(targetPath))
        {
            return new CacheDeletionResult(false, false, targetPath, null);
        }

        try
        {
            using var rootLease = OperatingSystem.IsWindows()
                ? OpenPhysicalDirectoryLease(root, "The cache root directory", allowDelete: false)
                : null;
            EnsureDirectChild(root, targetPath);
            EnsurePhysicalDirectory(targetPath, "The avid cache directory");
            if (dryRun)
            {
                InspectDirectoryTree(targetPath, CancellationToken.None);
                return new CacheDeletionResult(true, false, targetPath, null);
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Race-safe permanent cache deletion is currently supported only on Windows. Move the cache to trash instead.");
            }

            // Each recursive level holds a non-share-delete handle while enumerating its
            // children. All deletions use those handles, never a revalidated path.
            DeleteDirectoryTree(targetPath, new PurgeDeletionProgress(), inspectBeforeDeletion: true);
            return new CacheDeletionResult(true, true, targetPath, null);
        }
        catch (Exception ex) when (IsPurgeFailure(ex))
        {
            return new CacheDeletionResult(true, false, targetPath, ex.Message);
        }
    }
}
