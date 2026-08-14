using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

/// <summary>
/// Permanently deletes one physical root/{avid}/... cache tree.
/// </summary>
public sealed class FileSystemCacheDeletionService : ICacheDeletionService
{
    public CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false)
    {
        var root = CacheRootSafety.ValidatePhysicalRoot(rootDirectory);
        if (avid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(avid), "Avid must be positive.");
        }

        var avidFolderName = avid.ToString(CultureInfo.InvariantCulture);
        var targetPath = Path.Combine(root, avidFolderName);
        if (!Directory.Exists(targetPath))
        {
            return new CacheDeletionResult(
                found: false,
                deleted: false,
                targetPath: targetPath,
                errorMessage: null);
        }

        try
        {
            CacheRootSafety.EnsurePhysicalDirectory(targetPath, "The avid cache directory");
            ValidateDirectoryTree(targetPath);

            if (dryRun)
            {
                return new CacheDeletionResult(
                    found: true,
                    deleted: false,
                    targetPath: targetPath,
                    errorMessage: null);
            }

            // Revalidate both paths immediately before the destructive step.
            CacheRootSafety.ValidatePhysicalRoot(root);
            CacheRootSafety.EnsurePhysicalDirectory(targetPath, "The avid cache directory");
            DeleteDirectoryTree(targetPath);
            return new CacheDeletionResult(
                found: true,
                deleted: true,
                targetPath: targetPath,
                errorMessage: null);
        }
        catch (Exception ex)
        {
            return new CacheDeletionResult(
                found: true,
                deleted: false,
                targetPath: targetPath,
                errorMessage: ex.Message);
        }
    }

    private static void ValidateDirectoryTree(string directoryPath)
    {
        CacheRootSafety.EnsurePhysicalDirectory(directoryPath, "A managed cache directory");

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "A managed cache directory contains a symbolic link or directory junction.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                ValidateDirectoryTree(path);
            }
        }
    }

    private static void DeleteDirectoryTree(string directoryPath)
    {
        CacheRootSafety.EnsurePhysicalDirectory(directoryPath, "A managed cache directory");

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "Refusing to follow a symbolic link while deleting a managed cache directory.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteDirectoryTree(path);
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
        }

        var directoryAttributes = File.GetAttributes(directoryPath);
        if (directoryAttributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(directoryPath, directoryAttributes & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(directoryPath);
    }
}
