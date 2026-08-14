using System.Globalization;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Json;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Core.Services;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

/// <summary>
/// 使用 System.IO 扫描本地文件系统，构建 B 站缓存索引并返回可观察的扫描报告。
/// </summary>
public sealed class FileSystemCacheIndexBuilder : ICacheIndexBuilder
{
    public CacheIndex BuildIndex(string rootDirectory, CacheIndexBuildOptions? options = null)
    {
        return BuildIndexWithReport(rootDirectory, options).Index;
    }

    public CacheIndexBuildResult BuildIndexWithReport(
        string rootDirectory,
        CacheIndexBuildOptions? options = null)
    {
        return BuildIndexWithReport(rootDirectory, options, CancellationToken.None);
    }

    public CacheIndexBuildResult BuildIndexWithReport(
        string rootDirectory,
        CacheIndexBuildOptions? options,
        CancellationToken cancellationToken,
        IProgress<CacheScanProgress>? progress = null)
    {
        var root = CacheRootSafety.ValidatePhysicalRoot(rootDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveOptions = (options ?? new CacheIndexBuildOptions()).Clone();
        NormalizeVideoExtensions(effectiveOptions);

        var state = new ScanAccumulator(effectiveOptions.MaxReportedIssues);
        var segments = new List<BiliSegment>();
        var avidDirectories = EnumerateDirectories(root, state, cancellationToken)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                CacheStorageLayout.TrashDirectoryName,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => TryGetDirectoryAvid(path, out _))
            .ToList();
        state.ScannedAvidDirectories = avidDirectories.Count;

        foreach (var avidDirectory in avidDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanAvidDirectory(
                avidDirectory,
                effectiveOptions,
                segments,
                state,
                cancellationToken,
                progress);
            state.ProcessedAvidDirectories++;
            ReportProgress(state, avidDirectory, progress);
        }

        var caches = segments
            .GroupBy(segment => segment.Avid)
            .Select(group => new BiliVideoCache(group.Key, group))
            .ToList();

        return new CacheIndexBuildResult(
            new CacheIndex(caches),
            state.ScannedAvidDirectories,
            state.ScannedSegmentDirectories,
            state.IncludedEntries,
            state.SkippedIncompleteEntries,
            state.InvalidEntries,
            state.InaccessibleDirectories,
            state.Issues);
    }

    private static void ScanAvidDirectory(
        string avidDirectory,
        CacheIndexBuildOptions options,
        IList<BiliSegment> accumulator,
        ScanAccumulator state,
        CancellationToken cancellationToken,
        IProgress<CacheScanProgress>? progress)
    {
        if (!TryValidatePhysicalDirectory(avidDirectory, state))
        {
            return;
        }

        if (!TryGetDirectoryAvid(avidDirectory, out var directoryAvid))
        {
            return;
        }

        foreach (var segmentDirectory in EnumerateDirectories(avidDirectory, state, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.ScannedSegmentDirectories++;
            var entryPath = Path.Combine(segmentDirectory, options.EntryFileName);
            try
            {
                if (!TryValidatePhysicalDirectory(segmentDirectory, state))
                {
                    continue;
                }

                if (!File.Exists(entryPath))
                {
                    continue;
                }

                var json = File.ReadAllText(entryPath);
                cancellationToken.ThrowIfCancellationRequested();
                var raw = CacheEntryRaw.FromJson(json);
                ValidateRawEntry(raw, directoryAvid);

                if (!options.IncludeIncompleteEntries && !raw.IsCompleted)
                {
                    state.SkippedIncompleteEntries++;
                    continue;
                }

                var videoFiles = EnumerateVideoFiles(segmentDirectory, options);
                accumulator.Add(BiliSegmentFactory.FromRaw(raw, entryPath, segmentDirectory, videoFiles));
                state.IncludedEntries++;
            }
            catch (Exception ex) when (!options.ThrowOnInvalidEntry && IsEntryFailure(ex))
            {
                state.InvalidEntries++;
                state.AddIssue(new CacheScanIssue(
                    CacheScanIssueKind.InvalidEntry,
                    entryPath,
                    ex.Message));
            }
            finally
            {
                ReportProgress(state, segmentDirectory, progress);
            }
        }
    }

    private static IReadOnlyList<string> EnumerateDirectories(
        string path,
        ScanAccumulator state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directories = new List<string>();
            foreach (var directory in Directory.GetDirectories(path)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryValidatePhysicalDirectory(directory, state))
                {
                    directories.Add(directory);
                }
            }

            return directories;
        }
        catch (Exception ex) when (IsDirectoryFailure(ex))
        {
            state.InaccessibleDirectories++;
            state.AddIssue(new CacheScanIssue(
                CacheScanIssueKind.InaccessibleDirectory,
                path,
                ex.Message));
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyCollection<string> EnumerateVideoFiles(
        string segmentDirectory,
        CacheIndexBuildOptions options)
    {
        return Directory.EnumerateFiles(
                segmentDirectory,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                })
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return !string.IsNullOrEmpty(extension) &&
                       options.VideoFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
            })
            .ToList();
    }

    private static bool TryValidatePhysicalDirectory(string path, ScanAccumulator state)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            state.InaccessibleDirectories++;
            state.AddIssue(new CacheScanIssue(
                CacheScanIssueKind.InaccessibleDirectory,
                path,
                attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? "已跳过符号链接或目录联接，避免扫描缓存根目录以外的内容。"
                    : "该路径不是物理目录，已跳过。"));
            return false;
        }
        catch (Exception ex) when (IsDirectoryFailure(ex))
        {
            state.InaccessibleDirectories++;
            state.AddIssue(new CacheScanIssue(
                CacheScanIssueKind.InaccessibleDirectory,
                path,
                ex.Message));
            return false;
        }
    }

    private static void ReportProgress(
        ScanAccumulator state,
        string currentPath,
        IProgress<CacheScanProgress>? progress)
    {
        progress?.Report(new CacheScanProgress(
            state.ProcessedAvidDirectories,
            state.ScannedSegmentDirectories,
            state.IncludedEntries,
            currentPath));
    }

    private static void NormalizeVideoExtensions(CacheIndexBuildOptions options)
    {
        for (var index = 0; index < options.VideoFileExtensions.Count; index++)
        {
            var extension = options.VideoFileExtensions[index];
            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }

            extension = extension.Trim();
            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            options.VideoFileExtensions[index] = extension;
        }
    }

    private static bool TryGetDirectoryAvid(string path, out long avid)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return long.TryParse(
                   directoryName,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out avid) &&
               avid > 0;
    }

    private static void ValidateRawEntry(CacheEntryRaw raw, long directoryAvid)
    {
        if (raw.Avid <= 0)
        {
            throw new InvalidDataException("entry.json 缺少有效的 avid。");
        }

        if (raw.Avid != directoryAvid)
        {
            throw new InvalidDataException(
                $"entry.json 的 avid ({raw.Avid}) 与缓存目录 ({directoryAvid}) 不一致。");
        }

        if (raw.PageData is null || raw.PageData.Page <= 0)
        {
            throw new InvalidDataException("entry.json 缺少有效的 page_data/page。");
        }

        if (string.IsNullOrWhiteSpace(raw.Title))
        {
            throw new InvalidDataException("entry.json 缺少标题。");
        }

        if (string.IsNullOrWhiteSpace(raw.PageData.Part))
        {
            throw new InvalidDataException("entry.json 缺少分段标题。");
        }

        if (raw.TotalBytes < 0 ||
            raw.DownloadedBytes < 0 ||
            raw.GuessedTotalBytes < 0)
        {
            throw new InvalidDataException("entry.json 的缓存字节数不能为负数。");
        }

        var maximumDurationMilliseconds =
            TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        if (raw.TotalTimeMilli < 0 ||
            raw.TotalTimeMilli > maximumDurationMilliseconds)
        {
            throw new InvalidDataException(
                "entry.json 的 total_time_milli 超出可表示范围。");
        }
    }

    private static bool IsDirectoryFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    }

    private static bool IsEntryFailure(Exception exception)
    {
        return IsDirectoryFailure(exception) ||
               exception is Newtonsoft.Json.JsonException or FormatException or
                   OverflowException or ArgumentException or InvalidDataException;
    }

    private sealed class ScanAccumulator(int maxReportedIssues)
    {
        public int ScannedAvidDirectories { get; set; }
        public int ProcessedAvidDirectories { get; set; }
        public int ScannedSegmentDirectories { get; set; }
        public int IncludedEntries { get; set; }
        public int SkippedIncompleteEntries { get; set; }
        public int InvalidEntries { get; set; }
        public int InaccessibleDirectories { get; set; }
        public List<CacheScanIssue> Issues { get; } = new();

        public void AddIssue(CacheScanIssue issue)
        {
            if (Issues.Count < maxReportedIssues)
            {
                Issues.Add(issue);
            }
        }
    }
}
