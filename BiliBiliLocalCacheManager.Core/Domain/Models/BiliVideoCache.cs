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

    public long TotalSize
    {
        get
        {
            var total = 0L;
            foreach (var segment in _segments)
            {
                var bytes = segment.TotalBytes;
                if (bytes <= 0)
                {
                    continue;
                }

                if (total > long.MaxValue - bytes)
                {
                    return long.MaxValue;
                }

                total += bytes;
            }

            return total;
        }
    }

    public TimeSpan TotalDuration
    {
        get
        {
            var totalTicks = 0L;
            foreach (var segment in _segments)
            {
                var ticks = segment.TotalDuration.Ticks;
                if (ticks <= 0)
                {
                    continue;
                }

                if (totalTicks > TimeSpan.MaxValue.Ticks - ticks)
                {
                    return TimeSpan.MaxValue;
                }

                totalTicks += ticks;
            }

            return TimeSpan.FromTicks(totalTicks);
        }
    }

    public bool IsAllCompleted => _segments.All(s => s.IsCompleted);

    public BiliVideoCache(long avid, IEnumerable<BiliSegment> segments)
    {
        // if (segments is null) throw new ArgumentNullException(nameof(segments));
        // 这个是新写法
        ArgumentNullException.ThrowIfNull(segments);

        var list = segments.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("segments must not be empty.", nameof(segments));
        }

        Avid = avid;
        _segments = new ReadOnlyCollection<BiliSegment>(list);

        var first = list[0];
        Title = first.Title;
        Bvid = first.Bvid;
        CoverUrl = first.CoverUrl;
        OwnerName = first.OwnerName;
        OwnerId = first.OwnerId;
    }
}
