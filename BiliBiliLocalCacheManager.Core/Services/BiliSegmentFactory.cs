using System.Collections.ObjectModel;
using BiliBiliLocalCacheManager.Core.Domain.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Json;

namespace BiliBiliLocalCacheManager.Core.Services;

/// <summary>
/// 负责把原始 JSON DTO 转换成领域模型 BiliSegment。
/// </summary>
public static class BiliSegmentFactory
{
    public static BiliSegment FromRaw(CacheEntryRaw raw, string entryJsonPath, string segmentDirectory,
        IEnumerable<string> videoFiles)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var pageData = raw.PageData ?? throw new ArgumentException("PageData must not be null.", nameof(raw));

        var version = DetermineVersion(raw);
        var createdAt = FromUnixMilliseconds(raw.TimeCreateStamp);
        var updatedAt = FromUnixMilliseconds(raw.TimeUpdateStamp);

        var videoFileList = new ReadOnlyCollection<string>(
            (videoFiles ?? Enumerable.Empty<string>()).ToList()
        );

        // 如果 VideoQuality 为空，就回退到 PreferedVideoQuality
        var videoQuality = raw.VideoQuality ?? raw.PreferedVideoQuality;

        return new BiliSegment(
            avid: raw.Avid,
            cid: pageData.Cid,
            bvid: raw.Bvid,
            pageIndex: pageData.Page,
            partName: pageData.Part,
            title: raw.Title,
            version: version,
            typeTag: raw.TypeTag,
            mediaType: raw.MediaType,
            videoQuality: videoQuality,
            qualityDescription: raw.QualityPithyDescription,
            isCompleted: raw.IsCompleted,
            totalBytes: raw.TotalBytes,
            downloadedBytes: raw.DownloadedBytes,
            totalDuration: TimeSpan.FromTicks(checked(
                raw.TotalTimeMilli * TimeSpan.TicksPerMillisecond)),
            danmakuCount: raw.DanmakuCount,
            createdAt: createdAt,
            updatedAt: updatedAt,
            segmentDirectory: segmentDirectory,
            entryJsonPath: entryJsonPath,
            videoFiles: videoFileList,
            coverUrl: raw.Cover,
            ownerName: raw.OwnerName,
            ownerId: raw.OwnerId
        );
    }

    private static CacheVersion DetermineVersion(CacheEntryRaw raw)
    {
        // 简单的启发式：出现这些字段中的任意一个，就判定为新版本。
        if (!string.IsNullOrEmpty(raw.Bvid) ||
            raw.MediaType.HasValue ||
            raw.HasDashAudio.HasValue ||
            raw.CacheVersionCode.HasValue)
        {
            return CacheVersion.Modern;
        }

        return CacheVersion.Legacy;
    }

    private static DateTimeOffset FromUnixMilliseconds(long ms)
    {
        return ms <= 0
            ?
            // 部分旧缓存可能为 0，这里统一返回 MinValue，调用方可根据实际需要处理。
            DateTimeOffset.MinValue
            : DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }
}
