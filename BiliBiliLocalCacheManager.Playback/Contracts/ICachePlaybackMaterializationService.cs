using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

/// <summary>
/// 只生成可播放的成品文件，不启动播放器。
/// 导出 MP4、预先转码等需要产物本身而不需要播放的场景使用。
/// </summary>
public interface ICachePlaybackMaterializationService
{
    /// <summary>
    /// 按播放计划生成成品文件。命中转码缓存时直接复用已有产物。
    /// </summary>
    /// <remarks>
    /// 返回结果中的 <see cref="PlaybackMaterializationResult.OutputPath"/> 可能指向
    /// 缓存中的原始文件（<see cref="PlaybackMaterializationResult.IsTemporary"/> 为 false）
    /// 或转码产物仓库中的文件，调用方都不应移动或删除它，只能复制。
    /// </remarks>
    Task<PlaybackMaterializationResult> MaterializeAsync(
        CachePlaybackPlan plan,
        IProgress<PlaybackPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
