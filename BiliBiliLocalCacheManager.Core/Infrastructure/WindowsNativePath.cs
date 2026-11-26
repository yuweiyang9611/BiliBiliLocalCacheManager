namespace BiliBiliLocalCacheManager.Core.Infrastructure;

public static class WindowsNativePath
{
    /// <summary>Formats an absolute path for Win32 file APIs, including long drive and UNC paths.</summary>
    public static string ForFileApi(string path)
    {
        if (path.Length < 260) return path;
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }
}
