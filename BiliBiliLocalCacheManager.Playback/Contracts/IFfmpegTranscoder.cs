using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Contracts;

public interface IFfmpegTranscoder
{
    void ConcatToMp4(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken);

    void MuxDashPairToMp4(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan expectedDuration,
        IProgress<PlaybackPreparationProgress>? progress,
        CancellationToken cancellationToken);
}
