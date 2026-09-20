using System.Collections.ObjectModel;

namespace BiliBiliLocalCacheManager.Core.Domain.Models;

/// <summary>
/// 按 avid 聚合的一个“视频缓存”，包含多个分段。
/// </summary>
public sealed class BiliVideoCache
{
    private readonly ReadOnlyCollection<BiliSegment> _segments;

    public long Avid { get; }

    /// <summary>
    /// 统一标题（通常取第一个分段的 Title）。
    /// </summary>
    public string Title { get; }

    public string? Bvid { get; }
    public string CoverUrl { get; }
    public string? OwnerName { get; }
    public long? OwnerId { get; }

    public IReadOnlyCollection<BiliSegment> Segments => _segments;

    public long TotalSize { get; }

    public TimeSpan TotalDuration { get; }

    public bool IsAllCompleted { get; }

    public BiliVideoCache(long avid, IEnumerable<BiliSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var list = segments.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("segments must not be empty.", nameof(segments));
        }

        Avid = avid;
        _segments = new ReadOnlyCollection<BiliSegment>(list);
        var totalBytes = 0L;
        var totalTicks = 0L;
        var allCompleted = true;
        foreach (var segment in list)
        {
            totalBytes = AddPositiveSaturating(totalBytes, segment.TotalBytes);
            totalTicks = AddPositiveSaturating(totalTicks, segment.TotalDuration.Ticks);
            allCompleted &= segment.IsCompleted;
        }
        TotalSize = totalBytes;
        TotalDuration = TimeSpan.FromTicks(totalTicks);
        IsAllCompleted = allCompleted;

        var first = list[0];
        Title = first.Title;
        Bvid = first.Bvid;
        CoverUrl = first.CoverUrl;
        OwnerName = first.OwnerName;
        OwnerId = first.OwnerId;
    }

    private static long AddPositiveSaturating(long total, long value)
        => value <= 0 ? total : value > long.MaxValue - total ? long.MaxValue : total + value;
}
