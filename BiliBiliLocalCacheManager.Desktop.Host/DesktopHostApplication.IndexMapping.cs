using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private async Task<CacheIndex> ResolveIndexAsync(
        string requestId,
        string operation,
        string root,
        bool includeIncomplete,
        CancellationToken cancellationToken)
    {
        var generation = ReadIndexGeneration();
        await _indexBuildGate.WaitAsync(cancellationToken);
        try
        {
            EnsureIndexGeneration(generation);
            lock (_stateSync)
            {
                if (_currentIndex is not null &&
                    _currentIndexToken is not null &&
                    PathsEqual(_currentRoot, root) &&
                    _currentIncludeIncomplete == includeIncomplete)
                {
                    return _currentIndex;
                }
            }

            var progress = new InlineProgress<CacheScanProgress>(value =>
                ReportProgress(new HostProgressEvent(
                    requestId,
                    operation,
                    "indexing",
                    Current: value.ProcessedAvidDirectories,
                    Message: value.CurrentPath,
                    Details: new
                    {
                        value.ProcessedAvidDirectories,
                        value.ProcessedSegmentDirectories,
                        value.IncludedEntries
                    }, Phase: "scan")));
            var options = new CacheIndexBuildOptions
            {
                IncludeIncompleteEntries = includeIncomplete,
                MaximumCacheBytes = MaximumWireBytes
            };
            var report = await Task.Run(
                () => _cacheManager.BuildIndexWithReport(
                    root,
                    options,
                    cancellationToken,
                    progress),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return SetCurrentIndex(
                report.Index,
                root,
                includeIncomplete,
                DateTimeOffset.UtcNow, report.Issues, generation, cancellationToken: cancellationToken,
                partCaptureProgress: count => ReportPartCaptureProgress(requestId, operation, count)).Index;
        }
        finally { _indexBuildGate.Release(); }
    }

    private CachePageDto CreateCachePage(
        CurrentIndexSnapshot snapshot,
        IEnumerable<BiliVideoCache> caches,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = caches as IReadOnlyCollection<BiliVideoCache> ?? caches.ToArray();
        var items = ordered
            .Skip(offset)
            .Take(pageSize)
            .Select(cache =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return snapshot.Summaries[cache.Avid];
            })
            .ToArray();
        return new CachePageDto(
            snapshot.IndexToken,
            offset,
            pageSize,
            ordered.Count,
            HasMore(offset, pageSize, ordered.Count),
            items);
    }

    private static CacheSummaryDto MapCacheSummary(BiliVideoCache cache)
    {
        var avid = cache.Avid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lastUpdated = GetLastUpdatedUtc(cache);
        return new CacheSummaryDto(
            avid,
            avid,
            BoundWireString(cache.Title),
            BoundWireString(cache.Bvid),
            BoundWireString(cache.OwnerName),
            cache.TotalDuration.TotalSeconds,
            cache.Segments.Count,
            cache.TotalSize,
            cache.IsAllCompleted,
            lastUpdated == DateTimeOffset.MinValue ? null : lastUpdated,
            cache.Segments.Select(segment => segment.PageIndex).Distinct().Count());
    }

    private SegmentDto MapSegment(BiliSegment segment)
    {
        CachePlaybackPlan? plan = null;
        try
        {
            plan = _playbackService.CreatePlan(segment);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException ||
                                           exception.GetType() == typeof(InvalidOperationException) ||
                                           exception.GetType() == typeof(ArgumentException))
        {
            _eventRecorder.Record("Playback", "Warning", $"Cannot inspect playback plan for av{segment.Avid} P{segment.PageIndex}: {exception.Message}", exception);
        }

        var segmentKey = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(segment.SegmentDirectory));
        return new SegmentDto(
            BoundWireString($"{segment.Avid}:{segment.PageIndex}:{segmentKey}"),
            BoundWireString(segmentKey),
            segment.PageIndex,
            BoundWireString(segment.PartName),
            BoundWireString(plan?.StructureKind ?? "Unknown"),
            BoundWireString(plan?.MaterialKind.ToString() ?? "Unavailable"),
            segment.TotalBytes,
            segment.TotalDuration.TotalSeconds,
            plan?.IsPlayable == true,
            BoundWireString(segment.SegmentDirectory));
    }

    private static PaginationRequest ParsePagination(JsonElement parameters)
    {
        var offset = parameters.OptionalInt32("offset") ?? 0;
        var pageSize = parameters.OptionalInt32("pageSize") ?? DefaultPageSize;
        if (offset < 0)
        {
            throw new RpcException("invalid_params", "offset must be a non-negative integer.");
        }

        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new RpcException(
                "invalid_params",
                $"pageSize must be between 1 and {MaximumPageSize}.");
        }

        return new PaginationRequest(offset, pageSize);
    }

    private static string RequireIndexToken(JsonElement parameters)
    {
        var indexToken = parameters.RequireString("indexToken");
        if (indexToken.Length > MaximumIndexTokenLength)
        {
            throw new RpcException(
                "invalid_params",
                $"indexToken may not exceed {MaximumIndexTokenLength} characters.");
        }

        return indexToken;
    }

    private CurrentIndexSnapshot ResolveCurrentIndex(string indexToken)
    {
        lock (_stateSync)
        {
            if (_currentIndex is null ||
                _currentIndexToken is null ||
                !string.Equals(_currentIndexToken, indexToken, StringComparison.Ordinal))
            {
                throw StaleIndexException();
            }

            return _indexSnapshot!;
        }
    }

    private CurrentIndexSnapshot SetCurrentIndex(
        CacheIndex index,
        string root,
        bool includeIncomplete,
        DateTimeOffset completedAtUtc,
        IReadOnlyList<CacheScanIssue>? issues = null,
        long? expectedGeneration = null,
        bool persistScanSettings = false,
        CancellationToken cancellationToken = default,
        Action<int>? partCaptureProgress = null)
    {
        var orderedIndex = new CacheIndex(index.VideoCaches.OrderByDescending(GetLastUpdatedUtc).ThenBy(cache => cache.Avid));
        var snapshot = new CurrentIndexSnapshot(orderedIndex, Guid.NewGuid().ToString("N"), root, issues ?? []);
        snapshot.CaptureTrashPartTargets(_trashService, cancellationToken, partCaptureProgress);
        lock (_stateSync)
        {
            if (expectedGeneration is { } generation && generation != _indexGeneration) throw StaleIndexException();
            _indexSnapshot?.Invalidate();
            _indexSnapshot = snapshot;
            _currentIndex = orderedIndex;
            _currentIndexToken = snapshot.IndexToken;
            _currentRoot = root;
            _currentIncludeIncomplete = includeIncomplete;
            _lastScanCompletedAtUtc = completedAtUtc;
            if (persistScanSettings) TryPersistScanSettings(root, includeIncomplete);
            return snapshot;
        }
    }

    private void EnsureIndexStillCurrent(CurrentIndexSnapshot snapshot)
    {
        lock (_stateSync)
        {
            if (!ReferenceEquals(_currentIndex, snapshot.Index) ||
                !string.Equals(_currentIndexToken, snapshot.IndexToken, StringComparison.Ordinal))
            {
                throw StaleIndexException();
            }
        }
    }

    private static RpcException StaleIndexException() => new(
        "stale_index",
        "The cache index token is missing or no longer current. Run scan again.");

    private static bool HasMore(int offset, int pageSize, int totalItems) =>
        offset < totalItems && pageSize < totalItems - offset;

    private static string BoundWireString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= MaximumWireTextLength)
        {
            return value;
        }

        var length = MaximumWireTextLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
