using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheStorageStatisticsServiceTests
{
    [Fact]
    public void GetStatistics_ShouldMeasureActualNestedFileSizes()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFile(root, Path.Combine("100", "c_1", "80", "video.m4s"), 17);
            WriteFile(root, Path.Combine("100", "c_1", "80", "audio.m4s"), 23);
            WriteFile(root, Path.Combine("200", "legacy", "1.blv"), 31);

            var result = new FileSystemCacheStorageStatisticsService().GetStatistics(root);

            Assert.Equal(Path.GetFullPath(root), result.RootDirectory);
            Assert.Equal(2, result.ManagedEntryCount);
            Assert.Equal(3, result.FileCount);
            Assert.Equal(71, result.TotalBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.Null(result.FirstErrorMessage);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatistics_ShouldOnlyRecognizePositiveAvidDirectChildDirectories()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFile(root, Path.Combine("123", "c_1", "video.bin"), 7);
            Directory.CreateDirectory(Path.Combine(root, "0"));
            Directory.CreateDirectory(Path.Combine(root, "-1"));
            Directory.CreateDirectory(Path.Combine(root, "not-an-avid"));
            Directory.CreateDirectory(Path.Combine(root, "container", "456"));
            File.WriteAllText(Path.Combine(root, "789"), "not a directory");
            WriteFile(
                CacheStorageLayout.GetTrashDirectory(root),
                Path.Combine("999", "trash.bin"),
                101);

            var result = new FileSystemCacheStorageStatisticsService().GetStatistics(root);

            Assert.Equal(1, result.ManagedEntryCount);
            Assert.Equal(1, result.FileCount);
            Assert.Equal(7, result.TotalBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(5, result.SkippedEntryCount);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatistics_ShouldNotFollowReparsePointAndShouldContinueWithNextEntry()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            WriteFile(root, Path.Combine("100", "local.bin"), 5);
            WriteFile(root, Path.Combine("200", "normal.bin"), 11);
            WriteFile(outside, "outside.bin", 29);
            if (!TryCreateDirectoryLink(Path.Combine(root, "100", "outside-link"), outside))
            {
                return;
            }

            var result = new FileSystemCacheStorageStatisticsService().GetStatistics(root);

            Assert.Equal(2, result.ManagedEntryCount);
            Assert.Equal(1, result.FileCount);
            Assert.Equal(11, result.TotalBytes);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.NotNull(result.FirstErrorMessage);
            Assert.True(File.Exists(Path.Combine(outside, "outside.bin")));
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void GetStatistics_ShouldSkipAvidDirectoryThatIsAReparsePoint()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            WriteFile(root, Path.Combine("100", "normal.bin"), 13);
            WriteFile(outside, "outside.bin", 37);
            if (!TryCreateDirectoryLink(Path.Combine(root, "300"), outside))
            {
                return;
            }

            var result = new FileSystemCacheStorageStatisticsService().GetStatistics(root);

            Assert.Equal(1, result.ManagedEntryCount);
            Assert.Equal(1, result.FileCount);
            Assert.Equal(13, result.TotalBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(1, result.SkippedEntryCount);
        }
        finally
        {
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(outside);
        }
    }

    [Fact]
    public void GetStatistics_ShouldHonorPreCanceledToken()
    {
        var root = CreateTempRoot();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                new FileSystemCacheStorageStatisticsService().GetStatistics(
                    root,
                    cancellation.Token));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void SaturatingAdd_ShouldClampInsteadOfOverflowing()
    {
        Assert.Equal(12, FileSystemCacheStorageStatisticsService.SaturatingAdd(5L, 7L));
        Assert.Equal(
            long.MaxValue,
            FileSystemCacheStorageStatisticsService.SaturatingAdd(long.MaxValue - 2, 3));
        Assert.Equal(
            int.MaxValue,
            FileSystemCacheStorageStatisticsService.SaturatingAdd(int.MaxValue - 2, 3));
    }

    private static void WriteFile(string root, string relativePath, int byteCount)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[byteCount]);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"bili_storage_statistics_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in tests.
        }
    }
}
