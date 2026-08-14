namespace BiliBiliLocalCacheManager.Core.Application.Models;

/// <summary>
/// 按 avid 删除缓存后的结果。
/// </summary>
public sealed class CacheDeletionResult(bool found,
    bool deleted,
    string? targetPath,
    string? errorMessage = null)
{
    /// <summary>
    /// 目标 avid 目录是否存在。
    /// </summary>
    public bool Found { get; } = found;

    /// <summary>
    /// 是否删除成功。
    /// </summary>
    public bool Deleted { get; } = deleted;

    /// <summary>
    /// 被删除（或将要删除）的目录路径。
    /// </summary>
    public string? TargetPath { get; } = targetPath;

    /// <summary>
    /// 删除失败时的错误信息（若有）。
    /// </summary>
    public string? ErrorMessage { get; } = errorMessage;
}