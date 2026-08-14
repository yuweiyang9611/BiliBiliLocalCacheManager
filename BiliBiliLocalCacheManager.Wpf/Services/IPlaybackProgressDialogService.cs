using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public interface IPlaybackProgressDialogService
{
    Task<PlaybackLaunchResult> RunAsync(
        string title,
        Func<IProgress<PlaybackPreparationProgress>, CancellationToken, Task<PlaybackLaunchResult>> operation);
}
