using System.Globalization;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class PlaybackArtifactStore
{
    internal Action? AfterStrictFileEnumerationForTesting { get; set; }
    internal Action<int>? BeforeStrictSnapshotAttemptForTesting { get; set; }

    private sealed record ManagedFileSnapshot(
        FileInfo File,
        long Length,
        DateTime LastWriteTimeUtc);

    private IReadOnlyList<ManagedFileSnapshot> SnapshotManagedFiles()
    {
        return SnapshotAllManagedFiles()
            .Where(snapshot => IsManagedArtifactFile(snapshot.File))
            .ToList();
    }

    private IReadOnlyList<ManagedFileSnapshot> SnapshotAllManagedFiles()
    {
        var snapshots = new List<ManagedFileSnapshot>();
        foreach (var file in EnumerateAllFiles()
                     .Where(file => IsManagedArtifactFile(file) || IsManagedBuildFile(file)))
        {
            if (TryGetFileMetadata(file, out var length, out var lastWriteTimeUtc))
            {
                snapshots.Add(new ManagedFileSnapshot(file, length, lastWriteTimeUtc));
            }
        }

        return snapshots;
    }

    private IReadOnlyList<ManagedFileSnapshot> SnapshotAllManagedFilesStrict()
    {
        const int maximumAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                BeforeStrictSnapshotAttemptForTesting?.Invoke(attempt);
                return SnapshotAllManagedFilesStrictOnce();
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException) &&
                attempt < maximumAttempts)
            {
                // Windows can briefly report delete-pending managed directories
                // as inaccessible while another instance removes empty folders.
                // Restart the whole snapshot so reparse checks and totals all
                // describe one fresh traversal. Persistent ACL failures still
                // escape after the bounded retry budget.
                Thread.Sleep(TimeSpan.FromMilliseconds(attempt * attempt));
            }
        }
    }

    private IReadOnlyList<ManagedFileSnapshot> SnapshotAllManagedFilesStrictOnce()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return Array.Empty<ManagedFileSnapshot>();
        }

        if (!TryGetAttributesStrict(RootDirectory, out var rootAttributes))
        {
            return Array.Empty<ManagedFileSnapshot>();
        }

        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "The playback artifact root must not be a symbolic link or directory junction.");
        }

        var snapshots = new List<ManagedFileSnapshot>();
        foreach (var avidDirectory in GetDirectoriesStrict(RootDirectory))
        {
            var avidName = Path.GetFileName(Path.TrimEndingDirectorySeparator(avidDirectory));
            if (!IsAvidDirectory(avidName))
            {
                continue;
            }

            if (!TryGetAttributesStrict(avidDirectory, out var avidAttributes))
            {
                continue;
            }

            if (avidAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "A managed playback artifact directory must not be a symbolic link or directory junction.");
            }

            foreach (var pageDirectory in GetDirectoriesStrict(avidDirectory))
            {
                var pageName = Path.GetFileName(Path.TrimEndingDirectorySeparator(pageDirectory));
                if (!IsManagedLocation(avidName, pageName))
                {
                    continue;
                }

                if (!TryGetAttributesStrict(pageDirectory, out var pageAttributes))
                {
                    continue;
                }

                if (pageAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(
                        "A managed playback artifact page must not be a symbolic link or directory junction.");
                }

                var pageFiles = GetFilesStrict(pageDirectory);
                AfterStrictFileEnumerationForTesting?.Invoke();
                foreach (var path in pageFiles)
                {
                    var file = new FileInfo(path);
                    if (!IsManagedArtifactFile(file) && !IsManagedBuildFile(file))
                    {
                        continue;
                    }

                    if (!TryGetAttributesStrict(path, out var attributes))
                    {
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw new InvalidOperationException(
                            "A managed playback artifact file must not be a symbolic link.");
                    }

                    if (TryGetFileMetadataStrict(
                            file,
                            out var length,
                            out var lastWriteTimeUtc))
                    {
                        snapshots.Add(new ManagedFileSnapshot(
                            file,
                            length,
                            lastWriteTimeUtc));
                    }
                }
            }
        }

        return snapshots;
    }

    private static string[] GetDirectoriesStrict(string path)
    {
        try
        {
            return Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    private static string[] GetFilesStrict(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryGetAttributesStrict(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool TryGetFileMetadataStrict(
        FileInfo file,
        out long length,
        out DateTime lastWriteTimeUtc)
    {
        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                length = 0;
                lastWriteTimeUtc = DateTime.MaxValue;
                return false;
            }

            length = file.Length;
            lastWriteTimeUtc = file.LastWriteTimeUtc;
            return true;
        }
        catch (FileNotFoundException)
        {
            length = 0;
            lastWriteTimeUtc = DateTime.MaxValue;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            length = 0;
            lastWriteTimeUtc = DateTime.MaxValue;
            return false;
        }
    }

    private static bool TryGetFileMetadata(
        FileInfo file,
        out long length,
        out DateTime lastWriteTimeUtc)
    {
        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                length = 0;
                lastWriteTimeUtc = DateTime.MaxValue;
                return false;
            }

            length = file.Length;
            lastWriteTimeUtc = file.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            length = 0;
            lastWriteTimeUtc = DateTime.MaxValue;
            return false;
        }
    }

    private static long SumLengths(IEnumerable<ManagedFileSnapshot> files)
    {
        var total = 0L;
        foreach (var file in files)
        {
            total = file.Length > long.MaxValue - total
                ? long.MaxValue
                : total + file.Length;
        }

        return total;
    }

    private bool IsManagedArtifactFile(FileInfo file)
    {
        var parts = GetRelativeParts(file.FullName);
        return parts is { Length: 3 } &&
            IsManagedLocation(parts[0], parts[1]) &&
            IsArtifactFileName(parts[2]);
    }

    private bool IsManagedBuildFile(FileInfo file)
    {
        var parts = GetRelativeParts(file.FullName);
        if (parts is not { Length: 3 } ||
            !IsManagedLocation(parts[0], parts[1]) ||
            !string.Equals(Path.GetExtension(parts[2]), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string marker = ".building-";
        var stem = Path.GetFileNameWithoutExtension(parts[2]);
        var markerIndex = stem.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return markerIndex == 24 &&
            IsHexFingerprint(stem[..markerIndex]) &&
            Guid.TryParseExact(stem[(markerIndex + marker.Length)..], "N", out _);
    }

    private bool IsManagedDirectory(string path)
    {
        var parts = GetRelativeParts(path);
        return parts switch
        {
            { Length: 1 } => IsAvidDirectory(parts[0]),
            { Length: 2 } => IsManagedLocation(parts[0], parts[1]),
            _ => false
        };
    }

    private string[] GetRelativeParts(string path)
    {
        var relative = Path.GetRelativePath(RootDirectory, Path.GetFullPath(path));
        return relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsManagedLocation(string avidDirectory, string pageDirectory)
    {
        return IsAvidDirectory(avidDirectory) &&
            pageDirectory.StartsWith("Page_", StringComparison.Ordinal) &&
            int.TryParse(
                pageDirectory.AsSpan("Page_".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var pageIndex) &&
            pageIndex >= 0;
    }

    private static bool IsAvidDirectory(string value)
    {
        return long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var avid) &&
            avid >= 0;
    }

    private static bool IsArtifactFileName(string fileName)
    {
        return string.Equals(Path.GetExtension(fileName), ".mp4", StringComparison.OrdinalIgnoreCase) &&
            IsHexFingerprint(Path.GetFileNameWithoutExtension(fileName));
    }

    private static bool IsHexFingerprint(string value)
    {
        return value.Length == 24 && value.All(Uri.IsHexDigit);
    }

    private static EnumerationOptions CreateSafeRecursiveEnumerationOptions()
    {
        return new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
    }

    private bool IsRootSafeForTraversal()
    {
        try
        {
            return !Directory.Exists(RootDirectory) ||
                !File.GetAttributes(RootDirectory).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private void EnsureExistingPathDoesNotUseReparsePoints(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(RootDirectory);
        var current = File.Exists(path) || Directory.Exists(path)
            ? Path.GetFullPath(path)
            : Path.GetDirectoryName(Path.GetFullPath(path));

        while (!string.IsNullOrWhiteSpace(current))
        {
            var relative = Path.GetRelativePath(root, current);
            if (relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                break;
            }

            FileAttributes? attributes = null;
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    attributes = File.GetAttributes(current);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Another instance removed this cache path after the existence check.
            }

            if (attributes?.HasFlag(FileAttributes.ReparsePoint) == true)
            {
                throw new InvalidOperationException(
                    "Playback artifact paths must not traverse symbolic links or directory junctions.");
            }

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
