using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using Xunit;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class CacheManagerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildIndex_ShouldPassIncludeIncomplete(bool includeIncomplete)
    {
        var builder = new RecordingIndexBuilder(new CacheIndex(new[] { CreateCache(1, "Alpha") }));
        var deletionService = new RecordingDeletionService();
        var manager = new CacheManager(builder, deletionService);

        manager.BuildIndex("root", includeIncomplete);

        Assert.NotNull(builder.Options);
        Assert.Equal(includeIncomplete, builder.Options!.IncludeIncompleteEntries);
    }

    [Fact]
    public void Search_ShouldUseBuilderOptionsAndReturnMatches()
    {
        var cacheA = CreateCache(1, "Alpha");
        var cacheB = CreateCache(2, "Beta");
        var builder = new RecordingIndexBuilder(new CacheIndex(new[] { cacheA, cacheB }));
        var deletionService = new RecordingDeletionService();
        var manager = new CacheManager(builder, deletionService);

        var options = CacheSearchOptionsFactory.Create(
            keyword: "Alpha",
            matchMode: CacheSearchMatchMode.Contains,
            caseSensitive: false,
            splitKeywords: false,
            requireAllKeywords: true,
            scope: CacheSearchScope.Title);

        var results = manager.Search("root", includeIncomplete: false, options);

        Assert.NotNull(builder.Options);
        Assert.False(builder.Options!.IncludeIncompleteEntries);
        Assert.Single(results);
        Assert.Equal(1, results.First().Avid);
    }

    [Fact]
    public void FindByAvid_ShouldReturnNull_WhenMissing()
    {
        var builder = new RecordingIndexBuilder(new CacheIndex(new[] { CreateCache(1, "Alpha") }));
        var deletionService = new RecordingDeletionService();
        var manager = new CacheManager(builder, deletionService);

        var result = manager.FindByAvid("root", new CacheIndexBuildOptions(), avid: 999);

        Assert.Null(result);
    }

    [Fact]
    public void FindByAvid_ShouldReturnCache_WhenFound()
    {
        var cache = CreateCache(7, "Gamma");
        var builder = new RecordingIndexBuilder(new CacheIndex(new[] { cache }));
        var deletionService = new RecordingDeletionService();
        var manager = new CacheManager(builder, deletionService);

        var result = manager.FindByAvid("root", new CacheIndexBuildOptions(), avid: 7);

        Assert.Same(cache, result);
    }

    [Fact]
    public void DeleteByAvid_ShouldDelegateToDeletionService()
    {
        var builder = new RecordingIndexBuilder(new CacheIndex(new[] { CreateCache(1, "Alpha") }));
        var deletionService = new RecordingDeletionService
        {
            Result = new CacheDeletionResult(found: true, deleted: false, targetPath: "root\\1")
        };
        var manager = new CacheManager(builder, deletionService);

        var result = manager.DeleteByAvid("root", 1, dryRun: true);

        Assert.Equal("root", deletionService.RootDirectory);
        Assert.Equal(1, deletionService.Avid);
        Assert.True(deletionService.DryRun);
        Assert.Same(deletionService.Result, result);
    }

    private static BiliVideoCache CreateCache(long avid, string title)
    {
        var segment = new BiliSegment(
            avid: avid,
            cid: 1,
            bvid: null,
            pageIndex: 1,
            partName: "P1",
            title: title,
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
            segmentDirectory: "segment",
            entryJsonPath: "entry.json",
            videoFiles: new[] { "video.mp4" },
            coverUrl: "cover",
            ownerName: null,
            ownerId: null);

        return new BiliVideoCache(avid, new[] { segment });
    }

    private sealed class RecordingIndexBuilder : ICacheIndexBuilder
    {
        public RecordingIndexBuilder(CacheIndex index)
        {
            Index = index;
        }

        public CacheIndex Index { get; }
        public string? RootDirectory { get; private set; }
        public CacheIndexBuildOptions? Options { get; private set; }

        public CacheIndex BuildIndex(string rootDirectory, CacheIndexBuildOptions? options = null)
        {
            RootDirectory = rootDirectory;
            Options = options;
            return Index;
        }
    }

    private sealed class RecordingDeletionService : ICacheDeletionService
    {
        public string? RootDirectory { get; private set; }
        public long Avid { get; private set; }
        public bool DryRun { get; private set; }
        public CacheDeletionResult Result { get; set; } =
            new CacheDeletionResult(found: true, deleted: true, targetPath: "root\\1");

        public CacheDeletionResult DeleteByAvid(string rootDirectory, long avid, bool dryRun = false)
        {
            RootDirectory = rootDirectory;
            Avid = avid;
            DryRun = dryRun;
            return Result;
        }
    }
}
