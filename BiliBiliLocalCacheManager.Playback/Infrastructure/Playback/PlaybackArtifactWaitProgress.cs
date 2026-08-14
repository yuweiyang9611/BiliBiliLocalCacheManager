using System.Diagnostics;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

internal static class PlaybackArtifactWaitProgress
{
    public static Action<string, double?>? Create(
        IProgress<PlaybackPreparationProgress>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        return (stage, percentage) => progress.Report(
            new PlaybackPreparationProgress(
                stage,
                percentage,
                stopwatch.Elapsed,
                EstimatedRemaining: null));
    }
}
