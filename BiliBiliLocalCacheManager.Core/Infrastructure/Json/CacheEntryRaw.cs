using Newtonsoft.Json;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Json;

/// <summary>
/// 对应 entry.json 顶层的原始结构（新旧 JSON 共用）。
/// 共有字段非空，新增字段可空。
/// </summary>
public sealed class CacheEntryRaw
{
    // ===== 新版特有/非共有字段（可空） =====

    [JsonProperty("media_type")] public int? MediaType { get; set; }

    [JsonProperty("has_dash_audio")] public bool? HasDashAudio { get; set; }

    [JsonProperty("video_quality")] public int? VideoQuality { get; set; }

    [JsonProperty("can_play_in_advance")] public bool? CanPlayInAdvance { get; set; }

    [JsonProperty("interrupt_transform_temp_file")]
    public bool? InterruptTransformTempFile { get; set; }

    [JsonProperty("quality_pithy_description")]
    public string? QualityPithyDescription { get; set; }

    [JsonProperty("quality_superscript")] public string? QualitySuperscript { get; set; }

    [JsonProperty("cache_version_code")] public int? CacheVersionCode { get; set; }

    [JsonProperty("preferred_audio_quality")]
    public int? PreferredAudioQuality { get; set; }

    [JsonProperty("audio_quality")] public int? AudioQuality { get; set; }

    [JsonProperty("bvid")] public string? Bvid { get; set; }

    [JsonProperty("owner_id")] public long? OwnerId { get; set; }

    [JsonProperty("owner_name")] public string? OwnerName { get; set; }

    [JsonProperty("owner_avatar")] public string? OwnerAvatar { get; set; }

    // ===== 共有字段（新旧 JSON 都有，非空） =====

    [JsonProperty("is_completed")] public bool IsCompleted { get; set; }

    [JsonProperty("total_bytes")] public long TotalBytes { get; set; }

    [JsonProperty("downloaded_bytes")] public long DownloadedBytes { get; set; }

    [JsonProperty("title")] public string Title { get; set; } = string.Empty;

    [JsonProperty("type_tag")] public string TypeTag { get; set; } = string.Empty;

    [JsonProperty("cover")] public string Cover { get; set; } = string.Empty;

    /// <summary>
    /// 注意 JSON 字段名拼写为 "prefered_video_quality"
    /// </summary>
    [JsonProperty("prefered_video_quality")]
    public int PreferedVideoQuality { get; set; }

    [JsonProperty("guessed_total_bytes")] public long GuessedTotalBytes { get; set; }

    [JsonProperty("total_time_milli")] public long TotalTimeMilli { get; set; }

    [JsonProperty("danmaku_count")] public int DanmakuCount { get; set; }

    [JsonProperty("time_update_stamp")] public long TimeUpdateStamp { get; set; }

    [JsonProperty("time_create_stamp")] public long TimeCreateStamp { get; set; }

    [JsonProperty("avid")] public long Avid { get; set; }

    [JsonProperty("spid")] public long Spid { get; set; }

    /// <summary>
    /// 注意 JSON 字段名拼写为 "seasion_id"
    /// </summary>
    [JsonProperty("seasion_id")] public long SeasionId { get; set; }

    [JsonProperty("page_data")] public PageDataRaw? PageData { get; set; }

    /// <summary>
    /// 方便调用端直接通过字符串反序列化。
    /// </summary>
    public static CacheEntryRaw FromJson(string json)
    {
        var entry = JsonConvert.DeserializeObject<CacheEntryRaw>(json);
        return entry ?? throw new JsonSerializationException("Failed to deserialize CacheEntryRaw from JSON.");
    }
}