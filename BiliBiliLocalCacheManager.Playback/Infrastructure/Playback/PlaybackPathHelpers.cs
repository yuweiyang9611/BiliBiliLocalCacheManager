using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal static class PlaybackPathHelpers
{
    public static bool IsNumericName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(char.IsDigit);
    }

    public static bool HasLuaChild(CachePlaybackProbe probe)
    {
        return probe.ChildDirectoryNames.Any(name => name.StartsWith("lua.", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> GetFilesUnderDirectory(
        CachePlaybackProbe probe,
        string childDirectoryName,
        string extension)
    {
        return probe.NestedFiles
            .Where(path =>
                string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
                RelativePathStartsWith(path, probe.SegmentDirectory, childDirectoryName) &&
                IsNonEmptyFile(path))
            .OrderBy(path => ExtractNumericStem(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> GetQualityDirectories(CachePlaybackProbe probe)
    {
        return probe.ChildDirectories
            .Where(path => IsNumericName(Path.GetFileName(path)))
            .OrderByDescending(path => ParseNumber(Path.GetFileName(path)))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static long GetTotalBytes(IEnumerable<string> files)
    {
        return files.Sum(file =>
        {
            try
            {
                return new FileInfo(file).Length;
            }
            catch
            {
                return 0L;
            }
        });
    }

    public static string? GetFileInChildDirectory(string childDirectoryPath, string fileName)
    {
        if (!IsPhysicalDirectory(childDirectoryPath))
        {
            return null;
        }

        var candidate = Path.Combine(childDirectoryPath, fileName);
        return IsNonEmptyFile(candidate) ? candidate : null;
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.ReparsePoint) &&
                   !attributes.HasFlag(FileAttributes.Directory) &&
                   new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsPhysicalDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) &&
                   !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool RelativePathStartsWith(string filePath, string segmentDirectory, string childDirectoryName)
    {
        var relative = Path.GetRelativePath(segmentDirectory, filePath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var splitIndex = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        var firstPart = splitIndex >= 0 ? relative[..splitIndex] : relative;
        return string.Equals(firstPart, childDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractNumericStem(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return int.TryParse(stem, out var number) ? number : int.MaxValue;
    }

    private static int ParseNumber(string? value)
    {
        return int.TryParse(value, out var number) ? number : int.MinValue;
    }
}
