using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class CorrectnessRegressionTests
{
    [Fact]
    public void BuildIndex_ShouldRejectEntryWhoseAvidDoesNotMatchParentDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, directoryAvid: 100, jsonAvid: 200, createdAt: 0);
            CreateEntry(root, directoryAvid: 200, jsonAvid: 200, createdAt: 0);

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root);

            var cache = Assert.Single(report.Index.VideoCaches);
            Assert.Equal(200, cache.Avid);
            Assert.All(cache.Segments, segment =>
                Assert.StartsWith(Path.Combine(root, "200"), segment.SegmentDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, report.IncludedEntries);
            Assert.Equal(1, report.InvalidEntries);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void BuildIndex_ShouldReportOutOfRangeTimestampAndContinue()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, directoryAvid: 100, jsonAvid: 100, createdAt: long.MaxValue);
            CreateEntry(root, directoryAvid: 200, jsonAvid: 200, createdAt: 0);

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root);

            Assert.Single(report.Index.VideoCaches);
            Assert.Equal(200, report.Index.VideoCaches.Single().Avid);
            Assert.Equal(1, report.InvalidEntries);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void BuildIndex_ShouldRejectMissingRequiredPageData()
    {
        var root = CreateTempRoot();
        try
        {
            var segment = Path.Combine(root, "300", "c_1");
            Directory.CreateDirectory(segment);
            File.WriteAllText(
                Path.Combine(segment, "entry.json"),
                """{ "is_completed": true, "avid": 300, "title": "Missing page" }""");

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root);

            Assert.Empty(report.Index.VideoCaches);
            Assert.Equal(1, report.InvalidEntries);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ShouldRejectTrashDirectoryBelongingToAnotherAvid()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, directoryAvid: 100, jsonAvid: 100, createdAt: 0);
            var service = new FileSystemCacheTrashService();
            var moved = service.MoveToTrash(root, 100);

            Assert.True(moved.Succeeded);
            Assert.Throws<InvalidOperationException>(() => service.Restore(root, 101, moved.TrashPath!));
            Assert.True(Directory.Exists(moved.TrashPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static void CreateEntry(string root, long directoryAvid, long jsonAvid, long createdAt)
    {
        var segment = Path.Combine(root, directoryAvid.ToString(), "c_1");
        Directory.CreateDirectory(segment);
        File.WriteAllText(
            Path.Combine(segment, "entry.json"),
            $$"""
              {
                "is_completed": true,
                "total_bytes": 10,
                "downloaded_bytes": 10,
                "title": "Title {{jsonAvid}}",
                "type_tag": "80",
                "cover": "cover",
                "prefered_video_quality": 80,
                "guessed_total_bytes": 10,
                "total_time_milli": 1000,
                "danmaku_count": 0,
                "time_update_stamp": 0,
                "time_create_stamp": {{createdAt}},
                "avid": {{jsonAvid}},
                "spid": 0,
                "seasion_id": 0,
                "page_data": {
                  "cid": {{jsonAvid}},
                  "page": 1,
                  "from": "local",
                  "part": "P1",
                  "vid": "",
                  "has_alias": false,
                  "tid": 0
                }
              }
              """);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bili_correctness_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore test cleanup failures.
        }
    }
}
