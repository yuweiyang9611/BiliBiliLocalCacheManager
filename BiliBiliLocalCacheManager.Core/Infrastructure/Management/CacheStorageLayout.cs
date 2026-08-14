namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

public static class CacheStorageLayout
{
    public const string TrashDirectoryName = ".BiliBiliLocalCacheManager-Trash";

    public static string GetTrashDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return Path.Combine(Path.GetFullPath(rootDirectory), TrashDirectoryName);
    }
}
