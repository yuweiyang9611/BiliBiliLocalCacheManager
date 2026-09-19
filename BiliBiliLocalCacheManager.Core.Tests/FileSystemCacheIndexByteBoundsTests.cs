using System.Globalization;
using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheIndexByteBoundsTests : IDisposable
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"bili_scan_byte_bounds_{Guid.NewGuid():N}");

    public FileSystemCacheIndexByteBoundsTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(MaximumSafeInteger)]
    [InlineData(long.MaxValue)]
    public void ConfiguredLimit_IncludesExactBoundary(long maximum)
    {
        WriteEntry("1", "a", maximum, maximum, maximum);

        var report = Scan(maximum);

        var cache = Assert.Single(report.Index.VideoCaches);
        Assert.Equal(maximum, cache.TotalSize);
        Assert.Equal(maximum, Assert.Single(cache.Segments).DownloadedBytes);
        Assert.Equal(1, report.IncludedEntries);
        Assert.Empty(report.Issues);
    }

    [Theory]
    [InlineData("total_bytes", MaximumSafeInteger + 1)]
    [InlineData("downloaded_bytes", MaximumSafeInteger + 1)]
    [InlineData("guessed_total_bytes", MaximumSafeInteger + 1)]
    [InlineData("total_bytes", long.MaxValue)]
    [InlineData("downloaded_bytes", long.MaxValue)]
    [InlineData("guessed_total_bytes", long.MaxValue)]
    public void ConfiguredLimit_RejectsEachOversizedRawFieldWithoutLosingOtherCaches(
        string fieldName,
        long bytes)
    {
        var invalidPath = WriteEntry(
            "1", "bad",
            totalBytes: fieldName == "total_bytes" ? bytes : 1,
            downloadedBytes: fieldName == "downloaded_bytes" ? bytes : 1,
            guessedTotalBytes: fieldName == "guessed_total_bytes" ? bytes : 1);
        WriteEntry("2", "good", 1);

        var report = Scan();

        Assert.Equal(2, Assert.Single(report.Index.VideoCaches).Avid);
        Assert.Equal(1, report.IncludedEntries);
        Assert.Equal(1, report.InvalidEntries);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(CacheScanIssueKind.InvalidEntry, issue.Kind);
        Assert.Equal(invalidPath, issue.Path);
        Assert.Contains(fieldName, issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredLimit_RejectsOnlyAggregateOverflowAndRetainsScanOrder(bool aliasDirectory)
    {
        WriteEntry(aliasDirectory ? "001" : "1", "a", MaximumSafeInteger - 2);
        var overflowPath = WriteEntry("1", "b", 3);
        WriteEntry("1", "c", 2);
        WriteEntry("1", "d_zero", 0);
        WriteEntry("2", "a", 10);

        var report = Scan();

        Assert.Equal(4, report.IncludedEntries);
        Assert.Equal(1, report.InvalidEntries);
        Assert.Equal(2, report.Index.VideoCaches.Count);
        var cache = Assert.Single(report.Index.VideoCaches, item => item.Avid == 1);
        Assert.Equal(MaximumSafeInteger, cache.TotalSize);
        Assert.Equal(new[] { "a", "c", "d_zero" }, cache.Segments.Select(
            segment => Path.GetFileName(segment.SegmentDirectory)));
        Assert.Equal(10, Assert.Single(report.Index.VideoCaches, item => item.Avid == 2).TotalSize);
        Assert.Equal(overflowPath, Assert.Single(report.Issues).Path);
    }

    [Fact]
    public void ConfiguredLongMaximum_DoesNotOverflowDuringAggregateValidation()
    {
        WriteEntry("1", "a", long.MaxValue);
        WriteEntry("1", "b", 1);

        var report = Scan(long.MaxValue);

        Assert.Equal(long.MaxValue, Assert.Single(report.Index.VideoCaches).TotalSize);
        Assert.Equal(1, report.IncludedEntries);
        Assert.Equal(1, report.InvalidEntries);
    }

    [Fact]
    public void RejectedAndSkippedEntries_DoNotConsumeAggregateBudget()
    {
        WriteEntry("1", "a_invalid_metadata", MaximumSafeInteger, title: " ");
        WriteEntry("1", "b_factory_failure", MaximumSafeInteger, createdAt: long.MaxValue);
        WriteEntry("1", "c_incomplete", MaximumSafeInteger, completed: false);
        WriteEntry("1", "d_valid", MaximumSafeInteger);

        var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(
            _root,
            new CacheIndexBuildOptions
            {
                MaximumCacheBytes = MaximumSafeInteger,
                IncludeIncompleteEntries = false
            });

        Assert.Equal(1, report.IncludedEntries);
        Assert.Equal(1, report.SkippedIncompleteEntries);
        Assert.Equal(2, report.InvalidEntries);
        Assert.Equal(MaximumSafeInteger, Assert.Single(report.Index.VideoCaches).TotalSize);
    }

    [Fact]
    public void OversizedEntries_TruncateDetailsWithoutLosingInvalidCounts()
    {
        for (var index = 0; index < 6; index++)
        {
            WriteEntry("1", $"bad_{index}", MaximumSafeInteger + 1);
        }

        WriteEntry("2", "good", 1);
        var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(
            _root,
            new CacheIndexBuildOptions { MaximumCacheBytes = MaximumSafeInteger, MaxReportedIssues = 2 });

        Assert.Equal(7, report.ScannedSegmentDirectories);
        Assert.Equal(6, report.InvalidEntries);
        Assert.Equal(1, report.IncludedEntries);
        Assert.Equal(2, report.Issues.Count);
        Assert.All(report.Issues, issue => Assert.Equal(CacheScanIssueKind.InvalidEntry, issue.Kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictMode_ThrowsForRawAndAggregateBounds(bool aggregate)
    {
        WriteEntry("1", "a", aggregate ? MaximumSafeInteger : MaximumSafeInteger + 1);
        if (aggregate)
        {
            WriteEntry("1", "b", 1);
        }

        Assert.Throws<InvalidDataException>(() => new FileSystemCacheIndexBuilder().BuildIndexWithReport(
            _root,
            new CacheIndexBuildOptions { MaximumCacheBytes = MaximumSafeInteger, ThrowOnInvalidEntry = true }));
    }

    [Fact]
    public void DefaultOptions_RetainFullInt64FieldsAndSaturatingAggregate()
    {
        WriteEntry("1", "a", long.MaxValue, long.MaxValue, long.MaxValue);
        WriteEntry("1", "b", 1);

        var report = new FileSystemCacheIndexBuilder().BuildIndexWithReport(_root);

        Assert.Null(new CacheIndexBuildOptions().MaximumCacheBytes);
        Assert.Equal(2, report.IncludedEntries);
        Assert.Equal(long.MaxValue, Assert.Single(report.Index.VideoCaches).TotalSize);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Clone_PreservesConfiguredByteLimit()
    {
        var options = new CacheIndexBuildOptions { MaximumCacheBytes = MaximumSafeInteger };

        var cloned = options.Clone();

        Assert.NotSame(options, cloned);
        Assert.Equal(MaximumSafeInteger, cloned.MaximumCacheBytes);
        Assert.Null(new CacheIndexBuildOptions().Clone().MaximumCacheBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeConfiguredLimit_IsRejectedBeforeScanningEvenEmptyRoot(bool strict)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSystemCacheIndexBuilder().BuildIndexWithReport(
            _root,
            new CacheIndexBuildOptions { MaximumCacheBytes = -1, ThrowOnInvalidEntry = strict }));
    }

    private CacheIndexBuildResult Scan(long maximum = MaximumSafeInteger)
    {
        return new FileSystemCacheIndexBuilder().BuildIndexWithReport(
            _root,
            new CacheIndexBuildOptions { MaximumCacheBytes = maximum });
    }

    private string WriteEntry(
        string avidDirectory,
        string segmentName,
        long totalBytes,
        long downloadedBytes = 0,
        long guessedTotalBytes = 0,
        bool completed = true,
        string title = "Title",
        long createdAt = 0)
    {
        var directory = Path.Combine(_root, avidDirectory, segmentName);
        Directory.CreateDirectory(directory);
        var entryPath = Path.Combine(directory, "entry.json");
        File.WriteAllText(entryPath, JsonSerializer.Serialize(new
        {
            avid = long.Parse(avidDirectory, CultureInfo.InvariantCulture),
            title,
            is_completed = completed,
            total_bytes = totalBytes,
            downloaded_bytes = downloadedBytes,
            guessed_total_bytes = guessedTotalBytes,
            total_time_milli = 1_000,
            time_create_stamp = createdAt,
            page_data = new { cid = 1, page = 1, part = segmentName }
        }));
        return entryPath;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
