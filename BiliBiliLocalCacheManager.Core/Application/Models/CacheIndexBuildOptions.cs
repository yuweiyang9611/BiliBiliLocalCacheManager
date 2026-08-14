namespace BiliBiliLocalCacheManager.Core.Application.Models;

/// <summary>
/// 构建缓存索引时的一些可选参数。
/// </summary>
public sealed class CacheIndexBuildOptions
{
    /// <summary>
    /// entry.json 文件名，默认 "entry.json"。
    /// </summary>
    public string EntryFileName { get; set; } = "entry.json";

    /// <summary>
    /// 认为是视频数据文件的扩展名列表（不区分大小写）。
    /// 默认包含 .blv, .flv, .mp4, .m4s, .ts。
    /// </summary>
    public IList<string> VideoFileExtensions { get; } = new List<string>
    {
        ".blv",
        ".flv",
        ".mp4",
        ".m4s",
        ".ts"
    };

    /// <summary>
    /// 是否包含未完成的缓存（IsCompleted = false）。
    /// 默认为 true，即全部包含。
    /// </summary>
    public bool IncludeIncompleteEntries { get; set; } = true;

    /// <summary>
    /// 是否在遇到无效 entry.json 时抛异常。
    /// 为 false 时则跳过该条目继续。
    /// 默认 false。
    /// </summary>
    public bool ThrowOnInvalidEntry { get; set; }

    /// <summary>
    /// 报告中最多保留的具体问题数量，统计总数不受此限制。
    /// </summary>
    public int MaxReportedIssues { get; set; } = 100;

    public CacheIndexBuildOptions Clone()
    {
        var copy = new CacheIndexBuildOptions
        {
            EntryFileName = EntryFileName,
            IncludeIncompleteEntries = IncludeIncompleteEntries,
            ThrowOnInvalidEntry = ThrowOnInvalidEntry,
            MaxReportedIssues = Math.Max(0, MaxReportedIssues)
        };

        copy.VideoFileExtensions.Clear();
        foreach (var extension in VideoFileExtensions)
        {
            copy.VideoFileExtensions.Add(extension);
        }

        return copy;
    }
}
