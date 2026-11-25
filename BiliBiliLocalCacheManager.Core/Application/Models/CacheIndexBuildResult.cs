using System.Collections.ObjectModel;
using BiliBiliLocalCacheManager.Core.Domain.Models;

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
        IEnumerable<CacheScanIssue>? issues = null,
        bool issuesCollected = true)
    {
        Index = index ?? throw new ArgumentNullException(nameof(index));
        ScannedAvidDirectories = scannedAvidDirectories;
        ScannedSegmentDirectories = scannedSegmentDirectories;
        IncludedEntries = includedEntries;
        SkippedIncompleteEntries = skippedIncompleteEntries;
        InvalidEntries = invalidEntries;
        InaccessibleDirectories = inaccessibleDirectories;
        IssuesCollected = issuesCollected;
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

    /// <summary>False when a legacy builder supplied only an index, not scan diagnostics.</summary>
    public bool IssuesCollected { get; }

    /// <summary>Physical identities (or the reason binding failed) for segments read during this build.</summary>
    public IReadOnlyDictionary<BiliSegment, CacheScanPartIdentity>? PartIdentities { get; internal init; }

    /// <summary>Logical AV/page selections that could not be bound, including issues beyond the detail limit.</summary>
    public int UnavailablePartIdentities { get; init; }

    public bool HasWarnings => SkippedIncompleteEntries > 0 || InvalidEntries > 0 ||
        InaccessibleDirectories > 0 || UnavailablePartIdentities > 0;

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
            inaccessibleDirectories: 0,
            issuesCollected: false);
    }
}

public readonly record struct CacheScanPartIdentity(CacheTrashPartTarget? Target, string? Error);
