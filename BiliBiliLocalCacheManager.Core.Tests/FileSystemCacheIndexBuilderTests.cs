using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;
using Xunit;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheIndexBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildIndex_OnlyBindsUniqueIncludedCandidates(bool includeIncomplete)
    {
        var root = CreateTempRoot();
        try
        {
            foreach (var (directory, page, completed) in new[]
            {
                ("a", 1, true), ("b", 1, false), ("c", 1, true), ("d", 2, true), ("e", 2, false),
            })
            {
                var path = Directory.CreateDirectory(Path.Combine(root, "101", directory)).FullName;
                var json = System.Text.Json.Nodes.JsonNode.Parse(BuildEntryJson(101, "Video", directory, completed))!;
                json["page_data"]!["page"] = page;
                File.WriteAllText(Path.Combine(path, "entry.json"), json.ToJsonString());
            }

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root,
                new CacheIndexBuildOptions { CapturePartIdentities = true, IncludeIncompleteEntries = includeIncomplete });
            Assert.Equal(includeIncomplete ? 5 : 3, report.IncludedEntries);
            Assert.Equal(includeIncomplete ? 0 : 2, report.SkippedIncompleteEntries);
            foreach (var (segment, identity) in report.PartIdentities!)
            {
                if (segment.PageIndex == 2 && !includeIncomplete)
                {
                    Assert.Equal(segment.SegmentDirectory, identity.Target?.SourcePath);
                    Assert.Null(identity.Error);
                }
                else
                {
                    Assert.Null(identity.Target);
                    Assert.Contains("多个", identity.Error);
                }
            }
            Assert.Equal(report.IncludedEntries, report.PartIdentities.Count);
            Assert.Equal(includeIncomplete ? 2 : 1, report.UnavailablePartIdentities);
            Assert.Equal(report.UnavailablePartIdentities, report.Issues.Count(issue => issue.Kind == CacheScanIssueKind.PartIdentityUnavailable));
        }
        finally { SafeDeleteDirectory(root); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CapturePartIdentities_MissingCidWarnsEvenWhenIssueDetailsAreLimited(int issueLimit)
    {
        var root = CreateTempRoot();
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "101", "c_1")).FullName;
            var path = Path.Combine(directory, "entry.json");
            var entry = System.Text.Json.Nodes.JsonNode.Parse(BuildEntryJson(101, "Missing CID", "P1", true))!;
            Assert.True(entry["page_data"]!.AsObject().Remove("cid"));
            File.WriteAllText(path, entry.ToJsonString());
            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root,
                new CacheIndexBuildOptions { CapturePartIdentities = true, MaxReportedIssues = issueLimit });

            Assert.Single(report.Index.VideoCaches);
            Assert.True(report.HasWarnings);
            Assert.Equal(1, report.UnavailablePartIdentities);
            Assert.Equal(0, report.InvalidEntries);
            Assert.Equal(issueLimit, report.Issues.Count);
            if (issueLimit > 0)
            {
                var issue = Assert.Single(report.Issues);
                Assert.Equal(CacheScanIssueKind.PartIdentityUnavailable, issue.Kind);
                Assert.Equal(path, issue.Path);
                Assert.Contains("cid", issue.Message);
            }
        }
        finally { SafeDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("0100", "entry.json")]
    [InlineData("100", "other.json")]
    public void BuildIndex_NoncanonicalMetadataCanBeListedWithoutCreatingADeletionTarget(string avidDirectory, string entryFile)
    {
        var root = CreateTempRoot();
        try
        {
            var part = Directory.CreateDirectory(Path.Combine(root, avidDirectory, "c_1")).FullName;
            var path = Path.Combine(part, entryFile);
            File.WriteAllText(path, BuildEntryJson(100, "Listed video", "P1", true));

            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(root,
                new CacheIndexBuildOptions { EntryFileName = entryFile, CapturePartIdentities = true });
            var cache = Assert.Single(report.Index.VideoCaches);
            Assert.Equal("Listed video", cache.Title);
            var segment = Assert.Single(cache.Segments);
            Assert.Equal(path, segment.EntryJsonPath);
            var identity = report.PartIdentities![segment];
            Assert.Null(identity.Target);
            Assert.False(string.IsNullOrWhiteSpace(identity.Error));
        }
        finally { SafeDeleteDirectory(root); }
    }

    [Fact]
    public void BuildIndex_ShouldIncludeCompletedSegmentAndVideoFiles()
    {
        // 1) 准备临时目录结构：root/100/c_1/entry.json + 1.mp4
        var root = CreateTempRoot();
        try
        {
            var avidDir = Path.Combine(root, "100");
            var segmentDir = Path.Combine(avidDir, "c_1");
            Directory.CreateDirectory(segmentDir);

            var entryPath = Path.Combine(segmentDir, "entry.json");
            File.WriteAllText(entryPath, BuildEntryJson(
                avid: 100,
                title: "测试标题",
                part: "第一集",
                isCompleted: true));

            var videoPath = Path.Combine(segmentDir, "1.mp4");
            File.WriteAllText(videoPath, "dummy");

            // 2) 执行扫描
            var builder = new FileSystemCacheIndexBuilder();
            var index = builder.BuildIndex(root);

            // 3) 断言：应当扫描到一个缓存与一个分段，且视频文件被收集
            Assert.Single(index.VideoCaches);
            var cache = index.VideoCaches.First();
            Assert.Equal(100, cache.Avid);
            Assert.Single(cache.Segments);

            var segment = cache.Segments.First();
            Assert.Contains(videoPath, segment.VideoFiles);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void BuildIndex_ShouldSkipIncompleteWhenOptionDisabled()
    {
        // 1) 准备临时目录结构：root/200/1/entry.json（未完成）
        var root = CreateTempRoot();
        try
        {
            var avidDir = Path.Combine(root, "200");
            var segmentDir = Path.Combine(avidDir, "1");
            Directory.CreateDirectory(segmentDir);

            var entryPath = Path.Combine(segmentDir, "entry.json");
            File.WriteAllText(entryPath, BuildEntryJson(
                avid: 200,
                title: "未完成视频",
                part: "P1",
                isCompleted: false));

            // 2) 禁用“包含未完成缓存”，期望扫描结果为空
            var builder = new FileSystemCacheIndexBuilder();
            var options = new CacheIndexBuildOptions
            {
                IncludeIncompleteEntries = false
            };

            var index = builder.BuildIndex(root, options);

            // 3) 断言：没有缓存被纳入索引
            Assert.Empty(index.VideoCaches);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static string CreateTempRoot()
    {
        // 使用独立的临时目录，避免影响开发者本地文件
        var root = Path.Combine(Path.GetTempPath(), $"bili_cache_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void SafeDeleteDirectory(string path)
    {
        // 测试清理阶段尽量“尽力而为”，避免清理失败影响断言
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 清理失败时不抛异常
        }
    }

    private static string BuildEntryJson(long avid, string title, string part, bool isCompleted)
    {
        // 只填入 CacheEntryRaw 必需字段，确保能被成功反序列化
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return $@"{{
  ""is_completed"": {isCompleted.ToString().ToLowerInvariant()},
  ""total_bytes"": 1000,
  ""downloaded_bytes"": {(isCompleted ? 1000 : 100)},
  ""title"": ""{EscapeJson(title)}"",
  ""type_tag"": ""type"",
  ""cover"": ""cover"",
  ""prefered_video_quality"": 80,
  ""guessed_total_bytes"": 1000,
  ""total_time_milli"": 60000,
  ""danmaku_count"": 0,
  ""time_update_stamp"": {timestamp},
  ""time_create_stamp"": {timestamp},
  ""avid"": {avid.ToString(CultureInfo.InvariantCulture)},
  ""spid"": 0,
  ""seasion_id"": 0,
  ""page_data"": {{
    ""cid"": 1,
    ""page"": 1,
    ""from"": ""local"",
    ""part"": ""{EscapeJson(part)}"",
    ""vid"": ""vid"",
    ""has_alias"": false,
    ""tid"": 0
  }}
}}";
    }

    private static string EscapeJson(string value)
    {
        // 简化版转义：确保双引号不会破坏 JSON
        return value.Replace("\"", "\\\"");
    }
}
