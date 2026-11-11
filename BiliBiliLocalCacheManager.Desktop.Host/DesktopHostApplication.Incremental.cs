using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private CacheIndexBuildResult? _lastScanReport;
    private MutationRefresh? _mutationRefresh;
    private sealed record MutationRefresh(CurrentIndexSnapshot Snapshot, CacheIndexBuildResult Report,
        string Root, bool IncludeIncomplete, HashSet<long> Avids);

    private void InvalidateAfterMutation(string root, long avid)
    {
        lock (_stateSync)
        {
            var refresh = _mutationRefresh;
            // A clean, fully represented baseline lets us subtract per-video counts exactly.
            // Missing metadata, skipped entries and bounded issue lists require a full scan.
            if (refresh is null && _indexSnapshot is { } snapshot && _lastScanReport is { } report &&
                snapshot.PartIdentityIssueCount == 0 &&
                PathsEqual(_currentRoot, root) && report.IssuesCollected && !report.HasWarnings &&
                report.ScannedAvidDirectories == report.Index.VideoCaches.Count &&
                report.ScannedSegmentDirectories == report.IncludedEntries)
                refresh = new(snapshot, report, root, _currentIncludeIncomplete, []);
            ClearCurrentIndex();
            if (refresh is not null && PathsEqual(refresh.Root, root))
            {
                refresh.Avids.Add(avid);
                _mutationRefresh = refresh;
            }
        }
    }

    private MutationRefresh? GetMutationRefresh(string root, bool includeIncomplete)
    {
        lock (_stateSync)
        {
            var refresh = _mutationRefresh;
            return refresh is not null && PathsEqual(refresh.Root, root) && refresh.IncludeIncomplete == includeIncomplete
                ? refresh with { Avids = new HashSet<long>(refresh.Avids) } : null;
        }
    }

    private static CacheIndexBuildResult MergeMutationRefresh(MutationRefresh source, CacheIndexBuildResult changed)
    {
        var retained = source.Report.Index.VideoCaches.Where(cache => !source.Avids.Contains(cache.Avid)).ToArray();
        var removed = source.Report.Index.VideoCaches.Where(cache => source.Avids.Contains(cache.Avid)).ToArray();
        return new(new CacheIndex(retained.Concat(changed.Index.VideoCaches)),
            source.Report.ScannedAvidDirectories - removed.Length + changed.ScannedAvidDirectories,
            source.Report.ScannedSegmentDirectories - removed.Sum(cache => cache.Segments.Count) + changed.ScannedSegmentDirectories,
            retained.Sum(cache => cache.Segments.Count) + changed.IncludedEntries,
            changed.SkippedIncompleteEntries, changed.InvalidEntries, changed.InaccessibleDirectories, changed.Issues);
    }
}
