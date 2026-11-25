using BiliBiliLocalCacheManager.Core.Application.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host;

internal sealed partial class DesktopHostApplication
{
    private (int AvidDirectories, int SegmentDirectories)? _completeScanCounts;
    private MutationRefresh? _mutationRefresh;
    private sealed record MutationRefresh(CurrentIndexSnapshot Snapshot, int AvidDirectories, int SegmentDirectories,
        string Root, bool IncludeIncomplete, HashSet<long> Avids);

    private void InvalidateAfterMutation(string root, long avid)
    {
        lock (_stateSync)
        {
            var refresh = _mutationRefresh;
            if (refresh is null && _indexSnapshot is { } snapshot && _completeScanCounts is { } counts &&
                PathsEqual(_currentRoot, root))
                refresh = new(snapshot, counts.AvidDirectories, counts.SegmentDirectories, root, _currentIncludeIncomplete, []);
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
        var retained = source.Snapshot.Index.VideoCaches.Where(cache => !source.Avids.Contains(cache.Avid)).ToArray();
        var removed = source.Snapshot.Index.VideoCaches.Where(cache => source.Avids.Contains(cache.Avid)).ToArray();
        return new(new CacheIndex(retained.Concat(changed.Index.VideoCaches)),
            source.AvidDirectories - removed.Length + changed.ScannedAvidDirectories,
            source.SegmentDirectories - removed.Sum(cache => cache.Segments.Count) + changed.ScannedSegmentDirectories,
            retained.Sum(cache => cache.Segments.Count) + changed.IncludedEntries,
            changed.SkippedIncompleteEntries, changed.InvalidEntries, changed.InaccessibleDirectories, changed.Issues)
        {
            // An incremental refresh is only available from a complete, warning-free baseline.
            UnavailablePartIdentities = changed.UnavailablePartIdentities
        };
    }
}
