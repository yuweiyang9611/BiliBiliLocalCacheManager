using Newtonsoft.Json;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Json;

/// <summary>
/// 对应 entry.json 中的 page_data。
/// </summary>
public sealed class PageDataRaw
{
    // ===== 共有字段（新旧 JSON 都有，非空） =====

    [JsonProperty("cid")] public long Cid { get; set; }

    [JsonProperty("page")] public int Page { get; set; }

    [JsonProperty("from")] public string From { get; set; } = string.Empty;

    [JsonProperty("part")] public string Part { get; set; } = string.Empty;

    [JsonProperty("vid")] public string Vid { get; set; } = string.Empty;

    [JsonProperty("has_alias")] public bool HasAlias { get; set; }

    [JsonProperty("tid")] public int Tid { get; set; }

    // ===== 非共有字段（可空） =====

    /// <summary>
    /// 新 JSON 示例中出现的 link 字段。
    /// </summary>
    [JsonProperty("link")] public string? Link { get; set; }

    /// <summary>
    /// 旧 JSON 示例中出现的 weblink 字段。
    /// </summary>
    [JsonProperty("weblink")] public string? Weblink { get; set; }

    [JsonProperty("width")] public int? Width { get; set; }

    [JsonProperty("height")] public int? Height { get; set; }

    [JsonProperty("rotate")] public int? Rotate { get; set; }

    [JsonProperty("download_title")] public string? DownloadTitle { get; set; }

    [JsonProperty("download_subtitle")] public string? DownloadSubtitle { get; set; }
}