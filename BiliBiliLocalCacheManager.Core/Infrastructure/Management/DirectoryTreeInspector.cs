namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

internal static class DirectoryTreeInspector
{
    public static DirectoryTreeStatistics Inspect(
        string directoryPath,
        CancellationToken cancellationToken,
        Action? activity = null)
    {
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(directoryPath));
        var fileCount = 0;
        var totalBytes = 0L;
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Refresh directory identity at traversal time; enumeration metadata is only
            // reused for read-only file statistics, never to authorize deletion.
            directory.Refresh();
            if (!directory.Exists ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "A managed entry must be a physical directory, not a symbolic link or directory junction.");
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                activity?.Invoke();
                entry.Refresh();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(
                        "A managed entry contains a symbolic link or directory junction.");
                }
                if (entry is DirectoryInfo child)
                {
                    directories.Push(child);
                    continue;
                }

                fileCount = FileSystemCacheStorageStatisticsService.SaturatingAdd(fileCount, 1);
                totalBytes = FileSystemCacheStorageStatisticsService.SaturatingAdd(
                    totalBytes, ((FileInfo)entry).Length);
            }
        }
        return new DirectoryTreeStatistics(fileCount, totalBytes);
    }
}

internal sealed record DirectoryTreeStatistics(int FileCount, long TotalBytes);
