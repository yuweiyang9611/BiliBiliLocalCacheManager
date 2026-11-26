using System.Globalization;
using System.IO.Enumeration;
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
        if (effectiveOptions.MaximumCacheBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumCacheBytes cannot be negative.");
        }

        NormalizeVideoExtensions(effectiveOptions);

        var state = new ScanAccumulator(effectiveOptions.MaxReportedIssues, effectiveOptions.CapturePartIdentities);
        var segments = new List<BiliSegment>();
        var avidDirectories = new List<(string Path, long Avid)>();
        foreach (var path in EnumerateDirectories(root, state, cancellationToken, effectiveOptions.AvidFilter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetDirectoryAvid(path, out var avid))
            {
                if (effectiveOptions.RequireCanonicalAvidDirectory &&
                    Path.GetFileName(path) != avid.ToString(CultureInfo.InvariantCulture))
                {
                    var message = $"已跳过带前导零的 AV 目录。缓存管理操作需要标准十进制目录名 {avid}。";
                    if (effectiveOptions.ThrowOnInvalidEntry) throw new InvalidDataException(message);
                    state.InvalidEntries++;
                    state.AddIssue(new CacheScanIssue(CacheScanIssueKind.InvalidEntry, path, message));
                    continue;
                }
                avidDirectories.Add((path, avid));
            }
        }
        state.ScannedAvidDirectories = avidDirectories.Count;

        foreach (var avidDirectory in avidDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanAvidDirectory(
                root,
                avidDirectory.Path,
                avidDirectory.Avid,
                effectiveOptions,
                segments,
                state,
                cancellationToken,
                progress);
            state.ProcessedAvidDirectories++;
            ReportProgress(state, avidDirectory.Path, progress);
        }

        var caches = segments
            .GroupBy(segment => segment.Avid)
            .Select(group => new BiliVideoCache(group.Key, group))
            .ToList();

        var unavailableParts = state.CollectIdentityIssues(cancellationToken);
        return new CacheIndexBuildResult(
            new CacheIndex(caches),
            state.ScannedAvidDirectories,
            state.ScannedSegmentDirectories,
            state.IncludedEntries,
            state.SkippedIncompleteEntries,
            state.InvalidEntries,
            state.InaccessibleDirectories,
            state.Issues)
        {
            PartIdentities = state.PartIdentities?.AsReadOnly(),
            UnavailablePartIdentities = unavailableParts
        };
    }

    private static void ScanAvidDirectory(
        string root,
        string avidDirectory,
        long directoryAvid,
        CacheIndexBuildOptions options,
        IList<BiliSegment> accumulator,
        ScanAccumulator state,
        CancellationToken cancellationToken,
        IProgress<CacheScanProgress>? progress)
    {
        using var parentLease = FileSystemCacheTrashService.LeaseScanParent(root, avidDirectory, options.CapturePartIdentities, out var parentError);
        if (parentLease is null && !TryValidatePhysicalDirectory(avidDirectory, state))
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
                // With a pinned Windows AV, ReadEntryForScan validates the part through its no-follow directory handle.
                if (parentLease is null && !TryValidatePhysicalDirectory(segmentDirectory, state))
                {
                    continue;
                }

                FileSystemCacheTrashService.ScannedPartMetadata metadata;
                try
                {
                    metadata = FileSystemCacheTrashService.ReadEntryForScan(root, avidDirectory, segmentDirectory,
                        entryPath, options.CapturePartIdentities, parentError, state.SmallEntryBuffer, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    // Missing entry.json is normal only while the enumerated part directory still exists.
                    TryValidatePhysicalDirectory(segmentDirectory, state);
                    continue;
                }
                using var partLease = metadata;
                cancellationToken.ThrowIfCancellationRequested();
                var raw = CacheEntryRaw.FromJson(metadata.ReadText());
                ValidateRawEntry(raw, directoryAvid, options.MaximumCacheBytes);

                if (!options.IncludeIncompleteEntries && !raw.IsCompleted)
                {
                    state.SkippedIncompleteEntries++;
                    continue;
                }

                var videoFiles = EnumerateVideoFiles(segmentDirectory, options);
                var segment = BiliSegmentFactory.FromRaw(raw, entryPath, segmentDirectory, videoFiles);
                if (options.MaximumCacheBytes is long maximumCacheBytes)
                {
                    var includedBytes = state.IncludedCacheBytes.GetValueOrDefault(directoryAvid);
                    if (segment.TotalBytes > maximumCacheBytes - includedBytes)
                    {
                        throw new InvalidDataException(
                            $"同一 avid ({directoryAvid}) 的缓存总字节数超出允许上限 ({maximumCacheBytes})。");
                    }

                    state.IncludedCacheBytes[directoryAvid] = includedBytes + segment.TotalBytes;
                }

                state.CaptureIncludedPart(segment, metadata);
                accumulator.Add(segment);
                state.IncludedEntries++;
            }
            catch (Exception ex) when (IsEntryFailure(ex))
            {
                // A directory can change between its initial check and the failing read on either platform.
                if (!TryValidatePhysicalDirectory(segmentDirectory, state)) continue;
                if (options.ThrowOnInvalidEntry) throw;
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
        CancellationToken cancellationToken,
        IReadOnlySet<long>? avidFilter = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directories = new List<string>();
            var entries = avidFilter is null
                ? new DirectoryInfo(path).EnumerateDirectories()
                : EnumerateFilteredAvidDirectories(path, avidFilter, cancellationToken);
            foreach (var directory in entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // DirectoryInfo already carries the attributes observed during enumeration.
                // Revalidate the path immediately before use in ScanAvidDirectory.
                if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) directories.Add(directory.FullName);
                else ReportLinkedDirectory(directory.FullName, state);
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

    private static IEnumerable<FileSystemInfo> EnumerateFilteredAvidDirectories(
        string root, IReadOnlySet<long> avidFilter, CancellationToken cancellationToken)
    {
        // Inspect names first, including leading-zero aliases. Only selected directories need
        // FileSystemInfo objects, their attributes and stable ordering in a partial refresh.
        return new FileSystemEnumerable<FileSystemInfo>(root, static (ref FileSystemEntry entry) => entry.ToFileSystemInfo(),
            new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TryParseAvid(entry.FileName, out var avid) && avidFilter.Contains(avid) && entry.IsDirectory;
            }
        };
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

            if (attributes.HasFlag(FileAttributes.ReparsePoint)) ReportLinkedDirectory(path, state);
            else
            {
                state.InaccessibleDirectories++;
                state.AddIssue(new(CacheScanIssueKind.InaccessibleDirectory, path, "该路径不是物理目录，已跳过。"));
            }
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

    private static void ReportLinkedDirectory(string path, ScanAccumulator state)
    {
        state.InaccessibleDirectories++;
        state.AddIssue(new(CacheScanIssueKind.InaccessibleDirectory, path, "已跳过符号链接或目录联接，避免扫描缓存根目录以外的内容。"));
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
        => TryParseAvid(Path.GetFileName(Path.TrimEndingDirectorySeparator(path.AsSpan())), out avid);

    private static bool TryParseAvid(ReadOnlySpan<char> name, out long avid)
        => long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out avid) && avid > 0;

    private static void ValidateRawEntry(CacheEntryRaw raw, long directoryAvid, long? maximumCacheBytes)
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

        if (maximumCacheBytes is long maximum)
        {
            ValidateByteCount(raw.TotalBytes, "total_bytes", maximum);
            ValidateByteCount(raw.DownloadedBytes, "downloaded_bytes", maximum);
            ValidateByteCount(raw.GuessedTotalBytes, "guessed_total_bytes", maximum);
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

    private static void ValidateByteCount(long bytes, string fieldName, long maximum)
    {
        if (bytes > maximum)
        {
            throw new InvalidDataException(
                $"entry.json 的 {fieldName} 超出允许的缓存字节数上限 ({maximum})。");
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

    private sealed class ScanAccumulator(int maxReportedIssues, bool capturePartIdentities)
    {
        public int ScannedAvidDirectories { get; set; }
        public int ProcessedAvidDirectories { get; set; }
        public int ScannedSegmentDirectories { get; set; }
        public int IncludedEntries { get; set; }
        public int SkippedIncompleteEntries { get; set; }
        public int InvalidEntries { get; set; }
        public int InaccessibleDirectories { get; set; }
        public List<CacheScanIssue> Issues { get; } = new();
        public byte[] SmallEntryBuffer { get; } = new byte[4096];
        public Dictionary<long, long> IncludedCacheBytes { get; } = new();
        public Dictionary<BiliSegment, CacheScanPartIdentity>? PartIdentities { get; } = capturePartIdentities ? new() : null;
        private readonly Dictionary<(long Avid, int Page), BiliSegment>? _firstCandidates = capturePartIdentities ? new() : null;

        public void CaptureIncludedPart(BiliSegment segment, FileSystemCacheTrashService.ScannedPartMetadata metadata)
        {
            if (PartIdentities is null) return;
            var key = (segment.Avid, segment.PageIndex);
            if (_firstCandidates!.TryAdd(key, segment))
                PartIdentities.Add(segment, metadata.Bind(segment));
            else
            {
                var ambiguous = new CacheScanPartIdentity(null, "该分 P 对应多个物理目录，无法安全确定删除范围。");
                PartIdentities[_firstCandidates[key]] = ambiguous;
                PartIdentities.Add(segment, ambiguous);
            }
        }

        public void AddIssue(CacheScanIssue issue)
        {
            if (Issues.Count < maxReportedIssues)
            {
                Issues.Add(issue);
            }
        }

        public int CollectIdentityIssues(CancellationToken cancellationToken)
        {
            if (_firstCandidates is null) return 0;
            var count = 0;
            foreach (var first in _firstCandidates.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PartIdentities![first].Error is not { } error) continue;
                count++;
                if (Issues.Count < maxReportedIssues)
                    Issues.Add(new(CacheScanIssueKind.PartIdentityUnavailable, first.EntryJsonPath, error));
            }
            return count;
        }
    }
}
