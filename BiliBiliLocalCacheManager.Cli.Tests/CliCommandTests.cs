using System.Globalization;
using BiliBiliLocalCacheManager.Cli.Commands;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Models;
using Spectre.Console;
using Xunit;

namespace BiliBiliLocalCacheManager.Cli.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public void ScanCommand_ShouldReturnError_WhenRootMissing()
    {
        var command = new ScanCommand();
        var exitCode = command.Execute(Array.Empty<string>());

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void ScanCommand_ShouldSucceed_WithValidRoot()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 100, isCompleted: true);

            var command = new ScanCommand();
            var exitCode = command.Execute(new[]
            {
                "--root",
                root
            });

            Assert.Equal(0, exitCode);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void ShowCommand_ShouldReturnError_WhenAvidMissing()
    {
        var command = new ShowCommand();
        var exitCode = command.Execute(new[]
        {
            "--root",
            "C:\\dummy"
        });

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void ShowCommand_ShouldSucceed_WhenCacheFound()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 123, isCompleted: true);

            var command = new ShowCommand();
            var exitCode = command.Execute(new[]
            {
                "123",
                "--root",
                root
            });

            Assert.Equal(0, exitCode);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void SearchCommand_ShouldSucceed_WhenMatchFound()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 456, isCompleted: true, title: "Test Title");

            var command = new SearchCommand();
            var exitCode = command.Execute(new[]
            {
                "Test",
                "--root",
                root,
                "--scope",
                "title"
            });

            Assert.Equal(0, exitCode);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void SearchCommand_ShouldMatchOwnerName_WhenIncludeOwnerName()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 901, isCompleted: true, title: "Alpha", part: "P1", ownerName: "OwnerX");

            var command = new SearchCommand();
            AnsiConsole.Record();
            var exitCode = command.Execute(new[]
            {
                "OwnerX",
                "--root",
                root,
                "--include-owner-name"
            });
            var output = AnsiConsole.ExportText();

            Assert.Equal(0, exitCode);
            Assert.Contains("901", output);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void SearchCommand_ShouldMatchBvid_WhenIncludeBvid()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 902, isCompleted: true, title: "Beta", part: "P2", bvid: "BV1TEST");

            var command = new SearchCommand();
            AnsiConsole.Record();
            var exitCode = command.Execute(new[]
            {
                "BV1TEST",
                "--root",
                root,
                "--include-bvid"
            });
            var output = AnsiConsole.ExportText();

            Assert.Equal(0, exitCode);
            Assert.Contains("902", output);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void SearchCommand_ShouldIgnorePartName_WhenNoPartName()
    {
        var root = CreateTempRoot();
        try
        {
            CreateEntry(root, avid: 903, isCompleted: true, title: "Gamma", part: "SegmentX");

            var command = new SearchCommand();
            AnsiConsole.Record();
            var exitCode = command.Execute(new[]
            {
                "SegmentX",
                "--root",
                root,
                "--no-part-name"
            });
            var output = AnsiConsole.ExportText();

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("903", output);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void DeleteCommand_DryRun_ShouldNotDeleteDirectory()
    {
        var root = CreateTempRoot();
        try
        {
            var avidDir = Path.Combine(root, "789");
            Directory.CreateDirectory(avidDir);

            var command = new DeleteCommand();
            var exitCode = command.Execute(new[]
            {
                "789",
                "--root",
                root,
                "--dry-run"
            });

            Assert.Equal(0, exitCode);
            Assert.True(Directory.Exists(avidDir));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void PlayCommand_ShouldReturnError_WhenMultiPageCacheHasNoSegmentOption()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 910, segmentName: "c_1", part: "P1", pageIndex: 1);
            CreateDashEntry(root, avid: 910, segmentName: "c_2", part: "P2", pageIndex: 2);

            var playbackService = new FakePlaybackService();
            var command = new PlayCommand(new CacheManager(), playbackService);

            var exitCode = command.Execute(new[]
            {
                "910",
                "--root",
                root
            });

            Assert.Equal(1, exitCode);
            Assert.Equal(0, playbackService.PlayCallCount);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void PlayCommand_ShouldSucceed_WhenSegmentSpecified()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 911, segmentName: "c_1", part: "P1", pageIndex: 1);
            CreateDashEntry(root, avid: 911, segmentName: "c_2", part: "P2", pageIndex: 2);

            var playbackService = new FakePlaybackService();
            var command = new PlayCommand(new CacheManager(), playbackService);

            var exitCode = command.Execute(new[]
            {
                "911",
                "--root",
                root,
                "--segment",
                "c_2"
            });

            Assert.Equal(0, exitCode);
            Assert.Equal(1, playbackService.PlayCallCount);
            Assert.Equal("c_2", playbackService.LastSegmentKey);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void PlayCommand_ShouldTreatSamePageSegmentsAsSingleVideo()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 913, segmentName: "c_1", part: "P1", pageIndex: 1);
            CreateDashEntry(root, avid: 913, segmentName: "1", part: "P1", pageIndex: 1);

            var playbackService = new FakePlaybackService();
            var command = new PlayCommand(new CacheManager(), playbackService);

            var exitCode = command.Execute(new[]
            {
                "913",
                "--root",
                root
            });

            Assert.Equal(0, exitCode);
            Assert.Equal(1, playbackService.PlayCallCount);
            Assert.Null(playbackService.LastSegmentKey);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void PlayCommand_ShouldPassPlayerPreference()
    {
        var root = CreateTempRoot();
        try
        {
            CreateDashEntry(root, avid: 912, segmentName: "c_1", part: "P1", pageIndex: 1);

            var playbackService = new FakePlaybackService();
            var command = new PlayCommand(new CacheManager(), playbackService);

            var exitCode = command.Execute(new[]
            {
                "912",
                "--root",
                root,
                "--player",
                "system"
            });

            Assert.Equal(0, exitCode);
            Assert.NotNull(playbackService.LastLaunchOptions);
            Assert.Equal(PlaybackPlayerPreference.SystemDefaultOnly, playbackService.LastLaunchOptions!.PreferredPlayer);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static void CreateEntry(
        string root,
        long avid,
        bool isCompleted,
        string title = "Default Title",
        string part = "P1",
        string? ownerName = null,
        string? bvid = null)
    {
        // Build a minimal entry.json for CLI/Core scanning.
        var avidDir = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
        var segmentDir = Path.Combine(avidDir, "c_1");
        Directory.CreateDirectory(segmentDir);

        var entryPath = Path.Combine(segmentDir, "entry.json");
        File.WriteAllText(entryPath, BuildEntryJson(avid, title, part, isCompleted, ownerName, bvid));

        var videoPath = Path.Combine(segmentDir, "1.mp4");
        File.WriteAllText(videoPath, "dummy");
    }

    private static void CreateDashEntry(string root, long avid, string segmentName, string part, int pageIndex)
    {
        var avidDir = Path.Combine(root, avid.ToString(CultureInfo.InvariantCulture));
        var segmentDir = Path.Combine(avidDir, segmentName);
        var qualityDir = Path.Combine(segmentDir, "80");
        Directory.CreateDirectory(qualityDir);

        var entryPath = Path.Combine(segmentDir, "entry.json");
        File.WriteAllText(entryPath, BuildEntryJson(avid, "Dash Title", part, isCompleted: true, ownerName: null, bvid: null, pageIndex: pageIndex));

        File.WriteAllText(Path.Combine(qualityDir, "video.m4s"), "video");
        File.WriteAllText(Path.Combine(qualityDir, "audio.m4s"), "audio");
        File.WriteAllText(Path.Combine(qualityDir, "index.json"), "{}");
    }

    private static string BuildEntryJson(
        long avid,
        string title,
        string part,
        bool isCompleted,
        string? ownerName,
        string? bvid,
        int pageIndex = 1)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ownerField = string.IsNullOrWhiteSpace(ownerName)
            ? string.Empty
            : $"""
               ,{Environment.NewLine}  "owner_name": "{EscapeJson(ownerName)}"
               """;
        var bvidField = string.IsNullOrWhiteSpace(bvid)
            ? string.Empty
            : $"""
               ,{Environment.NewLine}  "bvid": "{EscapeJson(bvid)}"
               """;

        return $$"""
                 {
                   "is_completed": {{isCompleted.ToString().ToLowerInvariant()}},
                   "total_bytes": 1000,
                   "downloaded_bytes": {{(isCompleted ? 1000 : 100)}},
                   "title": "{{EscapeJson(title)}}",
                   "type_tag": "type",
                   "cover": "cover",
                   "prefered_video_quality": 80,
                   "guessed_total_bytes": 1000,
                   "total_time_milli": 60000,
                   "danmaku_count": 0,
                   "time_update_stamp": {{timestamp}},
                   "time_create_stamp": {{timestamp}},
                   "avid": {{avid.ToString(CultureInfo.InvariantCulture)}},
                   "spid": 0,
                   "seasion_id": 0,
                   "page_data": {
                     "cid": 1,
                     "page": {{pageIndex}},
                     "from": "local",
                     "part": "{{EscapeJson(part)}}",
                     "vid": "vid",
                     "has_alias": false,
                     "tid": 0
                   }{{ownerField}}{{bvidField}}
                 }
                 """;
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\"", "\\\"");
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_cli_test_{Guid.NewGuid():N}");
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
            // Ignore cleanup failures in tests.
        }
    }

    private sealed class FakePlaybackService : ICachePlaybackService
    {
        public int PlayCallCount { get; private set; }
        public string? LastSegmentKey { get; private set; }
        public PlaybackLaunchOptions? LastLaunchOptions { get; private set; }

        public CachePlaybackPlan CreatePlan(BiliSegment segment)
        {
            return CachePlaybackPlan.Playable(
                segment.Avid,
                segment.Title,
                segment.PageIndex,
                segment.PartName,
                Path.GetFileName(segment.SegmentDirectory),
                segment.SegmentDirectory,
                "Test",
                CachePlaybackMaterialKind.SingleFile,
                new[] { "file.mp4" });
        }

        public CachePlaybackPlan CreatePlan(BiliVideoCache cache, string? segmentKey = null)
        {
            LastSegmentKey = segmentKey;
            return CreatePagePlan(cache, segmentKey).SelectedPlan;
        }

        public CachePlaybackPagePlan CreatePagePlan(BiliVideoCache cache, string? segmentKey = null)
        {
            LastSegmentKey = segmentKey;
            var targetGroup = cache.Segments
                .GroupBy(segment => segment.PageIndex)
                .OrderBy(group => group.Key)
                .First(group =>
                {
                    if (string.IsNullOrWhiteSpace(segmentKey))
                    {
                        return true;
                    }

                    return string.Equals(group.Key.ToString(CultureInfo.InvariantCulture), segmentKey, StringComparison.OrdinalIgnoreCase) ||
                           group.Any(segment =>
                               string.Equals(Path.GetFileName(segment.SegmentDirectory), segmentKey, StringComparison.OrdinalIgnoreCase));
                });

            var plans = targetGroup.Select(CreatePlan).ToList();
            return new CachePlaybackPagePlan(
                cache.Avid,
                cache.Title,
                targetGroup.Key,
                targetGroup.First().PartName,
                plans,
                plans[0]);
        }

        public IReadOnlyList<CachePlaybackPagePlan> CreatePagePlans(BiliVideoCache cache)
        {
            return cache.Segments
                .GroupBy(segment => segment.PageIndex)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var plans = group.Select(CreatePlan).ToList();
                    return new CachePlaybackPagePlan(
                        cache.Avid,
                        cache.Title,
                        group.Key,
                        group.First().PartName,
                        plans,
                        plans[0]);
                })
                .ToList();
        }

        public PlaybackLaunchResult Play(BiliSegment segment, PlaybackLaunchOptions? launchOptions = null)
        {
            PlayCallCount++;
            LastLaunchOptions = launchOptions;
            return PlaybackLaunchResult.Success("ok", "test");
        }

        public PlaybackLaunchResult Play(BiliVideoCache cache, string? segmentKey = null, PlaybackLaunchOptions? launchOptions = null)
        {
            PlayCallCount++;
            LastSegmentKey = segmentKey;
            LastLaunchOptions = launchOptions;
            return PlaybackLaunchResult.Success("ok", "test");
        }
    }
}
