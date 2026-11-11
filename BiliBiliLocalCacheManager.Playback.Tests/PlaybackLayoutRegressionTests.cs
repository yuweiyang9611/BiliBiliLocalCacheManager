using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackLayoutRegressionTests
{
    [Fact]
    public void DirectoryPipeline_ShouldRecognizeNewDash()
    {
        using var fixture = new CacheDirectoryFixture("c_1");
        fixture.WriteFile(Path.Combine("80", "video.m4s"), 32);
        fixture.WriteFile(Path.Combine("80", "audio.m4s"), 16);

        var plan = fixture.BuildPlan();

        Assert.Equal("NewDash", plan.StructureKind);
        Assert.Equal(CachePlaybackMaterialKind.DashPair, plan.MaterialKind);
    }

    [Fact]
    public void DirectoryPipeline_ShouldRecognizeMiddleDash()
    {
        using var fixture = new CacheDirectoryFixture("1");
        fixture.WriteFile(Path.Combine("64", "video.m4s"), 32);
        fixture.WriteFile(Path.Combine("64", "audio.m4s"), 16);

        Assert.Equal("MidDash", fixture.BuildPlan().StructureKind);
    }

    [Fact]
    public void DirectoryPipeline_ShouldRecognizeLegacyBlv()
    {
        using var fixture = new CacheDirectoryFixture("1");
        fixture.WriteFile(Path.Combine("lua.flv.bili2api.80", "0.blv"), 32);

        var plan = fixture.BuildPlan();

        Assert.Equal("LegacyBlv", plan.StructureKind);
        Assert.Equal(CachePlaybackMaterialKind.SingleFile, plan.MaterialKind);
    }

    [Fact]
    public void DirectoryPipeline_ShouldRecognizeHybridLegacy()
    {
        using var fixture = new CacheDirectoryFixture("c_1");
        fixture.WriteFile(Path.Combine("lua.mp4.bapi.9", "0.mp4"), 32);

        Assert.Equal("HybridCLegacy", fixture.BuildPlan().StructureKind);
    }

    [Fact]
    public void DirectoryPipeline_ShouldPreferLargerVariantForMixedLegacy()
    {
        using var fixture = new CacheDirectoryFixture("1");
        fixture.WriteFile(Path.Combine("lua.flv.bili2api.80", "0.blv"), 16);
        fixture.WriteFile(Path.Combine("lua.mp4.bapi.9", "0.mp4"), 64);

        var plan = fixture.BuildPlan();

        Assert.Equal("LegacyMixed", plan.StructureKind);
        Assert.EndsWith(".mp4", Assert.Single(plan.MediaFiles), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".blv")]
    [InlineData(".mp4")]
    public void LegacyLayout_UsesOrderedPartsFromOneQualityDirectory(string extension)
    {
        using var fixture = new CacheDirectoryFixture("1");
        var prefix = extension == ".blv" ? "lua.flv.bili2api." : "lua.mp4.bapi.";
        fixture.WriteFile(Path.Combine(prefix + "64", "0" + extension), 10);
        fixture.WriteFile(Path.Combine(prefix + "80", "1" + extension), 26);
        fixture.WriteFile(Path.Combine(prefix + "80", "0" + extension), 28);
        var plan = fixture.BuildPlan();
        Assert.Equal(CachePlaybackMaterialKind.OrderedPair, plan.MaterialKind);
        Assert.Equal(new[] { "0" + extension, "1" + extension }, plan.MediaFiles.Select(Path.GetFileName));
        Assert.All(plan.MediaFiles, path => Assert.Equal(prefix + "80", Path.GetFileName(Path.GetDirectoryName(path))));
    }

    [Theory]
    [InlineData(".mp4", "1", 0)]
    [InlineData(".blv", "2", 16)]
    [InlineData(".mp4", "00", 16)]
    public void LegacyLayout_RejectsAnIncompleteVariantBeforeRanking(string extension, string badPart, int length)
    {
        using var fixture = new CacheDirectoryFixture("1");
        var prefix = extension == ".mp4" ? "lua.mp4.bapi." : "lua.flv.bili2api.";
        fixture.WriteFile(Path.Combine(prefix + "80", "0" + extension), 24);
        fixture.WriteFile(Path.Combine(prefix + "80", badPart + extension), length);
        Assert.False(fixture.BuildPlan().IsPlayable);

        // Some legacy downloads number their fragments starting at one.
        fixture.WriteFile(Path.Combine(prefix + "64", "1" + extension), 12);
        fixture.WriteFile(Path.Combine(prefix + "64", "2" + extension), 12);
        var plan = fixture.BuildPlan();
        Assert.Equal(CachePlaybackMaterialKind.OrderedPair, plan.MaterialKind);
        Assert.Equal(new[] { "1" + extension, "2" + extension }, plan.MediaFiles.Select(Path.GetFileName));
        Assert.All(plan.MediaFiles, path => Assert.Equal(prefix + "64", Path.GetFileName(Path.GetDirectoryName(path))));
    }

    [Theory]
    [InlineData("1", "MidDash")]
    [InlineData("c_1", "NewDash")]
    public void DashLayout_RemainsPlayableWithEmptyLegacyMedia(string segment, string structure)
    {
        using var fixture = new CacheDirectoryFixture(segment);
        fixture.WriteFile(Path.Combine("lua.flv.bili2api.80", "0.blv"), 0);
        fixture.WriteFile(Path.Combine("80", "video.m4s"), 16);
        fixture.WriteFile(Path.Combine("80", "audio.m4s"), 16);
        var plan = fixture.BuildPlan();
        Assert.Equal(structure, plan.StructureKind);
        Assert.Equal(CachePlaybackMaterialKind.DashPair, plan.MaterialKind);
    }

    [Fact]
    public void LegacyLayout_KeepsCaseDistinctDirectoryEntriesSeparate()
    {
        using var fixture = new CacheDirectoryFixture("1");
        const string first = "lua.mp4.bapi.80", second = "lua.MP4.bapi.80";
        fixture.WriteFile(Path.Combine(first, "0.mp4"), 32);
        fixture.WriteFile(Path.Combine(second, "1.mp4"), 16);
        var segment = fixture.ReadSegment();
        var firstDirectory = Path.Combine(segment.SegmentDirectory, first);
        var secondDirectory = Path.Combine(segment.SegmentDirectory, second);
        var firstFile = Path.Combine(firstDirectory, "0.mp4");
        var secondFile = Path.Combine(secondDirectory, "1.mp4");
        // Keep the enumerated spellings in the probe even on a case-insensitive test volume.
        var probe = new CachePlaybackProbe(segment, [], [firstDirectory, secondDirectory], [firstFile, secondFile]);
        Assert.Equal(firstFile, Assert.Single(PlaybackPathHelpers.GetCompleteLegacyFragments(probe, first, ".mp4")));
        Assert.Equal(secondFile, Assert.Single(PlaybackPathHelpers.GetCompleteLegacyFragments(probe, second, ".mp4")));
        Assert.Equal(firstFile, Assert.Single(LegacyLayoutPlanBuilder.Build(probe, isHybrid: false).MediaFiles));
    }

    private sealed class CacheDirectoryFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"bili_layout_regression_{Guid.NewGuid():N}");
        private readonly string _segmentDirectory;

        public CacheDirectoryFixture(string segmentName)
        {
            _segmentDirectory = Path.Combine(_root, "100", segmentName);
            Directory.CreateDirectory(_segmentDirectory);
            File.WriteAllText(Path.Combine(_segmentDirectory, "entry.json"), BuildEntryJson());
        }

        public void WriteFile(string relativePath, int byteCount)
        {
            var path = Path.Combine(_segmentDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[byteCount]);
        }

        public CachePlaybackPlan BuildPlan() => CreateService().CreatePlan(ReadSegment());

        public BiliSegment ReadSegment()
        {
            var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(
                _root,
                new CacheIndexBuildOptions
                {
                    IncludeIncompleteEntries = true,
                    ThrowOnInvalidEntry = true
                });
            var cache = Assert.Single(report.Index.VideoCaches);
            return Assert.Single(cache.Segments);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Ignore test cleanup failures.
            }
        }

        private static CachePlaybackService CreateService()
        {
            return new CachePlaybackService(
                new ICachePlaybackLayoutHandler[]
                {
                    new HybridCLegacyCachePlaybackLayoutHandler(),
                    new NewDashCachePlaybackLayoutHandler(),
                    new MidDashCachePlaybackLayoutHandler(),
                    new LegacyLuaCachePlaybackLayoutHandler()
                },
                new NoOpMaterializer(),
                new NoOpLauncher());
        }

        private static string BuildEntryJson()
        {
            return """
                   {
                     "is_completed": true,
                     "total_bytes": 100,
                     "downloaded_bytes": 100,
                     "title": "Regression",
                     "type_tag": "80",
                     "cover": "cover",
                     "prefered_video_quality": 80,
                     "guessed_total_bytes": 100,
                     "total_time_milli": 1000,
                     "danmaku_count": 0,
                     "time_update_stamp": 0,
                     "time_create_stamp": 0,
                     "avid": 100,
                     "spid": 0,
                     "seasion_id": 0,
                     "page_data": {
                       "cid": 1,
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
    }

    private sealed class NoOpMaterializer : IPlaybackMaterializer
    {
        public bool CanHandle(CachePlaybackPlan plan) => true;

        public PlaybackMaterializationResult Materialize(CachePlaybackPlan plan) =>
            PlaybackMaterializationResult.Failure("not used");
    }

    private sealed class NoOpLauncher : IPlaybackLauncher
    {
        public PlaybackLaunchResult Launch(
            PlaybackMaterializationResult materializationResult,
            PlaybackLaunchOptions? launchOptions = null) =>
            PlaybackLaunchResult.Failure("not used");
    }
}
