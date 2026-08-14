using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheIndexReportTests
{
    [Fact]
    public void BuildIndexWithReport_ShouldDescribeInvalidAndIncompleteEntries()
    {
        var root = CreateTempRoot();
        try
        {
            var invalidDirectory = Path.Combine(root, "100", "c_invalid");
            Directory.CreateDirectory(invalidDirectory);
            File.WriteAllText(Path.Combine(invalidDirectory, "entry.json"), "{not-json");

            var incompleteDirectory = Path.Combine(root, "200", "c_incomplete");
            Directory.CreateDirectory(incompleteDirectory);
            File.WriteAllText(
                Path.Combine(incompleteDirectory, "entry.json"),
                BuildEntryJson(200, isCompleted: false));

            var completedDirectory = Path.Combine(root, "300", "c_completed");
            Directory.CreateDirectory(completedDirectory);
            File.WriteAllText(
                Path.Combine(completedDirectory, "entry.json"),
                BuildEntryJson(300, isCompleted: true));

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(
                root,
                new CacheIndexBuildOptions
                {
                    IncludeIncompleteEntries = false
                });

            Assert.Single(report.Index.VideoCaches);
            Assert.Equal(1, report.IncludedEntries);
            Assert.Equal(1, report.SkippedIncompleteEntries);
            Assert.Equal(1, report.InvalidEntries);
            Assert.Equal(0, report.InaccessibleDirectories);
            Assert.True(report.HasWarnings);
            var issue = Assert.Single(report.Issues);
            Assert.Equal(CacheScanIssueKind.InvalidEntry, issue.Kind);
            Assert.EndsWith("entry.json", issue.Path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void BuildIndexWithReport_ShouldLimitIssueDetailsWithoutLosingCounts()
    {
        var root = CreateTempRoot();
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var segmentDirectory = Path.Combine(root, $"{index + 1}", "c_invalid");
                Directory.CreateDirectory(segmentDirectory);
                File.WriteAllText(Path.Combine(segmentDirectory, "entry.json"), "invalid");
            }

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(
                root,
                new CacheIndexBuildOptions
                {
                    MaxReportedIssues = 1
                });

            Assert.Equal(3, report.InvalidEntries);
            Assert.Single(report.Issues);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void BuildIndexWithReport_OutOfRangeDuration_ShouldSkipOnlyBadEntry()
    {
        var root = CreateTempRoot();
        try
        {
            var invalidDirectory = Path.Combine(root, "100", "c_invalid_duration");
            Directory.CreateDirectory(invalidDirectory);
            File.WriteAllText(
                Path.Combine(invalidDirectory, "entry.json"),
                BuildEntryJson(100, isCompleted: true, totalTimeMilli: long.MaxValue));

            var validDirectory = Path.Combine(root, "200", "c_valid");
            Directory.CreateDirectory(validDirectory);
            File.WriteAllText(
                Path.Combine(validDirectory, "entry.json"),
                BuildEntryJson(200, isCompleted: true));

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root);

            var cache = Assert.Single(report.Index.VideoCaches);
            Assert.Equal(200, cache.Avid);
            Assert.Equal(1, report.IncludedEntries);
            Assert.Equal(1, report.InvalidEntries);
            var issue = Assert.Single(report.Issues);
            Assert.Contains("total_time_milli", issue.Message, StringComparison.Ordinal);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static string BuildEntryJson(
        long avid,
        bool isCompleted,
        long totalTimeMilli = 1_000)
    {
        return $$"""
                 {
                   "is_completed": {{isCompleted.ToString().ToLowerInvariant()}},
                   "total_bytes": 100,
                   "downloaded_bytes": 100,
                   "title": "Title-{{avid}}",
                   "type_tag": "64",
                   "cover": "cover",
                   "prefered_video_quality": 64,
                   "guessed_total_bytes": 100,
                   "total_time_milli": {{totalTimeMilli}},
                   "danmaku_count": 0,
                   "time_update_stamp": 0,
                   "time_create_stamp": 0,
                   "avid": {{avid}},
                   "spid": 0,
                   "seasion_id": 0,
                   "page_data": {
                     "cid": {{avid}},
                     "page": 1,
                     "from": "vupload",
                     "part": "P1",
                     "vid": "",
                     "has_alias": false,
                     "tid": 0
                   }
                 }
                 """;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_scan_report_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
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
            // Ignore test cleanup failures.
        }
    }
}
