namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

internal static class CacheRootSafety
{
    public static string ValidatePhysicalRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root directory not found: {rootDirectory}");
        }

        EnsurePhysicalDirectory(root, "The cache root directory");
        return root;
    }

    public static void EnsurePhysicalDirectory(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"{description} must be a physical directory, not a symbolic link or directory junction.");
        }
    }
}
