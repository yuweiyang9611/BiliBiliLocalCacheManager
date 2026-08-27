using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class PlaybackArtifactStore : IPlaybackArtifactStore
{
    private static readonly ConcurrentDictionary<string, PathLock> PathLocks =
        new(PlaybackFileSystem.PathComparer);

    public static PlaybackArtifactStore Shared { get; } = new();

    public PlaybackArtifactStore(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? GetDefaultRootDirectory());
    }

    public string RootDirectory { get; }

    public PlaybackArtifactMaterialization GetOrCreate(
        CachePlaybackPlan plan,
        string extension,
        Action<string> producer,
        CancellationToken cancellationToken = default)
    {
        return GetOrCreate(
            plan,
            extension,
            producer,
            cancellationToken,
            reportProgress: null);
    }

    public PlaybackArtifactMaterialization GetOrCreate(
        CachePlaybackPlan plan,
        string extension,
        Action<string> producer,
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(producer);

        var normalizedExtension = NormalizeExtension(extension);
        var outputPath = BuildOutputPath(plan, normalizedExtension);
        EnsurePathIsInsideRoot(outputPath);

        var pathLock = AcquirePathLock(outputPath);
        try
        {
            using (new SemaphoreReleaser(pathLock.SyncRoot, cancellationToken))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var crossProcessLock = AcquireCrossProcessLock(
                    outputPath,
                    cancellationToken,
                    reportProgress);
                cancellationToken.ThrowIfCancellationRequested();

                if (IsReusable(outputPath))
                {
                    Touch(outputPath);
                    return new PlaybackArtifactMaterialization(outputPath, WasReused: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var buildPath = Path.Combine(
                    Path.GetDirectoryName(outputPath)!,
                    $"{Path.GetFileNameWithoutExtension(outputPath)}.building-{Guid.NewGuid():N}{normalizedExtension}");

                EnsurePathIsInsideRoot(buildPath);
                try
                {
                    producer(buildPath);
                    if (!IsReusable(buildPath))
                    {
                        throw new InvalidDataException("播放产物生成器没有创建有效的输出文件。");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(
                            outputPath,
                            BuildOutputPath(plan, normalizedExtension),
                            PlaybackFileSystem.PathComparison))
                    {
                        throw new IOException("Source media changed while the playback artifact was being generated.");
                    }

                    File.Move(buildPath, outputPath, overwrite: true);
                    return new PlaybackArtifactMaterialization(outputPath, WasReused: false);
                }
                finally
                {
                    TryDeleteFile(buildPath);
                }
            }
        }
        finally
        {
            ReleasePathLock(outputPath, pathLock);
        }
    }

    private static PathLock AcquirePathLock(string outputPath)
    {
        while (true)
        {
            var pathLock = PathLocks.GetOrAdd(outputPath, static _ => new PathLock());
            lock (pathLock.ReferenceSync)
            {
                if (pathLock.Retired)
                {
                    continue;
                }

                pathLock.ReferenceCount++;
                return pathLock;
            }
        }
    }

    private static void ReleasePathLock(string outputPath, PathLock pathLock)
    {
        lock (pathLock.ReferenceSync)
        {
            pathLock.ReferenceCount--;
            if (pathLock.ReferenceCount != 0)
            {
                return;
            }

            pathLock.Retired = true;
            if (PathLocks.TryGetValue(outputPath, out var current) && ReferenceEquals(current, pathLock))
            {
                PathLocks.TryRemove(outputPath, out _);
            }
        }
    }

    private HashSet<string> SnapshotProtectedPaths(IEnumerable<string> paths)
    {
        var protectedPaths = new HashSet<string>(PlaybackFileSystem.PathComparer);
        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var fullPath = Path.GetFullPath(path);
            EnsurePathIsInsideRoot(fullPath);
            if (!IsManagedArtifactFile(new FileInfo(fullPath)))
            {
                throw new ArgumentException(
                    $"Protected path is not a managed playback artifact: {path}",
                    nameof(paths));
            }

            protectedPaths.Add(fullPath);
        }

        return protectedPaths;
    }

    private string BuildOutputPath(CachePlaybackPlan plan, string extension)
    {
        var fingerprint = ComputeFingerprint(plan);
        return Path.Combine(
            RootDirectory,
            plan.Avid.ToString(CultureInfo.InvariantCulture),
            $"Page_{plan.PageIndex}",
            $"{fingerprint}{extension}");
    }

    private static string ComputeFingerprint(CachePlaybackPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine(ArtifactProfile);
        builder.AppendLine(plan.StructureKind);
        builder.AppendLine(plan.MaterialKind.ToString());

        foreach (var path in plan.MediaFiles)
        {
            var fullPath = Path.GetFullPath(path);
            var normalizedPath = OperatingSystem.IsWindows()
                ? fullPath.ToUpperInvariant()
                : fullPath;
            builder.Append(normalizedPath).Append('|');
            try
            {
                var info = new FileInfo(fullPath);
                builder.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                builder.Append("missing");
            }

            builder.AppendLine();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private IReadOnlyList<FileInfo> EnumerateAllFiles()
    {
        try
        {
            if (!IsRootSafeForTraversal())
            {
                return Array.Empty<FileInfo>();
            }

            return Directory.EnumerateFiles(
                    RootDirectory,
                    "*",
                    CreateSafeRecursiveEnumerationOptions())
                .Select(path => new FileInfo(path))
                .ToList();
        }
        catch
        {
            return Array.Empty<FileInfo>();
        }
    }

    private bool DeleteManagedFile(
        FileInfo file,
        ref int deletedCount,
        ref int failedCount,
        ref long freedBytes)
    {
        try
        {
            EnsurePathIsInsideRoot(file.FullName);
            if (!TryGetFileMetadata(file, out var length, out _))
            {
                return true;
            }

            file.Delete();
            deletedCount++;
            freedBytes = length > long.MaxValue - freedBytes
                ? long.MaxValue
                : freedBytes + length;
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            failedCount++;
            return false;
        }
    }

    private void DeleteEmptyDirectories()
    {
        try
        {
            if (!IsRootSafeForTraversal())
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(
                         RootDirectory,
                         "*",
                         CreateSafeRecursiveEnumerationOptions())
                         .Where(IsManagedDirectory)
                         .OrderByDescending(path => GetRelativeParts(path).Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch
        {
            // 清理属于尽力而为，不影响播放主流程。
        }
    }

    private void EnsurePathIsInsideRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, fullPath);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("播放产物路径超出受管目录，已拒绝操作。");
        }



        EnsureExistingPathDoesNotUseReparsePoints(fullPath);
    }

    private static bool IsReusable(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // 文件仍然可播放时，更新时间失败不应阻断复用。
        }
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
        {
            normalized = "." + normalized;
        }

        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains(Path.DirectorySeparatorChar) ||
            normalized.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Invalid artifact extension.", nameof(extension));
        }

        return normalized;
    }


    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 尽力清理构建失败留下的临时文件。
        }
    }

    private sealed class PathLock
    {
        public object ReferenceSync { get; } = new();
        public SemaphoreSlim SyncRoot { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }
}
