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

    public static IReadOnlyList<string> GetCompleteLegacyFragments(
        CachePlaybackProbe probe,
        string childDirectoryName,
        string extension)
    {
        var files = probe.NestedFiles
            .Where(path =>
                string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
                RelativePathStartsWith(path, probe.SegmentDirectory, childDirectoryName))
            .OrderBy(path => ExtractNumericStem(path) ?? int.MaxValue)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int? previous = null;
        foreach (var path in files)
        {
            if (!IsNonEmptyFile(path)) return [];
            if (ExtractNumericStem(path) is not { } number) continue;
            // Legacy fragment numbering starts at zero or one. A gap or repeated number
            // makes the whole quality unusable; dropping a bad fragment shortens the page.
            if (previous is null ? number is not (0 or 1) : number != (long)previous.Value + 1) return [];
            previous = number;
        }
        return files;
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
        // Both names came from enumerating this parent. Preserve their physical spelling:
        // a case-sensitive filesystem can contain both "lua.X" and "lua.x".
        return string.Equals(firstPart, childDirectoryName, StringComparison.Ordinal);
    }

    private static int? ExtractNumericStem(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return int.TryParse(stem, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static int ParseNumber(string? value)
    {
        return int.TryParse(value, out var number) ? number : int.MinValue;
    }
}
