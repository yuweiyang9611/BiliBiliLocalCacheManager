namespace BiliBiliLocalCacheManager.Core.Domain.Models;

/// <summary>
/// 表示一个缓存分段（对应一个 entry.json + 一个分段目录）。
/// </summary>
public sealed class BiliSegment(long avid,
    long cid,
    string? bvid,
    int pageIndex,
    string partName,
    string title,
    CacheVersion version,
    string typeTag,
    int? mediaType,
    int? videoQuality,
    string? qualityDescription,
    bool isCompleted,
    long totalBytes,
    long downloadedBytes,
    TimeSpan totalDuration,
    int danmakuCount,
    DateTimeOffset createdAt,
    DateTimeOffset updatedAt,
    string segmentDirectory,
    string entryJsonPath,
    IReadOnlyCollection<string> videoFiles,
    string coverUrl,
    string? ownerName,
    long? ownerId)
{
    // 标识与基本信息
    public long Avid { get; } = avid;
    public long Cid { get; } = cid;
    public string? Bvid { get; } = bvid;

    /// <summary>
    /// 分 P 序号（page 字段），一般从 1 开始。
    /// </summary>
    public int PageIndex { get; } = pageIndex;

    /// <summary>
    /// 分 P 名称（page_data.part）。
    /// </summary>
    public string PartName { get; } = partName ?? throw new ArgumentNullException(nameof(partName));

    /// <summary>
    /// 视频标题（entry.json title）。
    /// </summary>
    public string Title { get; } = title ?? throw new ArgumentNullException(nameof(title));

    // 版本 / 类型信息
    public CacheVersion Version { get; } = version;
    public string TypeTag { get; } = typeTag ?? throw new ArgumentNullException(nameof(typeTag));
    public int? MediaType { get; } = mediaType;
    public int? VideoQuality { get; } = videoQuality;
    public string? QualityDescription { get; } = qualityDescription;

    // 下载状态 / 统计
    public bool IsCompleted { get; } = isCompleted;
    public long TotalBytes { get; } = totalBytes;
    public long DownloadedBytes { get; } = downloadedBytes;
    public TimeSpan TotalDuration { get; } = totalDuration;
    public int DanmakuCount { get; } = danmakuCount;

    // 时间信息
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public DateTimeOffset UpdatedAt { get; } = updatedAt;

    // 文件路径信息
    public string SegmentDirectory { get; } =
        segmentDirectory ?? throw new ArgumentNullException(nameof(segmentDirectory));

    public string EntryJsonPath { get; } = entryJsonPath ?? throw new ArgumentNullException(nameof(entryJsonPath));

    public IReadOnlyCollection<string> VideoFiles { get; } =
        videoFiles ?? throw new ArgumentNullException(nameof(videoFiles));

    // 其他
    public string CoverUrl { get; } = coverUrl ?? throw new ArgumentNullException(nameof(coverUrl));
    public string? OwnerName { get; } = ownerName;
    public long? OwnerId { get; } = ownerId;
}