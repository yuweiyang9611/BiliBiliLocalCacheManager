using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class PlaybackArtifactStore
{
    private const string ArtifactProfile = "mp4-stream-copy-v2";
    private const string CrossProcessLockSuffix = ".lock";
    private const string CrossProcessWaitStage = "正在等待其他实例生成播放缓存";

    public PlaybackArtifactCacheStatistics GetStatistics()
    {
        return CreateCacheStatistics(SnapshotAllManagedFilesStrict());
    }

    public PlaybackArtifactCleanupResult Clear()
    {
        if (!Directory.Exists(RootDirectory))
        {
            var emptyStatistics = new PlaybackArtifactCacheStatistics(RootDirectory, 0, 0);
            return new PlaybackArtifactCleanupResult(0, 0, 0, 0, emptyStatistics);
        }

        var deletedCount = 0;
        var failedCount = 0;
        var freedBytes = 0L;
        foreach (var file in SnapshotAllManagedFiles())
        {
            if (IsManagedBuildFile(file.File))
            {
                DeleteStaleBuildFileIfUnlocked(
                    file.File,
                    ref deletedCount,
                    ref failedCount,
                    ref freedBytes);
            }
            else
            {
                DeleteManagedFileIfUnlocked(
                    file.File,
                    ref deletedCount,
                    ref failedCount,
                    ref freedBytes);
            }
        }

        DeleteEmptyDirectories();
        var statistics = CreateCacheStatistics(SnapshotAllManagedFiles());
        return new PlaybackArtifactCleanupResult(
            deletedCount,
            freedBytes,
            failedCount,
            statistics.TotalBytes,
            statistics);
    }

    private PlaybackArtifactCacheStatistics CreateCacheStatistics(
        IReadOnlyCollection<ManagedFileSnapshot> files)
    {
        return new PlaybackArtifactCacheStatistics(
            RootDirectory,
            files.Count,
            SumLengths(files));
    }

    private static string GetDefaultRootDirectory()
    {
        var baseDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.GetTempPath();
        }

        return Path.Combine(
            baseDirectory,
            "BiliBiliLocalCacheManager",
            "TranscodeCache");
    }

    private static FileStream AcquireCrossProcessLock(
        string outputPath,
        CancellationToken cancellationToken,
        Action<string, double?>? reportProgress)
    {
        var lockPath = outputPath + CrossProcessLockSuffix;
        var waitTimer = Stopwatch.StartNew();
        var waitReported = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (IsCrossProcessLockContention(ex))
            {
                if (!waitReported)
                {
                    reportProgress?.Invoke(CrossProcessWaitStage, null);
                    waitReported = true;
                }

                if (waitTimer.Elapsed >= TimeSpan.FromMinutes(10))
                {
                    throw new TimeoutException(
                        "Timed out waiting for another process to finish generating the playback artifact.",
                        ex);
                }

                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }

    private static bool IsCrossProcessLockContention(IOException exception)
    {
        var nativeErrorCode = exception.HResult & 0xFFFF;
        return nativeErrorCode is 32 or 33 ||
               (OperatingSystem.IsLinux() && nativeErrorCode == 11);
    }

    private bool DeleteStaleBuildFileIfUnlocked(
        FileInfo file,
        ref int deletedCount,
        ref int failedCount,
        ref long freedBytes)
    {
        var outputPath = TryGetOutputPathForBuildFile(file);
        if (outputPath is null)
        {
            return DeleteManagedFile(
                file,
                ref deletedCount,
                ref failedCount,
                ref freedBytes);
        }

        using var fileLock = TryAcquireCrossProcessLock(outputPath);
        if (fileLock is null)
        {
            failedCount++;
            return false;
        }

        return DeleteManagedFile(
            file,
            ref deletedCount,
            ref failedCount,
            ref freedBytes);
    }

    private string? TryGetOutputPathForBuildFile(FileInfo file)
    {
        const string marker = ".building-";
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        var markerIndex = stem.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return null;
        }

        var buildId = stem[(markerIndex + marker.Length)..];
        if (!Guid.TryParseExact(buildId, "N", out _))
        {
            return null;
        }

        var outputPath = Path.Combine(
            file.DirectoryName!,
            stem[..markerIndex] + file.Extension);
        EnsurePathIsInsideRoot(outputPath);
        return outputPath;
    }

    private bool DeleteManagedFileIfUnlocked(
        FileInfo file,
        ref int deletedCount,
        ref int failedCount,
        ref long freedBytes)
    {
        using var fileLock = TryAcquireCrossProcessLock(file.FullName);
        if (fileLock is null)
        {
            failedCount++;
            return false;
        }

        return DeleteManagedFile(
            file,
            ref deletedCount,
            ref failedCount,
            ref freedBytes);
    }

    private FileStream? TryAcquireCrossProcessLock(string outputPath)
    {
        var lockPath = outputPath + CrossProcessLockSuffix;
        EnsurePathIsInsideRoot(lockPath);
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    private sealed class SemaphoreReleaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public SemaphoreReleaser(
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            semaphore.Wait(cancellationToken);
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
