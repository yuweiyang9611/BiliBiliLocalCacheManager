using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public sealed class FileSystemCacheStorageStatisticsService : ICacheStorageStatisticsService
{
    public CacheStorageStatistics GetStatistics(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = CacheRootSafety.ValidatePhysicalRoot(rootDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var managedEntryCount = 0;
        var fileCount = 0;
        var failedEntryCount = 0;
        var skippedEntryCount = 0;
        var totalBytes = 0L;
        string? firstError = null;

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (string.Equals(
                    name,
                    CacheStorageLayout.TrashDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsPositiveAvid(name))
            {
                skippedEntryCount = SaturatingAdd(skippedEntryCount, 1);
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(path);
                if (!attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skippedEntryCount = SaturatingAdd(skippedEntryCount, 1);
                    continue;
                }

                managedEntryCount = SaturatingAdd(managedEntryCount, 1);
                var inspection = InspectDirectoryTree(path, cancellationToken);
                fileCount = SaturatingAdd(fileCount, inspection.FileCount);
                totalBytes = SaturatingAdd(totalBytes, inspection.TotalBytes);
            }
            catch (Exception ex) when (IsInspectionFailure(ex))
            {
                failedEntryCount = SaturatingAdd(failedEntryCount, 1);
                firstError ??= ex.Message;
            }
        }

        return new CacheStorageStatistics(
            root,
            managedEntryCount,
            fileCount,
            totalBytes,
            failedEntryCount,
            skippedEntryCount,
            firstError);
    }

    private static bool IsPositiveAvid(string value)
    {
        return long.TryParse(
                   value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var avid) &&
               avid > 0;
    }

    private static DirectoryInspection InspectDirectoryTree(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(directoryPath);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "A managed cache entry contains a symbolic link or directory junction.");
        }

        var fileCount = 0;
        var totalBytes = 0L;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "A managed cache entry contains a symbolic link or directory junction.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var child = InspectDirectoryTree(path, cancellationToken);
                fileCount = SaturatingAdd(fileCount, child.FileCount);
                totalBytes = SaturatingAdd(totalBytes, child.TotalBytes);
                continue;
            }

            fileCount = SaturatingAdd(fileCount, 1);
            totalBytes = SaturatingAdd(totalBytes, new FileInfo(path).Length);
        }

        return new DirectoryInspection(fileCount, totalBytes);
    }

    internal static long SaturatingAdd(long left, long right)
    {
        if (left < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(left));
        }

        if (right < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(right));
        }

        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }

    internal static int SaturatingAdd(int left, int right)
    {
        if (left < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(left));
        }

        if (right < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(right));
        }

        return right > int.MaxValue - left ? int.MaxValue : left + right;
    }

    private static bool IsInspectionFailure(Exception ex)
    {
        return ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            System.Security.SecurityException;
    }

    private sealed record DirectoryInspection(int FileCount, long TotalBytes);
}
