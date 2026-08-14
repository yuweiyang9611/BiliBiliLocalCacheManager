using BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed class BundledFfmpegPrewarmService : IFfmpegPrewarmService
{
    public async Task<FfmpegPrewarmResult> PrewarmAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(
                    () => BundledFfmpegBootstrapper.EnsureConfigured(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return FfmpegPrewarmResult.Ok("FFmpeg 已就绪。");
        }
        catch (OperationCanceledException)
        {
            return FfmpegPrewarmResult.Failed("FFmpeg 预热已取消。");
        }
        catch (Exception ex)
        {
            // 预热失败不影响使用：真正播放或导出时会走原有路径重试并把错误呈现给用户。
            return FfmpegPrewarmResult.Failed(ex.Message);
        }
    }
}
