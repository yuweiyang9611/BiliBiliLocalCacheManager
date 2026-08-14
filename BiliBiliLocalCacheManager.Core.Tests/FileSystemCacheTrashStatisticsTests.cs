using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheTrashStatisticsTests
{
    [Fact]
    public void GetStatistics_ShouldReturnZeroForMissingAndEmptyTrash()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();

            AssertZero(service.GetStatistics(root), service.GetTrashDirectory(root));

            Directory.CreateDirectory(service.GetTrashDirectory(root));
            AssertZero(service.GetStatistics(root), service.GetTrashDirectory(root));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatistics_ShouldCountOnlyManagedEntriesAndSkipUnknownContents()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashRoot = service.GetTrashDirectory(root);
            var source = Path.Combine(root, "100");
            WriteFile(source, Path.Combine("c_1", "video.bin"), 10);
            WriteFile(source, Path.Combine("c_1", "audio.bin"), 20);
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            Directory.CreateDirectory(Path.Combine(trashRoot, "manual-folder"));
            File.WriteAllText(Path.Combine(trashRoot, "notes.txt"), "keep");

            var result = service.GetStatistics(root);

            Assert.Equal(Path.GetFullPath(trashRoot), result.TrashDirectory);
            Assert.Equal(1, result.ManagedEntryCount);
            Assert.Equal(3, result.FileCount);
            Assert.True(result.TotalBytes > 30);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(2, result.SkippedEntryCount);
            Assert.Null(result.FirstErrorMessage);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatistics_ShouldTreatManagedLookingFileAsSkipped()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashRoot = service.GetTrashDirectory(root);
            Directory.CreateDirectory(trashRoot);
            File.WriteAllText(Path.Combine(trashRoot, CreateManagedEntryName(100)), "keep");

            var result = service.GetStatistics(root);

            Assert.Equal(0, result.ManagedEntryCount);
            Assert.Equal(0, result.FileCount);
            Assert.Equal(0, result.TotalBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(1, result.SkippedEntryCount);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatistics_ShouldTreatNameOnlyDirectoryAsSkipped()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                CreateManagedEntryName(100));
            WriteFile(trashPath, "keep.bin", 12);

            var result = service.GetStatistics(root);

            Assert.Equal(0, result.ManagedEntryCount);
            Assert.Equal(0, result.FileCount);
            Assert.Equal(0, result.TotalBytes);
            Assert.Equal(0, result.FailedEntryCount);
            Assert.Equal(1, result.SkippedEntryCount);
            Assert.Equal(1, result.UntrustedLegacyEntryCount);
            Assert.Equal(1, result.UntrustedLegacyFileCount);
            Assert.Equal(12, result.UntrustedLegacyBytes);
            Assert.True(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatistics_ShouldPreserveCorruptMetadataAndReportFailure()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            var trashPath = Path.Combine(
                service.GetTrashDirectory(root),
                CreateManagedEntryName(100));
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(Path.Combine(trashPath, ".trash-info.json"), "not-json");

            var result = service.GetStatistics(root);

            Assert.Equal(0, result.ManagedEntryCount);
            Assert.Equal(1, result.FailedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.NotNull(result.FirstErrorMessage);
            Assert.True(Directory.Exists(trashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatistics_ShouldNotFollowLinkAndShouldContinueWithNextManagedEntry()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            var service = new FileSystemCacheTrashService();
            WriteFile(Path.Combine(root, "100"), "local.bin", 5);
            WriteFile(Path.Combine(root, "200"), "normal.bin", 17);
            var linkedMove = service.MoveToTrash(root, 100);
            var normalMove = service.MoveToTrash(root, 200);
            Assert.True(linkedMove.Succeeded);
            Assert.True(normalMove.Succeeded);
            var linkedEntry = linkedMove.TrashPath!;
            WriteFile(outside, "outside.bin", 41);
            if (!TryCreateDirectoryLink(Path.Combine(linkedEntry, "outside-link"), outside))
            {
                return;
            }

            var result = service.GetStatistics(root);

            Assert.Equal(2, result.ManagedEntryCount);
            Assert.Equal(2, result.FileCount);
            Assert.True(result.TotalBytes > 17);
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
    public void GetStatistics_ShouldRejectTrashRootLink()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            WriteFile(outside, "outside.bin", 19);
            var trashRoot = CacheStorageLayout.GetTrashDirectory(root);
            if (!TryCreateDirectoryLink(trashRoot, outside))
            {
                return;
            }

            var service = new FileSystemCacheTrashService();
            Assert.Throws<InvalidOperationException>(() => service.GetStatistics(root));
            Assert.True(File.Exists(Path.Combine(outside, "outside.bin")));
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
                new FileSystemCacheTrashService().GetStatistics(root, cancellation.Token));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static void AssertZero(
        BiliBiliLocalCacheManager.Core.Application.Models.CacheTrashStatistics result,
        string expectedPath)
    {
        Assert.Equal(Path.GetFullPath(expectedPath), result.TrashDirectory);
        Assert.Equal(0, result.ManagedEntryCount);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.TotalBytes);
        Assert.Equal(0, result.FailedEntryCount);
        Assert.Equal(0, result.SkippedEntryCount);
        Assert.Null(result.FirstErrorMessage);
        Assert.Equal(0, result.UntrustedLegacyEntryCount);
        Assert.Equal(0, result.UntrustedLegacyFileCount);
        Assert.Equal(0, result.UntrustedLegacyBytes);
    }

    private static string CreateManagedEntryName(long avid)
    {
        return $"{avid}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
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
            $"bili_trash_statistics_test_{Guid.NewGuid():N}");
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
