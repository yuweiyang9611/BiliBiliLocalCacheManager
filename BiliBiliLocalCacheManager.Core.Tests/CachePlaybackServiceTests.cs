using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Playback.Services;
using Xunit;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class CachePlaybackServiceTests
{
    [Fact]
    public void CreatePlan_ShouldDetectNewDash()
    {
        var root = CreateTempRoot();
        try
        {
            var segmentDir = Path.Combine(root, "100", "c_1");
            var qualityDir = Path.Combine(segmentDir, "80");
            Directory.CreateDirectory(qualityDir);
            File.WriteAllText(Path.Combine(qualityDir, "video.m4s"), "video");
            File.WriteAllText(Path.Combine(qualityDir, "audio.m4s"), "audio");

            var segment = CreateSegment(100, 1, segmentDir, "P1");

            var plan = CreateService().CreatePlan(segment);

            Assert.True(plan.IsPlayable);
            Assert.Equal("NewDash", plan.StructureKind);
            Assert.Equal(CachePlaybackMaterialKind.DashPair, plan.MaterialKind);
            Assert.Equal(2, plan.MediaFiles.Count);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void CreatePlan_ShouldDetectMidDash()
    {
        var root = CreateTempRoot();
        try
        {
            var segmentDir = Path.Combine(root, "100", "1");
            var qualityDir = Path.Combine(segmentDir, "64");
            Directory.CreateDirectory(qualityDir);
            File.WriteAllText(Path.Combine(qualityDir, "video.m4s"), "video");
            File.WriteAllText(Path.Combine(qualityDir, "audio.m4s"), "audio");

            var segment = CreateSegment(100, 1, segmentDir, "P1");
            var plan = CreateService().CreatePlan(segment);

            Assert.True(plan.IsPlayable);
            Assert.Equal("MidDash", plan.StructureKind);
            Assert.Equal(CachePlaybackMaterialKind.DashPair, plan.MaterialKind);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void CreatePlan_ShouldHandleLegacyMixed_WhenEqualBytes()
    {
        var root = CreateTempRoot();
        try
        {
            var segmentDir = Path.Combine(root, "100", "1");
            var blvDir = Path.Combine(segmentDir, "lua.flv.bili2api.80");
            var mp4Dir = Path.Combine(segmentDir, "lua.mp4.bapi.9");
            Directory.CreateDirectory(blvDir);
            Directory.CreateDirectory(mp4Dir);

            File.WriteAllBytes(Path.Combine(blvDir, "0.blv"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(mp4Dir, "0.mp4"), new byte[1024]);

            var segment = CreateSegment(100, 1, segmentDir, "P1");
            var plan = CreateService().CreatePlan(segment);

            Assert.True(plan.IsPlayable);
            Assert.Equal("LegacyMixed", plan.StructureKind);
            Assert.Single(plan.MediaFiles);
            Assert.EndsWith(".blv", plan.MediaFiles[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void CreatePlan_ShouldHandleLegacyMixed_WhenMp4IsLarger()
    {
        var root = CreateTempRoot();
        try
        {
            var segmentDir = Path.Combine(root, "100", "1");
            var blvDir = Path.Combine(segmentDir, "lua.flv.bili2api.80");
            var mp4Dir = Path.Combine(segmentDir, "lua.mp4.bapi.9");
            Directory.CreateDirectory(blvDir);
            Directory.CreateDirectory(mp4Dir);

            File.WriteAllBytes(Path.Combine(blvDir, "0.blv"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(mp4Dir, "0.mp4"), new byte[4096]);

            var segment = CreateSegment(100, 1, segmentDir, "P1");
            var plan = CreateService().CreatePlan(segment);

            Assert.True(plan.IsPlayable);
            Assert.Equal("LegacyMixed", plan.StructureKind);
            Assert.Single(plan.MediaFiles);
            Assert.EndsWith(".mp4", plan.MediaFiles[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void CreatePlan_ShouldHandleHybridCLegacy()
    {
        var root = CreateTempRoot();
        try
        {
            var segmentDir = Path.Combine(root, "100", "c_1");
            var blvDir = Path.Combine(segmentDir, "lua.flv.bili2api.80");
            Directory.CreateDirectory(blvDir);
            File.WriteAllBytes(Path.Combine(blvDir, "0.blv"), new byte[2048]);

            var segment = CreateSegment(100, 1, segmentDir, "P1");
            var plan = CreateService().CreatePlan(segment);

            Assert.True(plan.IsPlayable);
            Assert.Equal("HybridCLegacy", plan.StructureKind);
            Assert.Equal(CachePlaybackMaterialKind.SingleFile, plan.MaterialKind);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void CreatePagePlans_ShouldTreatSamePageSegmentsAsSingleVideo()
    {
        var root = CreateTempRoot();
        try
        {
            var dashSegmentDir = Path.Combine(root, "100", "c_1");
            var dashQualityDir = Path.Combine(dashSegmentDir, "80");
            Directory.CreateDirectory(dashQualityDir);
            File.WriteAllBytes(Path.Combine(dashQualityDir, "video.m4s"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(dashQualityDir, "audio.m4s"), new byte[1024]);

            var legacySegmentDir = Path.Combine(root, "100", "1");
            var mp4Dir = Path.Combine(legacySegmentDir, "lua.mp4.bapi.9");
            Directory.CreateDirectory(mp4Dir);
            File.WriteAllBytes(Path.Combine(mp4Dir, "0.mp4"), new byte[4096]);

            var cache = new BiliVideoCache(100, new[]
            {
                CreateSegment(100, 1, dashSegmentDir, "P1"),
                CreateSegment(100, 1, legacySegmentDir, "P1")
            });

            var pages = CreateService().CreatePagePlans(cache);

            Assert.Single(pages);
            Assert.Equal(2, pages[0].CandidatePlans.Count);
            Assert.Equal("1", pages[0].SelectedPlan.SegmentName);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void CreatePagePlan_ShouldResolveExplicitSegmentKey()
    {
        var root = CreateTempRoot();
        try
        {
            var firstSegmentDir = Path.Combine(root, "100", "c_1");
            var firstQualityDir = Path.Combine(firstSegmentDir, "64");
            Directory.CreateDirectory(firstQualityDir);
            File.WriteAllText(Path.Combine(firstQualityDir, "video.m4s"), "video");
            File.WriteAllText(Path.Combine(firstQualityDir, "audio.m4s"), "audio");

            var secondSegmentDir = Path.Combine(root, "100", "c_2");
            var secondQualityDir = Path.Combine(secondSegmentDir, "80");
            Directory.CreateDirectory(secondQualityDir);
            File.WriteAllText(Path.Combine(secondQualityDir, "video.m4s"), "video");
            File.WriteAllText(Path.Combine(secondQualityDir, "audio.m4s"), "audio");

            var cache = new BiliVideoCache(100, new[]
            {
                CreateSegment(100, 1, firstSegmentDir, "P1"),
                CreateSegment(100, 2, secondSegmentDir, "P2")
            });

            var pagePlan = CreateService().CreatePagePlan(cache, "c_2");

            Assert.Equal(2, pagePlan.PageIndex);
            Assert.Equal("c_2", pagePlan.SelectedPlan.SegmentName);
            Assert.Equal("P2", pagePlan.PartName);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Play_ShouldDelegateToMaterializerAndLauncher()
    {
        var root = CreateTempRoot();
        try
        {
            var segmentDir = Path.Combine(root, "100", "c_1");
            var qualityDir = Path.Combine(segmentDir, "80");
            Directory.CreateDirectory(qualityDir);
            File.WriteAllText(Path.Combine(qualityDir, "video.m4s"), "video");
            File.WriteAllText(Path.Combine(qualityDir, "audio.m4s"), "audio");

            var materializer = new RecordingMaterializer();
            var launcher = new RecordingLauncher();
            var service = CreateService(materializer, launcher);
            var segment = CreateSegment(100, 1, segmentDir, "P1");

            var result = service.Play(segment);

            Assert.True(result.Succeeded);
            Assert.NotNull(materializer.LastPlan);
            Assert.NotNull(launcher.LastMaterializationResult);
            Assert.Equal("NewDash", materializer.LastPlan!.StructureKind);
            Assert.Equal("prepared.mp4", Path.GetFileName(launcher.LastMaterializationResult!.OutputPath));
            Assert.Equal(launcher.LastMaterializationResult.OutputPath, result.ManagedArtifactPath);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Play_ShouldPassLaunchOptionsToLauncher()
    {
        var root = CreateTempRoot();
        try
        {
            var segmentDir = Path.Combine(root, "100", "1");
            var mp4Dir = Path.Combine(segmentDir, "lua.mp4.bapi.9");
            Directory.CreateDirectory(mp4Dir);
            File.WriteAllBytes(Path.Combine(mp4Dir, "0.mp4"), new byte[2048]);

            var launcher = new RecordingLauncher();
            var service = CreateService(new RecordingMaterializer(), launcher);
            var segment = CreateSegment(100, 1, segmentDir, "P1");

            service.Play(segment, new PlaybackLaunchOptions
            {
                PreferredPlayer = PlaybackPlayerPreference.SystemDefaultOnly
            });

            Assert.NotNull(launcher.LastLaunchOptions);
            Assert.Equal(PlaybackPlayerPreference.SystemDefaultOnly, launcher.LastLaunchOptions!.PreferredPlayer);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    private static CachePlaybackService CreateService(
        IPlaybackMaterializer? materializer = null,
        IPlaybackLauncher? launcher = null)
    {
        return new CachePlaybackService(
            new ICachePlaybackLayoutHandler[]
            {
                new HybridCLegacyCachePlaybackLayoutHandler(),
                new NewDashCachePlaybackLayoutHandler(),
                new MidDashCachePlaybackLayoutHandler(),
                new LegacyLuaCachePlaybackLayoutHandler()
            },
            materializer ?? new RecordingMaterializer(),
            launcher ?? new RecordingLauncher());
    }

    private static BiliSegment CreateSegment(long avid, int pageIndex, string segmentDirectory, string partName)
    {
        return new BiliSegment(
            avid: avid,
            cid: pageIndex,
            bvid: null,
            pageIndex: pageIndex,
            partName: partName,
            title: $"Title-{avid}",
            version: CacheVersion.Modern,
            typeTag: "type",
            mediaType: null,
            videoQuality: 80,
            qualityDescription: null,
            isCompleted: true,
            totalBytes: 1000,
            downloadedBytes: 1000,
            totalDuration: TimeSpan.FromSeconds(60),
            danmakuCount: 0,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            segmentDirectory: segmentDirectory,
            entryJsonPath: Path.Combine(segmentDirectory, "entry.json"),
            videoFiles: Array.Empty<string>(),
            coverUrl: "cover",
            ownerName: null,
            ownerId: null);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bili_playback_test_{Guid.NewGuid():N}");
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

    private sealed class RecordingMaterializer : IPlaybackMaterializer
    {
        public CachePlaybackPlan? LastPlan { get; private set; }

        public bool CanHandle(CachePlaybackPlan plan)
        {
            return true;
        }

        public PlaybackMaterializationResult Materialize(CachePlaybackPlan plan)
        {
            LastPlan = plan;
            return PlaybackMaterializationResult.Success(
                Path.Combine(Path.GetTempPath(), "prepared.mp4"),
                isTemporary: true,
                "ok",
                nameof(RecordingMaterializer));
        }
    }

    private sealed class RecordingLauncher : IPlaybackLauncher
    {
        public PlaybackMaterializationResult? LastMaterializationResult { get; private set; }
        public PlaybackLaunchOptions? LastLaunchOptions { get; private set; }

        public PlaybackLaunchResult Launch(PlaybackMaterializationResult materializationResult, PlaybackLaunchOptions? launchOptions = null)
        {
            LastMaterializationResult = materializationResult;
            LastLaunchOptions = launchOptions;
            return PlaybackLaunchResult.Success("ok", "test");
        }
    }
}
