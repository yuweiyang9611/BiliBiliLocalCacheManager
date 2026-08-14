using System.Collections.ObjectModel;

namespace BiliBiliLocalCacheManager.Core.Application.Models;

public sealed class CacheIndexBuildResult
{
    private readonly ReadOnlyCollection<CacheScanIssue> _issues;

    public CacheIndexBuildResult(
        CacheIndex index,
        int scannedAvidDirectories,
        int scannedSegmentDirectories,
        int includedEntries,
        int skippedIncompleteEntries,
        int invalidEntries,
        int inaccessibleDirectories,
        IEnumerable<CacheScanIssue>? issues = null)
    {
        Index = index ?? throw new ArgumentNullException(nameof(index));
        ScannedAvidDirectories = scannedAvidDirectories;
        ScannedSegmentDirectories = scannedSegmentDirectories;
        IncludedEntries = includedEntries;
        SkippedIncompleteEntries = skippedIncompleteEntries;
        InvalidEntries = invalidEntries;
        InaccessibleDirectories = inaccessibleDirectories;
        _issues = new ReadOnlyCollection<CacheScanIssue>((issues ?? Array.Empty<CacheScanIssue>()).ToList());
    }

    public CacheIndex Index { get; }

    public int ScannedAvidDirectories { get; }

    public int ScannedSegmentDirectories { get; }

    public int IncludedEntries { get; }

    public int SkippedIncompleteEntries { get; }

    public int InvalidEntries { get; }

    public int InaccessibleDirectories { get; }

    public IReadOnlyList<CacheScanIssue> Issues => _issues;

    public bool HasWarnings => SkippedIncompleteEntries > 0 || InvalidEntries > 0 || InaccessibleDirectories > 0;

    public static CacheIndexBuildResult FromIndex(CacheIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return new CacheIndexBuildResult(
            index,
            scannedAvidDirectories: index.VideoCaches.Count,
            scannedSegmentDirectories: index.VideoCaches.Sum(cache => cache.Segments.Count),
            includedEntries: index.VideoCaches.Sum(cache => cache.Segments.Count),
            skippedIncompleteEntries: 0,
            invalidEntries: 0,
            inaccessibleDirectories: 0);
    }
}
