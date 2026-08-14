using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class ReparsePointSafetyTests
{
    [Fact]
    public void BuildIndex_ShouldReportAndSkipLinkedAvidAndSegmentDirectories()
    {
        var root = CreateTempRoot("scan-root");
        var externalAvid = CreateTempRoot("external-avid");
        var externalSegment = CreateTempRoot("external-segment");
        var avidLink = Path.Combine(root, "111");
        var physicalAvid = Path.Combine(root, "222");
        var segmentLink = Path.Combine(physicalAvid, "c_1");

        try
        {
            CreateSegment(externalAvid, 111);
            Directory.CreateDirectory(physicalAvid);
            File.WriteAllText(Path.Combine(externalSegment, "entry.json"), BuildEntryJson(222));
            File.WriteAllText(Path.Combine(externalSegment, "video.mp4"), "external-media");

            if (!TryCreateDirectoryLink(avidLink, externalAvid) ||
                !TryCreateDirectoryLink(segmentLink, externalSegment))
            {
                return;
            }

            var result = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root);

            Assert.Empty(result.Index.VideoCaches);
            Assert.Contains(result.Issues, issue =>
                issue.Kind == CacheScanIssueKind.InaccessibleDirectory &&
                string.Equals(issue.Path, avidLink, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Issues, issue =>
                issue.Kind == CacheScanIssueKind.InaccessibleDirectory &&
                string.Equals(issue.Path, segmentLink, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectoryLink(segmentLink);
            DeleteDirectoryLink(avidLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalAvid);
            SafeDeleteDirectory(externalSegment);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldRejectLinkedTrashRootWithoutMovingOriginalCache()
    {
        var root = CreateTempRoot("trash-root");
        var externalTrash = CreateTempRoot("external-trash");
        var source = Path.Combine(root, "100");
        var marker = Path.Combine(externalTrash, "external-marker.txt");
        var service = new FileSystemCacheTrashService();
        var trashLink = service.GetTrashDirectory(root);

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "cache.txt"), "cache");
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(trashLink, externalTrash))
            {
                return;
            }

            var result = service.MoveToTrash(root, 100);

            Assert.False(result.Succeeded);
            Assert.True(Directory.Exists(source));
            Assert.Equal("external", File.ReadAllText(marker));
            Assert.Single(Directory.EnumerateFileSystemEntries(externalTrash));
        }
        finally
        {
            DeleteDirectoryLink(trashLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalTrash);
        }
    }

    [Fact]
    public void MoveToTrash_ShouldRejectLinkedAvidWithoutChangingExternalTarget()
    {
        var root = CreateTempRoot("linked-avid-root");
        var externalAvid = CreateTempRoot("linked-avid-target");
        var avidLink = Path.Combine(root, "100");
        var marker = Path.Combine(externalAvid, "external-marker.txt");

        try
        {
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(avidLink, externalAvid))
            {
                return;
            }

            var result = new FileSystemCacheTrashService().MoveToTrash(root, 100);

            Assert.False(result.Succeeded);
            Assert.True(Directory.Exists(avidLink));
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(avidLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalAvid);
        }
    }

    [Fact]
    public void Restore_ShouldRejectLinkedTrashEntryWithoutChangingExternalTarget()
    {
        var root = CreateTempRoot("restore-root");
        var externalEntry = CreateTempRoot("restore-target");
        var service = new FileSystemCacheTrashService();
        var trashRoot = service.GetTrashDirectory(root);
        var entryLink = Path.Combine(
            trashRoot,
            $"100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
        var marker = Path.Combine(externalEntry, "external-marker.txt");

        try
        {
            Directory.CreateDirectory(trashRoot);
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(entryLink, externalEntry))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() => service.Restore(root, 100, entryLink));
            Assert.False(Directory.Exists(Path.Combine(root, "100")));
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(entryLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalEntry);
        }
    }

    [Fact]
    public void Restore_ShouldRejectLinkedTrashRootWithoutChangingExternalTarget()
    {
        var root = CreateTempRoot("restore-linked-trash-root");
        var externalTrash = CreateTempRoot("restore-linked-trash-target");
        var service = new FileSystemCacheTrashService();
        var trashLink = service.GetTrashDirectory(root);
        var entryName = $"100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        var externalEntry = Path.Combine(externalTrash, entryName);
        var marker = Path.Combine(externalEntry, "external-marker.txt");

        try
        {
            Directory.CreateDirectory(externalEntry);
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(trashLink, externalTrash))
            {
                return;
            }

            var linkedEntry = Path.Combine(trashLink, entryName);
            Assert.Throws<InvalidOperationException>(() => service.Restore(root, 100, linkedEntry));
            Assert.False(Directory.Exists(Path.Combine(root, "100")));
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(trashLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalTrash);
        }
    }

    [Fact]
    public void Restore_ShouldRejectLinkedOriginalAvidWithoutOverwritingExternalTarget()
    {
        var root = CreateTempRoot("restore-linked-avid-root");
        var externalAvid = CreateTempRoot("restore-linked-avid-target");
        var service = new FileSystemCacheTrashService();
        var avidLink = Path.Combine(root, "100");
        var marker = Path.Combine(externalAvid, "external-marker.txt");

        try
        {
            Directory.CreateDirectory(avidLink);
            File.WriteAllText(Path.Combine(avidLink, "cache.txt"), "cache");
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(avidLink, externalAvid))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() => service.Restore(root, 100, moved.TrashPath!));
            Assert.True(Directory.Exists(moved.TrashPath));
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(avidLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalAvid);
        }
    }

    [Fact]
    public void LinkedCacheRoot_ShouldBeRejectedByScannerAndStorageStatistics()
    {
        var linkParent = CreateTempRoot("linked-root-parent");
        var externalRoot = CreateTempRoot("linked-root-target");
        var rootLink = Path.Combine(linkParent, "cache-root");
        var marker = Path.Combine(externalRoot, "external-marker.txt");

        try
        {
            CreateSegment(Path.Combine(externalRoot, "100"), 100);
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(rootLink, externalRoot))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() =>
                new FileSystemCacheIndexBuilder().BuildIndexWithReport(rootLink));
            Assert.Throws<InvalidOperationException>(() =>
                new FileSystemCacheStorageStatisticsService().GetStatistics(rootLink));
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(rootLink);
            SafeDeleteDirectory(linkParent);
            SafeDeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void Purge_ShouldRejectLinkedCacheRootWithoutChangingExternalTrash()
    {
        var linkParent = CreateTempRoot("purge-linked-root-parent");
        var externalRoot = CreateTempRoot("purge-linked-root-target");
        var rootLink = Path.Combine(linkParent, "cache-root");
        var service = new FileSystemCacheTrashService();
        var trashRoot = service.GetTrashDirectory(externalRoot);
        var managedEntry = Path.Combine(
            trashRoot,
            $"100_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}");
        var marker = Path.Combine(managedEntry, "external-marker.txt");

        try
        {
            Directory.CreateDirectory(managedEntry);
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(rootLink, externalRoot))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() => service.Purge(rootLink));
            Assert.True(Directory.Exists(managedEntry));
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(rootLink);
            SafeDeleteDirectory(linkParent);
            SafeDeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void DeleteByAvid_ShouldRejectLinkedCacheRootWithoutChangingExternalCache()
    {
        var linkParent = CreateTempRoot("delete-linked-root-parent");
        var externalRoot = CreateTempRoot("delete-linked-root-target");
        var rootLink = Path.Combine(linkParent, "cache-root");
        var externalAvid = Path.Combine(externalRoot, "100");
        var marker = Path.Combine(externalAvid, "external-marker.txt");

        try
        {
            Directory.CreateDirectory(externalAvid);
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(rootLink, externalRoot))
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() =>
                new FileSystemCacheDeletionService().DeleteByAvid(rootLink, 100));
            Assert.True(Directory.Exists(externalAvid));
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(rootLink);
            SafeDeleteDirectory(linkParent);
            SafeDeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void DeleteByAvid_ShouldRejectLinkedAvidWithoutChangingExternalTarget()
    {
        var root = CreateTempRoot("delete-linked-avid-root");
        var externalAvid = CreateTempRoot("delete-linked-avid-target");
        var avidLink = Path.Combine(root, "100");
        var marker = Path.Combine(externalAvid, "external-marker.txt");

        try
        {
            File.WriteAllText(marker, "external");
            if (!TryCreateDirectoryLink(avidLink, externalAvid))
            {
                return;
            }

            var result = new FileSystemCacheDeletionService().DeleteByAvid(root, 100);

            Assert.True(result.Found);
            Assert.False(result.Deleted);
            Assert.NotNull(result.ErrorMessage);
            Assert.Equal("external", File.ReadAllText(marker));
        }
        finally
        {
            DeleteDirectoryLink(avidLink);
            SafeDeleteDirectory(root);
            SafeDeleteDirectory(externalAvid);
        }
    }

    private static void CreateSegment(string avidDirectory, long avid)
    {
        var segment = Path.Combine(avidDirectory, "c_1");
        Directory.CreateDirectory(segment);
        File.WriteAllText(Path.Combine(segment, "entry.json"), BuildEntryJson(avid));
        File.WriteAllText(Path.Combine(segment, "video.mp4"), "external-media");
    }

    private static string BuildEntryJson(long avid) =>
        $$"""
        {
          "is_completed": true,
          "total_bytes": 14,
          "downloaded_bytes": 14,
          "title": "Reparse test",
          "type_tag": "80",
          "cover": "cover",
          "prefered_video_quality": 80,
          "guessed_total_bytes": 14,
          "total_time_milli": 1000,
          "danmaku_count": 0,
          "time_update_stamp": 0,
          "time_create_stamp": 0,
          "avid": {{avid.ToString(CultureInfo.InvariantCulture)}},
          "spid": 0,
          "seasion_id": 0,
          "page_data": { "cid": 1, "page": 1, "from": "local", "part": "P1", "vid": "", "has_alias": false, "tid": 0 }
        }
        """;

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException or
                                       PlatformNotSupportedException or
                                       NotSupportedException)
        {
            DeleteDirectoryLink(linkPath);
            return false;
        }
    }

    private static string CreateTempRoot(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bili_{purpose}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Best effort: never recursively delete a link during test cleanup.
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not mask the safety assertion.
        }
    }
}
