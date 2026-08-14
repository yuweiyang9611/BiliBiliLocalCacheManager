namespace BiliBiliLocalCacheManager.Playback.Contracts;

public sealed record FfmpegPrewarmResult(bool Succeeded, string Message)
{
    public static FfmpegPrewarmResult Ok(string message) => new(true, message);

    public static FfmpegPrewarmResult Failed(string message) => new(false, message);
}

/// <summary>
/// 提前准备 FFmpeg，避免用户第一次点播放时才开始下载与解压。
/// </summary>
public interface IFfmpegPrewarmService
{
    /// <summary>
    /// 幂等；与用户手动触发的播放并发调用是安全的，先到者负责准备、后到者等待同一把锁。
    /// 失败不抛出，由返回值描述原因（例如离线时下载失败），真正播放时仍会重试。
    /// </summary>
    Task<FfmpegPrewarmResult> PrewarmAsync(CancellationToken cancellationToken = default);
}
